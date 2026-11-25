using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace KommitEye.Services;

internal sealed record PatchPosition(int File, int Files, int Part, long Offset, long LineOffset, string Side, long? OldLine, long? NewLine);
internal sealed record StreamCursor(int File, int Part, long EndOffset, bool EndFile, string Digest, int Accepted);
internal sealed record StreamPacket(ReviewPacket Packet, StreamCursor Cursor);

// A bounded prefix for the existing plan builder. An overflow hands both prefix and reader
// to the streaming consumer, so a file is read by one Git process per pass.
internal sealed class PatchProbe(IAsyncEnumerator<(string Patch, bool Last)> source) : IAsyncDisposable
{
    private readonly Queue<(string Patch, bool Last)> prefix = new();
    private bool disposed;

    internal async Task<bool> FitsAsync(int limit)
    {
        int size = 0;
        while (await source.MoveNextAsync())
        {
            prefix.Enqueue(source.Current);
            size += source.Current.Patch.Length;
            if (size > limit) return false;
        }
        return true;
    }

    internal string PreparedPatch() => string.Concat(prefix.Select(p => p.Patch));

    internal async IAsyncEnumerable<(string Patch, bool Last)> ContinueAsync()
    {
        while (prefix.TryDequeue(out var piece)) yield return piece;
        while (await source.MoveNextAsync()) yield return source.Current;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        prefix.Clear();
        await source.DisposeAsync();
    }
}

public sealed partial class GitToolSession
{
    // Track source coordinates across chunk boundaries, including boundaries inside a single minified line.
    private sealed class DiffPosition
    {
        private readonly StringBuilder header = new();
        private char marker;
        private bool inHunk;
        private long oldLine, newLine, column;
        internal long Offset { get; private set; }

        internal PatchPosition Start(int file, int files, int part, char first)
        {
            var side = (column == 0 ? first : marker) switch
            {
                '+' when inHunk => "NEW",
                '-' when inHunk => "OLD",
                ' ' when inHunk => "BOTH",
                _ => "HUNK"
            };
            return new(file, files, part, Offset, column, side,
                side is "OLD" or "BOTH" ? oldLine : null, side is "NEW" or "BOTH" ? newLine : null);
        }

        internal void Advance(string piece)
        {
            foreach (char ch in piece)
            {
                if (column == 0) { marker = ch; header.Clear(); }
                if (marker == '@' && header.Length < 128) header.Append(ch);
                column++;
                if (ch != '\n') continue;
                if (marker == '@' && HunkHeader.Match(header.ToString()) is { Success: true } match)
                {
                    oldLine = long.Parse(match.Groups[1].Value);
                    newLine = long.Parse(match.Groups[2].Value);
                    inHunk = true;
                }
                else if (inHunk)
                {
                    if (marker is '-' or ' ') oldLine++;
                    if (marker is '+' or ' ') newLine++;
                }
                column = 0;
            }
            Offset += piece.Length;
        }
    }

