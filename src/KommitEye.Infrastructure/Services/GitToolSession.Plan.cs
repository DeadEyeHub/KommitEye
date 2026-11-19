using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KommitEye.Services;

internal sealed record ReviewPacket(string Path, string Patch, string References);
internal sealed record CommitReviewPlan(string? Parent, string Map, List<ReviewPacket> Packets);

public sealed partial class GitToolSession
{
    private const int PacketSize = 6000;
    internal async Task<string> SearchPacketReferencesAsync(string path, CancellationToken token)
    {
        var symbol = Path.GetFileNameWithoutExtension(path);
        if (symbol.Length is 0 or > 128 || symbol.Any(char.IsControl)) return "No searchable filename symbol.";
        var result = await GitService.ReadPageAsync(_repository, 0, 2000, token, "grep", "-n", "-I", "-w", "-F",
            "-e", symbol, Sha, "--");
        if (result.ExitCode is not (0 or 1)) throw new GitException("Cannot verify packet references: " + result.Error);
        return JsonSerializer.Serialize(new { commit = Sha, query = symbol, matches = result.Output, truncated = result.HasMore });
    }

    internal sealed record ByteFacts(string Commit, string Path, string Bom, string? Endings);

    internal async Task<ByteFacts?> ReadByteFactsAsync(string commit, string path, CancellationToken token)
    {
        ValidateModulePath(path);
        if (!_commits.Contains(commit)) throw new ArgumentException("Unexpected evidence revision.");
        var result = await GitService.ReadBytesAsync(_repository, 0, 1_048_576, token, null, "cat-file", "blob", commit + ":" + path);
        if (result.ExitCode != 0) return null;
        var bytes = Convert.FromHexString(result.Output);
        var bom = DetectBom(result.Output);
        // Do not infer file-wide line endings from a prefix or misinterpret multibyte text.
        string? endings = null;
        if (!result.HasMore)
        {
            var encoding = bom switch
            {
                "UTF-32BE" => (Encoding)new UTF32Encoding(true, true, true),
                "UTF-32LE" => new UTF32Encoding(false, true, true),
                "UTF-16BE" => new UnicodeEncoding(true, true, true),
                "UTF-16LE" => new UnicodeEncoding(false, true, true),
                _ => new UTF8Encoding(false, true)
            };
            try
            {
                var text = encoding.GetString(bytes);
                if (!text.Contains('\0'))
                {
                    var crlf = text.Contains("\r\n", StringComparison.Ordinal);
                    var rest = text.Replace("\r\n", "");
                    var cr = rest.Contains('\r');
                    var lf = rest.Contains('\n');
                    endings = (crlf, cr, lf) switch
                    {
                        (true, false, false) => "CRLF", (false, true, false) => "CR",
                        (false, false, true) => "LF", (false, false, false) => "none", _ => "mixed"
                    };
                }
            }
            catch (DecoderFallbackException) { /* File encoding unknown. Only the signature is usable. */ }
        }
        return new(commit, path, bom, endings);
    }
    internal int ValidatedPackets { get; private set; }
    internal int PlannedPackets { get; private set; }

    internal void AcceptPacket(int index)
    {
        if (index != ValidatedPackets || index >= PlannedPackets)
            throw new InvalidOperationException("Unexpected review packet.");
        ValidatedPackets++;
    }

