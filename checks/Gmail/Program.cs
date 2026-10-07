using System.Net;
using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Http;
using KommitEye.Models;
using KommitEye.Services;
using MimeKit;

var root = Path.Combine(Path.GetTempPath(), "kommiteye-gmail-" + Guid.NewGuid());
Directory.CreateDirectory(root);
var config = Path.Combine(root, "desktop.json");
await File.WriteAllTextAsync(config, """{"installed":{"client_id":"fixture-client","client_secret":"fixture-secret","token_uri":"https://untrusted.invalid"}}""");
var checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
var oauth = new FakeOAuth();
var handler = new GmailHandler();
using var http = new HttpClient(handler);
var provider = new GmailEmailProvider(root, oauth, http);
Check(provider.Account is null, "starts disconnected");
await provider.ConnectAsync(config, default);
Check(provider.Account?.Address == "a@example.invalid", "authenticated mailbox");
var vault = Path.Combine(root, "oauth", "gmail-account.bin");
var encrypted = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(vault));
Check(!encrypted.Contains("fixture-secret") && !encrypted.Contains("refresh-secret") && !encrypted.Contains("access-secret") && !encrypted.Contains("a@example.invalid"), "no plaintext credentials or identity on disk");
provider = new GmailEmailProvider(root, oauth, http);
Check(provider.Account?.Id == "account-a", "restart loads protected session");
var job = new MailJob { Provider = "gmail", AccountId = "account-a", From = "a@example.invalid", Recipients = "recipient@example.invalid",
    MessageId = "fixture@example.invalid", Subject = "Report", Report = "# Review\nПример", AttachmentFormat = "md" };
var wrongJob = new MailJob { Provider = "gmail", AccountId = "another-account", From = job.From };
try { await provider.SendAsync(wrongJob, default); throw new Exception("Cross-account send accepted"); }
catch (MailProviderException) { }
Check(handler.Calls == 0 && !provider.Account!.NeedsAuthorization, "account mismatch blocked without invalidating connected account");
handler.Statuses.Enqueue(HttpStatusCode.Unauthorized);
handler.Statuses.Enqueue(HttpStatusCode.OK);
await provider.SendAsync(job, default);
Check(handler.Calls == 2 && oauth.ForcedRefreshes == 1, "401 refreshes once and retries");
Check(handler.Tokens.SequenceEqual(new[] { "access-secret", "refreshed-secret" }), "retry uses new token");
Check(handler.Messages.All(m => m.From.Mailboxes.Single().Address == "a@example.invalid" && m.To.Mailboxes.Single().Address == "recipient@example.invalid"), "sender and recipient retained");
Check(handler.Messages.All(m => m.Attachments.OfType<MimePart>().Single().FileName == "commit-review.md" && m.TextBody!.Contains("Пример")), "MIME body and attachment survive Gmail encoding");
var saved = new OAuthVault(vault).Load<GoogleMailSession>()!;
Check(saved.Token.AccessToken == "refreshed-secret" && saved.Token.RefreshToken == "rotated-secret", "rotated tokens persisted");
handler.Statuses.Enqueue(HttpStatusCode.Unauthorized); handler.Statuses.Enqueue(HttpStatusCode.Unauthorized);
try { await provider.SendAsync(job, default); throw new Exception("Unauthorized accepted"); }
catch (MailAuthorizationException) { }
Check(handler.Calls == 4 && provider.Account!.NeedsAuthorization, "second 401 requires reconnect");
provider = new GmailEmailProvider(root, oauth, http);
Check(provider.Account!.NeedsAuthorization, "authorization state survives restart");
var before = handler.Calls;
try { await provider.SendAsync(job, default); throw new Exception("Revoked account used"); }
catch (MailAuthorizationException) { }
Check(handler.Calls == before, "no network while reauthorization required");
await provider.ConnectAsync(config, default);
handler.Statuses.Enqueue(HttpStatusCode.Forbidden);
try { await provider.SendAsync(job, default); throw new Exception("Forbidden accepted"); }
catch (MailAuthorizationException) { }
Check(provider.Account!.NeedsAuthorization, "permissions need reconnect");
await provider.ConnectAsync(config, default);
handler.Statuses.Enqueue(HttpStatusCode.TooManyRequests);
try { await provider.SendAsync(job, default); throw new Exception("Rate limit accepted"); }
catch (MailProviderException) { }
Check(!provider.Account!.NeedsAuthorization, "rate limit does not invalidate account");
oauth.RevokeFails = true;
try { await provider.DisconnectAsync(default); throw new Exception("Revocation failure hidden"); }
catch (MailProviderException) { }
Check(provider.Account is null && !File.Exists(vault), "disconnect clears local tokens even offline");
oauth.RevokeFails = false;
await provider.ConnectAsync(config, default);
oauth.CancelConnect = true;
try { await provider.ConnectAsync(config, default); throw new Exception("Canceled connection accepted"); }
catch (OperationCanceledException) { }
Check(provider.Account!.Id == "account-a", "canceled sign-in preserves current account");
oauth.CancelConnect = false;
await File.WriteAllTextAsync(vault, "corrupt");
var corrupt = new GmailEmailProvider(root, oauth, http);
Check(corrupt.Account is null && corrupt.StatusError is not null, "corrupt secure store fails closed");
await provider.ConnectAsync(config, default);

