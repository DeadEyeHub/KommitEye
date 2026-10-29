using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using KommitEye.Models;
using KommitEye.Platform;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using KommitEye.Services;
using KommitEye.Server.Hosting;
using Microsoft.AspNetCore.DataProtection;

var root = Path.Combine(Path.GetTempPath(), "kommiteye-server-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
await MailSettingsChecks.RunAsync(root);
var useCertificate = !OperatingSystem.IsWindows() || args.Contains("--certificate-protection");
var certificatePath = Path.Combine(root, "fixture.pfx");
if (useCertificate)
{
    using var rsa = RSA.Create(2048);
    var request = new CertificateRequest("CN=KommitEye fixture", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    File.WriteAllBytes(certificatePath, certificate.Export(X509ContentType.Pfx));
}
var repo = Path.Combine(root, "repo"); Directory.CreateDirectory(repo);
async Task<string> Git(params string[] arguments)
{
    var start = new ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var arg in arguments) start.ArgumentList.Add(arg);
    using var child = Process.Start(start)!;
    var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
    await child.WaitForExitAsync(); if (child.ExitCode != 0) throw new Exception(await error); return (await output).Trim();
}
await Git("init", "-b", "main"); await Git("config", "user.name", "Fixture"); await Git("config", "user.email", "fixture@example.invalid");
await Git("commit", "--allow-empty", "-m", "Empty fixture commit");
var sha = await Git("rev-parse", "HEAD");
using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
#if DEBUG
const string buildConfiguration = "Debug";
#else
const string buildConfiguration = "Release";
#endif
var serviceConfig = args.Contains("--service-config");
var serverArgument = args.FirstOrDefault(arg => arg is not ("--certificate-protection" or "--service-config"));
var serverPath = serverArgument is not null ? Path.GetFullPath(serverArgument) : Path.GetFullPath($"src/KommitEye.Server/bin/{buildConfiguration}/net10.0/KommitEye.Server.dll");
var useAppHost = !Path.GetExtension(serverPath).Equals(".dll", StringComparison.OrdinalIgnoreCase);
var gitExecutable = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
    .Select(path => Path.Combine(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')), OperatingSystem.IsWindows() ? "git.exe" : "git"))
    .First(File.Exists);
HostConfigurationChecks.Run(root, gitExecutable);
await StartupConfigurationChecks.RunAsync(root, serverPath);
await CommandLineChecks.RunAsync(root, serverPath);
var configurationPath = Path.Combine(root, "service.json");
File.WriteAllText(configurationPath, JsonSerializer.Serialize(new {
    DataDirectory = Path.Combine(root, "data"), Urls = $"http://127.0.0.1:{port}", GitExecutable = gitExecutable,
    CredentialProtection = serviceConfig && OperatingSystem.IsWindows() ? "LocalMachine" : "CurrentUser"
}));
Process? server = null;
var outputLog = new List<string>();
void Start()
{
    var start = new ProcessStartInfo(useAppHost ? serverPath : "dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    if (!useAppHost) start.ArgumentList.Add(serverPath);
    if (serviceConfig) { start.ArgumentList.Add("--config"); start.ArgumentList.Add(configurationPath); }
    else
    {
        start.ArgumentList.Add("--config"); start.ArgumentList.Add(Path.Combine(root, "console", "server.json"));
        start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(Path.Combine(root, "data"));
        start.ArgumentList.Add("--urls"); start.ArgumentList.Add($"http://127.0.0.1:{port}");
        start.ArgumentList.Add("--git-path"); start.ArgumentList.Add(gitExecutable);
    }
    start.Environment["KOMMITEYE_DATA_DIR"] = Path.Combine(root, "data"); start.Environment.Remove("KOMMITEYE_ADMIN_PASSWORD");
    start.Environment.Remove("KOMMITEYE_KEY_CERTIFICATE_PASSWORD_FILE");
    start.Environment.Remove("KOMMITEYE_KEY_CERTIFICATE");
    if (useCertificate) start.Environment["KOMMITEYE_KEY_CERTIFICATE"] = certificatePath;
    server = Process.Start(start)!;
    server.OutputDataReceived += (_, e) => { if (e.Data is not null) lock(outputLog) outputLog.Add(e.Data); };
    server.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock(outputLog) outputLog.Add(e.Data); };
    server.BeginOutputReadLine(); server.BeginErrorReadLine();
}
void Stop() { if (server is { HasExited: false }) { server.Kill(true); server.WaitForExit(); } server?.Dispose(); }
using var http = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = new Uri($"http://127.0.0.1:{port}/api/v1/"), Timeout = TimeSpan.FromSeconds(15) };
var checks = 0;
void Check(bool value, string description) { if (!value) throw new Exception(description); checks++; Console.WriteLine("PASS: " + description); }
async Task Wait()
{
    for(var i=0;i<100;i++)
    {
        if(server!.HasExited) throw new Exception(string.Join("\n",outputLog));
        try { using var response = await http.GetAsync("session"); return; } catch(HttpRequestException) { await Task.Delay(100); }
    }
    throw new Exception("Server did not start.");
}
async Task<JsonNode> Get(string path) => JsonNode.Parse(await http.GetStringAsync(path))!;
async Task<JsonNode?> Post(string path, object value)
{
    using var response = await http.PostAsJsonAsync(path,value);
    var text = await response.Content.ReadAsStringAsync();
    if(!response.IsSuccessStatusCode) throw new Exception(path+": "+response.StatusCode+" "+text);
    return string.IsNullOrWhiteSpace(text)?null:JsonNode.Parse(text);
}
try
{
    Start(); await Wait();
    if (useAppHost)
    {
        var indexPath = Path.Combine(Path.GetDirectoryName(serverPath)!, "wwwroot", "index.html");
        var index = await http.GetStringAsync("/");
        Check(index == await File.ReadAllTextAsync(indexPath), "packaged executable serves its bundled React entry point");
        var assets = System.Text.RegularExpressions.Regex.Matches(index, "(?:src|href)=\"(/assets/[^\"]+)\"");
        Check(assets.Count > 0, "packaged React entry point references built assets");
        foreach (System.Text.RegularExpressions.Match asset in assets)
        {
            using var response = await http.GetAsync(asset.Groups[1].Value);
            Check(response.IsSuccessStatusCode && (await response.Content.ReadAsByteArrayAsync()).Length > 0,
                "packaged asset is available: " + asset.Groups[1].Value);
        }
    }
    Check((await Get("setup"))["required"]!.GetValue<bool>() && !File.Exists(Path.Combine(root,"data","initial-password.txt")) && !File.Exists(Path.Combine(root,"data","admin.json")),"first launch waits for user password without generating one");
    Check((await http.GetAsync("projects")).StatusCode == HttpStatusCode.Unauthorized,"anonymous project access denied");
    Check((await http.GetAsync("mail/log")).StatusCode == HttpStatusCode.Unauthorized,"mail log requires authentication");
    Check((await http.PostAsJsonAsync("login",new {password="1"})).StatusCode == HttpStatusCode.Forbidden,"cross-site form commands blocked");
    http.DefaultRequestHeaders.Add("X-KommitEye-Client","web");
    Check((await http.PostAsJsonAsync("repositories/browse",new {path=repo})).StatusCode == HttpStatusCode.Unauthorized,"server file browsing requires authentication");
    Check((await http.PostAsJsonAsync("login",new {password="incorrect"})).StatusCode == HttpStatusCode.Unauthorized,"wrong password denied");
    await Post("setup",new {password="1"});
    Check(!(await Get("setup"))["required"]!.GetValue<bool>(),"one-character password is accepted during setup");
    Check((await http.PostAsJsonAsync("setup",new {password="replacement"})).StatusCode==HttpStatusCode.Conflict,"setup cannot replace an existing administrator password");
    var snapshot = await Get("projects");
    var project = snapshot["projects"]![0]!.Deserialize<AppSettings>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    using (var invalid = await http.PostAsJsonAsync($"projects/{project.ProjectId}/start", new { requestKey = Guid.NewGuid().ToString() }))
        Check(invalid.StatusCode == HttpStatusCode.BadRequest && (await invalid.Content.ReadAsStringAsync()).Contains("repository", StringComparison.OrdinalIgnoreCase), "start rejects an unconfigured repository");
    // Simulate a legacy project with a repository but no explicitly selected branch.
    project.RepositoryPath = repo;
    project.BranchRef = "";
    File.WriteAllText(Path.Combine(root, "data", "projects.json"), JsonSerializer.Serialize(new ProjectCatalog { Projects = [project] }));
    using (var invalid = await http.PostAsJsonAsync($"projects/{project.ProjectId}/start", new { requestKey = Guid.NewGuid().ToString() }))
        Check(invalid.StatusCode == HttpStatusCode.BadRequest && (await invalid.Content.ReadAsStringAsync()).Contains("branch", StringComparison.OrdinalIgnoreCase), "start rejects missing branch instead of silently using HEAD");
    Check((await Get("jobs")).AsArray().Count == 0, "invalid starts create no jobs");
    project.DisplayName="Fixture";project.RepositoryPath=repo;project.BranchRef="refs/heads/main";project.PullEnabled=false;
    var fixtureKey = Path.Combine(repo,"fixture.key"); await File.WriteAllTextAsync(fixtureKey,"PRIVATE-KEY-CONTENT-MUST-NOT-LEAVE-SERVER");
    var folders = await Post("repositories/browse",new {path=repo,files=false});
    Check(folders!["entries"]!.AsArray().All(e => e!["directory"]!.GetValue<bool>()),"repository picker lists directories only");
    var files = await Post("repositories/browse",new {path=fixtureKey,files=true});
    Check(files!["entries"]!.AsArray().Any(e => (string)e!["path"]! == fixtureKey) && !files.ToJsonString().Contains("PRIVATE-KEY-CONTENT"),"key picker returns metadata without private key contents");
    Check((string)(await Post("repositories/detect",project))!["mode"]! == "ssh-agent","authentication detection supports local repositories");
    foreach(var mode in new[]{"auto","ssh-agent","ssh-key","putty-key","https"})
    {
        project.GitAuthenticationMode=mode;
        Check((string)(await Post("repositories/test",project))!["remote"]! == "","connection test validates local repository in "+mode+" mode");
    }
    project.GitAuthenticationMode="invalid";
    Check((await http.PostAsJsonAsync("repositories/test",project)).StatusCode==HttpStatusCode.BadRequest,"unsupported authentication mode rejected");
    project.GitAuthenticationMode="auto";
    project.BranchRef = " ";
    using (var invalid = await http.PostAsJsonAsync("projects", new { revision = (long)snapshot["revision"]!, settings = project }))
        Check(invalid.StatusCode == HttpStatusCode.BadRequest && (await invalid.Content.ReadAsStringAsync()).Contains("branch", StringComparison.OrdinalIgnoreCase), "missing branch returns an actionable validation error");
    project.BranchRef = "refs/heads/main";
    await Post("projects",new {revision=(long)snapshot["revision"]!,settings=project});
    Check((await http.PostAsJsonAsync("projects",new {revision=(long)snapshot["revision"]!,settings=project})).StatusCode==HttpStatusCode.Conflict,"stale project edit rejected");
    var models=await Get("models");
    var profile=models["profiles"]![0]!["profile"]!.Deserialize<ModelProfile>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    profile.Name="fixture";profile.Model="fixture";profile.Endpoint="http://127.0.0.1:1/v1";profile.ApiKey="fixture-secret";
    await Post("models",new {revision=(string)models["revision"]!,activeProfile="fixture",profiles=new[]{profile},clearKeys=Array.Empty<string>()});
    Check(!(await http.GetStringAsync("models")).Contains("fixture-secret"),"model API does not expose saved secret");
    var key=Guid.NewGuid().ToString();
    var job=await Post($"projects/{project.ProjectId}/reviews",new {requestKey=key,sha});
    var duplicate=await Post($"projects/{project.ProjectId}/reviews",new {requestKey=key,sha});
    Check((string)job!["id"]! == (string)duplicate!["id"]!,"repeated request returns the same job");
    for(var i=0;i<100;i++){job=await Get("jobs/"+(string)job!["id"]!);if((string)job["status"]! !="running")break;await Task.Delay(100);}
    Check((string)job!["status"]! == "completed","manual review completed without a model request for empty diff");
    var mailLog = await http.GetStringAsync("mail/log");
    Check(mailLog.Contains("Mail:") && mailLog.Contains(sha),"mail log includes notification result for reviewed commit");
    Check((await Get("reports")).AsArray().Count==1,"persisted report available through API");
    var reportName=(string)(await Get("reports"))[0]!["name"]!;
    var entries = await Get("report-entries?q=" + sha);
    Check((long)entries["total"]! == 1 && (string)entries["items"]![0]!["sha"]! == sha,
        "commit report API searches by SHA");
    var entryId = (long)entries["items"]![0]!["id"]!;
    Check((string)entries["items"]![0]!["outcome"]! == "empty", "empty diff is not classified as a completed clean review");
    Check((long)(await Get("report-entries?outcome=bug"))["total"]! == 0 &&
          (long)(await Get("report-entries?outcome=empty"))["total"]! == 1,
        "outcome API filter scopes results");
    Check((await http.GetAsync("report-entries?outcome=invalid")).StatusCode == HttpStatusCode.BadRequest,
        "invalid outcome filter rejected");
    Check((await http.GetStringAsync("report-entries/" + entryId)).Contains(sha), "individual Markdown report is available");
    using (var entryHtml = await http.GetAsync("report-entries/" + entryId + "?format=html&download=true"))
        Check(entryHtml.Content.Headers.ContentType?.MediaType == "text/html" &&
              entryHtml.Content.Headers.ContentDisposition?.DispositionType == "attachment",
            "individual HTML report can be downloaded");
    Check((long)(await Get("report-entries?q=no-such-report"))["total"]! == 0, "report search returns empty results");
    Check((await http.GetAsync("report-entries?sort=invalid")).StatusCode == HttpStatusCode.BadRequest &&
          (await http.GetAsync("report-entries?offset=-1")).StatusCode == HttpStatusCode.BadRequest,
        "report API rejects invalid sorting and pagination");
    Check((await http.GetAsync("report-entries/9223372036854775807")).StatusCode == HttpStatusCode.NotFound,
        "missing review returns 404");
    using(var htmlResponse=await http.GetAsync("reports/"+Uri.EscapeDataString(reportName)+"?format=html"))
    {
        var html=await htmlResponse.Content.ReadAsStringAsync();
        Check(htmlResponse.Content.Headers.ContentType?.MediaType=="text/html" && htmlResponse.Content.Headers.ContentDisposition is null && html.Contains("<!doctype html>") && html.Contains("<h2>"),"HTML report opens inline as a standalone formatted document");
    }
    using (var download = await http.GetAsync("reports/" + Uri.EscapeDataString(reportName) + "?format=html&download=true"))
        Check(download.Content.Headers.ContentDisposition?.DispositionType == "attachment", "explicit HTML download remains available");
    var hostileHtml=KommitEye.Server.HtmlReport.Render("<script>title</script>","## `<img src=x onerror=alert(1)>`\n<script>alert(1)</script>\n```text\n</code><script>alert(1)</script>\n```");
    Check(!hostileHtml.Contains("<script>") && !hostileHtml.Contains("<img") && hostileHtml.Contains("&lt;script&gt;"),"HTML rendering escapes executable Git and model content");
    Check((await Get($"projects/{project.ProjectId}/commits?offset=0"))["commits"]!.AsArray().Count==1,"commit paging uses server repository");
    await Post($"projects/{project.ProjectId}/start-commit",new {requestKey=Guid.NewGuid().ToString(),sha});
    var smtp=await Get("mail/smtp");smtp["settings"]!["host"]="smtp.example.invalid";smtp["settings"]!["from"]="sender@example.invalid";smtp["settings"]!["recipients"]="recipient@example.invalid";
    await Post("mail/smtp",new {revision=(string)smtp["revision"]!,settings=smtp["settings"],password="fixture-mail-secret",clearPassword=false,activate=true});
    Check(!(await http.GetStringAsync("mail/smtp")).Contains("fixture-mail-secret"),"SMTP API does not expose password");
    var monitoring = await Post($"projects/{project.ProjectId}/start",new {requestKey=Guid.NewGuid().ToString()});
    await Post("logout",new {});
    await Post("login",new {password="1"});
    Check((await Get("projects"))["statuses"]![0]!["running"]!.GetValue<bool>(),"signing out does not stop server monitoring");
    Check((await http.PostAsJsonAsync($"projects/{project.ProjectId}/delete",new {revision=(long)(await Get("projects"))["revision"]!})).StatusCode==HttpStatusCode.Conflict,"running project cannot be deleted");
    await Post($"projects/{project.ProjectId}/stop",new {});
    Check((string)(await Get("jobs/"+(string)monitoring!["id"]!))["status"]! == "canceled","explicit Stop persists canceled job");
    var interrupted = await Post($"projects/{project.ProjectId}/start",new {requestKey=Guid.NewGuid().ToString()});
    Stop();
    var detailPath=Path.Combine(root,"data","logs",project.ProjectId,"model.log");
    Directory.CreateDirectory(Path.GetDirectoryName(detailPath)!);
    await File.WriteAllTextAsync(detailPath,"fixture detail log\n"); await File.WriteAllTextAsync(detailPath+".1","fixture archived log\n");
    Start(); await Wait();
    await Post("login",new {password="1"});
    var journalBefore=await http.GetStringAsync($"projects/{project.ProjectId}/log");
    Check((await http.GetStringAsync($"projects/{project.ProjectId}/log?details=true")).Contains("fixture archived log"),"detail log includes persisted rotated history");
    await Post($"projects/{project.ProjectId}/log/clear?details=true",new {});
    Check((await http.GetStringAsync($"projects/{project.ProjectId}/log?details=true")) == "" && !File.Exists(detailPath) && !File.Exists(detailPath+".1"),"clear removes detailed log and rotated archive");
    Check(await http.GetStringAsync($"projects/{project.ProjectId}/log") == journalBefore && (await Get("reports")).AsArray().Count==1,"clearing details preserves journal and reports");
    Check((await Get("jobs")).AsArray().Count==3,"job history survives restart");
    Check((string)(await Get("jobs/"+(string)interrupted!["id"]!))["status"]! == "interrupted","crashed monitoring is visible as interrupted after restart");
    var after=await Post($"projects/{project.ProjectId}/reviews",new {requestKey=key,sha});
    Check((string)after!["id"]! == (string)job["id"]!,"request deduplication survives restart");
    Check((await Get("mail/smtp"))["passwordConfigured"]!.GetValue<bool>(),"SMTP secret survives restart");
    var persistedDocument = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "data", "mail-settings.json")))!;
    Check((string)persistedDocument["activeProvider"]! == "smtp" && !File.Exists(Path.Combine(root, "data", "mail-settings.json.smtp")), "service saves mail settings in one file");
    var persistedMail = persistedDocument["providers"]!["smtp"]!.Deserialize<MailSettings>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    if (useCertificate)
    {
        using var certificate = Pkcs12Certificates.Load(certificatePath);
        var provider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "data", "session-keys")),
            options => options.SetApplicationName("KommitEye.Server").ProtectKeysWithCertificate(certificate));
        var protector = new DataProtectionSecretProtector(provider);
        Check(persistedMail.GetPassword(protector) == "fixture-mail-secret", "certificate-protected SMTP secret decrypts with persisted keys in a new provider");
        var vaultPath = Path.Combine(root, "data", "oauth-fixture.bin");
        new OAuthVault(vaultPath, protector).Save(new Dictionary<string, string> { ["token"] = "fixture-oauth-token" });
        var reloadedProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "data", "session-keys")),
            options => options.SetApplicationName("KommitEye.Server").ProtectKeysWithCertificate(certificate));
        Check(new OAuthVault(vaultPath, new DataProtectionSecretProtector(reloadedProvider)).Load<Dictionary<string, string>>()!["token"] == "fixture-oauth-token",
            "certificate-protected OAuth vault survives provider recreation");
        Check(Directory.GetFiles(Path.Combine(root, "data", "session-keys"), "*.xml")
            .All(path => File.ReadAllText(path).Contains("encryptedSecret")), "persisted key ring is encrypted at rest");
    }
    else if (serviceConfig && OperatingSystem.IsWindows())
        Check(persistedMail.GetPassword(new WindowsMachineSecretProtector()) == "fixture-mail-secret", "service config retains LocalMachine-protected SMTP secret after restart");
    else Check(persistedMail.GetPassword() == "fixture-mail-secret", "persisted SMTP password retains Windows DPAPI compatibility");
    Check((await http.GetStringAsync("mail/log")).Contains(sha),"mail log survives server restart");
    var deleteRevision=(long)(await Get("projects"))["revision"]!;
    Check((await http.PostAsJsonAsync($"projects/{project.ProjectId}/delete",new {revision=deleteRevision-1})).StatusCode==HttpStatusCode.Conflict,"stale deletion rejected");
    await Post($"projects/{project.ProjectId}/delete",new {revision=deleteRevision});
    Check((await Get("projects"))["projects"]!.AsArray().Count==0 && (await Get("projects"))["statuses"]!.AsArray().Count==0,"last project can be deleted without a placeholder");
    Check(Directory.Exists(Path.Combine(repo,".git")) && (await Get("reports")).AsArray().Count==1 && (await Get("jobs")).AsArray().Count==3,"project deletion preserves repository reports and job history");
    Stop(); Start(); await Wait(); await Post("login",new {password="1"});
    Check((await Get("projects"))["projects"]!.AsArray().Count==0,"empty project catalog survives restart");
    project.ProjectId="";
    await Post("projects",new {revision=(long)(await Get("projects"))["revision"]!,settings=project});
    Check((await Get("projects"))["projects"]!.AsArray().Count==1,"project can be added after deleting the last project");
    await Post("logout",new {});
    Check((await http.GetAsync("projects")).StatusCode==HttpStatusCode.Unauthorized,"logout invalidates browser session");
    Console.WriteLine($"PASS: {checks} server integration checks. Fixture: {root}");
}
finally {Stop();}
