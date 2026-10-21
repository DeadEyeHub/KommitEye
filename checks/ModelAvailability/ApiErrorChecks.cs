using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using KommitEye.Models;
using KommitEye.Services;

static class ApiErrorChecks
{
    public static async Task RunAsync(string repo, GitService git, ConfigurationStore config)
    {
        var profile = new ModelProfile { Model = "fixture", Endpoint = "https://fixture.invalid/v1", ApiKey = "secret-fixture-key",
            Parameters = new ModelParameters { MaxRetries = 1 } };
        var sha = await git.GetHeadAsync(repo, default);
        foreach (var (status, body, kind, count) in new[]
        {
            (400, "context_length_exceeded", ReviewApiErrorKind.ContextOverflow, 1),
            (401, "invalid key", ReviewApiErrorKind.Authentication, 1),
            (403, "denied", ReviewApiErrorKind.Authentication, 1),
            (404, "model_not_found", ReviewApiErrorKind.Configuration, 1),
            (404, "route not found", ReviewApiErrorKind.Configuration, 1),
            (404, "{\"error\":{\"code\":\"model_loading\"}}", ReviewApiErrorKind.Service, 2),
            (422, "invalid parameter", ReviewApiErrorKind.Configuration, 1),
            (418, "original unknown diagnosis", ReviewApiErrorKind.Unknown, 1),
            (418, "[]", ReviewApiErrorKind.Unknown, 1),
            (429, "rate limited", ReviewApiErrorKind.RateLimit, 2),
            (503, "overloaded", ReviewApiErrorKind.Service, 2),
            (500, "server error", ReviewApiErrorKind.Service, 2)
        })
        {
            var handler = new ErrorHandler(() => Error(status, body));
            using var http = new HttpClient(handler);
            using var tools = await GitToolSession.CreateAsync(repo, sha, default);
            var runner = new ReviewRunner(git, new ModelClient(http), config, new StateStore(), new ReportWriter());
            try
            {
                await runner.ReviewSingleCommitAsync(new AppSettings { RepositoryPath = repo, BranchRef = "refs/heads/main", PullEnabled = false }, profile, sha, default);
                throw new Exception("Expected API failure");
            }
            catch (ReviewApiException error)
            {
                Check(error.Kind == kind && (int)error.StatusCode! == status, "structured classification");
                Check(error.Message.Contains(body) && error.RequestId == "fixture-request", "original diagnostics retained");
                Check(!error.Message.Contains("vLLM"), "no invented tool diagnosis");
            }
            Check(handler.Calls == count, $"bounded runner retries for {status}");
            Console.WriteLine($"PASS: API {status} / {kind}, {count} request(s).");
        }

        var network = new ErrorHandler(() => throw new HttpRequestException("fixture network failure"));
        using (var http = new HttpClient(network))
        using (var tools = await GitToolSession.CreateAsync(repo, sha, default))
        {
            try { await new ModelClient(http).ReviewAsync(profile, tools, "main", "Review", default); }
            catch (ReviewApiException error) { Check(error.Kind == ReviewApiErrorKind.Network && error.InnerException is HttpRequestException, "network diagnostics"); }
            Check(network.Calls == 2, "network retries bounded");
        }

        foreach (var dateHeader in new[] { false, true })
        {
            var handler = new ErrorHandler(() =>
            {
                var response = Error(429, "slow down");
                response.Headers.RetryAfter = dateHeader ? new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(3)) : new RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
                return response;
            });
            using var http = new HttpClient(handler);
            using var tools = await GitToolSession.CreateAsync(repo, sha, default);
            var elapsed = Stopwatch.StartNew();
            try { await new ModelClient(http).ReviewAsync(profile, tools, "main", "Review", default); }
            catch (ReviewApiException) { }
            Check(handler.Calls == 2 && elapsed.Elapsed >= TimeSpan.FromSeconds(1.9), "Retry-After respected");
        }
        Console.WriteLine("PASS: bounded network retries and both Retry-After formats.");

        var cancelHandler = new ErrorHandler(() => Error(503, "overloaded"));
        using (var http = new HttpClient(cancelHandler))
        using (var tools = await GitToolSession.CreateAsync(repo, sha, default))
        using (var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            try { await new ModelClient(http).ReviewAsync(profile, tools, "main", "Review", caller.Token); throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) when (caller.IsCancellationRequested) { }
            Check(cancelHandler.Calls == 1, "caller cancels backoff immediately");
        }

        foreach (var cancel in new[] { false, true })
        {
            var handler = new ErrorHandler(() =>
            {
                var response = Error(401, "");
                response.Content = new StreamContent(new HangingErrorStream());
                return response;
            });
            using var http = new HttpClient(handler);
            using var caller = new CancellationTokenSource();
            if (cancel) caller.CancelAfter(100);
            try { await new ModelClient(http).DiscoverModelsAsync(profile, caller.Token); throw new Exception("Expected error"); }
            catch (ReviewApiException error) when (!cancel)
            {
                Check(error.StatusCode == HttpStatusCode.Unauthorized && error.RequestId == "fixture-request" && error.Message.Contains("timed out"), "body timeout retains HTTP diagnostics");
            }
            catch (OperationCanceledException) when (cancel && caller.IsCancellationRequested) { }
        }
        Console.WriteLine("PASS: cancellation interrupts retries/body reads; body timeout preserves HTTP error.");