// Exercise the actual Google SDK refresh path against a fake HTTPS token endpoint.
var factory = new TokenFactory();
var sdk = new GoogleOAuthClient(factory);
var session = new GoogleMailSession { Client = new ClientSecrets { ClientId = "fixture-client", ClientSecret = "fixture-secret" },
    Token = new TokenResponse { AccessToken = "expired", RefreshToken = "refresh-secret", ExpiresInSeconds = 3600, IssuedUtc = DateTime.UtcNow.AddHours(-2) } };
var access = await sdk.AccessTokenAsync(session, false, default);
Check(access == "sdk-refreshed" && factory.Calls == 1 && session.Token.RefreshToken == "sdk-rotated", "SDK automatically refreshes expired token and retains rotation");
factory.Revoked = true;
try { await sdk.AccessTokenAsync(session, true, default); throw new Exception("Revoked refresh accepted"); }
catch (MailAuthorizationException) { }
Check(factory.Calls == 2, "invalid_grant requires interactive reconnect");

var queueRoot = Path.Combine(root, "queue");
var queueOAuth = new FakeOAuth();
var queueHandler = new GmailHandler();
using var queueHttp = new HttpClient(queueHandler);
var queueProvider = new GmailEmailProvider(queueRoot, queueOAuth, queueHttp);
var logs = new List<string>();
await using (var mail = new MailNotifications(queueRoot, logs.Add, startWorker: false, providers: [queueProvider]))
{
    await mail.ConnectGoogleAsync(config, default);
    var settings = mail.Settings; settings.Enabled = true; settings.Recipients = "recipient@example.invalid";
    mail.SaveSettings(settings);
    var review = new CommitReviewed(root, "main", new string('a', 40), 1, false, false, false, "report", root);
    mail.Enqueue(review);
    await mail.DisconnectGoogleAsync(default);
    await mail.ProcessOnceAsync(default);
    Check(queueHandler.Calls == 0, "disconnect pauses queue");
    queueOAuth.Account = "b";
    await mail.ConnectGoogleAsync(config, default);
    settings = mail.Settings; settings.Enabled = true; mail.SaveSettings(settings);
    await mail.ProcessOnceAsync(default);
    Check(queueHandler.Calls == 0 && mail.PausedCount == 1, "new account cannot send old queue");
    queueOAuth.Account = "a";
    await mail.ConnectGoogleAsync(config, default);
    settings = mail.Settings; settings.Enabled = true; mail.SaveSettings(settings);
    queueHandler.Statuses.Enqueue(HttpStatusCode.Unauthorized); queueHandler.Statuses.Enqueue(HttpStatusCode.Unauthorized);
    await mail.ProcessOnceAsync(default);
    Check(mail.Settings.AuthorizationRequired && mail.Counts().Pending == 1 && mail.Counts().Failed == 0, "auth error pauses without exhausting attempts");
    await mail.ProcessOnceAsync(default);
    Check(queueHandler.Calls == 2, "paused worker does not retry auth");
    await mail.ConnectGoogleAsync(config, default);
    using var queueData = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(queueRoot, "mail-queue.json")));
    Check(queueData.RootElement[0].GetProperty("Attempts").GetInt32() == 0, "authorization failure does not consume retry");
    // Preserve the original delivery delay, then make it due using the worker's injected clock.
}
await using (var mail = new MailNotifications(queueRoot, logs.Add, startWorker: false, now: () => DateTimeOffset.UtcNow.AddHours(1), providers: [queueProvider]))
{
    await mail.ProcessOnceAsync(default);
    Check(mail.Counts().Pending == 0 && queueHandler.Calls == 3, "same-account reconnection resumes original queue");
}
Check(logs.All(l => !l.Contains("secret")), "logs contain no tokens");
var legacy = Path.Combine(root, "legacy"); Directory.CreateDirectory(legacy);
var oldSettings = new MailSettings { Enabled = true, Host = "old-smtp", From = "old@example.invalid", Recipients = "recipient@example.invalid" };
oldSettings.SetPassword("old-password");
await File.WriteAllTextAsync(Path.Combine(legacy, "mail-settings.json"), JsonSerializer.Serialize(oldSettings));
await using (var mail = new MailNotifications(legacy, logs.Add, startWorker: false))
{
    Check(!mail.Settings.Enabled && mail.Settings.EncryptedPassword.Length == 0, "legacy SMTP disabled and password removed");
    Check(JsonSerializer.Deserialize<MailSettings>(await File.ReadAllTextAsync(Path.Combine(legacy, "mail-settings.json")))!.EncryptedPassword.Length == 0, "legacy password removed on disk");
}
Console.WriteLine($"PASS: {checks} Gmail/OAuth checks; no real accounts accessed or mail sent.");

