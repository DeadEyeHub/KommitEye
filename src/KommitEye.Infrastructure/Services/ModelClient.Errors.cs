using System.Net;
using System.Text;
using System.Text.Json;
using KommitEye.Models;

namespace KommitEye.Services;

public sealed partial class ModelClient
{
    public static readonly TimeSpan ErrorBodyTimeLimit = TimeSpan.FromSeconds(3);

    private async Task EnsureSuccessAsync(HttpResponseMessage response, ModelProfile profile, CancellationToken token)
    {
        if (response.IsSuccessStatusCode) return;
        var requestId = new[] { "x-request-id", "request-id", "x-ms-request-id" }
            .Select(name => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null)
            .FirstOrDefault(value => value is not null);
        requestId = requestId is null ? null : ReviewApiException.Sanitize(requestId, ResolveApiKey(profile), 256);
        var retryAfter = response.Headers.RetryAfter?.Delta ??
            (response.Headers.RetryAfter?.Date is { } date ? date - _timeProvider.GetUtcNow() : (TimeSpan?)null);
        if (retryAfter < TimeSpan.Zero) retryAfter = TimeSpan.Zero;
        var body = new StringBuilder();
        string? readFailure = null;
        using var timeout = new CancellationTokenSource(ErrorBodyTimeLimit, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(linked.Token).WaitAsync(linked.Token);
            using var reader = new StreamReader(stream);
            var buffer = new char[512];
            while (body.Length < 4096)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, 4096 - body.Length)), linked.Token)
                    .AsTask().WaitAsync(linked.Token);
                if (read == 0) break;
                body.Append(buffer, 0, read);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { readFailure = "Error body read timed out."; }
        catch (OperationCanceledException) { readFailure = "Error body read was interrupted."; }
        catch (Exception error) when (error is System.IO.IOException or HttpRequestException or InvalidOperationException)
        { readFailure = "Error body could not be read: " + ReviewApiException.Sanitize(error.Message, ResolveApiKey(profile), 256); }
        token.ThrowIfCancellationRequested();
        var excerpt = ReviewApiException.Sanitize(body.ToString(), ResolveApiKey(profile));
        var lower = body.ToString().ToLowerInvariant();
        string? code = null;
        try
        {
            using var json = JsonDocument.Parse(body.ToString());
            if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
                error.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String)
                code = value.GetString()?.ToLowerInvariant();
        }
        catch (JsonException) { }
        var status = (int)response.StatusCode;
        var kind = status is 401 or 403 ? ReviewApiErrorKind.Authentication :
            lower.Contains("context_length_exceeded") || lower.Contains("maximum context length") ||
            lower.Contains("context window") || lower.Contains("context overflow") ? ReviewApiErrorKind.ContextOverflow :
            status == 429 ? ReviewApiErrorKind.RateLimit :
            status == 404 && code is "model_not_ready" or "model_loading" or "deployment_not_ready" or "service_unavailable" ? ReviewApiErrorKind.Service :
            status is 408 or 500 or 502 or 503 or 504 ? ReviewApiErrorKind.Service :
            status is 400 or 404 or 405 or 413 or 422 ? ReviewApiErrorKind.Configuration : ReviewApiErrorKind.Unknown;
        var advice = kind switch
        {
            ReviewApiErrorKind.Authentication => "Check the API key and access permissions.",
            ReviewApiErrorKind.Configuration => "Check the endpoint, model name and request configuration.",
            ReviewApiErrorKind.ContextOverflow => "The request exceeds the model context; reduce the review context or select a larger-context model.",
            _ => ""
        };
        throw new ReviewApiException(kind,
            $"Review API HTTP {status}; request ID: {requestId ?? "unavailable"}. {advice} {excerpt} {readFailure}".Trim(),
            response.StatusCode, requestId, excerpt, retryAfter);
    }

    // Retry only the current request, keeping its conversation and the review's original deadline.
    private async Task<JsonDocument> SendReviewRequestAsync(HttpRequestMessage template, ModelProfile profile,
        CancellationToken token, Action<string>? log, Action<TokenUsage?>? usage,
        Action? beforeRequest)
    {
        var payload = await template.Content!.ReadAsByteArrayAsync(token);
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            beforeRequest?.Invoke();
            ReviewApiException failure;
            try
            {
                using var request = new HttpRequestMessage(template.Method, template.RequestUri)
                { Content = new ByteArrayContent(payload) };
                foreach (var header in template.Headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                foreach (var header in template.Content.Headers) request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                log?.Invoke($"API response status: {(int)response.StatusCode}");
                await EnsureSuccessAsync(response, profile, token);
                if (response.Headers.TryGetValues("x-request-id", out var ids))
                    log?.Invoke("API request ID: " + ReviewApiException.Sanitize(string.Join(", ", ids), ResolveApiKey(profile), 256));
                return await ModelResponseReader.ReadAsync(response.Content, log, token, usage, profile.Parameters);
            }
            catch (ReviewApiException error) { failure = error; }
            catch (HttpRequestException error)
            {
                failure = new ReviewApiException(ReviewApiErrorKind.Network,
                    "Model network failure: " + ReviewApiException.Sanitize(error.Message, ResolveApiKey(profile)), inner: error);
            }
            catch (IOException error)
            {
                failure = new ReviewApiException(ReviewApiErrorKind.Network,
                    "Model response transport failure: " + ReviewApiException.Sanitize(error.Message, ResolveApiKey(profile)), inner: error);
            }
            catch (OperationCanceledException error) when (!token.IsCancellationRequested && error.InnerException is TimeoutException)
            {
                failure = new ReviewApiException(ReviewApiErrorKind.Network, "Model HTTP request timed out.", inner: error);
            }
            token.ThrowIfCancellationRequested();
            if (!failure.IsTransient || attempt >= profile.Parameters.MaxRetries) throw failure;
            var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));
            if (failure.RetryAfter is { } requested && requested > delay) delay = requested;
            log?.Invoke($"{failure.Message} Retry {attempt + 1}/{profile.Parameters.MaxRetries} in {delay.TotalSeconds:F1} seconds.");
            // Large Retry-After values are bounded by the linked overall deadline, not shortened.
            while (delay > TimeSpan.Zero)
            {
                var part = delay > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : delay;
                await Task.Delay(part, _timeProvider, token);
                delay -= part;
            }
        }
    }
}