    internal void FinishPlan()
    {
        if (PlannedPackets == 0 || ValidatedPackets != PlannedPackets)
            throw new InvalidOperationException("Incomplete packet coverage.");
        DiffComplete = true;
    }
    private static readonly Regex LiteralRow = new("""^[+-][ \t]*"([a-zA-Z0-9|=_ .:/-]{1,512})",[ \t]*$""");
    private static readonly Regex VariableField = new(@"[a-f0-9]{16,}|[0-9]+");
    private static readonly Regex HunkHeader = new(@"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@");
    internal async Task<Dictionary<int, string>> GetLineCandidatesAsync(string path, string side, string hint,
        string description, CancellationToken token)
    {
        ValidateModulePath(path);
        var names = DiffArguments(true).ToList();
        names.Insert(names.IndexOf("--name-status"), "-z");
        var listing = await GitService.ReadPageAsync(_repository, 0, 4096, token, [.. names, path]);
        var fields = listing.Output.Split('\0');
        if (listing.ExitCode != 0 || listing.HasMore || fields.Length != 3 || fields[1] != path) return [];
        var patch = await GitService.ReadPageAsync(_repository, 0, 4_000_000, token, [.. DiffArguments(false), path]);
        if (patch.ExitCode != 0) return [];
        var hints = Regex.Matches(hint, @"\d+").Select(m => int.TryParse(m.Value, out var n) ? n : 0).Take(8).ToArray();
        var words = Regex.Matches(description, @"[\p{L}_][\w]{2,}").Select(m => m.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToArray();
        var candidates = new List<(int Line, int Score, string Text)>();
        int old = 0, current = 0;
        var inHunk = false;
        var rows = patch.Output.Split('\n');
        foreach (var row in rows.Take(rows.Length - (patch.HasMore ? 1 : 0)))
        {
            if (HunkHeader.Match(row) is { Success: true } header)
            {
                old = int.Parse(header.Groups[1].Value);
                current = int.Parse(header.Groups[2].Value);
                inHunk = true;
                continue;
            }
            if (!inHunk || row.Length == 0 || row[0] is not ('+' or '-' or ' ')) continue;
            var useOld = side == "OLD" || (side == "HUNK" && fields[0] == "D");
            if (row[0] != (useOld ? '+' : '-'))
            {
                var number = useOld ? old : current;
                var excerpt = row[..Math.Min(row.Length, 240)];
                var score = words.Count(w => excerpt.Contains(w, StringComparison.OrdinalIgnoreCase)) +
                    (hints.Any(n => Math.Abs((long)n - number) <= 2) ? 20 : 0);
                candidates.Add((number, score, excerpt));
            }
            if (row[0] != '+') old++;
            if (row[0] != '-') current++;
        }
        return candidates.OrderByDescending(c => c.Score).Take(60).OrderBy(c => c.Line).ToDictionary(c => c.Line, c => c.Text);
    }
    private static readonly Regex Declaration = new(@"^[+-]\s*.*?\b(?:class|interface|struct|enum|record)\s+([A-Za-z_]\w*)\b", RegexOptions.Multiline);
    private static readonly Regex Method = new(@"^[+-]\s*(?:(?:public|private|protected|internal|static|async|override|virtual)\s+)+[\w<>?.\[\],]+\s+(\w+)\s*\(", RegexOptions.Multiline);
    private static readonly Regex ArrayPrefix = new(
        @"\A(?:\s|//[^\n]*\n)*(?:namespace\s+[\w.]+\s*;\s*)?(?:(?:public|internal|static|sealed|partial)\s+)*class\s+\w+\s*\{\s*(?:(?:public|private|internal|static|readonly)\s+)*string\s*\[\s*\]\s+\w+\s*=\s*\[\s*\z",
        RegexOptions.NonBacktracking);

    internal async Task<CommitReviewPlan?> CreateReviewPlanAsync(CancellationToken token)
    {
        var probe = await GitService.ReadPageAsync(_repository, 0, PageSize, token, DiffArguments(false));
        if (probe.ExitCode != 0) throw new GitException("Cannot measure reviewed patch. " + probe.Error);
        if (!probe.HasMore) return null;
        await LoadSubmodulesAsync(token, null);
        if (_links.Count > 0) return null; // The paged protocol follows every pinned gitlink.

        var names = DiffArguments(true).ToList();
        names.Insert(names.IndexOf("--name-status"), "-z");
        var listing = await GitService.ReadPageAsync(_repository, 0, 4000, token, names.ToArray());
        if (listing.ExitCode != 0) throw new GitException("Cannot enumerate review paths. " + listing.Error);
        if (listing.HasMore) return null;
        var fields = listing.Output.Split('\0');
        if (fields.Length % 2 != 1 || fields[^1] != "") throw new GitException("Incomplete Git path list.");
        var packets = new List<ReviewPacket>();
        var map = new StringBuilder();
        long remaining = Math.Min(_diskLimit / 2, 16_000_000);
        for (var i = 0; i < fields.Length - 1; i += 2)
        {
            var path = fields[i + 1];
            var diff = await GitService.ReadPageAsync(_repository, 0, (int)Math.Min(remaining, 4_000_000), token,
                [.. DiffArguments(false), path]);
            if (diff.ExitCode != 0) throw new GitException("Cannot read patch for " + path);
            if (diff.HasMore || diff.Output.Length == 0) return null;
            remaining -= diff.Output.Length;
            var symbols = Declaration.Matches(diff.Output).Concat(Method.Matches(diff.Output))
                .Select(m => m.Groups[1].Value).Prepend(Path.GetFileNameWithoutExtension(path))
                .Where(s => s.Length is > 1 and <= 128).Distinct(StringComparer.Ordinal).Take(12).ToArray();
            map.AppendLine($"{fields[i]} {path} {string.Join(' ', symbols)}");
            if (map.Length > PacketSize) return null;
            var references = "";
            if (symbols.Length > 0)
            {
                var search = await GitService.ReadPageAsync(_repository, 0, 1500, token,
                    ["grep", "-n", "-I", "-F", "-w", .. symbols.SelectMany(s => new[] { "-e", s }), Sha, "--"]);
                if (search.ExitCode is not (0 or 1)) throw new GitException("Cannot search for consumers of " + path);
                references = search.Output.Replace(Sha + ":", "", StringComparison.Ordinal);
                if (search.HasMore) references += "\n[partial matches; narrow git_search if needed]";
            }
            foreach (var piece in SplitPatch(path, fields[i], diff.Output))
                packets.Add(new(path, piece, references));
        }
        PlannedPackets = packets.Count;
        ValidatedPackets = 0;
        return new(_parent, map.ToString(), packets);
    }

    private static IEnumerable<string> SplitPatch(string path, string status, string patch)
    {
        var buffer = new StringBuilder();
        var hunk = "";
        long oldLine = 0, newLine = 0;
        var lines = patch.Replace("\r\n", "\n").Split('\n');
        // Only fold rows in a positively identified C# string-array initializer.
        // A raw string, comment or arbitrary list with similar looking lines stays literal.
        var start = Array.FindIndex(lines, line => line.StartsWith("@@ ", StringComparison.Ordinal)) + 1;
        var firstRow = start;
        var prefix = new StringBuilder();
        while (firstRow < lines.Length && prefix.Length < 2048 && lines[firstRow].Length > 0 && !LiteralRow.IsMatch(lines[firstRow]))
            prefix.AppendLine(lines[firstRow++][1..]);
        var compact = path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && status is "A" or "D" &&
            ArrayPrefix.IsMatch(prefix.ToString());
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var sourceLines = 1;
            if (compact && i >= firstRow && !LiteralRow.IsMatch(line)) compact = false;
            if (compact && i >= firstRow && LiteralRow.Match(line) is { Success: true } match)
            {
                var shape = VariableField.Replace(match.Groups[1].Value, m => $"<{m.Length}>");
                var end = i + 1;
                while (end < lines.Length && LiteralRow.Match(lines[end]) is { Success: true } next &&
                       VariableField.Replace(next.Groups[1].Value, m => $"<{m.Length}>") == shape)
                    end++;
                if (end - i >= 16)
                {
                    sourceLines = end - i;
                    var duplicates = lines[i..end].Select(s => s.Trim()).GroupBy(s => s).Count(g => g.Count() > 1);
                    line += $"\n[structural check: {end - i} consecutive literal rows with matching field layout; " +
                        $"{duplicates} repeated row values; first and last shown; individual values not validated]\n" + lines[end - 1];
                    i = end - 1;
                }
            }
            if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                if (hunk.Length > 0 && buffer.Length > 0) { yield return buffer.ToString(); buffer.Clear(); }
                hunk = line[..Math.Min(line.Length, 256)];
                var header = HunkHeader.Match(line);
                if (header.Success)
                {
                    oldLine = long.Parse(header.Groups[1].Value);
                    newLine = long.Parse(header.Groups[2].Value);
                }
            }
            if (buffer.Length + line.Length >= PacketSize && buffer.Length > 0)
            {
                yield return buffer.ToString();
                buffer.Clear();
                if (hunk.Length > 0 && !line.StartsWith("@@ ")) buffer.AppendLine($"[continued at OLD {oldLine}, NEW {newLine}] {hunk}");
            }
            for (var offset = 0; offset < line.Length;)
            {
                if (offset > 0) { yield return buffer.ToString(); buffer.Clear(); buffer.AppendLine($"[continued line OLD {oldLine}, NEW {newLine}, character {offset}]"); }
                var count = Math.Min(PacketSize, line.Length - offset);
                if (offset + count < line.Length && char.IsHighSurrogate(line[offset + count - 1])) count--;
                buffer.Append(line, offset, count);
                offset += count;
            }
            buffer.AppendLine();
            if (hunk.Length > 0 && line.Length > 0)
            {
                if (line[0] is ' ' or '-') oldLine += sourceLines;
                if (line[0] is ' ' or '+') newLine += sourceLines;
            }
        }
        if (buffer.Length > 0) yield return buffer.ToString();
    }
}
