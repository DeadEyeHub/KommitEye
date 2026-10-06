using System.Net;
using System.Text;
using GitReviewer.Services;
using MimeKit;

var root = Path.Combine(Path.GetTempPath(), "outlook-check-" + Guid.NewGuid());
var oauth = new FakeOAuth();
var handler = new Handler();
using var http = new HttpClient(handler);
var provider = new OutlookEmailProvider(root, oauth, http);
var count = 0;
void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; }
await provider.ConnectAsync(Guid.NewGuid().ToString(), default);
var vault = Path.Combine(root, "oauth", "outlook-account.bin");
Check(!Encoding.UTF8.GetString(File.ReadAllBytes(vault)).Contains("private-token"), "encrypted cache");
provider = new OutlookEmailProvider(root, oauth, http);
Check(provider.Account?.Id == "account", "restart restore");
var job = new MailJob { Provider = "outlook", AccountId = "account", From = "sender@example.invalid", Recipients = "to@example.invalid", MessageId = "fixture@example.invalid", Subject = "Report", Report = "# Review", AttachmentFormat = "md" };
handler.Statuses.Enqueue(HttpStatusCode.Unauthorized);
handler.Statuses.Enqueue(HttpStatusCode.Accepted);
await provider.SendAsync(job, default);
Check(oauth.Forced == 1 && handler.Calls == 2, "401 retries with refresh once");
Check(handler.Message!.Attachments.Count() == 1 && handler.Message.From.Mailboxes.Single().Address == job.From, "MIME sender and attachment");
Check(Encoding.UTF8.GetString(new OAuthVault(vault).Load<MicrosoftMailSession>()!.Cache) == "rotated-private-token", "rotation persisted");
job.AccountId = "wrong";
try { await provider.SendAsync(job, default); throw new Exception("wrong account accepted"); } catch (MailProviderException) { }
Check(handler.Calls == 2 && !provider.Account!.NeedsAuthorization, "identity isolation");
job.AccountId = "account";
handler.Statuses.Enqueue(HttpStatusCode.TooManyRequests);
try { await provider.SendAsync(job, default); throw new Exception("429 accepted"); } catch (MailProviderException) { }
Check(!provider.Account!.NeedsAuthorization, "429 remains retryable");
handler.Statuses.Enqueue(HttpStatusCode.Unauthorized); handler.Statuses.Enqueue(HttpStatusCode.Unauthorized);
try { await provider.SendAsync(job, default); throw new Exception("401 accepted"); } catch (MailAuthorizationException) { }
Check(provider.Account!.NeedsAuthorization, "second 401 pauses");
var calls = handler.Calls;
try { await provider.SendAsync(job, default); } catch (MailAuthorizationException) { }
Check(calls == handler.Calls, "paused sends do not hit network");
await provider.ConnectAsync("", default);
oauth.Cancel = true;
try { await provider.ConnectAsync(Guid.NewGuid().ToString(), default); } catch (OperationCanceledException) { }
Check(provider.Account!.Id == "account", "canceled sign in preserves account");
await provider.DisconnectAsync(default);
Check(!File.Exists(vault) && provider.Account is null, "disconnect removes cache");
var google = new FakeProvider("gmail"); var outlook = new FakeProvider("outlook");
await using (var notifications = new MailNotifications(root, _ => { }, startWorker:false, providers: [google, outlook]))
{
    await notifications.ConnectAsync("gmail", "", default);
    var settings = notifications.Settings; settings.Recipients = "google@example.invalid"; settings.Enabled = true;
    notifications.SaveSettings(settings);
    await notifications.ConnectAsync("outlook", "", default);
    settings = notifications.Settings; settings.Recipients = "outlook@example.invalid"; notifications.SaveSettings(settings);
    Check(notifications.GetSettings("gmail").Recipients == "google@example.invalid", "Google settings preserved");
    Check(notifications.GoogleAccount is not null, "Google remains connected");
    await notifications.DisconnectAsync("gmail", default);
    Check(notifications.Settings.Provider == "outlook" && !notifications.Settings.AuthorizationRequired, "disconnect inactive account leaves active settings");
}
Console.WriteLine($"{count} Outlook checks passed");
sealed class FakeOAuth : IMicrosoftOAuthClient
{
    public int Forced; public bool Cancel;
    public Task<MicrosoftMailSession> ConnectAsync(string client, CancellationToken token)
    {
        if (Cancel) throw new OperationCanceledException();
        return Task.FromResult(new MicrosoftMailSession { ClientId = Guid.Parse(client).ToString(), AccountId = "account", Email = "sender@example.invalid", Cache = Encoding.UTF8.GetBytes("private-token") });
    }
    public Task<string> AccessTokenAsync(MicrosoftMailSession session, bool force, CancellationToken token)
    { if(force) { Forced++; session.Cache = Encoding.UTF8.GetBytes("rotated-private-token"); } return Task.FromResult(force ? "new-token" : "token"); }
}
sealed class Handler : HttpMessageHandler
{
    public Queue<HttpStatusCode> Statuses = new(); public int Calls; public MimeMessage? Message;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Calls++;
        if(request.RequestUri!.AbsoluteUri != "https://graph.microsoft.com/v1.0/me/sendMail" || request.Content!.Headers.ContentType!.MediaType != "text/plain") throw new Exception("Graph request");
        Message = MimeMessage.Load(new MemoryStream(Convert.FromBase64String(await request.Content.ReadAsStringAsync(token))));
        return new HttpResponseMessage(Statuses.Count > 0 ? Statuses.Dequeue() : HttpStatusCode.Accepted);
    }
}
sealed class FakeProvider(string id) : IEmailProvider
{
    public string Id => id; public EmailAccount? Account { get; private set; } public string? StatusError => null;
    public Task<EmailAccount> ConnectAsync(string input, CancellationToken token) { Account = new(Id, Id+"-account", Id+"@example.invalid", false); return Task.FromResult(Account); }
    public Task DisconnectAsync(CancellationToken token) { Account = null; return Task.CompletedTask; }
    public Task SendAsync(MailJob job, CancellationToken token) => Task.CompletedTask;
}
