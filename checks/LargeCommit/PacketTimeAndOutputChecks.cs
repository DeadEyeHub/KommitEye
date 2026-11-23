using System.Net;
using System.Text.Json;
using KommitEye.Models;
using KommitEye.Services;

internal sealed class PacketTimeAndOutputChecks(ReviewClock clock, string scenario) : HttpMessageHandler
{
    private int requests;

    internal static async Task RunAsync(string repo, string sha)
    {
        foreach (var scenario in new[] { "complete", "retry-time", "retry-output" })
        {
            var clock = new ReviewClock();
            var handler = new PacketTimeAndOutputChecks(clock, scenario);
            using var http = new HttpClient(handler);
            var profile = new ModelProfile { Model = "fixture", Endpoint = "https://fixture.invalid/v1",
                Parameters = new ModelParameters { ReviewMinutes = 1, ToolOutputLimit = 16000 } };
            using var session = await GitToolSession.CreateAsync(repo, sha, default, profile.Parameters);
            try
            {
                var report = await new ModelClient(http, clock).ReviewAsync(profile, session, "test", "Review", default);
                if (scenario != "complete" || report != "NO_BUGS" || !session.ReadyForFinal || handler.requests != 18)
                    throw new Exception("Expected two requests per packet and two for the final review.");
            }
            catch (ReviewComplexityException) when (scenario == "retry-time" && handler.requests == 3 && !session.ReadyForFinal) { }
            catch (ReviewBudgetException error) when (scenario == "retry-output" && handler.requests == 3 &&
                error.Message.Contains("output budget") && !session.ReadyForFinal) { }
            Console.WriteLine($"PASS packet time and output: {scenario}, {handler.requests} requests, {clock.Elapsed.TotalSeconds}s simulated.");
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        requests++;
        if (scenario != "retry-output") clock.Advance(TimeSpan.FromSeconds(25));
        token.ThrowIfCancellationRequested();
        var messages = body.RootElement.GetProperty("messages");
        var result = messages.EnumerateArray().FirstOrDefault(m => m.GetProperty("role").GetString() == "tool");
        if (result.ValueKind == JsonValueKind.Undefined)
        {
            var call = new { id = "read", type = "function", function = new { name = "git_file",
                arguments = """{"path":"Fixtures/Dataset01.cs","start_line":8,"end_line":78}""" } };
            var json = JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "tool_calls", message = new { tool_calls = new[] { call } } } } });
            return new(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
        if (result.GetProperty("content").GetString()!.Length < 8000)
            throw new Exception("The fixture must consume over half the packet output allowance.");
        // A free-form response rebuilds this packet's conversation with its original evidence.
        var answer = scenario != "complete" && requests == 2 ? "{}" : "NO_BUGS";
        var verdict = JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = answer } } } });
        return new(HttpStatusCode.OK) { Content = new StringContent(verdict) };
    }
}
