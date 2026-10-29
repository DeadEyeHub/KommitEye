using System.Text.Json;
using KommitEye.Models;
using Microsoft.Data.Sqlite;

namespace KommitEye.Services;

public sealed record FailureLogEntry(DateTimeOffset Time, string Source, string Text);

/// <summary>A per-runner ring buffer, persisted only when a review fails.</summary>
public sealed class FailureDiagnostics(string directory)
{
    private readonly object _gate = new();
    private readonly Queue<FailureLogEntry> _lines = new();
    public string DatabasePath => Path.Combine(directory, "diagnostics.db");

    public void Clear() { lock (_gate) _lines.Clear(); }
    public void Append(string source, string text)
    {
        lock (_gate)
            foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                _lines.Enqueue(new(DateTimeOffset.UtcNow, source, line.Length > 8192 ? line[..8192] + " [truncated]" : line));
                while (_lines.Count > 50) _lines.Dequeue();
            }
    }

    public long Save(AppSettings settings, ModelProfile profile, string sha, string error, bool manual)
    {
        var secrets = new[] { profile.ApiKey, string.IsNullOrEmpty(profile.ApiKeyEnvironment) ? null : Environment.GetEnvironmentVariable(profile.ApiKeyEnvironment) };
        string Clean(string value)
        {
            foreach (var secret in secrets)
                if (!string.IsNullOrEmpty(secret)) value = value.Replace(secret, "[redacted]", StringComparison.Ordinal);
            return ReviewApiException.Sanitize(value, limit: 16384);
        }
        FailureLogEntry[] logs;
        lock (_gate) logs = _lines.Select(line => line with { Text = Clean(line.Text) }).ToArray();
        Directory.CreateDirectory(directory);
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, DefaultTimeout = 2, Pooling = false }.ToString());
        db.Open();
        using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS review_failures (
                id INTEGER PRIMARY KEY, saved_at TEXT NOT NULL,
                project_id TEXT, project_name TEXT, repository TEXT, branch TEXT,
                commit_sha TEXT, model TEXT, manual INTEGER NOT NULL,
                error TEXT NOT NULL, logs_json TEXT NOT NULL);
            INSERT INTO review_failures(saved_at,project_id,project_name,repository,branch,commit_sha,model,manual,error,logs_json)
            VALUES($time,$project,$name,$repo,$branch,$sha,$model,$manual,$error,$logs) RETURNING id;
            """;
        foreach (var (key, value) in new (string, object?)[] {
            ("$time", DateTimeOffset.UtcNow.ToString("O")), ("$project", settings.ProjectId),
            ("$name", Clean(settings.DisplayName)), ("$repo", Clean(settings.RepositoryPath)),
            ("$branch", settings.BranchRef), ("$sha", sha), ("$model", profile.Model),
            ("$manual", manual ? 1 : 0), ("$error", Clean(error)), ("$logs", JsonSerializer.Serialize(logs)) })
            command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        var id = (long)command.ExecuteScalar()!;
        transaction.Commit();
        return id;
    }
}
