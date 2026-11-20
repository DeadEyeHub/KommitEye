using System.Security.Cryptography;
using System.Text.Json;
using KommitEye.Models;

namespace KommitEye.Services;

// One draft per project, repository, branch and immutable commit. The runner owns
// the lease until the report and (for automatic reviews) cursor are committed.
public sealed class PacketCheckpoint(string project, string repository, string branch, string sha) : IDisposable
{
    internal sealed record PacketResult(string Report, string? FollowUp);
    private sealed record Snapshot(string Fingerprint, List<PacketResult> Packets, int FinalBudget, string? FinalReport, int FinalVersion = 0);
    private readonly string _path = Path.Combine(AppPaths.DataDirectory, "review-progress",
        Hash(new { project, repository, branch, sha }) + ".json");
    private FileStream? _lease;
    private Snapshot? _state;
    internal IReadOnlyList<PacketResult> Packets => _state!.Packets;
    internal int FinalBudget => _state!.FinalBudget;
    // Reuse validated packets when the final audit rules change.
    private const int CurrentFinalVersion = 3;
    internal string? FinalReport => _state!.FinalVersion == CurrentFinalVersion ? _state.FinalReport : null;
    public bool HasProgress => _state?.Packets.Count > 0;
    public int CompletedPackets => _state?.Packets.Count ?? 0;
    public Action<string>? Progress { get; init; }

    private static string Hash<T>(T value) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    internal void Open(CommitReviewPlan plan, ModelProfile profile, string prompt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _lease ??= new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.None, 1, FileOptions.DeleteOnClose);
        var fingerprint = Hash(new { protocol = 1, plan, profile.Name, profile.Model, profile.Endpoint,
            profile.Parameters, prompt, Localization.Language });
        if (File.Exists(_path))
        {
            using var file = File.OpenRead(_path);
            var saved = JsonSerializer.Deserialize<Snapshot>(file) ?? throw new InvalidDataException("Empty packet checkpoint.");
            if (saved.Fingerprint == fingerprint)
            {
                if (saved.Packets.Count > plan.Packets.Count ||
                    (saved.Packets.Count == plan.Packets.Count && saved.FinalBudget <= 0) ||
                    (saved.Packets.Count != plan.Packets.Count && saved.FinalReport != null))
                    throw new InvalidDataException("Invalid packet checkpoint coverage.");
                _state = saved;
                return;
            }
        }
        _state = new(fingerprint, [], 0, null);
        Save();
    }

    internal void Accept(string report, string? followUp, int finalBudget)
    {
        _state!.Packets.Add(new(report, followUp));
        _state = _state with { FinalBudget = finalBudget };
        Save();
    }

    internal void SetFinal(string report)
    {
        _state = _state! with { FinalReport = report, FinalVersion = CurrentFinalVersion };
        Save();
    }

    private void Save()
    {
        var temporary = _path + ".tmp";
        using (var file = File.Create(temporary))
        {
            JsonSerializer.Serialize(file, _state);
            file.Flush(flushToDisk: true);
        }
        File.Move(temporary, _path, overwrite: true);
    }

    public void Complete()
    {
        if (_lease != null) File.Delete(_path);
    }

    public void Dispose() => _lease?.Dispose();
}
