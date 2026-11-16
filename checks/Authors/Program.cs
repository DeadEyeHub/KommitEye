using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using KommitEye.Models;
using KommitEye.Platform;
using KommitEye.Server;
using KommitEye.Services;
using MimeKit;

var root = Path.Combine(Path.GetTempPath(), "kommiteye-authors-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS: " + message); }
var clock = DateTimeOffset.UtcNow;
var access = new AuthorAccess(Path.Combine(root, "unit"), new FixtureProtector(), () => clock);
var settings = new AuthorPortalSettings { Enabled = true, PublicUrl = "http://localhost", MaxAttempts = 2 };
access.Update(settings, 0);
var issued = access.Issue("a@example.invalid")!.Value;
Check(access.Issue("a@example.invalid") is null, "resend interval enforced");
Check(!File.ReadAllText(Path.Combine(root, "unit", "author-access.json")).Contains(issued.Code), "login code is not stored in plaintext");
access = new AuthorAccess(Path.Combine(root, "unit"), new FixtureProtector(), () => clock);
Check(access.Verify("a@example.invalid", issued.Code) && !access.Verify("a@example.invalid", issued.Code), "code survives restart and is consumed once");
clock = clock.AddMinutes(2);
issued = access.Issue("a@example.invalid")!.Value;
var wrong = issued.Code == "000000" ? "111111" : "000000";
Check(!access.Verify("a@example.invalid", wrong), "incorrect code rejected");
clock = clock.AddMinutes(2);
issued = access.Issue("a@example.invalid")!.Value;
wrong = issued.Code == "000000" ? "111111" : "000000";
Check(!access.Verify("a@example.invalid", wrong) && access.Issue("a@example.invalid") is null, "resending cannot reset the attempt budget; lockout enforced");
clock = clock.AddMinutes(16);
issued = access.Issue("a@example.invalid")!.Value;
Check(access.Verify("a@example.invalid", issued.Code), "sign-in recovers after lockout");
clock = clock.AddMinutes(2);
issued = access.Issue("a@example.invalid")!.Value;
clock = clock.AddMinutes(11);
Check(!access.Verify("a@example.invalid", issued.Code), "expired code rejected");
access.Update(settings with { SendsPerHour = 1 }, 1);
issued = access.Issue("a@example.invalid")!.Value; clock = clock.AddMinutes(2);
Check(access.Issue("a@example.invalid") is null, "hourly mail limit enforced");
access.Update(settings with { Enabled = false }, 2);
Check(!access.Verify("a@example.invalid", issued.Code), "disabling portal invalidates codes");

var data = Path.Combine(root, "server");
var reports = new ReportStore(data);
var repository = Path.Combine(root, "project");
void Seed(string sha, string email, bool findings = true)
{
    var result = new ReviewResult();
    if (findings) result.Findings.Add(new("src/example.cs", 12, "NEW", "A placeholder returns an empty result. / Заглушка возвращает пустой результат."));
    reports.Save("project.md", "project.md", repository, "main", new CommitInfo(sha, "Fixture Author", DateTimeOffset.UtcNow, "Prepare future feature", email), result, false, "Fixture review", default);
}
Seed(new string('a', 40), "a@example.invalid"); Seed(new string('b', 40), "b@example.invalid"); Seed(new string('c', 40), "a@example.invalid", false);
var review = reports.AuthorReviews("a@example.invalid").Single(r => r.FindingCount == 1);
var foreign = reports.AuthorReviews("b@example.invalid").Single();
Check(reports.AuthorReviews("a@example.invalid").Count == 2 && reports.AuthorDetail(foreign.Id, "a@example.invalid") is null, "author queries isolate owners and include clean commits");
try { reports.Respond(foreign.Id, 0, "a@example.invalid", "confirmed", "", true); throw new Exception("Foreign response allowed"); } catch (KeyNotFoundException) { }
Check(reports.AuthorReviews("a@example.invalid", filter: "pending").Count == 1, "awaiting-response filter works");
try { reports.Respond(review.Id, 0, "a@example.invalid", "confirmed", "", false, "stale"); throw new Exception("Stale response allowed"); } catch (InvalidOperationException) { }
reports.Respond(review.Id, 0, "a@example.invalid", "false_positive", "Future feature placeholder", true, review.SavedAt);
reports.Respond(review.Id, 0, "a@example.invalid", "false_positive", "Updated explanation", true, review.SavedAt);
Check(reports.PendingPortalNotices().Count(n => n.Kind == "feedback") == 1, "comment edits do not enqueue duplicate false-positive notices");
try { Seed(new string('a', 40), "a@example.invalid"); throw new Exception("Answered review overwritten"); } catch (InvalidOperationException) { }
Check(new ReportStore(data).AuthorDetail(review.Id, "a@example.invalid")!.Findings[0].Comment == "Updated explanation", "responses survive restart and blocked rechecks");

