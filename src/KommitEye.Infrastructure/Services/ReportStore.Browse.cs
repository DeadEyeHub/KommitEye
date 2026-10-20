using System.Globalization;

namespace KommitEye.Services;

public sealed record ReviewReportEntry(long Id, string ReportName, string Repository, string Branch,
    string Sha, string Subject, string Author, DateTimeOffset? CommitDate, DateTimeOffset SavedAt,
    bool Legacy, string Outcome)
{
    public string ShortSha => Sha[..Math.Min(8, Sha.Length)];
    public string OutcomeLabel => Outcome switch
    {
        "bug" => Localization.Text("Bug", "Баг"),
        "clean" => Localization.Text("No bug", "Не баг"),
        "failed" => Localization.Text("Review failed", "Ошибка проверки"),
        "empty" => Localization.Text("Empty diff", "Нет изменений"),
        _ => Localization.Text("Unknown", "Не определено")
    };
}

public sealed record ReviewReportPage(IReadOnlyList<ReviewReportEntry> Items, long Total, int Offset, int Limit);

public sealed partial class ReportStore
{
    // Legacy text stays unchanged. Only recognizable metadata is extracted for browsing.
    private static string? LegacyField(string markdown, string field)
    {
        foreach (var line in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith("### ", StringComparison.Ordinal) || line.StartsWith(new string((char)96, 3), StringComparison.Ordinal)) break;
            if (field == "subject" && line.StartsWith("## " + (char)96, StringComparison.Ordinal))
            {
                var end = line.IndexOf((char)96 + " - ", StringComparison.Ordinal);
                if (end >= 0) return line[(end + 4)..];
            }
            var prefixes = field switch
            {
                "author" => new[] { "- Author: ", "- Автор: " },
                "branch" => new[] { "- Branch: ", "- Ветка: " },
                "date" => new[] { "- Date: ", "- Дата: " },
                "result" => new[] { "- Result: ", "- Результат: " },
                _ => Array.Empty<string>()
            };
            foreach (var prefix in prefixes)
                if (line.StartsWith(prefix, StringComparison.Ordinal)) return line[prefix.Length..];
        }
        return null;
    }

    public ReviewReportPage Browse(string? query = null, string sort = "saved", bool descending = true,
        int offset = 0, int limit = 50, string? reportName = null, string? outcome = null)
    {
        query = query?.Trim() ?? "";
        if (query.Length > 500) throw new ArgumentException("Search is limited to 500 characters.");
        if (offset < 0 || limit is < 1 or > 200) throw new ArgumentException("Invalid report page.");
        if (outcome is not (null or "" or "bug" or "clean" or "failed" or "unknown" or "empty"))
            throw new ArgumentException("Unknown report outcome.");
        var order = sort switch
        {
            "saved" => "julianday(saved_at)",
            "date" => "julianday(commit_date)",
            "author" => "author COLLATE REPORT_TEXT",
            "subject" => "subject COLLATE REPORT_TEXT",
            "branch" => "branch COLLATE REPORT_TEXT",
            "repository" => "repository COLLATE REPORT_TEXT",
            "sha" => "commit_sha",
            "outcome" => "outcome",
            _ => throw new ArgumentException("Unknown report sort.")
        };
        using var db = Open();
        db.CreateFunction<string, string, string?>("legacy_field", LegacyField, isDeterministic: true);
        db.CreateFunction<string, string>("legacy_outcome", LegacyOutcome, isDeterministic: true);
        db.CreateFunction<string?, string?, long>("report_contains",
            (text, search) => (text ?? "").Contains(search ?? "", StringComparison.OrdinalIgnoreCase) ? 1L : 0L,
            isDeterministic: true);
        db.CreateFunction<string?, string?>("report_date", value =>
            DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date.ToUniversalTime().ToString("O") : null, isDeterministic: true);
        db.CreateCollation("REPORT_TEXT", (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left, right));
        const string source = """
            WITH entries AS (
                SELECT r.id,g.name,COALESCE(g.repository_key,g.name) AS repository,
                    COALESCE(g.branch,legacy_field(r.markdown,'branch'),'') AS branch,
                    r.commit_sha,COALESCE(r.subject,legacy_field(r.markdown,'subject'),'') AS subject,
                    COALESCE(r.author,legacy_field(r.markdown,'author'),'') AS author,
                    report_date(COALESCE(r.commit_date,legacy_field(r.markdown,'date'))) AS commit_date,
                    COALESCE(r.reviewed_at,g.modified) AS saved_at,
                    r.reviewed_at IS NULL AS legacy,r.markdown,r.author_email,
                    CASE
                        WHEN r.reviewed_at IS NULL THEN legacy_outcome(r.markdown)
                        WHEN r.failure IS NOT NULL THEN 'failed'
                        WHEN r.empty_diff=1 THEN 'empty'
                        WHEN EXISTS(SELECT 1 FROM findings f WHERE f.review_id=r.id) THEN 'bug'
                        WHEN r.unstructured_response IS NOT NULL THEN 'unknown'
                        ELSE 'clean'
                    END AS outcome
                FROM reviews r JOIN report_groups g ON g.id=r.group_id WHERE g.archived=0
            ), matches AS (
                SELECT * FROM entries WHERE ($name IS NULL OR name=$name)
                    AND ($outcome IS NULL OR $outcome='' OR outcome=$outcome) AND
                    ($query='' OR report_contains(commit_sha,$query) OR report_contains(subject,$query)
                    OR report_contains(author,$query) OR report_contains(author_email,$query)
                    OR report_contains(repository,$query) OR report_contains(name,$query)
                    OR report_contains(branch,$query) OR report_contains(markdown,$query))
            )
            """;
        using var transaction = db.BeginTransaction(deferred: true);
        using var count = Command(db, transaction, source + "SELECT COUNT(*) FROM matches",
            ("$query", query), ("$name", reportName), ("$outcome", outcome));
        var total = (long)count.ExecuteScalar()!;
        using var command = Command(db, transaction, source + $"""
            SELECT id,name,repository,branch,commit_sha,subject,author,commit_date,saved_at,legacy,outcome FROM matches
            ORDER BY ({order}) IS NULL, {order} {(descending ? "DESC" : "ASC")},id {(descending ? "DESC" : "ASC")}
            LIMIT $limit OFFSET $offset
            """, ("$query", query), ("$name", reportName), ("$outcome", outcome), ("$limit", limit), ("$offset", offset));
        var items = new List<ReviewReportEntry>();
        using (var rows = command.ExecuteReader())
            while (rows.Read()) items.Add(new(rows.GetInt64(0), rows.GetString(1), rows.GetString(2),
                rows.GetString(3), rows.GetString(4), rows.GetString(5), rows.GetString(6),
                rows.IsDBNull(7) ? null : DateTimeOffset.Parse(rows.GetString(7), CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(rows.GetString(8), CultureInfo.InvariantCulture), rows.GetBoolean(9), rows.GetString(10)));
        transaction.Commit();
        return new(items, total, offset, limit);
    }

    private static string LegacyOutcome(string markdown)
    {
        var result = LegacyField(markdown, "result")?.Trim();
        if (result is null) return "unknown";
        if (result.StartsWith("review failed; skipped:", StringComparison.OrdinalIgnoreCase) ||
            result.StartsWith("проверка не выполнена; пропущен:", StringComparison.OrdinalIgnoreCase)) return "failed";
        if (result.Equals("empty diff; no model request", StringComparison.OrdinalIgnoreCase) ||
            result.Equals("пустой diff; запрос к модели не выполнялся", StringComparison.OrdinalIgnoreCase)) return "empty";
        if (result.Equals("no bugs found", StringComparison.OrdinalIgnoreCase) ||
            result.Equals("ошибок не найдено", StringComparison.OrdinalIgnoreCase)) return "clean";
        foreach (var prefix in new[] { "potential bugs:", "потенциальных ошибок:" })
            if (result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(result[prefix.Length..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count))
                return count > 0 ? "bug" : "unknown";
        return "unknown";
    }

    public string? ReadReview(long id)
    {
        using var db = Open();
        using var command = Command(db, null, """
            SELECT r.markdown FROM reviews r JOIN report_groups g ON g.id=r.group_id
            WHERE r.id=$id AND g.archived=0
            """, ("$id", id));
        return command.ExecuteScalar() as string;
    }
}
