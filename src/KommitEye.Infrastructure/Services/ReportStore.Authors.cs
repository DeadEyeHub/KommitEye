using System.Text.Json;

namespace KommitEye.Services;

public sealed record AuthorFinding(int Id, string File, int? Line, string Description,
    string Status, string Comment, string? RespondedAt);
public sealed record AuthorReview(long Id, string Project, string Branch, string Sha, string Subject,
    string? SavedAt, string Outcome, int FindingCount, int PendingCount);
public sealed record AuthorReviewDetail(AuthorReview Review, IReadOnlyList<AuthorFinding> Findings, string AuthorEmail);
public sealed record PortalNotice(long Id, string Kind, string Email, string Payload);

public sealed partial class ReportStore
{
    private static void CreateAuthorSchema(Microsoft.Data.Sqlite.SqliteConnection db, Microsoft.Data.Sqlite.SqliteTransaction tx)
    {
        using var command = Command(db, tx, """
            CREATE INDEX IF NOT EXISTS reviews_author_email ON reviews(lower(trim(author_email)));
            CREATE TABLE IF NOT EXISTS author_feedback(
                review_id INTEGER NOT NULL, ordinal INTEGER NOT NULL,
                email TEXT NOT NULL, status TEXT NOT NULL, comment TEXT NOT NULL, responded_at TEXT NOT NULL,
                PRIMARY KEY(review_id,ordinal),
                FOREIGN KEY(review_id,ordinal) REFERENCES findings(review_id,ordinal));
            CREATE TABLE IF NOT EXISTS portal_notices(
                id INTEGER PRIMARY KEY AUTOINCREMENT, kind TEXT NOT NULL, email TEXT NOT NULL,
                payload TEXT NOT NULL, delivered INTEGER NOT NULL DEFAULT 0);
            CREATE UNIQUE INDEX IF NOT EXISTS portal_review_notice ON portal_notices(kind,email,payload) WHERE kind='review';
            """);
        command.ExecuteNonQuery();
    }

    public bool HasAuthor(string email)
    {
        using var db = Open();
        using var command = Command(db, null, """
            SELECT 1 FROM reviews r JOIN report_groups g ON g.id=r.group_id
            WHERE g.archived=0 AND lower(trim(r.author_email))=$email LIMIT 1
            """, ("$email", email));
        return command.ExecuteScalar() is not null;
    }

    private const string AuthorSelect = """
        SELECT r.id,g.repository_key,g.name,g.branch,r.commit_sha,r.subject,r.reviewed_at,
            CASE WHEN r.failure IS NOT NULL THEN 'failed' WHEN r.empty_diff=1 THEN 'empty'
            WHEN EXISTS(SELECT 1 FROM findings f WHERE f.review_id=r.id) THEN 'bug'
            WHEN r.unstructured_response IS NOT NULL OR r.reviewed_at IS NULL THEN 'unknown' ELSE 'clean' END,
            (SELECT count(*) FROM findings f WHERE f.review_id=r.id),
            (SELECT count(*) FROM findings f WHERE f.review_id=r.id AND NOT EXISTS
                (SELECT 1 FROM author_feedback a WHERE a.review_id=f.review_id AND a.ordinal=f.ordinal AND a.status<>'pending'))
        FROM reviews r JOIN report_groups g ON g.id=r.group_id
        WHERE g.archived=0 AND ($email IS NULL OR lower(trim(r.author_email))=$email)
        """;

