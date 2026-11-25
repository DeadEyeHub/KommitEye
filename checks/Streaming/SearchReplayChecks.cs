using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using KommitEye.Models;
using KommitEye.Services;

internal sealed class SearchReplayChecks(string sha) : HttpMessageHandler
{
    private const string PathInRepo = "web/index.html";
    private const string Query = "observeGroupedSidebarListRect(e";
    private int verifiedPackets;

    internal static async Task RunAsync(string root)
    {
        var repo = Directory.CreateDirectory(Path.Combine(root, "search")).FullName;
        async Task<string> Git(params string[] args)
        {
            var info = new ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var arg in args) info.ArgumentList.Add(arg);
            using var proc = Process.Start(info)!;
            var text = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            if (proc.ExitCode != 0) throw new Exception("Fixture Git failed: " + string.Join(" ", args));
            return text.TrimEnd();
        }
        await Git("init", "-b", "main");
        await Git("config", "user.email", "test@example.invalid");
        await Git("config", "user.name", "Fixture");
        await Git("config", "core.autocrlf", "false");
        Directory.CreateDirectory(Path.Combine(repo, "web"));
        using (var writer = new StreamWriter(Path.Combine(repo, PathInRepo), false, new UTF8Encoding(false)) { NewLine = "\n" })
        {
            for (int i = 1; i < 189; i++) writer.WriteLine("padding");
            writer.WriteLine(new string('Ж', 1_100_003) + "before🙂" + Query + "🙂after" + new string('b', 40_000));
            for (int i = 190; i <= 329; i++) writer.WriteLine(Query + new string('x', 55));
        }
        await Git("add", ".");
        await Git("commit", "-qm", "base");
        var parent = await Git("rev-parse", "HEAD");
        File.WriteAllText(Path.Combine(repo, "review.txt"), new string('a', 19_000));
        await Git("add", ".");
        await Git("commit", "-qm", "review");
        var sha = await Git("rev-parse", "HEAD");
        var profile = new ModelProfile { Model = "fixture", Endpoint = "https://fixture.invalid/v1",
            Parameters = new ModelParameters { SnapshotMb = 1, ToolOutputLimit = 45_000 } };
        using var session = await GitToolSession.CreateAsync(repo, sha, default, profile.Parameters);
        string Args(string path, string query, string commit) => JsonSerializer.Serialize(new { path, query, commit });
        foreach (var args in new[] { Args(PathInRepo, Query, sha), Args(PathInRepo, Query, parent), Args("web", Query, sha),
            Args(PathInRepo, "before🙂", sha), Args(PathInRepo, "absent", sha) })
        {
            using var result = JsonDocument.Parse(await session.ExecuteAsync("git_search", args, default));
            if (result.RootElement.GetProperty("status").GetString() != "ok" ||
                (result.RootElement.GetProperty("content").GetString() == "") != args.Contains("absent"))
                throw new Exception("Searches with different arguments must be independent.");
        }
        var handler = new SearchReplayChecks(sha);
        using var http = new HttpClient(handler);
        var report = await new ModelClient(http).ReviewAsync(profile, session, "main", "Review", default);
        if (report != "NO_BUGS" || !session.ReadyForFinal || handler.verifiedPackets < 2)
            throw new Exception("Did not finish multiple packets with independent search histories.");
        Console.WriteLine($"PASS search replay: five identical calls, one excerpt page, four receipts and a complete second page in each of {handler.verifiedPackets} packets.");
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        var messages = body.RootElement.GetProperty("messages");
        using var evidence = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
        if (evidence.RootElement.GetProperty("packet").ValueKind == JsonValueKind.Null) return Verdict();
        var results = messages.EnumerateArray().Where(m => m.GetProperty("role").GetString() == "tool")
            .Select(m => m.GetProperty("content").GetString()!).ToArray();
        int next = 0;
        if (results.Length > 0)
        {
            using var first = JsonDocument.Parse(results[0]);
            next = first.RootElement.GetProperty("next_offset").GetInt32();
            var text = first.RootElement.GetProperty("content").GetString()!;
            if (!text.StartsWith($"{sha}:{PathInRepo}:189:…") || !text.Contains("before🙂" + Query + "🙂after") ||
                text.Split('\n')[0].Length > 600 || text.Contains('�'))
                throw new Exception("Minified excerpt lost its match, context or source location.");
            foreach (var receipt in results.Skip(1).Take(4))
            {
                using var replay = JsonDocument.Parse(receipt);
                if (receipt.Length > 500 || replay.RootElement.GetProperty("status").GetString() != "already_returned" ||
                    replay.RootElement.GetProperty("first_match").GetString() != $"{sha}:{PathInRepo}:189" ||
                    replay.RootElement.GetProperty("next_offset").GetInt32() != next || replay.RootElement.TryGetProperty("content", out _))
                    throw new Exception("Repeated search page must return a compact receipt.");
            }
            if (results.Length == 6)
            {
                using var second = JsonDocument.Parse(results[^1]);
                if (second.RootElement.GetProperty("offset").GetInt32() != next || second.RootElement.GetProperty("next_offset").ValueKind != JsonValueKind.Null)
                    throw new Exception("Incorrect next search page.");
                var rows = (text + second.RootElement.GetProperty("content").GetString()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (rows.Length != 141 || !rows.Select((r, i) => r.StartsWith($"{sha}:{PathInRepo}:{189+i}:")).All(v => v))
                    throw new Exception("Search pagination lost or duplicated a matching line.");
                verifiedPackets++;
                return Verdict();
            }
        }
        // Default commit/offset and explicit values address the same page.
        var args = results.Length == 0 ? JsonSerializer.Serialize(new { path = PathInRepo, query = Query }) :
            JsonSerializer.Serialize(new { commit = sha, query = Query, offset = results.Length == 5 ? next : 0, path = PathInRepo });
        var call = new { id = "search-" + results.Length, type = "function", function = new { name = "git_search", arguments = args } };
        return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] {
            new { finish_reason = "tool_calls", message = new { tool_calls = new[] { call } } } } })) };
    }

    private static HttpResponseMessage Verdict() => new(HttpStatusCode.OK) {
        Content = new StringContent("""{"choices":[{"finish_reason":"stop","message":{"content":"NO_BUGS"}}]}""")
    };
}
