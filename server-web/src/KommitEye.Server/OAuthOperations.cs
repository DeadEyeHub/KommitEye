using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using KommitEye.Services;

namespace KommitEye.Server;

public sealed class OAuthOperations : IAsyncDisposable
{
    public sealed record Operation(string Id, string Status, string Message, string? Url = null, string? Code = null);
    private readonly ConcurrentDictionary<string, Operation> _operations = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _callbacks = new();
    private readonly AsyncLocal<string?> _current = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private Task? _task;
    private MailNotifications _mail = null!;
    public MailNotifications CreateMail() => _mail = new MailNotifications(AppPaths.DataDirectory, Console.WriteLine,
        providers: [new GmailEmailProvider(AppPaths.DataDirectory, new ServerGoogleClient(this)),
            new OutlookEmailProvider(AppPaths.DataDirectory, new MicrosoftOAuthClient(result =>
            {
                Update("waiting", "Enter this code on Microsoft's sign-in page.", result.VerificationUrl, result.UserCode);
                return Task.CompletedTask;
            }))]);
    private void Update(string status, string message, string? url = null, string? code = null)
    {
        var id = _current.Value!; _operations[id] = new(id, status, message, url, code);
    }
    public Operation Begin(string provider, string configuration)
    {
        if (provider is not ("gmail" or "outlook")) throw new ArgumentException("Unsupported provider.");
        lock (_gate)
        {
            if (_task is { IsCompleted: false }) throw new InvalidOperationException("Another sign-in is already in progress.");
            _operations.Clear();
            var id = Guid.NewGuid().ToString("N");
            _operations[id] = new(id, "starting", "Preparing sign-in…");
            _task = Task.Run(async () =>
            {
                _current.Value = id;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromMinutes(3));
                try
                {
                    if (provider == "outlook")
                    {
                        var parts = configuration.Split(';', 2, StringSplitOptions.TrimEntries);
                        configuration = JsonSerializer.Serialize(new { clientId = parts[0], tenantId = parts.Length > 1 ? parts[1] : "common" });
                    }
                    await _mail.ConnectAsync(provider, configuration, timeout.Token);
                    Update("completed", "Account connected. Reload mail settings before saving preferences.");
                }
                catch (OperationCanceledException) { Update("failed", "Sign-in canceled or timed out."); }
                catch (Exception e) { Update("failed", e is MailProviderException or MailAuthorizationException ? e.Message : "Sign-in failed. Check OAuth settings on the server."); }
            });
            return _operations[id];
        }
    }
    public Operation Get(string id) => _operations.GetValueOrDefault(id) ?? throw new KeyNotFoundException();
    public bool Callback(string state, string? code, string? error)
    {
        if (!_callbacks.TryRemove(state, out var pending)) return false;
        if (error is not null || string.IsNullOrEmpty(code)) pending.TrySetException(new MailAuthorizationException());
        else pending.TrySetResult(code);
        return true;
    }
    public async ValueTask DisposeAsync() { _stop.Cancel(); if (_task is not null) await _task; _stop.Dispose(); }
    private sealed class ServerGoogleClient(OAuthOperations owner) : IGoogleOAuthClient
    {
        private readonly GoogleOAuthClient _tokens = new();
        public Task<string> AccessTokenAsync(GoogleMailSession session, bool refresh, CancellationToken token) => _tokens.AccessTokenAsync(session, refresh, token);
        public Task RevokeAsync(GoogleMailSession session, CancellationToken token) => _tokens.RevokeAsync(session, token);
        public async Task<GoogleMailSession> ConnectAsync(ClientSecrets client, CancellationToken token)
        {
            var redirect = Environment.GetEnvironmentVariable("KOMMITEYE_GOOGLE_REDIRECT_URI");
            if (string.IsNullOrWhiteSpace(redirect))
            {
                if (!Environment.UserInteractive) throw new MailProviderException("Configure KOMMITEYE_GOOGLE_REDIRECT_URI and a Google Web OAuth client for service sign-in.");
                owner.Update("waiting", "Complete Google sign-in in the browser on the server computer.");
                return await _tokens.ConnectAsync(client, token);
            }
            if (!Uri.TryCreate(redirect, UriKind.Absolute, out var uri) || uri.Scheme != "https" && !(uri.IsLoopback && uri.Scheme == "http") || uri.AbsolutePath != "/api/v1/oauth/google/callback")
                throw new MailProviderException("Google redirect must use HTTPS (or localhost HTTP) and /api/v1/oauth/google/callback.");
            using var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
            { ClientSecrets = client, Scopes = ["openid", "email", GoogleOAuthClient.SendScope] });
            var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            owner._callbacks[state] = pending;
            try
            {
                var request = flow.CreateAuthorizationCodeRequest(redirect);
                request.State = state;
                var url = request.Build().ToString() + "&prompt=consent";
                owner.Update("waiting", "Open Google sign-in in your browser.", url);
                var code = await pending.Task.WaitAsync(token);
                var tokens = await flow.ExchangeCodeForTokenAsync("gmail", code, redirect, token);
                if (string.IsNullOrEmpty(tokens.RefreshToken)) throw new MailAuthorizationException();
                using var http = new HttpClient();
                using var identityRequest = new HttpRequestMessage(HttpMethod.Get, "https://openidconnect.googleapis.com/v1/userinfo");
                identityRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
                using var response = await http.SendAsync(identityRequest, token);
                if (!response.IsSuccessStatusCode) throw new MailAuthorizationException();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                if (!json.RootElement.GetProperty("email_verified").GetBoolean()) throw new MailAuthorizationException();
                return new GoogleMailSession { Client = client, Token = tokens, AccountId = json.RootElement.GetProperty("sub").GetString()!, Email = json.RootElement.GetProperty("email").GetString()! };
            }
            finally { owner._callbacks.TryRemove(state, out _); }
        }
    }
}
