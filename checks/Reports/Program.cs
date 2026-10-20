using System.Text;
using KommitEye.Models;
using KommitEye.Services;
using Microsoft.Data.Sqlite;

var directory = Path.Combine(Path.GetTempPath(), "kommiteye-reports-" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("KOMMITEYE_DATA_DIR", directory);
Directory.CreateDirectory(Path.Combine(directory, "reports"));
var repository = Path.Combine(directory, "repo");
var identity = Path.Combine(repository, ".git");
var commit = new CommitInfo(new string('a', 40), "Автор", DateTimeOffset.Now, "Первый коммит", "author@example.test");
var next = commit with { Sha = new string('b', 40), Subject = "Next" };
void Check(bool value, string message)
{
    if (!value) throw new Exception(message);
    Console.WriteLine("PASS: " + message);
}
object? Scalar(string sql)
{
    using var db = new SqliteConnection($"Data Source={Path.Combine(directory, "reports.db")}");
    db.Open();
    using var command = db.CreateCommand();
    command.CommandText = sql;
    return command.ExecuteScalar();
}
try
{
    var writer = new ReportWriter();
    var legacyName = Path.GetFileName(writer.GetReportPath(repository, "main"));
    var old = $"# Legacy\r\n\r\n<!-- commit:{commit.Sha} -->\r\n## `aaaaaaaa` - old\r\nold finding\r\n\r\n";
    var manual = $"<!-- manual-review:{commit.Sha}:123 -->\r\n## `aaaaaaaa` - manual\r\nnewer finding\r\n\r\n";
    var fenced = $"<!-- commit:{next.Sha} -->\r\n## `bbbbbbbb` - next\r\n```text\r\n<!-- commit:cccc -->\r\n## `cccc` - fake\r\n```\r\n";
    File.WriteAllText(Path.Combine(directory, "reports", legacyName), old + manual + fenced);
    File.WriteAllText(Path.Combine(directory, "reports", "unknown.md"), "# Неизвестный отчёт\nТекст без маркеров.");
    writer = new ReportWriter();
    Check(writer.List().Count == 2, "legacy files imported, including unknown format");
    Check((long)Scalar("SELECT count(*) FROM reviews")! == 2, "manual duplicates coalesced; fenced markers ignored");
    Check((string)Scalar($"SELECT original_markdown FROM legacy_imports WHERE name='{legacyName}'")! == old + manual + fenced,
        "original legacy text preserved without loss");
    var imported = writer.Read(legacyName)!;
    Check(imported.Contains("newer finding") && !imported.Contains("old finding"), "latest legacy duplicate wins");
    Check(new ReportWriter().Read(legacyName) == imported, "restart does not duplicate import");
    Check(writer.Read("unknown.md") == "# Неизвестный отчёт\nТекст без маркеров.", "unknown content round trips");

    var result = new ReviewResult { AnalysisDuration = TimeSpan.FromSeconds(3) };
    result.Findings.Add(new("file.cs", 7, "NEW", "Ошибка"));
    await writer.AppendAsync(repository, "main", commit, result, false, default, identity);
    var report = writer.Read(legacyName)!;
    Check(writer.List().Count == 2 && !report.Contains("newer finding") && report.Contains("Ошибка"),
        "worktree identity binds legacy group; repeat replaces imported commit");
    Check(report.IndexOf("aaaaaaaa", StringComparison.Ordinal) < report.IndexOf("bbbbbbbb", StringComparison.Ordinal),
        "replacement keeps original commit position");
    Check((long)Scalar("SELECT count(*) FROM findings")! == 1, "structured findings stored");
    Check(result.ReportMarkdown.Contains("Ошибка") && result.RepositoryIdentity == identity && result.AuthorEmail == commit.AuthorEmail,
        "mail notification payload preserved");
    Check(writer.List().Single(r => r.Name == legacyName).Size == Encoding.UTF8.GetByteCount(report), "API size matches UTF-8 export");

    await writer.AppendAsync(repository, "main", commit, new ReviewResult { EmptyDiff = true }, true, default, identity);
    Check((long)Scalar("SELECT count(*) FROM findings")! == 0 && !writer.Read(legacyName)!.Contains("Ошибка"),
        "repeat removes stale findings");
    await writer.AppendAsync(repository, "develop", commit, result, false, default, identity);
    await writer.AppendAsync(Path.Combine(directory, "other"), "main", commit, result, false, default);
    Check((long)Scalar("SELECT count(*) FROM reviews")! == 4, "same SHA is distinct in other branches and repositories");

    // Abort after the review upsert and deletion of old findings: the entire transaction must roll back.
    Scalar("CREATE TRIGGER reject_finding BEFORE INSERT ON findings BEGIN SELECT RAISE(ABORT,'fixture'); END;");
    var before = writer.Read(legacyName);
    try
    {
        await writer.AppendAsync(repository, "main", commit, result, false, default, identity);
        throw new Exception("Expected database failure");
    }
    catch (SqliteException) { }
    Check(writer.Read(legacyName) == before, "database failure rolls back review and findings together");
    Scalar("DROP TRIGGER reject_finding;");
    using (var canceled = new CancellationTokenSource())
    {
        canceled.Cancel();
        try { await writer.AppendAsync(repository, "main", next, result, false, canceled.Token, identity); }
        catch (OperationCanceledException) { }
    }
    Check(writer.Read(legacyName) == before, "cancellation leaves stored report intact");
    await Task.WhenAll(Enumerable.Range(1, 12).Select(i => new ReportWriter().AppendAsync(repository, "main",
        commit with { Sha = i.ToString("x40") }, new ReviewResult(), false, default, identity)));
    Check((long)Scalar("SELECT count(*) FROM reviews")! == 16, "concurrent writers preserve all commits");

    await writer.ClearAsync(repository, "main", identity);
    Check(writer.Read(legacyName) is null && new ReportWriter().Read(legacyName) is null,
        "clear archives group and restart cannot resurrect legacy file");
    Check((long)Scalar("SELECT count(*) FROM report_groups WHERE archived=1")! == 1, "cleared data retained for recovery");
    await writer.AppendAsync(repository, "main", next, new ReviewResult { Failure = "fixture failure" }, false, default, identity);
    var newName = Path.GetFileName(writer.GetReportPath(repository, "main", identity));
    Check(writer.Read(newName)!.Contains("fixture failure") && !writer.Read(newName)!.Contains("aaaaaaaa"),
        "review after clear starts a fresh report");
    Check(File.ReadAllText(Path.Combine(directory, "reports", legacyName)) == old + manual + fenced,
        "original Markdown file untouched");
    Check(new ReportWriter().List().Count == 4, "restart retains SQLite-only reports");
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(directory, true);
}
