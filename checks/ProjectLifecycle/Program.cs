using System.Diagnostics;
using KommitEye.Models;
using KommitEye.Services;
using KommitEye.Server;

var root = Path.Combine(Path.GetTempPath(), "kommiteye-lifecycle-" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("KOMMITEYE_DATA_DIR", root);
Directory.CreateDirectory(root);
int checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS: " + name); checks++; }
var configuration = new ConfigurationStore();
configuration.InitializeProjects();
Check(!configuration.LoadSettings().AutoStart, "existing projects default to manual start");
var settings = configuration.LoadSettings(); settings.AutoStart = true; configuration.SaveSettings(settings);
Check(configuration.LoadSettings().AutoStart, "autostart survives saving and loading");
var sent = new List<MailJob>();
var sentBodies = new List<string>();
var mailDirectory = Path.Combine(root, "mail");
var mail = new MailNotifications(mailDirectory, _ => {}, (settings, job, token) => { sent.Add(job); sentBodies.Add(job.Report); return Task.CompletedTask; }, startWorker: false, providers: []);
mail.EnqueueOperational("fixture", "Project error", "disabled");
Check(mail.Counts().Pending == 0, "disabled mail does not queue alerts");
mail.SaveSettings(new MailSettings { Enabled = true, Host = "smtp.invalid", From = "sender@example.invalid", Recipients = "operator@example.invalid", SendWithoutBugs = false });
mail.EnqueueOperational("fixture", "Project error", "https://user:secret@example.invalid/repo?token=private");
var queued = File.ReadAllText(Path.Combine(mailDirectory, "mail-queue.json"));
Check(!queued.Contains("secret") && !queued.Contains("private"), "alert diagnostics redact URL credentials");
await mail.ProcessOnceAsync(default);
Check(sent.Count == 1 && sent[0].Recipients.Contains("operator@example.invalid"), "operational alert ignores clean-report filter and uses explicit recipients");

var repo = Path.Combine(root, "repo"); Directory.CreateDirectory(repo);
async Task<string> Git(params string[] args) {
    var start = new ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var arg in args) start.ArgumentList.Add(arg);
    using var child = Process.Start(start)!;
    var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
    await child.WaitForExitAsync(); if (child.ExitCode != 0) throw new Exception(await error); return (await output).Trim();
}
await Git("init", "-b", "main");
await Git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "--allow-empty", "-m", "fixture");
var git = new GitService(); var identity = await git.GetRepositoryIdentityAsync(repo, default);
var state = new StateStore();
await state.SetCursorAsync(identity.CommonGitDirectory, "refs/heads/main", await Git("rev-parse", "HEAD"), default);
var profile = new ModelProfile { Name = "fixture", Model = "fixture", Endpoint = "http://127.0.0.1:1", Parameters = new() { MaxRetries = 0 } };
configuration.SaveModels(new ModelsConfiguration { ActiveProfile = "fixture", Profiles = { profile } });
var broken = new AppSettings { ProjectId = Guid.NewGuid().ToString("N"), AutoStart = true, RepositoryPath = Path.Combine(root, "missing"), BranchRef = "main" };
var enabled = new AppSettings { ProjectId = Guid.NewGuid().ToString("N"), AutoStart = true, RepositoryPath = repo, BranchRef = "refs/heads/main", PullEnabled = false };
var disabled = new AppSettings { ProjectId = Guid.NewGuid().ToString("N"), AutoStart = false, RepositoryPath = repo, BranchRef = "refs/heads/main" };
configuration.SaveProjects(new ProjectCatalog { Projects = [broken, enabled, disabled] });
var jobs = new JobStore(root);
var coordinator = new ProjectCoordinator(configuration, new ModelClient(), state, new ReportWriter(), mail, jobs);
await coordinator.StartConfiguredProjectsAsync();
var snapshot = await coordinator.SnapshotAsync();
Check(snapshot.Statuses.Single(s => s.ProjectId == enabled.ProjectId).Running, "valid project autostarts after another project fails");
Check(!snapshot.Statuses.Single(s => s.ProjectId == disabled.ProjectId).Running, "unselected project remains stopped");
Check(snapshot.Statuses.Single(s => s.ProjectId == broken.ProjectId).Status == "failed", "autostart error visible in status");
using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
    while ((await coordinator.SnapshotAsync()).Statuses.Single(s => s.ProjectId == enabled.ProjectId).Stage != "WaitingForChanges")
        await Task.Delay(20, timeout.Token);
Check((await coordinator.SnapshotAsync()).Statuses.Single(s => s.ProjectId == enabled.ProjectId).Running,
    "repository polling wait is distinct from model wait and remains running");
await coordinator.DisposeAsync();
Check(jobs.List().All(j => j.Status != "running"), "shutdown stops workers and finalizes jobs");

var runner = new ReviewRunner(new GitService(), new ModelClient(), configuration, state, new ReportWriter());
var notices = new List<(string Kind, string Detail)>();
runner.OperationalEvent += notice => { lock (notices) notices.Add(notice); };
runner.Start(broken, profile);
await runner.StopAsync();
Check(notices.Count(n => n.Kind == "Project stopped") == 1, "manual stop emits exactly one stop event");
notices.Clear();
profile.Parameters.MaxRetries = 3;
runner.Start(broken, profile);
using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
    while (runner.IsRunning) await Task.Delay(20, timeout.Token);