        using (var http = new HttpClient(new ErrorHandler(() =>
        {
            var response = Error(503, "");
            response.Content = new StreamContent(new HangingErrorStream(fail: true));
            return response;
        })))
        {
            try { await new ModelClient(http).DiscoverModelsAsync(profile, default); throw new Exception("Expected HTTP error"); }
            catch (ReviewApiException error)
            {
                Check(error.StatusCode == HttpStatusCode.ServiceUnavailable && error.RequestId == "fixture-request" &&
                    error.ResponseExcerpt == "partial diagnostic" && error.Message.Contains("could not be read"), "partial body read failure retains HTTP diagnostics");
            }
        }

        using (var http = new HttpClient(new ErrorHandler(() => Error(400,
            "secret-fixture-key\nBearer hidden-token password=hidden-password " + new string('x', 6000)))))
        {
            try { await new ModelClient(http).DiscoverModelsAsync(profile, default); throw new Exception("Expected error"); }
            catch (ReviewApiException error)
            {
                Check(error.ResponseExcerpt.Length <= 2049 && !error.ResponseExcerpt.Any(char.IsControl), "bounded clean excerpt");
                Check(!error.Message.Contains("secret-fixture-key") && !error.Message.Contains("hidden-token") && !error.Message.Contains("hidden-password"), "redacted secrets");
            }
        }
        Console.WriteLine("PASS: bounded sanitized response diagnostics.");

        var recovery = new RecoveryHandler();
        using (var http = new HttpClient(recovery))
        using (var tools = await GitToolSession.CreateAsync(repo, sha, default))
        {
            var result = await new ModelClient(http).ReviewAsync(profile, tools, "main", "Review", default);
            Check(result == "NO_BUGS" && recovery.Calls == 3 && recovery.SamePayload, "recovery retains current request/conversation");
        }

        var clock = new ReviewClock();
        using (var http = new HttpClient(new ErrorHandler(() => Error(503, "unavailable"))))
        using (var tools = await GitToolSession.CreateAsync(repo, sha, default))
        {
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var review = new ModelClient(http, clock).ReviewAsync(profile, tools, "main", "Review", default,
                log: message => { if (message.Contains("Retry 1/")) waiting.TrySetResult(); });
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromMinutes(profile.Parameters.ReviewMinutes));
            try { await review; throw new Exception("Expected deadline"); }
            catch (ReviewComplexityException) { }
        }

        var metadataClock = new ReviewClock();
        using (var http = new HttpClient(new MetadataTimeoutHandler(metadataClock)))
        {
            Check((await new ModelClient(http, metadataClock).DiscoverModelsAsync(profile, default)).Count == 0,
                "optional metadata timeout falls back");
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            try { await new ModelClient(http, metadataClock).DiscoverModelsAsync(profile, canceled.Token); throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) { }
        }
        Console.WriteLine("PASS: request recovery, deadline during backoff, metadata fallback and caller cancellation.");

        var auth = new ErrorHandler(() => Error(401, "invalid key"));
        using (var http = new HttpClient(auth))
        {
            var state = new StateStore();
            var runner = new ReviewRunner(git, new ModelClient(http), config, state, new ReportWriter());
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            runner.AutomaticStopped += () => stopped.TrySetResult();
            runner.Start(new AppSettings { RepositoryPath = repo, BranchRef = "refs/heads/main", PullEnabled = false }, profile);
            try { await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { await runner.StopAsync(); }
            var identity = await git.GetRepositoryIdentityAsync(repo, default);
            Check(auth.Calls == 1 && await state.GetCursorAsync(identity.CommonGitDirectory, "refs/heads/main", default) is null,
                "authentication stops automatic review without probes or cursor advancement");
        }
        Console.WriteLine("PASS: authentication stops automatic review immediately.");
    }

    static HttpResponseMessage Error(int status, string body)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) };
        response.Headers.Add("x-request-id", "fixture-request");
        return response;
    }
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}

sealed class MetadataTimeoutHandler(ReviewClock clock) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        clock.Advance(TimeSpan.FromSeconds(5));
        token.ThrowIfCancellationRequested();
        throw new Exception("Expected metadata cancellation");
    }
}

sealed class RecoveryHandler : HttpMessageHandler
{
    public int Calls;
    public bool SamePayload;
    private string? first;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var body = await request.Content!.ReadAsStringAsync(token);
        Calls++;
        if (Calls == 1)
        {
            first = body;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
        if (Calls == 2) SamePayload = first == body;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Calls == 2
            ? """{"choices":[{"message":{"content":null,"tool_calls":[{"id":"diff","type":"function","function":{"name":"git_diff","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}"""
            : """{"choices":[{"message":{"content":"NO_BUGS"},"finish_reason":"stop"}]}""") };
    }
}

sealed class ErrorHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
{
    public int Calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    { Calls++; return Task.FromResult(response()); }
}

sealed class HangingErrorStream(bool fail = false) : Stream
{
    private bool sent;
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        if (fail)
        {
            if (sent) throw new IOException("fixture interrupted body");
            sent = true;
            var bytes = System.Text.Encoding.UTF8.GetBytes("partial diagnostic");
            bytes.CopyTo(buffer);
            return bytes.Length;
        }
        await Task.Delay(Timeout.InfiniteTimeSpan, token); return 0;
    }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
