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
    var legacyPage = writer.Browse("NEWER FINDING");
    Check(legacyPage.Total == 1 && legacyPage.Items[0].Legacy && legacyPage.Items[0].Subject == "manual",
        "legacy report content is searchable and subject is extracted");
    Check(writer.ReadReview(legacyPage.Items[0].Id)!.Contains("newer finding"), "individual commit can be read");

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
    var ascending = writer.Browse(sort: "sha", descending: false, limit: 200);
    var descending = writer.Browse(sort: "sha", descending: true, limit: 200);
    Check(ascending.Total == 16 && ascending.Items.Select(r => r.Id).Reverse().SequenceEqual(descending.Items.Select(r => r.Id)),
        "sorting reverses all results with deterministic ties");
    var page1 = writer.Browse(sort: "sha", descending: false, limit: 3);
    var page2 = writer.Browse(sort: "sha", descending: false, offset: 3, limit: 3);
    Check(page1.Total == 16 && page1.Items.Concat(page2.Items).Select(r => r.Id)
        .SequenceEqual(ascending.Items.Take(6).Select(r => r.Id)), "pagination follows sorted results without overlap");
    Check(writer.Browse("ошИБКА").Total == 2, "Unicode case-insensitive search includes finding text");
    Check(writer.Browse("авТОР").Total == 15, "Unicode case-insensitive author search");
    Check(writer.Browse(reportName: legacyName).Total == 14, "branch filter scopes commit results");
    Check(writer.Browse("%_").Total == 0 && writer.Browse("' OR 1=1 --").Total == 0,
        "search treats SQL wildcards and SQL syntax as literal text");
    foreach (var sort in new[] { "saved", "date", "author", "subject", "branch", "repository", "sha" })
        Check(writer.Browse(sort: sort).Total == 16, "supported sort: " + sort);
    try { writer.Browse(sort: "id; DROP TABLE reviews"); throw new Exception("Invalid sort accepted"); }
    catch (ArgumentException) { }
    try { writer.Browse(offset: -1); throw new Exception("Invalid page accepted"); }
    catch (ArgumentException) { }
    var archivedReviewId = writer.Browse(reportName: legacyName).Items[0].Id;

    await writer.ClearAsync(repository, "main", identity);
    Check(writer.Browse().Total == 2 && writer.ReadReview(archivedReviewId) is null,
        "archived groups are excluded from search and individual reads");
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
    OutcomeChecks.Run(Path.Combine(directory, "outcome-checks"));
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(directory, true);
}
