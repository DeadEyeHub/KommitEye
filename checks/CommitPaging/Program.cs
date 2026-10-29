using System.Diagnostics;
using System.Text;
using KommitEye.Services;

var repo = Path.Combine(Path.GetTempPath(), "KommitEye-paging-" + Guid.NewGuid());
Directory.CreateDirectory(repo);
await Git(null, "init", "-b", "main");
var input = new StringBuilder();
for (var i = 1; i <= 205; i++)
{
    var subject = $"Commit {i}";
    var message = subject + "\n\nDescription with a tab\tand another line\n" + new string('x', 400);
    var content = $"Value {i}\n";
    input.Append($"commit refs/heads/main\ncommitter Fixture <fixture@example.invalid> 1000000000 +0000\ndata {message.Length}\n{message}\nM 100644 inline file.txt\ndata {content.Length}\n{content}\n");
}
await Git(input.ToString(), "fast-import", "--quiet");
var git = new GitService();
var head = await git.GetBranchHeadAsync(repo, "refs/heads/main", default);
var first = await git.GetCommitPageAsync(repo, head, 0, 101, default);
Check(first.Count == 101 && first[0].Subject == "Commit 205", "first page and lookahead");
Check(first.All(c => c.Author == "Fixture" && c.Date == DateTimeOffset.FromUnixTimeSeconds(1000000000)), "author and commit timestamp preserved");
Check(first[0].Message == "Commit 205\n\nDescription with a tab\tand another line\n" + new string('x', 400), "full multiline description preserved without truncation");
// Move the branch while paging the pinned history.
await Git(null, "update-ref", "refs/heads/main", first[10].Sha);
var second = await git.GetCommitPageAsync(repo, head, 100, 101, default);
var last = await git.GetCommitPageAsync(repo, head, 200, 101, default);
var all = first.Take(100).Concat(second.Take(100)).Concat(last).ToArray();
Check(second.Count == 101 && last.Count == 5, "page sizes");
Check(all.Select(c => c.Sha).Distinct().Count() == 205, "no duplicates");
Check(all.Select(c => c.Subject).SequenceEqual(Enumerable.Range(1, 205).Reverse().Select(i => $"Commit {i}")), "no missing or reordered commits after branch moves");
Check((await git.GetCommitPageAsync(repo, head, 205, 101, default)).Count == 0, "end of history");
Check((await git.GetRecentCommitsAsync(repo, "refs/heads/main", default)).Count == 100, "existing API remains compatible");
using var cancellation = new CancellationTokenSource();
cancellation.Cancel();
try { await git.GetCommitPageAsync(repo, head, 0, 101, cancellation.Token); throw new Exception("Cancellation ignored"); }
catch (OperationCanceledException) { }
Console.WriteLine("PASS: 205 commits across three pages; no duplicates or gaps after branch moves; end of history and cancellation.");
await SubmoduleFetchChecks.RunAsync();

async Task Git(string? inputText, params string[] args)
{
    var start = new ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var arg in args) start.ArgumentList.Add(arg);
    using var process = Process.Start(start)!;
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    if (inputText is not null) await process.StandardInput.WriteAsync(inputText);
    process.StandardInput.Close();
    await process.WaitForExitAsync();
    await output;
    if (process.ExitCode != 0) throw new Exception(await error);
}
static void Check(bool value, string name) { if (!value) throw new Exception(name); }