    private static AuthorReview ReadAuthorRow(Microsoft.Data.Sqlite.SqliteDataReader row)
    {
        var identity = row.IsDBNull(1) ? row.GetString(2) : row.GetString(1);
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(identity));
        if (name.Equals(".git", StringComparison.OrdinalIgnoreCase)) name = Path.GetFileName(Path.GetDirectoryName(identity));
        return new(row.GetInt64(0), name ?? "Repository", row.IsDBNull(3) ? "" : row.GetString(3),
            row.GetString(4), row.IsDBNull(5) ? "" : row.GetString(5), row.IsDBNull(6) ? null : row.GetString(6),
            row.GetString(7), row.GetInt32(8), row.GetInt32(9));
    }

    public IReadOnlyList<AuthorReview> AuthorReviews(string email, int offset = 0, string filter = "")
    {
        if (offset < 0 || filter is not ("" or "bug" or "clean" or "pending")) throw new ArgumentException("Invalid review filter.");
        using var db = Open();
        using var command = Command(db, null, AuthorSelect + """
            AND ($filter='' OR ($filter='bug' AND EXISTS(SELECT 1 FROM findings f WHERE f.review_id=r.id))
            OR ($filter='clean' AND r.failure IS NULL AND r.unstructured_response IS NULL AND r.reviewed_at IS NOT NULL
                AND NOT EXISTS(SELECT 1 FROM findings f WHERE f.review_id=r.id))
            OR ($filter='pending' AND EXISTS(SELECT 1 FROM findings f WHERE f.review_id=r.id AND NOT EXISTS
                (SELECT 1 FROM author_feedback a WHERE a.review_id=f.review_id AND a.ordinal=f.ordinal AND a.status<>'pending'))))
            ORDER BY r.reviewed_at DESC,r.id DESC LIMIT 51 OFFSET $offset
            """, ("$email", email), ("$filter", filter), ("$offset", offset));
        using var rows = command.ExecuteReader();
        var result = new List<AuthorReview>();
        while (rows.Read()) result.Add(ReadAuthorRow(rows));
        return result;
    }

    public AuthorReviewDetail? AuthorDetail(long id, string? email)
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: true);
        using var command = Command(db, tx, AuthorSelect + " AND r.id=$id", ("$email", email), ("$id", id));
        AuthorReview review;
        using (var row = command.ExecuteReader()) { if (!row.Read()) return null; review = ReadAuthorRow(row); }
        using var findings = Command(db, tx, """
            SELECT f.ordinal,f.file,f.line,f.description,COALESCE(a.status,'pending'),COALESCE(a.comment,''),a.responded_at
            FROM findings f LEFT JOIN author_feedback a ON a.review_id=f.review_id AND a.ordinal=f.ordinal
            WHERE f.review_id=$id ORDER BY f.ordinal
            """, ("$id", id));
        var items = new List<AuthorFinding>();
        using (var row = findings.ExecuteReader()) while (row.Read()) items.Add(new(row.GetInt32(0), row.GetString(1),
            row.IsDBNull(2) ? null : row.GetInt32(2), row.GetString(3), row.GetString(4), row.GetString(5), row.IsDBNull(6) ? null : row.GetString(6)));
        using var author = Command(db, tx, "SELECT author_email FROM reviews WHERE id=$id", ("$id", id));
        var authorEmail = author.ExecuteScalar() as string ?? "";
        tx.Commit();
        return new(review, items, authorEmail);
    }

    public void Respond(long reviewId, int findingId, string email, string status, string comment, bool notify, string? savedAt = null)
    {
        if (status is not ("pending" or "confirmed" or "false_positive") || comment.Length > 4000)
            throw new ArgumentException("Choose a valid response and a comment of at most 4000 characters.");
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var owned = Command(db, tx, """
            SELECT f.description,r.commit_sha,r.reviewed_at FROM findings f JOIN reviews r ON r.id=f.review_id
            JOIN report_groups g ON g.id=r.group_id
            WHERE f.review_id=$id AND f.ordinal=$ordinal AND lower(trim(r.author_email))=$email AND g.archived=0
            """, ("$id", reviewId), ("$ordinal", findingId), ("$email", email));
        string description, sha;
        using (var row = owned.ExecuteReader())
        { if (!row.Read()) throw new KeyNotFoundException("Review not found."); description = row.GetString(0); sha = row.GetString(1);
          if (savedAt is not null && row.GetString(2) != savedAt) throw new InvalidOperationException("Review changed. Reload before responding."); }
        using var old = Command(db, tx, "SELECT status FROM author_feedback WHERE review_id=$id AND ordinal=$ordinal", ("$id", reviewId), ("$ordinal", findingId));
        var previous = old.ExecuteScalar() as string;
        using var save = Command(db, tx, """
            INSERT INTO author_feedback VALUES($id,$ordinal,$email,$status,$comment,$now)
            ON CONFLICT(review_id,ordinal) DO UPDATE SET status=excluded.status,comment=excluded.comment,responded_at=excluded.responded_at
            """, ("$id", reviewId), ("$ordinal", findingId), ("$email", email), ("$status", status), ("$comment", comment.Trim()), ("$now", DateTimeOffset.UtcNow.ToString("O")));
        save.ExecuteNonQuery();
        if (notify && status == "false_positive" && previous != status)
        {
            using var notice = Command(db, tx, "INSERT INTO portal_notices(kind,email,payload) VALUES('feedback',$email,$payload)",
                ("$email", email), ("$payload", JsonSerializer.Serialize(new { ReviewId = reviewId, Sha = sha, Description = description, Comment = comment.Trim() })));
            notice.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public IReadOnlyList<PortalNotice> PendingPortalNotices()
    {
        using var db = Open();
        using var command = Command(db, null, "SELECT id,kind,email,payload FROM portal_notices WHERE delivered=0 ORDER BY id LIMIT 100");
        using var rows = command.ExecuteReader();
        var items = new List<PortalNotice>();
        while (rows.Read()) items.Add(new(rows.GetInt64(0), rows.GetString(1), rows.GetString(2), rows.GetString(3)));
        return items;
    }
    public void CompletePortalNotice(long id)
    {
        using var db = Open();
        using var command = Command(db, null, "UPDATE portal_notices SET delivered=1,payload=CASE WHEN kind='review' THEN payload ELSE '' END WHERE id=$id", ("$id", id));
        command.ExecuteNonQuery();
    }
}
