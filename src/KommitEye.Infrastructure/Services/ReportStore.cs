using System.Text;
using System.Text.RegularExpressions;
using KommitEye.Models;
using Microsoft.Data.Sqlite;

namespace KommitEye.Services;

public sealed record ReportSummary(string Name, DateTimeOffset Modified, long Size);

/// <summary>SQLite is authoritative; Markdown files are legacy input or disposable exports.</summary>
public sealed partial class ReportStore
{
    private readonly string _connection;

    public ReportStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _connection = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "reports.db"),
            ForeignKeys = true,
            DefaultTimeout = 15
        }.ToString();
        using var db = Open();
        using var version = Command(db, null, "PRAGMA user_version");
        var current = Convert.ToInt32(version.ExecuteScalar());
        if (current > 1) throw new InvalidOperationException("Unsupported reports database version.");
        using (var wal = Command(db, null, "PRAGMA journal_mode=WAL")) wal.ExecuteNonQuery();
        using (var transaction = db.BeginTransaction())
        {
            using var schema = Command(db, transaction, """
                CREATE TABLE IF NOT EXISTS report_groups(
                    id INTEGER PRIMARY KEY,
                    name TEXT NOT NULL,
                    repository_key TEXT,
                    branch TEXT,
                    header TEXT NOT NULL,
                    modified TEXT NOT NULL,
                    archived INTEGER NOT NULL DEFAULT 0);
                CREATE UNIQUE INDEX IF NOT EXISTS report_names ON report_groups(name) WHERE archived=0;
                CREATE UNIQUE INDEX IF NOT EXISTS report_identity
                    ON report_groups(repository_key,branch) WHERE archived=0 AND repository_key IS NOT NULL;
                CREATE TABLE IF NOT EXISTS reviews(
                    id INTEGER PRIMARY KEY,
                    group_id INTEGER NOT NULL REFERENCES report_groups(id),
                    commit_sha TEXT NOT NULL,
                    author TEXT, author_email TEXT, commit_date TEXT, subject TEXT,
                    reviewed_at TEXT, manual INTEGER, failure TEXT, empty_diff INTEGER,
                    duration_ticks INTEGER, unstructured_response TEXT,
                    markdown TEXT NOT NULL,
                    UNIQUE(group_id,commit_sha));
                CREATE TABLE IF NOT EXISTS findings(
                    review_id INTEGER NOT NULL REFERENCES reviews(id) ON DELETE CASCADE,
                    ordinal INTEGER NOT NULL,
                    file TEXT NOT NULL, line INTEGER, side TEXT NOT NULL, description TEXT NOT NULL,
                    PRIMARY KEY(review_id,ordinal));
                CREATE TABLE IF NOT EXISTS legacy_imports(
                    name TEXT PRIMARY KEY, original_markdown TEXT NOT NULL,
                    imported_at TEXT NOT NULL);
                PRAGMA user_version=1;
                """);
            schema.ExecuteNonQuery();
            transaction.Commit();
        }
        ImportLegacy(db, Path.Combine(directory, "reports"));
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connection);
        try { db.Open(); return db; }
        catch { db.Dispose(); throw; }
    }

    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? transaction,
        string sql, params (string Name, object? Value)[] parameters)
    {
        var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static string RepositoryKey(string identity)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(identity));
        return OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
    }

    private static long? FindGroup(SqliteConnection db, SqliteTransaction? transaction, string name)
    {
        using var command = Command(db, transaction,
            "SELECT id FROM report_groups WHERE name=$name AND archived=0", ("$name", name));
        return command.ExecuteScalar() is long id ? id : null;
    }

    private static void ImportLegacy(SqliteConnection db, string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, "*.md").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            using var transaction = db.BeginTransaction();
            using var imported = Command(db, transaction, "SELECT 1 FROM legacy_imports WHERE name=$name", ("$name", name));
            if (imported.ExecuteScalar() is not null) { transaction.Commit(); continue; }
            // Never re-import a generated export, including one belonging to an archived group.
            using var known = Command(db, transaction, "SELECT 1 FROM report_groups WHERE name=$name", ("$name", name));
            if (known.ExecuteScalar() is not null) { transaction.Commit(); continue; }
            var markdown = File.ReadAllText(path);
            var entries = SplitLegacy(markdown);
            var header = entries.Count == 0 ? markdown : markdown[..entries[0].Offset];
            using var group = Command(db, transaction, """
                INSERT INTO report_groups(name,header,modified) VALUES($name,$header,$modified) RETURNING id
                """, ("$name", name), ("$header", header), ("$modified", File.GetLastWriteTimeUtc(path).ToString("O")));
            var groupId = (long)group.ExecuteScalar()!;
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                var end = i + 1 < entries.Count ? entries[i + 1].Offset : markdown.Length;
                using var review = Command(db, transaction, """
                    INSERT INTO reviews(group_id,commit_sha,markdown) VALUES($group,$sha,$markdown)
                    ON CONFLICT(group_id,commit_sha) DO UPDATE SET markdown=excluded.markdown
                    """, ("$group", groupId), ("$sha", entry.Sha), ("$markdown", markdown[entry.Offset..end]));
                review.ExecuteNonQuery();
            }
            using var source = Command(db, transaction,
                "INSERT INTO legacy_imports VALUES($name,$markdown,$now)",
                ("$name", name), ("$markdown", markdown), ("$now", DateTimeOffset.UtcNow.ToString("O")));
            source.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    private static List<(int Offset, string Sha)> SplitLegacy(string markdown)
    {
        var result = new List<(int, string)>();
        var offset = 0;
        var inFence = false;
        var lines = markdown.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.StartsWith("```", StringComparison.Ordinal)) inFence = !inFence;
            if (!inFence && i + 1 < lines.Length && lines[i + 1].StartsWith("## `", StringComparison.Ordinal))
            {
                var match = Regex.Match(line, @"^<!-- (?:commit:([0-9a-fA-F]+)|manual-review:([0-9a-fA-F]+):.*?) -->$");
                if (match.Success) result.Add((offset, (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value).ToLowerInvariant()));
            }
            offset += lines[i].Length + 1;
        }
        return result;
    }

    public void Save(string name, string legacyName, string repositoryIdentity, string branch,
        CommitInfo commit, ReviewResult result, bool manual, string markdown, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = Open();
        using var transaction = db.BeginTransaction();
        var key = RepositoryKey(repositoryIdentity);
        using var lookup = Command(db, transaction,
            "SELECT id FROM report_groups WHERE repository_key=$key AND branch=$branch AND archived=0",
            ("$key", key), ("$branch", branch));
        var groupId = lookup.ExecuteScalar() as long? ?? FindGroup(db, transaction, name) ?? FindGroup(db, transaction, legacyName);
        var now = DateTimeOffset.UtcNow.ToString("O");
        if (groupId is null)
        {
            using var insert = Command(db, transaction, """
                INSERT INTO report_groups(name,repository_key,branch,header,modified)
                VALUES($name,$key,$branch,$header,$now) RETURNING id
                """, ("$name", name), ("$key", key), ("$branch", branch),
                ("$header", Localization.Text("# Git review report", "# Отчет проверки Git") + Environment.NewLine + Environment.NewLine),
                ("$now", now));
            groupId = (long)insert.ExecuteScalar()!;
        }
        else
        {
            using var bind = Command(db, transaction, """
                UPDATE report_groups SET repository_key=$key,branch=$branch,modified=$now WHERE id=$id
                """, ("$key", key), ("$branch", branch), ("$now", now), ("$id", groupId));
            bind.ExecuteNonQuery();
        }
        using var save = Command(db, transaction, """
            INSERT INTO reviews(group_id,commit_sha,author,author_email,commit_date,subject,reviewed_at,
                manual,failure,empty_diff,duration_ticks,unstructured_response,markdown)
            VALUES($group,$sha,$author,$email,$date,$subject,$now,$manual,$failure,$empty,$duration,$raw,$markdown)
            ON CONFLICT(group_id,commit_sha) DO UPDATE SET
                author=excluded.author,author_email=excluded.author_email,commit_date=excluded.commit_date,
                subject=excluded.subject,reviewed_at=excluded.reviewed_at,manual=excluded.manual,
                failure=excluded.failure,empty_diff=excluded.empty_diff,duration_ticks=excluded.duration_ticks,
                unstructured_response=excluded.unstructured_response,markdown=excluded.markdown
            RETURNING id
            """, ("$group", groupId), ("$sha", commit.Sha.ToLowerInvariant()), ("$author", commit.Author),
            ("$email", commit.AuthorEmail), ("$date", commit.Date.ToString("O")), ("$subject", commit.Subject),
            ("$now", now), ("$manual", manual), ("$failure", result.Failure), ("$empty", result.EmptyDiff),
            ("$duration", result.AnalysisDuration?.Ticks), ("$raw", result.UnstructuredResponse), ("$markdown", markdown));
        var reviewId = (long)save.ExecuteScalar()!;
        using (var clear = Command(db, transaction, "DELETE FROM findings WHERE review_id=$id", ("$id", reviewId)))
            clear.ExecuteNonQuery();
        for (var i = 0; i < result.Findings.Count; i++)
        {
            var finding = result.Findings[i];
            using var insert = Command(db, transaction, """
                INSERT INTO findings VALUES($id,$ordinal,$file,$line,$side,$description)
                """, ("$id", reviewId), ("$ordinal", i), ("$file", finding.File),
                ("$line", finding.Line), ("$side", finding.Side), ("$description", finding.Description));
            insert.ExecuteNonQuery();
        }
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    public IReadOnlyList<ReportSummary> List()
    {
        using var db = Open();
        using var command = Command(db, null, """
            SELECT g.name,g.modified,length(CAST(g.header AS BLOB)) +
                COALESCE((SELECT SUM(length(CAST(r.markdown AS BLOB))) FROM reviews r WHERE r.group_id=g.id),0)
            FROM report_groups g WHERE g.archived=0 ORDER BY g.modified DESC,g.id DESC
            """);
        using var rows = command.ExecuteReader();
        var result = new List<ReportSummary>();
        while (rows.Read()) result.Add(new(rows.GetString(0), DateTimeOffset.Parse(rows.GetString(1)), rows.GetInt64(2)));
        return result;
    }

    public string? Read(string name)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction(deferred: true);
        using var header = Command(db, transaction, "SELECT id,header FROM report_groups WHERE name=$name AND archived=0", ("$name", name));
        long id;
        var text = new StringBuilder();
        using (var row = header.ExecuteReader())
        {
            if (!row.Read()) return null;
            id = row.GetInt64(0);
            text.Append(row.GetString(1));
        }
        using var entries = Command(db, transaction, "SELECT markdown FROM reviews WHERE group_id=$id ORDER BY id", ("$id", id));
        using (var rows = entries.ExecuteReader()) while (rows.Read()) text.Append(rows.GetString(0));
        transaction.Commit();
        return text.ToString();
    }

    public void Clear(string name, string legacyName, string identity, string branch)
    {
        using var db = Open();
        using var command = Command(db, null, """
            UPDATE report_groups SET archived=1 WHERE archived=0 AND
                (name=$name OR name=$legacy OR (repository_key=$key AND branch=$branch))
            """, ("$name", name), ("$legacy", legacyName), ("$key", RepositoryKey(identity)), ("$branch", branch));
        command.ExecuteNonQuery();
    }

    public string? ReadForRepository(string name, string legacyName, string identity, string branch)
    {
        using var db = Open();
        using var command = Command(db, null, """
            SELECT name FROM report_groups WHERE repository_key=$key AND branch=$branch AND archived=0
            """, ("$key", RepositoryKey(identity)), ("$branch", branch));
        var storedName = command.ExecuteScalar() as string;
        return storedName is not null ? Read(storedName) : Read(name) ?? Read(legacyName);
    }
}