sealed class FakeOAuth : IGoogleOAuthClient
{
    public int ForcedRefreshes;
    public bool RevokeFails, CancelConnect;
    public string Account = "a";
    public Task<GoogleMailSession> ConnectAsync(ClientSecrets client, CancellationToken token)
    {
        if (CancelConnect) throw new OperationCanceledException();
        return Task.FromResult(new GoogleMailSession { Client = client, AccountId = "account-" + Account, Email = Account + "@example.invalid",
            Token = new TokenResponse { AccessToken = "access-secret", RefreshToken = "refresh-secret", IssuedUtc = DateTime.UtcNow, ExpiresInSeconds = 3600 } });
    }
    public Task<string> AccessTokenAsync(GoogleMailSession session, bool forceRefresh, CancellationToken token)
    {
        if (forceRefresh) { ForcedRefreshes++; session.Token.AccessToken = "refreshed-secret"; session.Token.RefreshToken = "rotated-secret"; }
        return Task.FromResult(session.Token.AccessToken);
    }
    public Task RevokeAsync(GoogleMailSession session, CancellationToken token) => RevokeFails ? throw new HttpRequestException() : Task.CompletedTask;
}

sealed class GmailHandler : HttpMessageHandler
{
    public Queue<HttpStatusCode> Statuses = new();
    public int Calls;
    public List<string> Tokens = [];
    public List<MimeMessage> Messages = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (request.RequestUri!.AbsoluteUri != "https://gmail.googleapis.com/gmail/v1/users/me/messages/send") throw new Exception("Unexpected endpoint");
        Calls++; Tokens.Add(request.Headers.Authorization!.Parameter!);
        using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        var raw = json.RootElement.GetProperty("raw").GetString()!.Replace('-', '+').Replace('_', '/');
        raw = raw.PadRight((raw.Length + 3) / 4 * 4, '=');
        using var bytes = new MemoryStream(Convert.FromBase64String(raw));
        Messages.Add(await MimeMessage.LoadAsync(bytes, token));
        return new HttpResponseMessage(Statuses.Count > 0 ? Statuses.Dequeue() : HttpStatusCode.OK)
        { Content = new StringContent("{}") };
    }
}

sealed class TokenFactory : Google.Apis.Http.IHttpClientFactory
{
    public int Calls;
    public bool Revoked;
    public ConfigurableHttpClient CreateHttpClient(CreateHttpClientArgs args) => new(new ConfigurableMessageHandler(new TokenHandler(this)));
    private sealed class TokenHandler(TokenFactory owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            owner.Calls++;
            if (request.RequestUri!.Host != "oauth2.googleapis.com" || !(await request.Content!.ReadAsStringAsync(token)).Contains("grant_type=refresh_token"))
                throw new Exception("Expected official refresh endpoint");
            return new HttpResponseMessage(owner.Revoked ? HttpStatusCode.BadRequest : HttpStatusCode.OK)
            { Content = new StringContent(owner.Revoked ? """{"error":"invalid_grant"}""" : """{"access_token":"sdk-refreshed","refresh_token":"sdk-rotated","expires_in":3600,"token_type":"Bearer"}""", Encoding.UTF8, "application/json") };
        }
    }
}
