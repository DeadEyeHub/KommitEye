using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using KommitEye.Models;

var root = Path.Combine(Path.GetTempPath(), "kommiteye-server-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
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
var serverDll = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath($"src/KommitEye.Server/bin/{buildConfiguration}/net10.0-windows/KommitEye.Server.dll");
Process? server = null;
var outputLog = new List<string>();
void Start()
{
    var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    start.ArgumentList.Add(serverDll); start.ArgumentList.Add("--urls"); start.ArgumentList.Add($"http://127.0.0.1:{port}");
    start.Environment["KOMMITEYE_DATA_DIR"] = Path.Combine(root, "data"); start.Environment["KOMMITEYE_ADMIN_PASSWORD"] = "fixture-password-12345";
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
    Check((await http.GetAsync("projects")).StatusCode == HttpStatusCode.Unauthorized,"anonymous project access denied");
    Check((await http.PostAsJsonAsync("login",new {password="fixture-password-12345"})).StatusCode == HttpStatusCode.Forbidden,"cross-site form commands blocked");
    http.DefaultRequestHeaders.Add("X-KommitEye-Client","web");
    Check((await http.PostAsJsonAsync("login",new {password="incorrect"})).StatusCode == HttpStatusCode.Unauthorized,"wrong password denied");
    await Post("login",new {password="fixture-password-12345"});
    var snapshot = await Get("projects");
    var project = snapshot["projects"]![0]!.Deserialize<AppSettings>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    project.DisplayName="Fixture";project.RepositoryPath=repo;project.BranchRef="refs/heads/main";project.PullEnabled=false;
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
    Check((await Get("reports")).AsArray().Count==1,"persisted report available through API");
    Check((await Get($"projects/{project.ProjectId}/commits?offset=0"))["commits"]!.AsArray().Count==1,"commit paging uses server repository");
    await Post($"projects/{project.ProjectId}/start-commit",new {requestKey=Guid.NewGuid().ToString(),sha});
    var smtp=await Get("mail/smtp");smtp["settings"]!["host"]="smtp.example.invalid";smtp["settings"]!["from"]="sender@example.invalid";smtp["settings"]!["recipients"]="recipient@example.invalid";
    await Post("mail/smtp",new {revision=(string)smtp["revision"]!,settings=smtp["settings"],password="fixture-mail-secret",clearPassword=false,activate=true});
    Check(!(await http.GetStringAsync("mail/smtp")).Contains("fixture-mail-secret"),"SMTP API does not expose password");
    var monitoring = await Post($"projects/{project.ProjectId}/start",new {requestKey=Guid.NewGuid().ToString()});
    await Post("logout",new {});
    await Post("login",new {password="fixture-password-12345"});
    Check((await Get("projects"))["statuses"]![0]!["running"]!.GetValue<bool>(),"signing out does not stop server monitoring");
    await Post($"projects/{project.ProjectId}/stop",new {});
    Check((string)(await Get("jobs/"+(string)monitoring!["id"]!))["status"]! == "canceled","explicit Stop persists canceled job");
    var interrupted = await Post($"projects/{project.ProjectId}/start",new {requestKey=Guid.NewGuid().ToString()});
    Stop(); Start(); await Wait();
    await Post("login",new {password="fixture-password-12345"});
    Check((await Get("jobs")).AsArray().Count==3,"job history survives restart");
    Check((string)(await Get("jobs/"+(string)interrupted!["id"]!))["status"]! == "interrupted","crashed monitoring is visible as interrupted after restart");
    var after=await Post($"projects/{project.ProjectId}/reviews",new {requestKey=key,sha});
    Check((string)after!["id"]! == (string)job["id"]!,"request deduplication survives restart");
    Check((await Get("mail/smtp"))["passwordConfigured"]!.GetValue<bool>(),"SMTP secret survives restart");
    await Post("logout",new {});
    Check((await http.GetAsync("projects")).StatusCode==HttpStatusCode.Unauthorized,"logout invalidates browser session");
    Console.WriteLine($"PASS: {checks} server integration checks. Fixture: {root}");
}
finally {Stop();}
