using System.Diagnostics;
using System.Net;
using System.Text.Json;
using KommitEye.Models;
using KommitEye.Services;

var root = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "skip-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(root);
var repo = Path.Combine(root, "repo"); Directory.CreateDirectory(repo);
await Git("init", "-b", "main"); await Git("config", "user.name", "Fixture"); await Git("config", "user.email", "fixture@example.invalid");
var shas = new List<string>();
for (var i = 0; i < 4; i++) {
    await File.WriteAllTextAsync(Path.Combine(repo, "file.txt"), $"revision {i}\n");
    await Git("add", "."); await Git("commit", "-m", "Fixture " + i);
    shas.Add(await Git("rev-parse", "HEAD"));
}
foreach (var mode in new[] { "skip", "stop", "invalid", "mail-failure", "api-failure", "decision-api-failure" }) {
    Environment.SetEnvironmentVariable("KOMMITEYE_DATA_DIR", Path.Combine(root, mode));
    var git = new GitService(); var identity = await git.GetRepositoryIdentityAsync(repo, default);
    var state = new StateStore(); await state.SetStartCommitAsync(identity.CommonGitDirectory, "refs/heads/main", shas[0], default);
    var handler = new FixtureHandler(mode); using var http = new HttpClient(handler);
    var notices = new List<CommitReviewed>();
    var reports = new ReportWriter();
    var runner = new ReviewRunner(git, new ModelClient(http), new ConfigurationStore(), state, reports,
        queueSkipNotice: notice => { if (mode == "mail-failure") throw new IOException("queue unavailable"); notices.Add(notice); });
    var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    runner.AutomaticStopped += () => ended.TrySetResult();
    var profile = new ModelProfile { Model = "fixture", Endpoint = "https://fixture.invalid/v1",
        Parameters = new ModelParameters { MaxRounds = 1, MaxRetries = 0 } };
    runner.Start(new AppSettings { RepositoryPath = repo, BranchRef = "refs/heads/main", PullEnabled = false }, profile);
    await ended.Task.WaitAsync(TimeSpan.FromSeconds(20)); await runner.StopAsync();
    var cursor = await state.GetCursorAsync(identity.CommonGitDirectory, "refs/heads/main", default);
    if (mode == "skip") {
        Check(cursor == shas[2], "third skipped commit cursor: " + runner.FailureReason);
        Check(notices.Count == 3 && handler.Decisions == 4 && handler.Reviews == 4, "fourth decision stops without fourth skip");
        Check(await new StateStore().GetConsecutiveBudgetSkipsAsync(identity.CommonGitDirectory, "refs/heads/main", default) == 3, "counter persists");
        Check(reports.Browse(outcome: "skipped").Total == 3 && reports.Browse(outcome: "clean").Total == 0, "skips never clean");
        Check(reports.Browse(outcome: "failed").Total == 1, "fourth commit remains failed and pending");
        ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Start(new AppSettings { RepositoryPath = repo, BranchRef = "refs/heads/main", PullEnabled = false }, profile);
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(20)); await runner.StopAsync();
        Check(handler.Decisions == 5 && notices.Count == 3, "restart retries pending commit but cannot authorize fourth skip");
        using var cleanHttp = new HttpClient(new FixtureHandler("clean"));
        var cleanRunner = new ReviewRunner(git, new ModelClient(cleanHttp), new ConfigurationStore(), state, reports);
        var cleanProfile = profile.Clone(); cleanProfile.Parameters.MaxRounds = 2;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cleanRunner.Reviewed += r => { if (!r.Failed) completed.TrySetResult(); };
        cleanRunner.Start(new AppSettings { RepositoryPath = repo, BranchRef = "refs/heads/main", PullEnabled = false }, cleanProfile);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(20)); await cleanRunner.StopAsync();
        Check(await state.GetConsecutiveBudgetSkipsAsync(identity.CommonGitDirectory, "refs/heads/main", default) == 0, "successful fourth automatic review resets counter");
        Check(await state.GetCursorAsync(identity.CommonGitDirectory, "refs/heads/main", default) == shas[3], "successful fourth commit advances normally");
    } else {
        Check(cursor == null, "failed authorization/notification never advances cursor");
        Check(handler.Decisions == (mode == "api-failure" ? 0 : 1), "no extra decision attempts");
    }
    Console.WriteLine("PASS " + mode);
}
// Skipping the final remote-tracking commit must leave monitoring alive, not replay it.
await Git("update-ref", "refs/remotes/origin/last-skip", shas[3]);
{
    var lastGit = new GitService();
    var lastIdentity = await lastGit.GetRepositoryIdentityAsync(repo, default);
    var lastState = new StateStore();
    const string branch = "refs/remotes/origin/last-skip";
    await lastState.SetCursorAsync(lastIdentity.CommonGitDirectory, branch, shas[2], default, 0);
    var handler = new FixtureHandler("skip"); using var http = new HttpClient(handler);
    var runner = new ReviewRunner(lastGit, new ModelClient(http), new ConfigurationStore(), lastState, new ReportWriter(), queueSkipNotice: _ => {});
    var waitedTwice = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var waits = 0;
    runner.Progress += p => { if (p.Stage == ReviewStage.WaitingForChanges && ++waits == 2) waitedTwice.TrySetResult(); };
    runner.AutomaticStopped += () => waitedTwice.TrySetException(new Exception(runner.FailureReason));
    runner.Start(new AppSettings { RepositoryPath = repo, BranchRef = branch, PullEnabled = false, PollIntervalSeconds = 5 },
        new ModelProfile { Model = "fixture", Endpoint = "https://fixture.invalid/v1", Parameters = new ModelParameters { MaxRounds = 1, MaxRetries = 0 } });
    try {
        await waitedTwice.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Check(runner.IsRunning && runner.FailureReason is null, "monitoring stays alive after last commit skip");
        Check(handler.Reviews == 1 && handler.Decisions == 1, "last skipped commit is not replayed on next poll");
        Check(await lastState.GetCursorAsync(lastIdentity.CommonGitDirectory, branch, default) == shas[3], "last skipped commit cursor saved");
    } finally { await runner.StopAsync(); }
}
Console.WriteLine("PASS final remote-tracking commit skip continues polling");
// The full diff exceeds 16,000 characters, but the host supplies it in smaller packets.
Environment.SetEnvironmentVariable("KOMMITEYE_DATA_DIR", Path.Combine(root, "limits"));
await File.WriteAllTextAsync(Path.Combine(repo, "file.txt"), new string('x', 30000));
await Git("add", "."); await Git("commit", "-m", "Large fixture");
var largeSha = await Git("rev-parse", "HEAD");
var limitedProfile = new ModelProfile { Model = "fixture", Endpoint = "https://fixture.invalid/v1",
    Parameters = new ModelParameters { ToolOutputLimit = 16000 } };
