using System.Diagnostics;
using System.Net;
using System.Text.Json;
using KommitEye.Models;
using KommitEye.Services;

var root = Path.Combine(Path.GetTempPath(), "KommitEye-availability-" + Guid.NewGuid());
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable("KOMMITEYE_DATA_DIR", Path.Combine(root, "data"));
var repo = Path.Combine(root, "repo");
Directory.CreateDirectory(repo);
await Git("init", "-b", "main");
await Git("config", "user.name", "Fixture");
await Git("config", "user.email", "fixture@example.invalid");
await File.WriteAllTextAsync(Path.Combine(repo, "file.txt"), "first\n");
await Git("add", ".");
await Git("commit", "-m", "Availability fixture");
var sha = await Git("rev-parse", "HEAD");
var git = new GitService();
var identity = await git.GetRepositoryIdentityAsync(repo, default);
var state = new StateStore();
var profile = new ModelProfile { Model = "fixture", Endpoint = "https://fixture.invalid/v1",
    Parameters = new ModelParameters { MaxRetries = 0, AvailabilityCheckMinutes = 1 } };
var config = new ConfigurationStore();
var models = new ModelsConfiguration { ActiveProfile = profile.Name = "fixture" };
models.Profiles.Add(profile);
config.SaveModels(models);
Check(config.LoadModels().Profiles.Single().Parameters.AvailabilityCheckMinutes == 1, "interval persists");
Check(JsonSerializer.Deserialize<ModelParameters>("{}")!.AvailabilityCheckMinutes == 5, "legacy default");
if (args.Contains("--deadlines"))
{
    await DeadlineChecks.RunAsync(repo, git, config);
    return;
}
await ApiErrorChecks.RunAsync(repo, git, config);

async Task<string> Git(params string[] args)
{
    var start = new ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var arg in args) start.ArgumentList.Add(arg);
    using var process = Process.Start(start)!;
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0) throw new Exception(await error);
    return (await output).Trim();
}
static void Check(bool value, string name) { if (!value) throw new Exception(name); }