await runner.StopAsync();
Check(notices.Any(n => n.Kind == "Project error") && notices.Count(n => n.Kind == "Project stopped") == 1, "repository failure emits error and automatic stop");

// A real SQLite failure must stop before moving the cursor or starting the next commit.
var firstPending = await Git("rev-parse", "HEAD");
await Git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "--allow-empty", "-m", "next commit must wait");
await state.SetStartCommitAsync(identity.CommonGitDirectory, "refs/heads/main", firstPending, default);
var cursorBeforeFailure = await state.GetCursorAsync(identity.CommonGitDirectory, "refs/heads/main", default);
var databaseDirectory = Path.Combine(root, "broken-reports");
var brokenWriter = new ReportWriter(new ReportStore(databaseDirectory));
using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(databaseDirectory, "reports.db")}"))
{
    db.Open();
    using var command = db.CreateCommand(); command.CommandText = "DROP TABLE report_groups"; command.ExecuteNonQuery();
}
var databaseRunner = new ReviewRunner(git, new ModelClient(), configuration, state, brokenWriter);
var started = new List<string>();
var stoppedWithError = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
databaseRunner.Progress += progress => { if (progress.Stage == ReviewStage.Started) started.Add(progress.Commit); };
databaseRunner.OperationalEvent += notice => {
    mail.EnqueueOperational("database fixture", notice.Kind, notice.Detail);
    if (notice.Kind == "Project stopped") stoppedWithError.TrySetResult(notice.Detail);
};
databaseRunner.Start(enabled, profile);
var stopDetail = await stoppedWithError.Task.WaitAsync(TimeSpan.FromSeconds(10));
await databaseRunner.StopAsync();
var positionAfterFailure = await state.GetReviewPositionAsync(identity.CommonGitDirectory, "refs/heads/main", default);
Check(started.SequenceEqual(new[] { firstPending }), "database error stops immediately without retries or next commit");
Check(positionAfterFailure.PendingStart == firstPending, "database error preserves pending commit for restart");
Check(positionAfterFailure.Cursor == cursorBeforeFailure, "database error does not advance saved cursor");
Check(stopDetail.Contains("report_groups") && stopDetail.Contains(firstPending), "stop notification includes original database error and commit");
for (var i = 0; i < 20 && mail.Counts().Pending > 0; i++) await mail.ProcessOnceAsync(default);
Check(sentBodies.Any(body => body.Contains("Project stopped") && body.Contains("report_groups")), "configured mail receives stop reason on database failure");
await using (var failedCoordinator = new ProjectCoordinator(configuration, new ModelClient(), state, brokenWriter, mail, jobs))
{
    var failedJob = await failedCoordinator.StartAsync(enabled.ProjectId, null, Guid.NewGuid().ToString());
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    while (jobs.List().Single(j => j.Id == failedJob.Id).Status == "running") await Task.Delay(20, timeout.Token);
    var savedJob = jobs.List().Single(j => j.Id == failedJob.Id);
    Check(savedJob.Status == "failed" && savedJob.Error!.Contains("report_groups"), "server records automatic failure as failed rather than completed");
}
_ = new ReportStore(databaseDirectory); // Repair this fixture's deliberately removed table.
started.Clear();
databaseRunner.Start(enabled, profile);
using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
    while (await state.GetCursorAsync(identity.CommonGitDirectory, "refs/heads/main", default) != await Git("rev-parse", "HEAD"))
        await Task.Delay(20, timeout.Token);
await databaseRunner.StopAsync();
Check(started.SequenceEqual(new[] { firstPending, await Git("rev-parse", "HEAD") }), "restart after repair retries failed commit before processing next one");

await mail.DisposeAsync();
Check(sent.Any(j => j.Subject.Contains("Project start failed")) && sent.Any(j => j.Subject.Contains("Project stopped")), "shutdown flush delivers start failure and stop alerts");
Check(sent.All(j => !j.Recipients.Contains("fixture@example.invalid")), "operational alerts never target commit authors");
var failureDirectory = Path.Combine(root, "failed-mail");
var failingMail = new MailNotifications(failureDirectory, _ => {}, (_, _, _) => throw new IOException("fixture offline"), startWorker: false, providers: []);
failingMail.SaveSettings(new MailSettings { Enabled = true, Host = "smtp.invalid", From = "sender@example.invalid", Recipients = "operator@example.invalid" });
failingMail.EnqueueOperational("fixture", "Project stopped", "shutdown");
await failingMail.DisposeAsync();
var recoveredMail = new MailNotifications(failureDirectory, _ => {}, (_, job, _) => { sent.Add(job); return Task.CompletedTask; }, startWorker: false, providers: [], now: () => DateTimeOffset.UtcNow.AddMinutes(2));
Check(recoveredMail.Counts().Pending == 1, "failed shutdown delivery survives restart");
await recoveredMail.ProcessOnceAsync(default);
Check(recoveredMail.Counts().Pending == 0, "persisted operational alert can be retried");
await recoveredMail.DisposeAsync();
var fragment = HtmlReport.Render("fixture", "# Review\n<script>alert(1)</script>\n- bug", fragment: true);
Check(!fragment.Contains("<script>") && fragment.Contains("&lt;script&gt;") && !fragment.Contains("<html>"), "modal fragment escapes untrusted report content");
Console.WriteLine($"PASS: {checks} lifecycle checks; no real model or mail service contacted.");

