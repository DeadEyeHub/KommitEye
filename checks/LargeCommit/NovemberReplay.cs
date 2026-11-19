using System.Net;
using System.Text.Json;
using KommitEye.Models;
using KommitEye.Services;

// Replay the response shapes from service diagnostics 22-24 against immutable Git objects.
internal sealed class NovemberReplay(bool broken, bool persistError, bool contradictBytes) : HttpMessageHandler
{
    private readonly Dictionary<string, int> visits = [];
    private int audits, lineRepairs, suppliedSearches;

    public static async Task Run(string repo, string deletion)
    {
        using var session = await GitToolSession.CreateAsync(repo, deletion, default);
        using var metadata = JsonDocument.Parse(await session.ExecuteAsync("git_metadata", "{}", default));
        var restored = metadata.RootElement.GetProperty("content").GetString()!.Split('\n').First(row => row.StartsWith("parent "))[7..].Trim();
        foreach (var (sha, retry, persistent, contradiction, expectedRetries) in new[]
        {
            (restored, true, false, false, 1), (deletion, true, false, false, 1),
            (restored, false, true, false, 0), (deletion, true, true, false, 1),
            (restored, false, false, true, 0)
        })
        {
            var handler = new NovemberReplay(sha == deletion, persistent, contradiction);
            using var http = new HttpClient(handler);
            var runner = new ReviewRunner(new GitService(), new ModelClient(http), new ConfigurationStore(), new StateStore(), new ReportWriter());
            var retries = 0;
            runner.Log += message => { if (message.Contains("automatically from")) retries++; };
            var settings = new AppSettings { ProjectId = Guid.NewGuid().ToString(), BranchRef = "refs/heads/master",
                RepositoryPath = Path.GetFullPath(repo), RetryPartialReview = retry };
            try
            {
                var result = await runner.ReviewSingleCommitAsync(settings,
                    new ModelProfile { Model = "fixture", Endpoint = "https://fixture.invalid/v1" }, sha, default);
                if (persistent || result.Findings.Count != (sha == deletion ? 1 : 0) ||
                    sha == deletion && (result.Findings[0].File != "Program.cs" || handler.lineRepairs != 1) || handler.audits != 1)
                    throw new Exception("Unexpected final audit result.");
            }
            catch (ReviewFormatException e) when (persistent && e.Message.Contains("retry budget")) { }
            if (retries != expectedRetries || handler.visits["Fixtures/Dataset01.cs"] != 1 ||
                !contradiction && handler.suppliedSearches == 0) throw new Exception("Retry lost progress or evidence.");
            Console.WriteLine($"PASS replay {sha[..8]} auto={retry} retries={retries} persistent={persistent} bytesConflict={contradiction}");
        }
    }

    private static string Block(string file, int line, string side, string description) =>
        $"BUG\nFILE: {file}\nLINE: {line}\nSIDE: {side}\nDESCRIPTION: {description}\nEND";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        using var doc = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        var data = doc.RootElement;
        string answer;
        if (data.TryGetProperty("packet", out var packet))
        {
            answer = "NO_BUGS";
            if (packet.ValueKind == JsonValueKind.Null)
            {
                if (broken) answer = Block("Program.cs", 4, "OLD", "Program still references the deleted Dataset03.Rows; compilation fails.");
            }
            else
            {
                var path = packet.GetProperty("Path").GetString()!;
                var visit = visits[path] = visits.GetValueOrDefault(path) + 1;
                if (path.EndsWith(broken ? "Dataset07.cs" : "Dataset05.cs"))
                {
                    if (data.GetProperty("reference_search").ValueKind == JsonValueKind.String)
                    {
                        using var evidence = JsonDocument.Parse(data.GetProperty("reference_search").GetString()!);
                        if (evidence.RootElement.GetProperty("query").GetString() != Path.GetFileNameWithoutExtension(path) ||
                            evidence.RootElement.GetProperty("commit").GetString() != data.GetProperty("reviewed_sha").GetString())
                            throw new Exception("Search not pinned to assigned packet and revision.");
                        suppliedSearches++;
                    }
                    if (!contradictBytes && (persistError || visit <= 2)) answer = "Let me check whether this type has any remaining references.";
                }
                if (!broken && path.EndsWith("Dataset02.cs")) answer = Block(path, 1107, "NEW", "Comment breaks the initializer.");
                if (!broken && path.EndsWith("Dataset04.cs")) answer = Block(path, 5, "NEW", "UTF-8 BOM and CRLF will corrupt fixture values.");
                if (broken && path.EndsWith("Dataset01.cs")) answer = Block(path, 1, "OLD", "Any consumer of this deleted file will fail.");
                if (broken && path.EndsWith("Dataset03.cs")) answer = "The deletion leaves a broken caller.\n\n" +
                    Block(path, 0, "OLD", "Dataset03 was deleted while Program.cs references its Rows; compilation fails.");
            }
        }
        else if (data.GetProperty("candidates").ValueKind == JsonValueKind.Object)
        {
            if (!data.GetProperty("candidates").GetProperty("4").GetString()!.Contains("class Dataset03"))
                throw new Exception("Line repair missing deleted declaration.");
            lineRepairs++;
            answer = "4";
        }
        else
        {
            audits++;
            var items = data.GetProperty("candidates").EnumerateArray().ToArray();
            if (items.Length != (broken ? 3 : 2)) throw new Exception("Audit dropped candidates.");
            if (!broken)
            {
                var bytes = items[1].GetProperty("bytes");
                if (bytes.GetProperty("Bom").GetString() != "none" || bytes.GetProperty("Endings").GetString() != "LF" ||
                    bytes.GetProperty("Commit").GetString() != data.GetProperty("reviewed_sha").GetString() ||
                    bytes.GetProperty("Path").GetString() != "Fixtures/Dataset04.cs") throw new Exception("Wrong byte evidence.");
            }
            answer = broken
                ? """[{"id":1,"action":"unsupported","evidence":"No consumer identified"},{"id":2,"action":"duplicate","duplicateOf":3,"evidence":"Same missing definition breaks Program.cs:4"},{"id":3,"action":"keep","evidence":"Program.cs:4 uses deleted Dataset03"}]"""
                : contradictBytes
                    ? """[{"id":1,"action":"unsupported","evidence":"Legal C# comment"},{"id":2,"action":"keep","expectedBom":"UTF-8","expectedEndings":"CRLF","evidence":"Claimed by model"}]"""
                    : """[{"id":1,"action":"unsupported","evidence":"Comment is legal between elements"},{"id":2,"action":"unsupported","evidence":"Committed bytes have LF and no BOM"}]""";
        }
        return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
            choices = new[] { new { finish_reason = "stop", message = new { content = answer } } }
        })) };
    }
}
