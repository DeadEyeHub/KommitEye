using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KommitEye.Models;
using KommitEye.Services;

var root = Directory.CreateTempSubdirectory("kommiteye-stream-").FullName;
Environment.SetEnvironmentVariable("KOMMITEYE_DATA_DIR", Path.Combine(root, "data"));
await RunCase(false);
await RunCase(true);
// Git marks loose objects read-only on Windows. Only clear attributes in this run's temporary fixture.
foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
    File.SetAttributes(file, FileAttributes.Normal);
Directory.Delete(root, recursive: true);

async Task RunCase(bool combined)
{
    var repo = Directory.CreateDirectory(Path.Combine(root, combined ? "combined" : "large")).FullName;
    Process Git(params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info)!;
    }
    async Task<string> Run(params string[] args)
    {
        using var proc = Git(args);
        var text = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0) throw new Exception("Fixture Git failed: " + string.Join(" ", args));
        return text.TrimEnd();
    }
    await Run("init", "-b", "main");
    await Run("config", "user.email", "test@example.invalid");
    await Run("config", "user.name", "Fixture");
    await Run("config", "core.autocrlf", "false");
    await Run("config", "gc.auto", "0");
    File.WriteAllText(Path.Combine(repo, "large.html"), "previous\n");
    await Run("add", ".");
    await Run("commit", "-qm", "base");
    var parent = await Run("rev-parse", "HEAD");
    File.WriteAllText(Path.Combine(repo, "a.txt"), "broken reference\n");
    var paths = new List<string>();
    int files = combined ? 5 : 1;
    for (int i = 0; i < files; i++)
    {
        var path = combined ? $"asset-{i}.html" : "large.html";
        paths.Add(path);
        using var writer = new StreamWriter(Path.Combine(repo, path), false, new UTF8Encoding(false));
        // One UTF-16 snapshot of the large patch exceeds 256 MiB; each combined asset stays below 4M.
        var block = new string((char)('a' + i), 8190) + char.ConvertFromUtf32(0x1F642);
        for (int n = 0; n < (combined ? 400 : 16400); n++) writer.Write(block);
        writer.Write("\nend\n");
    }
    File.WriteAllText(Path.Combine(repo, "z.txt"), "tail\n");
    await Run("add", ".");
    await Run("commit", "-qm", "assets");
    var sha = await Run("rev-parse", "HEAD");
    var tail = await Run("rev-parse", "HEAD:z.txt");
    var loose = Path.Combine(repo, ".git", "objects", tail[..2], tail[2..]);
    string overflow = paths[^1];
    var expected = new Dictionary<string, byte[]>();
    foreach (var path in new[] { overflow, "z.txt" })
    {
        using var process = Git("diff", "--no-renames", "--no-ext-diff", "--no-textconv", "--no-color", "--unified=5", parent, sha, "--", path);
        expected[path] = await SHA256.HashDataAsync(process.StandardOutput.BaseStream);
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new Exception("Could not build independent diff digest.");
    }

    var seen = new HashSet<string>();
    var hashes = expected.Keys.ToDictionary(key => key, _ => IncrementalHash.CreateHash(HashAlgorithmName.SHA256));
    var offsets = new Dictionary<string, long>();
    int fragments = 0, finals = 0, lastPart = 0;
    bool checkedSide = false;
    const string finding = "BUG\nFILE: a.txt\nLINE: 1\nSIDE: NEW\nDESCRIPTION: Broken fixture reference\nEND";
    var profile = new ModelProfile { Model = "fixture", Endpoint = "https://fixture.invalid/v1",
        Parameters = new ModelParameters { SnapshotMb = 256, MaxRounds = 1, CrossFileMaxRounds = 1 } };
    foreach (var attempt in combined ? new[] { "finish" } : new[] { "interrupt", "git-failure", "final-failure", "finish" })
    {
        using var cancel = new CancellationTokenSource();
        using var checkpoint = new PacketCheckpoint("fixture", repo, "main", sha) {
            Progress = message => { if (attempt == "interrupt" && message.StartsWith("Saved file") && lastPart == 4) cancel.Cancel(); }
        };
        using var tools = await GitToolSession.CreateAsync(repo, sha, default, profile.Parameters);
        var handler = new Handler(evidence =>
        {
            var p = evidence.GetProperty("packet");
            if (p.ValueKind == JsonValueKind.Null)
            {
                finals++;
                if (seen.Count != files + 2 || !offsets.ContainsKey("z.txt") || evidence.GetProperty("follow_up").GetArrayLength() != 1)
                    throw new Exception("Incomplete coverage or missing saved question in final phase.");
                return attempt == "final-failure" ? null : "NO_BUGS";
            }
            if (attempt == "finish" && !combined) throw new Exception("Repeated packet on final restart.");
            var path = p.GetProperty("Path").GetString()!;
            seen.Add(path);
            if (!p.TryGetProperty("Position", out var pos))
            {
                if (!combined && fragments > 0) throw new Exception("Repeated prepared prefix.");
                return "NO_BUGS";
            }
            var patch = p.GetProperty("Patch").GetString()!;
            var offset = pos.GetProperty("Offset").GetInt64();
            if (offset != offsets.GetValueOrDefault(path) || patch.Length is < 1 or > 6000)
                throw new Exception($"Repeated, omitted or oversized fragment of {path} at {offset}.");
            offsets[path] = offset + patch.Length;
            hashes[path].AppendData(Encoding.UTF8.GetBytes(patch));
            lastPart = pos.GetProperty("Part").GetInt32();
            fragments++;
            if (path == overflow && pos.GetProperty("LineOffset").GetInt64() > 0 && pos.GetProperty("Side").GetString() == "NEW"
                && pos.GetProperty("NewLine").GetInt32() == 1) checkedSide = true;
            if (attempt == "git-failure" && path == overflow && patch.EndsWith("+end\n")) File.Move(loose, loose + ".fixture");
            return path == overflow && lastPart == 1 ? "FOLLOW_UP: Verify the caller in a.txt\n" + finding : "NO_BUGS";
        });
        using var http = new HttpClient(new FixtureAuditHandler(handler));
        try
        {
            var report = await new ModelClient(http).ReviewAsync(profile, tools, "main", "Review", cancel.Token, checkpoint: checkpoint);
            if (attempt != "finish" || !tools.ReadyForFinal || report != finding) throw new Exception("Unexpected review result.");
            checkpoint.Complete();
        }
        catch (OperationCanceledException) when (attempt == "interrupt" && cancel.IsCancellationRequested && !tools.ReadyForFinal) { }
        catch (GitException e) when (attempt == "git-failure" && e.Message.Contains("z.txt") && finals == 0 && !tools.ReadyForFinal) { }
        catch (InvalidDataException e) when (attempt == "final-failure" && e.Message.Contains("Cross-file review round budget exhausted") && !tools.ReadyForFinal) { }
        finally { if (File.Exists(loose + ".fixture")) File.Move(loose + ".fixture", loose); }
    }
    if (!checkedSide || finals != (combined ? 1 : 2)) throw new Exception("Missing source coordinates or unexpected final phase.");
    foreach (var (path, hash) in hashes)
    {
        using (hash)
            if (!hash.GetHashAndReset().SequenceEqual(expected[path])) throw new Exception("Streamed text differs from git diff: " + path);
    }
    Console.WriteLine($"PASS {(combined ? "combined 16M preparation limit" : ">256 MiB snapshot, mid-file and final restart, Git failure")}: {fragments} fragments, exact Git output");
}

sealed class Handler(Func<JsonElement, string?> respond) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        using var evidence = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        var answer = respond(evidence.RootElement);
        var json = answer == null ?
            """{"choices":[{"finish_reason":"tool_calls","message":{"tool_calls":[{"id":"meta","type":"function","function":{"name":"git_metadata","arguments":"{}"}}]}}]}""" :
            JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = answer } } } });
        return new(HttpStatusCode.OK) { Content = new StringContent(json) };
    }
}
