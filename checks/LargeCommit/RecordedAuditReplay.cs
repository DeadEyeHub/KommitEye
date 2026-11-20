using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using KommitEye.Models;
using KommitEye.Services;

internal sealed class RecordedAuditReplay(int run, bool badQuote) : HttpMessageHandler
{
    private int pass;

    public static async Task Run(string repo, string sha)
    {
        foreach (var run in new[] { 1, 2, 3 })
        foreach (var badQuote in new[] { false, true })
        {
            var handler = new RecordedAuditReplay(run, badQuote);
            using var http = new HttpClient(handler);
            using var session = await GitToolSession.CreateAsync(repo, sha, default);
            var report = await new ModelClient(http).ReviewAsync(new ModelProfile { Model = "fixture",
                Endpoint = "https://fixture.invalid/v1" }, session, "test", "Review", default);
            var result = ReviewParser.Parse(report);
            var expectedPending = run switch { 1 => badQuote ? 4 : 0, 2 => badQuote ? 4 : 1, _ => badQuote ? 5 : 4 };
            var file = run == 3 ? "Fixtures/Dataset03.cs" : "Program.cs";
            var side = run == 3 ? "OLD" : "NEW";
            if (handler.pass != 2 || result.NeedsVerification.Count != expectedPending ||
                (badQuote ? result.Findings.Count != 0 || !result.NeedsVerification.Any(f => f.File == file &&
                    f.Description.Contains($"Quote does not match {file}:4 ({side})")) :
                    result.Findings.Count != 1 || result.Findings[0].File != file ||
                    result.Findings[0].Line != 4 || result.Findings[0].Side != side))
                throw new Exception($"Unexpected recorded audit result, run {run}, bad quote {badQuote}: {report}");
            Console.WriteLine($"PASS recorded {sha[..8]} run {run}, bad quote {badQuote}: {result.Findings.Count} confirmed, {expectedPending} pending");
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        using var input = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        string answer;
        if (input.RootElement.TryGetProperty("packet", out var packet))
        {
            // Reconstruct the draft locations in their observed candidate order.
            answer = packet.ValueKind != JsonValueKind.Null ? "NO_BUGS" : string.Join("\n\n", run switch
            {
                1 => new[] { Block("Fixtures/Dataset02.cs", 1, "OLD", "Dataset02 was removed."),
                    Block("Program.cs", 4, "OLD", "Dataset03 was deleted while Program.cs still uses it."),
                    Block("Fixtures/Dataset06.cs", 1, "OLD", "Dataset06 was removed."),
                    Block("Program.cs", 4, "NEW", "The remaining Dataset03 reference breaks compilation.") },
                2 => new[] { Block("Program.cs", 4, "NEW", "The remaining Dataset03 reference breaks compilation."),
                    Block("Fixtures/Dataset03.cs", 3, "OLD", "Removing Dataset03 breaks the caller."),
                    Block("Program.cs", 4, "OLD", "The deleted Dataset03 class is still referenced."),
                    Block("Program.cs", 4, "OLD", "Program.cs still needs Dataset03 to compile.") },
                3 => new[] { await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "59964711-run3-draft.txt"), token) },
                _ => throw new ArgumentOutOfRangeException(nameof(run))
            });
        }
        else
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", $"59964711-run{run}-pass{++pass}.txt");
            answer = await File.ReadAllTextAsync(path, token);
            var instruction = body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
            if (!instruction.Contains("JSON field \"evidence\"") || instruction.Contains("correct", StringComparison.OrdinalIgnoreCase))
                throw new Exception("Unexpected final audit instructions.");
            if (badQuote && pass == (run == 3 ? 1 : 2))
            {
                var decisions = JsonNode.Parse(answer)!.AsArray();
                decisions.Single(d => (string?)d!["action"] == "keep")!["quote"] = "Console.WriteLine(42);";
                answer = decisions.ToJsonString();
            }
        }
        return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
            choices = new[] { new { finish_reason = "stop", message = new { content = answer } } }
        })) };
    }

    private static string Block(string file, int line, string side, string claim) =>
        $"BUG\nFILE: {file}\nLINE: {line}\nSIDE: {side}\nDESCRIPTION: {claim}\nEND";
}