var packetHandler = new FixtureHandler("clean");
using (var http = new HttpClient(packetHandler))
using (var tools = await GitToolSession.CreateAsync(repo, largeSha, default)) {
    var report = await new ModelClient(http).ReviewAsync(limitedProfile, tools, "main", "Review", default);
    Check(report == "NO_BUGS" && tools.ReadyForFinal && packetHandler.Reviews > 2, "output allowance renews between packets");
}
// A full git_file page plus the supplied patch exceeds the first packet's own allowance.
var oversizedRead = new FixtureHandler("oversized-read");
using (var http = new HttpClient(oversizedRead))
using (var tools = await GitToolSession.CreateAsync(repo, largeSha, default)) {
    try {
        await new ModelClient(http).ReviewAsync(limitedProfile, tools, "main", "Review", default);
        throw new Exception("Expected the first packet's oversized read to exhaust its output allowance.");
    } catch (ReviewBudgetException e) {
        Check(e.Message.Contains("output budget") && oversizedRead.Reviews == 1 && !tools.ReadyForFinal, "typed per-packet output budget failure");
    }
}
Console.WriteLine("PASS output budget");
// Fresh databases remain v2; repair the previous experimental schema without losing records.
var migrationDir = Path.Combine(root, "migration");
var oldStore = new ReportStore(migrationDir);
oldStore.Save("fixture.md", "fixture.md", repo, "main", new CommitInfo(shas[0], "Fixture", DateTimeOffset.UtcNow, "Old"), new ReviewResult(), false, "old report", default);
using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(migrationDir, "reports.db")}")) {
    db.Open(); using var command = db.CreateCommand();
    command.CommandText = "PRAGMA user_version";
    Check(Convert.ToInt32(command.ExecuteScalar()) == 2, "fresh database retains old schema version");
    command.CommandText = "ALTER TABLE reviews ADD COLUMN budget_skipped INTEGER NOT NULL DEFAULT 0; UPDATE reviews SET budget_skipped=1, failure='Budget exhausted'; PRAGMA user_version=3;"; command.ExecuteNonQuery();
}
var upgraded = new ReportStore(migrationDir);
Check(upgraded.Browse(outcome: "skipped").Total == 1 && upgraded.Read("fixture.md")!.Contains("old report"), "v3 repair preserves reports and skip status");
using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(migrationDir, "reports.db")}")) {
    db.Open(); using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version";
    Check(Convert.ToInt32(command.ExecuteScalar()) == 2, "repaired database readable by old version gate");
    command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('reviews') WHERE name='budget_skipped'";
    Check(Convert.ToInt32(command.ExecuteScalar()) == 0, "no extra column");
    command.CommandText = "UPDATE reviews SET failure=NULL, markdown='old application successful review'"; command.ExecuteNonQuery();
}
Check(new ReportStore(migrationDir).Browse(outcome: "clean").Total == 1, "old application overwrite clears skipped status");
Console.WriteLine("PASS reports v2 migration");
var mailDir = Path.Combine(root, "mail");
await using (var mail = new MailNotifications(mailDir, _ => {}, startWorker: false)) {
    var notice = new CommitReviewed(repo, "main", shas[0], 0, false, false, false, "Skipped — incomplete", repo, Failed: true, BudgetSkipped: true);
    try { mail.EnqueueBudgetSkip(notice); throw new Exception("disabled mail accepted"); }
    catch (InvalidOperationException) { }
    mail.SaveSettings(new MailSettings { Enabled = true, Host = "smtp.invalid", From = "bot@example.invalid", Recipients = "operator@example.invalid", Security = "None" });
    mail.EnqueueBudgetSkip(notice); mail.EnqueueBudgetSkip(notice); mail.Enqueue(notice);
    var jobs = JsonSerializer.Deserialize<List<MailJob>>(await File.ReadAllTextAsync(Path.Combine(mailDir, "mail-queue.json")))!;
    Check(jobs.Count == 1 && !jobs[0].Sent, "durable deduplicated skip email, no duplicate regular notification");
}
Console.WriteLine("PASS durable mail queue");
var startConfig = new ConfigurationStore(); startConfig.InitializeProjects();
var startProject = new AppSettings { ProjectId = Guid.NewGuid().ToString("N"), RepositoryPath = repo, BranchRef = "refs/heads/main", PullEnabled = false, AutoStart = true };
startConfig.SaveProjects(new ProjectCatalog { Projects = [startProject] });
startConfig.SaveModels(new ModelsConfiguration { ActiveProfile = "fixture", Profiles = { new ModelProfile { Name = "fixture", Model = "fixture", Endpoint = "https://fixture.invalid/v1" } } });
var startState = new StateStore();
var startIdentity = await new GitService().GetRepositoryIdentityAsync(repo, default);
var startHead = await Git("rev-parse", "HEAD");
await startState.SetCursorAsync(startIdentity.CommonGitDirectory, startProject.BranchRef, startHead, default, 3);
await using (var startMail = new MailNotifications(Path.Combine(root, "start-mail"), _ => {}, startWorker: false))
await using (var coordinator = new ProjectCoordinator(startConfig, new ModelClient(), startState, new ReportWriter(), startMail, new JobStore(Path.Combine(root, "start-jobs")))) {
    Check(await coordinator.GetStartSkipCountAsync(startProject.ProjectId) == 3, "start preflight returns current counter");
    try {
        await coordinator.StartAsync(startProject.ProjectId, null, Guid.NewGuid().ToString(), manualStart: true);
        throw new Exception("unconfirmed reset accepted");
    } catch (InvalidOperationException e) when (e.Message.Contains("Confirm resetting")) { }
    Check(await coordinator.GetStartSkipCountAsync(startProject.ProjectId) == 3, "unconfirmed start preserves counter");
    Check(!(await coordinator.SnapshotAsync()).Statuses.Single().Running, "unconfirmed start stays stopped");
    var key = Guid.NewGuid().ToString();
    var job = await coordinator.StartAsync(startProject.ProjectId, null, key, manualStart: true, resetSkipCounter: true);
    Check(await coordinator.GetStartSkipCountAsync(startProject.ProjectId) == 0, "confirmed manual start resets counter");
    await startState.SetCursorAsync(startIdentity.CommonGitDirectory, startProject.BranchRef, startHead, default, 2);
    Check((await coordinator.StartAsync(startProject.ProjectId, null, key, manualStart: true, resetSkipCounter: true)).Id == job.Id, "repeated start is idempotent");
    Check(await coordinator.GetStartSkipCountAsync(startProject.ProjectId) == 2, "duplicate request does not reset twice");
    await coordinator.StopAsync(startProject.ProjectId);
    await coordinator.StartConfiguredProjectsAsync();
    Check(await coordinator.GetStartSkipCountAsync(startProject.ProjectId) == 2, "autostart preserves counter");
}
Console.WriteLine("PASS manual start confirmation, reset, idempotency and autostart");
Console.WriteLine("All budget skip checks passed.");