using var smtp = new FixtureSmtp(args.Contains("--preview"));
var smtpTask = smtp.Run();
using var portProbe = new TcpListener(IPAddress.Loopback, 0); portProbe.Start(); var port = ((IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
var config = Path.Combine(root, "server.json");
File.WriteAllText(config, JsonSerializer.Serialize(new { DataDirectory = data, Urls = $"http://127.0.0.1:{port}", GitExecutable = OperatingSystem.IsWindows() ? "git.exe" : "git", CredentialProtection = "CurrentUser" }));
#if DEBUG
const string configuration = "Debug";
#else
const string configuration = "Release";
#endif
var binary = Path.GetFullPath($"src/KommitEye.Server/bin/{configuration}/net10.0/KommitEye.Server.dll");
var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
start.Environment.Remove("KOMMITEYE_ADMIN_PASSWORD");
start.Environment.Remove("KOMMITEYE_KEY_CERTIFICATE");
start.Environment.Remove("KOMMITEYE_KEY_CERTIFICATE_PASSWORD_FILE");
if (!OperatingSystem.IsWindows() || args.Contains("--certificate-protection"))
{
    using var rsa = RSA.Create(2048);
    var request = new CertificateRequest("CN=KommitEye author fixture", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    var certificatePath = Path.Combine(root, "fixture.pfx");
    File.WriteAllBytes(certificatePath, certificate.Export(X509ContentType.Pfx));
    start.Environment["KOMMITEYE_KEY_CERTIFICATE"] = certificatePath;
}
start.ArgumentList.Add(binary); start.ArgumentList.Add("--config"); start.ArgumentList.Add(config);
using var process = Process.Start(start)!;
var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
try
{
    var origin = new Uri($"http://127.0.0.1:{port}");
    HttpClient Client()
    {
        var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = new Uri(origin, "/api/v1/"), Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.Add("X-KommitEye-Client", "web"); return client;
    }
    using var admin = Client(); using var author = Client();
    var startup = Stopwatch.StartNew();
    for (var i = 0; ; i++)
    {
        try { if ((await admin.GetAsync("setup")).IsSuccessStatusCode) break; } catch (HttpRequestException) { }
        if (process.HasExited || startup.Elapsed > TimeSpan.FromSeconds(30)) throw new Exception("Server did not start or localhost access is blocked.");
        await Task.Delay(100);
    }
    async Task<JsonNode> Get(HttpClient client, string path) => JsonNode.Parse(await client.GetStringAsync(path))!;
    async Task Post(HttpClient client, string path, object payload)
    {
        using var response = await client.PostAsJsonAsync(path, payload);
        if (!response.IsSuccessStatusCode) throw new Exception(path + ": " + response.StatusCode + " " + await response.Content.ReadAsStringAsync());
    }
    await Post(admin, "setup", new { password = "fixture-password" });
    var mail = await Get(admin, "mail/smtp");
    var mailSettings = new MailSettings { Enabled = true, Host = "127.0.0.1", Port = smtp.Port, Security = "None", From = "sender@example.invalid", Recipients = "admin@example.invalid", SendToAuthor = true, NotifyFalsePositives = true };
    await Post(admin, "mail/smtp", new { revision = (string)mail["revision"]!, settings = mailSettings, activate = true });
    var portal = await Get(admin, "author-settings");
    await Post(admin, "author-settings", new { revision = (long)portal["revision"]!, settings = settings with { PublicUrl = origin.ToString() } });
    Seed(new string('d', 40), "a@example.invalid", false);
    var invitation = await smtp.WaitFor(m => m.Subject?.Contains("dddddddd") == true);
    Check(invitation.To.Mailboxes.Single().Address == "a@example.invalid" && invitation.HtmlBody?.Contains("/author?review=") == true,
        "completed clean review sends a separate author invitation even when clean admin notifications are off");
    Check((await author.GetAsync("author/reviews")).StatusCode == HttpStatusCode.Unauthorized, "anonymous author API denied");
    await Post(author, "author/auth/request", new { email = "a@example.invalid" });
    var codeMail = await smtp.WaitFor(m => m.Subject?.Contains("Sign-in code") == true);
    var code = Regex.Match(codeMail.TextBody ?? "", @"Sign-in code: (\d{6})").Groups[1].Value;
    Check(code.Length == 6 && !codeMail.Attachments.Any(), "OTP is delivered without attachments through configured SMTP");
    await Post(author, "author/auth/verify", new { email = "a@example.invalid", code });
    Check((string)(await Get(author, "author/session"))["email"]! == "a@example.invalid", "email code creates author session");
    foreach (var path in new[] { "projects", "session", "mail/log", "reports", "author-settings", $"report-entries/{review.Id}", $"report-entries/{review.Id}/feedback" })
        Check((await author.GetAsync(path)).StatusCode == HttpStatusCode.Unauthorized, "author denied admin API " + path);
    Check((await author.PostAsJsonAsync(new Uri(origin, "/hubs/events/negotiate?negotiateVersion=1"), new { })).StatusCode == HttpStatusCode.Unauthorized, "author denied shared admin SignalR channel");
    Check((await author.GetAsync($"author/reviews/{foreign.Id}")).StatusCode == HttpStatusCode.NotFound, "HTTP detail rejects another author's ID");
    Check((await author.PostAsJsonAsync($"author/reviews/{foreign.Id}/findings/0", new { status = "confirmed", comment = "forged", savedAt = foreign.SavedAt })).StatusCode == HttpStatusCode.NotFound, "HTTP response rejects another author's ID");
    var list = await Get(author, "author/reviews");
    Check(list["items"]!.AsArray().Count == 3 && !list.ToJsonString().Contains(root.Replace("\\", "\\\\")), "author list includes only own commits and no server repository paths");
    await Post(author, $"author/reviews/{review.Id}/findings/0", new { status = "confirmed", comment = "Acknowledged", savedAt = review.SavedAt });
    await Post(author, $"author/reviews/{review.Id}/findings/0", new { status = "false_positive", comment = "Future feature", savedAt = review.SavedAt });
    var alert = await smtp.WaitFor(m => m.Subject?.Contains("False positive") == true && m.TextBody?.Contains("Future feature") == true);
    Check(alert.To.Mailboxes.Single().Address == "admin@example.invalid" && alert.HtmlBody?.Contains("href=") == true, "false-positive notification reaches configured administrator with a report link");
    var feedback = await Get(admin, $"report-entries/{review.Id}/feedback");
    Check((string)feedback["findings"]![0]!["comment"]! == "Future feature", "administrator sees author response and comment");
    var replay = await author.PostAsJsonAsync("author/auth/verify", new { email = "a@example.invalid", code });
    Check(replay.StatusCode == HttpStatusCode.Unauthorized, "HTTP code replay rejected");
    portal = await Get(admin, "author-settings");
    await Post(admin, "author-settings", new { revision = (long)portal["revision"]!, settings = settings with { Enabled = false, PublicUrl = origin.ToString() } });
    Check((await author.GetAsync("author/session")).StatusCode == HttpStatusCode.Unauthorized, "disabling portal revokes existing author sessions");
    Check((await admin.GetAsync("session")).IsSuccessStatusCode, "author configuration does not revoke administrator session");
    Console.WriteLine("All author portal checks passed.");
    if (args.Contains("--preview"))
    {
        portal = await Get(admin, "author-settings");
        await Post(admin, "author-settings", new { revision = (long)portal["revision"]!, settings = settings with { PublicUrl = origin.ToString() } });
        Console.WriteLine($"PREVIEW {origin} admin password: fixture-password; author: a@example.invalid");
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }
}
finally
{
    if (!process.HasExited) process.Kill(entireProcessTree: true);
    await process.WaitForExitAsync();
    var log = await output + await errors;
    File.WriteAllText(Path.Combine(root, "server.log"), log);
    if (!log.Contains("Request finished")) Console.WriteLine(string.Join(Environment.NewLine, log.Split('\n').TakeLast(25)));
    Console.WriteLine("Fixture files: " + root);
}

sealed class FixtureProtector : ISecretProtector
{
    public byte[] Protect(byte[] plaintext) => plaintext.ToArray();
    public byte[] Unprotect(byte[] ciphertext) => ciphertext.ToArray();
}
sealed class FixtureSmtp : IDisposable
{
    private readonly bool _preview;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<MimeMessage> _messages = new();
    public int Port { get; }
    public FixtureSmtp(bool preview = false) { _preview = preview; _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port; }
    public async Task Run()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                await writer.WriteLineAsync("220 fixture SMTP");
                while (await reader.ReadLineAsync(_stop.Token) is { } line)
                {
                    if (line.StartsWith("EHLO") || line.StartsWith("HELO")) await writer.WriteLineAsync("250 fixture");
                    else if (line == "DATA")
                    {
                        await writer.WriteLineAsync("354 send message"); var body = new StringBuilder();
                        while (await reader.ReadLineAsync(_stop.Token) is { } part && part != ".") body.AppendLine(part.StartsWith("..") ? part[1..] : part);
                        using var content = new MemoryStream(Encoding.ASCII.GetBytes(body.ToString()));
                        var message = MimeMessage.Load(content);
                        _messages.Enqueue(message);
                        if (_preview && message.Subject?.Contains("Sign-in code") == true) Console.WriteLine("FIXTURE EMAIL: " + message.TextBody);
                        await writer.WriteLineAsync("250 accepted");
                    }
                    else if (line == "QUIT") { await writer.WriteLineAsync("221 bye"); break; }
                    else await writer.WriteLineAsync("250 ok");
                }
            }
        }
        catch (OperationCanceledException) { }
    }
    public async Task<MimeMessage> WaitFor(Func<MimeMessage, bool> predicate)
    {
        for (var i = 0; i < 150; i++) { var result = _messages.FirstOrDefault(predicate); if (result is not null) return result; await Task.Delay(100); }
        throw new Exception("Expected fixture email did not arrive.");
    }
    public void Dispose() { _stop.Cancel(); _listener.Stop(); }
}
