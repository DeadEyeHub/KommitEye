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
        foreach (var run in new[] { 1, 2 })
        foreach (var badQuote in new[] { false, true })
        {
            var handler = new RecordedAuditReplay(run, badQuote);
            using var http = new HttpClient(handler);
            using var session = await GitToolSession.CreateAsync(repo, sha, default);
            var report = await new ModelClient(http).ReviewAsync(new ModelProfile { Model = "fixture",
                Endpoint = "https://fixture.invalid/v1" }, session, "test", "Review", default);
            var result = ReviewParser.Parse(report);
            var expectedPending = badQuote ? 4 : run == 1 ? 0 : 1;
            if (handler.pass != 2 || result.NeedsVerification.Count != expectedPending ||
                (badQuote ? result.Findings.Count != 0 || !result.NeedsVerification.Any(f => f.File == "Program.cs" &&
                    f.Line == 4 && f.Description.Contains("Quote does not match Program.cs:4 (NEW)")) :
                    result.Findings.Count != 1 || result.Findings[0] is not { File: "Program.cs", Line: 4, Side: "NEW" }))
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
            answer = packet.ValueKind != JsonValueKind.Null ? "NO_BUGS" : string.Join("\n\n", run == 1
                ? new[] { Block("Fixtures/Dataset02.cs", 1, "OLD", "Dataset02 was removed."),
                    Block("Program.cs", 4, "OLD", "Dataset03 was deleted while Program.cs still uses it."),
                    Block("Fixtures/Dataset06.cs", 1, "OLD", "Dataset06 was removed."),
                    Block("Program.cs", 4, "NEW", "The remaining Dataset03 reference breaks compilation.") }
                : new[] { Block("Program.cs", 4, "NEW", "The remaining Dataset03 reference breaks compilation."),
                    Block("Fixtures/Dataset03.cs", 3, "OLD", "Removing Dataset03 breaks the caller."),
                    Block("Program.cs", 4, "OLD", "The deleted Dataset03 class is still referenced."),
                    Block("Program.cs", 4, "OLD", "Program.cs still needs Dataset03 to compile.") });
        }
        else
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", $"59964711-run{run}-pass{++pass}.txt");
            answer = await File.ReadAllTextAsync(path, token);
            if (badQuote && pass == 2)
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