    internal async IAsyncEnumerable<StreamPacket> StreamPacketsAsync(CommitReviewPlan plan, StreamCursor? resume,
        Action<string>? log, [EnumeratorCancellation] CancellationToken token)
    {
        int accepted = resume?.Accepted ?? plan.Packets.Count;
        if (resume != null && (resume.File < 0 || resume.File >= plan.StreamPaths.Length || resume.Part < 1 ||
            resume.EndOffset < 1 || resume.Accepted <= plan.Packets.Count))
            throw new InvalidDataException("Invalid streamed packet checkpoint.");
        if (resume != null) ValidatedPackets = PlannedPackets = accepted;
        int start = resume?.File ?? 0;
        if (resume?.EndFile == true) start++;
        if (start > 0) await plan.FirstStream!.DisposeAsync();
        for (int i = start; i < plan.StreamPaths.Length; i++)
        {
            var path = plan.StreamPaths[i];
            log?.Invoke($"Streaming Git patch for {path}.");
            var position = new DiffPosition();
            int part = 0;
            string digest = "";
            bool replay = resume?.File == i && !resume.EndFile;
            var pieces = i == 0 ? plan.FirstStream!.ContinueAsync() : ReadPatchAsync(path, token);
            await foreach (var (patch, last) in pieces)
            {
                token.ThrowIfCancellationRequested();
                var location = position.Start(plan.FileCount - plan.StreamPaths.Length + i + 1, plan.FileCount, ++part, patch[0]);
                position.Advance(patch);
                digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(digest + patch)));
                if (replay)
                {
                    if (part == resume!.Part)
                    {
                        if (position.Offset != resume.EndOffset || digest != resume.Digest || last)
                            throw new InvalidDataException("Git patch differs from the saved stream position for " + path);
                        replay = false;
                    }
                    continue;
                }
                PlannedPackets = ++accepted;
                yield return new(new(path, patch, "") { Position = location }, new(i, part, position.Offset, last, digest, accepted));
            }
            if (i == 0) await plan.FirstStream!.DisposeAsync();
            if (part == 0 || replay) throw new GitException("Incomplete streamed Git patch for " + path);
        }
        _streamPending = false;
    }

    private async IAsyncEnumerable<(string Patch, bool Last)> ReadPatchAsync(string path, [EnumeratorCancellation] CancellationToken token)
    {
        var info = GitService.CreateStartInfo(_repository, null, [.. DiffArguments(false), path], localRead: true);
        info.StandardOutputEncoding = Encoding.UTF8;
        using var process = Process.Start(info) ?? throw new GitException("Cannot start Git diff for " + path);
        var errors = DrainErrorAsync(process.StandardError);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        var limit = TimeSpan.FromSeconds(_parameters.GitDiffTimeoutSeconds);
        var remaining = limit;
        // One extra character distinguishes EOF at an exact boundary. Suspend the Git timer while the model reviews.
        var buffer = new char[PacketSize + 1];
        int held = 0;
        try
        {
            while (true)
            {
                int filled;
                bool eof;
                if (remaining <= TimeSpan.Zero) throw new GitTimeoutException($"Git diff for {path} exceeded {limit.TotalSeconds}s of reading.");
                var started = Stopwatch.GetTimestamp();
                timeout.CancelAfter(remaining);
                try
                {
                    filled = held + await process.StandardOutput.ReadBlockAsync(buffer.AsMemory(held), timeout.Token);
                    eof = filled < buffer.Length;
                    if (eof)
                    {
                        await process.WaitForExitAsync(timeout.Token);
                        var error = await errors.WaitAsync(timeout.Token);
                        if (process.ExitCode != 0)
                            throw new GitException($"Git diff failed for {path} ({process.ExitCode}): {GitService.SanitizeDiagnostic(error)}");
                    }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw new GitTimeoutException($"Streaming Git diff for {path} exceeded the {limit.TotalSeconds}s read timeout.");
                }
                finally
                {
                    timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                    remaining -= Stopwatch.GetElapsedTime(started);
                }
                if (filled == 0) yield break;
                int count = filled;
                if (!eof)
                {
                    var newline = Array.LastIndexOf(buffer, '\n', PacketSize - 1, PacketSize);
                    count = newline >= 0 ? newline + 1 : PacketSize;
                    if (char.IsHighSurrogate(buffer[count - 1]) || (buffer[count - 1] == '\r' && buffer[count] == '\n')) count--;
                }
                var patch = new string(buffer, 0, count);
                held = filled - count;
                Array.Copy(buffer, count, buffer, 0, held);
                yield return (patch, eof);
                if (eof) yield break;
            }
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await errors;
        }
    }

    private static async Task<string> DrainErrorAsync(StreamReader reader)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory())) > 0)
            result.Append(buffer, 0, Math.Min(count, 16000 - result.Length));
        return result.ToString();
    }
}