async Task<string> Git(params string[] args) {
    var start = new ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var arg in args) start.ArgumentList.Add(arg);
    using var process = Process.Start(start)!;
    var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync(); if (process.ExitCode != 0) throw new Exception(await error);
    return (await output).Trim();
}
static void Check(bool value, string name) { if (!value) throw new Exception(name); }
sealed class FixtureHandler(string mode) : HttpMessageHandler {
    public int Decisions, Reviews;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) {
        var body = await request.Content!.ReadAsStringAsync(token);
        using var doc = JsonDocument.Parse(body);
        bool decision = !doc.RootElement.TryGetProperty("tools", out _);
        object message; string finish;
        if (decision) {
            Decisions++;
            if (mode == "decision-api-failure") return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("offline") };
            var evidence = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            if (!evidence.Contains("file.txt") || evidence.Contains("diff --git")) throw new Exception("Expected bounded numstat, not full diff");
            message = new { role = "assistant", content = mode == "invalid" ? "nonsense" : JsonSerializer.Serialize(new { decision = mode == "stop" ? "STOP" : "SKIP", reason = "Isolated change exceeds review budget; leave explicitly unreviewed." }) };
            finish = "stop";
        } else {
            Reviews++;
            if ((mode == "clean" && Reviews == 2) || (mode != "oversized-read" && body.Contains("Mandatory partitioned review protocol")))
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content = "NO_BUGS" }, finish_reason = "stop" } } }), System.Text.Encoding.UTF8, "application/json") };
            if (mode == "api-failure") return new(HttpStatusCode.Unauthorized) { Content = new StringContent("bad key") };
            var function = mode == "oversized-read"
                ? new { name = "git_file", arguments = """{"path":"file.txt"}""" }
                : new { name = "git_diff", arguments = """{"offset":0}""" };
            message = new { role = "assistant", content = "", tool_calls = new[] { new { id = "call-1", type = "function", function } } };
            finish = "tool_calls";
        }
        return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message, finish_reason = finish } } }), System.Text.Encoding.UTF8, "application/json") };
    }
}
