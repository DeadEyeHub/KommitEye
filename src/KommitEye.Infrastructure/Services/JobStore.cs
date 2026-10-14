using Microsoft.Data.Sqlite;
using System.Text.Json;
using KommitEye.Models;

namespace KommitEye.Services;

public sealed class JobStore
{
    private readonly string _connection;
    public JobStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _connection = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "server.db") }.ToString();
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS jobs(id TEXT PRIMARY KEY, request_key TEXT UNIQUE NOT NULL, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS versions(name TEXT PRIMARY KEY, revision INTEGER NOT NULL);
            INSERT OR IGNORE INTO versions VALUES('projects', 0);
            PRAGMA user_version=1;
            """;
        command.ExecuteNonQuery();
        foreach (var job in List().Where(j => j.Status is "running" or "queued"))
            Save(job with { Status = "interrupted", FinishedAt = DateTimeOffset.UtcNow, Error = "Server restarted. Start again to resume from the saved cursor." });
    }
    private SqliteConnection Open() { var db = new SqliteConnection(_connection); db.Open(); return db; }
    public void Save(ReviewJob job)
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO jobs VALUES($id,$key,$json) ON CONFLICT(id) DO UPDATE SET json=$json";
        command.Parameters.AddWithValue("$id", job.Id); command.Parameters.AddWithValue("$key", job.RequestKey);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(job)); command.ExecuteNonQuery();
    }
    public IReadOnlyList<ReviewJob> List()
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT json FROM jobs ORDER BY rowid DESC";
        using var rows = command.ExecuteReader(); var result = new List<ReviewJob>();
        while (rows.Read()) result.Add(JsonSerializer.Deserialize<ReviewJob>(rows.GetString(0))!);
        return result;
    }
    public long Revision()
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT revision FROM versions WHERE name='projects'";
        return (long)command.ExecuteScalar()!;
    }
    public void AdvanceRevision()
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "UPDATE versions SET revision=revision+1 WHERE name='projects'"; command.ExecuteNonQuery();
    }
}
