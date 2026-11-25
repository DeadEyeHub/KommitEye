using System.Security.Cryptography;
using System.Text.Json;
using KommitEye.Models;

namespace KommitEye.Services;

// One draft per project, repository, branch and immutable commit. The runner owns
// the lease until the report and (for automatic reviews) cursor are committed.
public sealed class PacketCheckpoint(string project, string repository, string branch, string sha) : IDisposable
{
    internal sealed record PacketResult(string Report, string? FollowUp);
    private sealed record Snapshot(string Fingerprint, List<PacketResult> Packets, int FinalBudget, string? FinalReport, int FinalVersion = 0,
        long? PacketRoundsUsed = null, StreamCursor? Stream = null);
    private readonly string _path = Path.Combine(AppPaths.DataDirectory, "review-progress",
        Hash(new { project, repository, branch, sha }) + ".json");
    private FileStream? _lease;
    private Snapshot? _state;
    internal IReadOnlyList<PacketResult> Packets => _state!.Packets;
    internal int FinalBudget => _state!.FinalBudget;
    // Reuse validated packets when the final audit rules change.
    private const int CurrentFinalVersion = 4;
    internal string? FinalReport => _state!.FinalVersion == CurrentFinalVersion ? _state.FinalReport : null;
    internal StreamCursor? Stream => _state!.Stream;
    public bool HasProgress => CompletedPackets > 0;
    public int CompletedPackets => _state?.Stream?.Accepted ?? _state?.Packets.Count ?? 0;
    public Action<string>? Progress { get; init; }

    private static string Hash<T>(T value) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    internal void Open(CommitReviewPlan plan, ModelProfile profile, string prompt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _lease ??= new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.None, 1, FileOptions.DeleteOnClose);
        string Fingerprint(int protocol, object plan, object settings) => Hash(new { protocol, plan, profile.Name, profile.Model, profile.Endpoint,
            Parameters = settings, prompt, Localization.Language });
        // Optional reference lookups can time out independently on each run at the same SHA.
        var evidence = new { plan.Parent, plan.Map, Packets = plan.Packets.Select(p => new { p.Path, p.Patch }) };
        var limits = profile.Parameters;
        var settings = new { limits.DisableThinking, limits.Temperature, limits.TopP, limits.MaxTokens };
        var streaming = plan.StreamPaths.Length > 0;
        var fingerprint = streaming ? Fingerprint(4, new { evidence, plan.StreamPaths }, settings) : Fingerprint(3, evidence, settings);
        if (File.Exists(_path))
        {
            Snapshot saved;
            using (var file = File.OpenRead(_path))
                saved = JsonSerializer.Deserialize<Snapshot>(file) ?? throw new InvalidDataException("Empty packet checkpoint.");
            var oldReserve = limits.CrossFileMaxRounds;
            bool matches = saved.Fingerprint == fingerprint;
            if (!matches && !streaming && saved.PacketRoundsUsed == null)
            {
                var parameters = JsonSerializer.SerializeToNode(profile.Parameters)!.AsObject();
                foreach (var key in parameters.Select(p => p.Key).Where(k => k.StartsWith("Git") && k.EndsWith("TimeoutSeconds")).ToArray())
                    parameters.Remove(key);
                bool MatchesLegacy() => saved.Fingerprint == Fingerprint(2, evidence, parameters) ||
                    saved.Fingerprint == Fingerprint(1, plan, parameters);
                matches = MatchesLegacy();
                // 4.0.5 stored only a hash and the remaining budget. After exhausting the carry,
                // that budget is the old reserve, so a user can raise it before upgrading.
                if (!matches && saved.FinalBudget is >= 1 and <= 500 && saved.FinalBudget != oldReserve)
                {
                    oldReserve = saved.FinalBudget;
                    parameters[nameof(limits.CrossFileMaxRounds)] = oldReserve;
                    matches = MatchesLegacy();
                }
            }
            if (matches)
            {
                var prefixCount = plan.Packets.Count;
                var complete = streaming ? saved.Stream is { EndFile: true } cursor && cursor.File == plan.StreamPaths.Length - 1 :
                    saved.Packets.Count == prefixCount;
                if ((saved.Stream == null && saved.Packets.Count > prefixCount) ||
                    (saved.Stream != null && (!streaming || saved.Packets.Count < prefixCount || saved.Packets.Count > saved.Stream.Accepted)) ||
                    (complete && saved.FinalBudget <= 0) || (!complete && saved.FinalReport != null))
                    throw new InvalidDataException("Invalid packet checkpoint coverage.");
                long used = saved.PacketRoundsUsed ?? 0;
                if (saved.PacketRoundsUsed == null && saved.Packets.Count > 0)
                {
                    // Older snapshots did not count rounds after the carry ran out.
                    var carry = saved.FinalBudget - oldReserve;
                    used = carry > 0 ? limits.MaxRounds - carry : int.MaxValue;
                }
                if (used < 0) throw new InvalidDataException("Invalid packet checkpoint round count.");
                var budget = limits.CrossFileMaxRounds + (int)Math.Max(0, limits.MaxRounds - used);
                _state = saved with { Fingerprint = fingerprint, PacketRoundsUsed = used, FinalBudget = budget };
                if (saved.PacketRoundsUsed == null) Save();
                return;
            }
            Progress?.Invoke($"Restarting packet review: checkpoint with {saved.Stream?.Accepted ?? saved.Packets.Count} validated packets is incompatible with the current plan, model, or instructions.");
        }
        else Progress?.Invoke("Starting packet review: no checkpoint for this project, repository, branch and commit.");
        _state = new(fingerprint, [], 0, null, PacketRoundsUsed: 0);
        Save();
    }

    internal void Accept(string report, string? followUp, int finalBudget, int rounds, StreamCursor? stream)
    {
        // Clean streamed fragments need only the cursor, even for very long generated lines.
        if (stream == null || report != "NO_BUGS" || followUp != null) _state!.Packets.Add(new(report, followUp));
        _state = _state! with { FinalBudget = finalBudget, PacketRoundsUsed = _state!.PacketRoundsUsed + rounds, Stream = stream };
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
        for (int retry = 0; ; retry++)
        {
            try { File.Move(temporary, _path, overwrite: true); return; }
            catch (Exception e) when (OperatingSystem.IsWindows() && retry < 5 &&
                (e is IOException or UnauthorizedAccessException) && (e.HResult & 0xffff) is 5 or 32 or 33)
            {
                // A reader without FileShare.Delete can briefly prevent the atomic rename.
                Thread.Sleep(20 * (retry + 1));
            }
        }
    }

    public void Complete()
    {
        if (_lease != null) File.Delete(_path);
    }

    public void Dispose() => _lease?.Dispose();
}
