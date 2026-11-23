using System.Net;
using System.Text.Json;
using KommitEye.Models;
using KommitEye.Services;

internal sealed class PacketBudgetChecks(bool expireSearch) : HttpMessageHandler
{
    private int requests, completedPackets, completedFinals;

    internal static async Task RunAsync(string repo, string sha)
    {
        foreach (var (rounds, calls) in new[] { (3, 2), (2, 2), (3, 1) })
        {
            var recover = rounds == 3 && calls == 2;
            var handler = new PacketBudgetChecks(recover);
            using var http = new HttpClient(handler);
            var parameters = new ModelParameters { MaxRounds = rounds, MaxToolCalls = calls, CrossFileMaxRounds = 3, GitSearchTimeoutSeconds = 1 };
            var profile = new ModelProfile { Model = "fixture", Endpoint = "https://fixture.invalid/v1", Parameters = parameters };
            using var session = await GitToolSession.CreateAsync(repo, sha, default, parameters);
            var automaticSearches = 0;
            var expireToolSearch = recover;
            void Log(string message)
            {
                // Hold the start trace past the real Git process deadline, independent of repository size.
                if (recover && message.StartsWith("Searching packet references"))
                {
                    automaticSearches++;
                    Thread.Sleep(1200);
                }
                if (expireToolSearch && message.StartsWith("Git command for tool") &&
                    message.Contains("\"started\"") && message.Contains("\"grep\""))
                {
                    expireToolSearch = false;
                    Thread.Sleep(1200);
                }
            }
            try
            {
                var report = await new ModelClient(http).ReviewAsync(profile, session, "test", "Review", default, log: Log);
                if (!recover || report != "NO_BUGS" || !session.ReadyForFinal || automaticSearches != 1 ||
                    handler.completedPackets != 8 || handler.completedFinals != 1 || handler.requests != 27)
                    throw new Exception("Incomplete packet coverage, missing timeouts or premature final verdict.");
            }
            catch (ReviewBudgetException error) when (rounds == 2 && error.Message.Contains("Packet 1/8 round budget exhausted") && handler.requests == 2 && !session.ReadyForFinal) { }
            catch (InvalidDataException error) when (calls == 1 && error.Message.Contains("Git tool call budget exhausted") && handler.requests == 2 && !session.ReadyForFinal) { }
            Console.WriteLine($"PASS packet limits: {rounds} rounds, {calls} calls, {handler.completedPackets} validated packets, {handler.completedFinals} final passes");
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        requests++;
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        var messages = body.RootElement.GetProperty("messages");
        using var evidence = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
        var packet = evidence.RootElement.GetProperty("packet");
        var final = packet.ValueKind == JsonValueKind.Null;
        if (final && completedPackets != 8) throw new Exception("Premature final review.");
        if (expireSearch && !final && !packet.GetProperty("References").GetString()!.Contains("unavailable after a timeout"))
            throw new Exception("Missing timeout notice for optional references.");
        var results = messages.EnumerateArray().Where(m => m.GetProperty("role").GetString() == "tool").ToArray();
        if (results.Length == 2)
        {
            using var result = JsonDocument.Parse(results[1].GetProperty("content").GetString()!);
            if (!result.RootElement.GetProperty("content").GetString()!.Contains("Program.cs:1:using System.Globalization"))
                throw new Exception("Missing scoped search evidence.");
            if (final) completedFinals++;
            else completedPackets++;
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"choices":[{"finish_reason":"stop","message":{"content":"NO_BUGS"}}]}""") };
        }
        var broadSearch = expireSearch && requests == 1;
        var name = broadSearch || results.Length == 1 ? "git_search" : "git_metadata";
        var arguments = broadSearch ? """{"query":"System.Globalization"}""" : results.Length == 1
            ? """{"query":"System.Globalization","path":"Program.cs"}""" : "{}";
        if (expireSearch && requests == 2)
        {
            using var result = JsonDocument.Parse(results.Single().GetProperty("content").GetString()!);
            if (!result.RootElement.GetProperty("recoverable").GetBoolean() ||
                !result.RootElement.GetProperty("message").GetString()!.Contains("Matches are unknown"))
                throw new Exception("Timed-out git_search was presented as complete.");
        }
        var call = new { id = "call-" + requests, type = "function", function = new { name, arguments } };
        var json = JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "tool_calls", message = new { tool_calls = new[] { call } } } } });
        return new(HttpStatusCode.OK) { Content = new StringContent(json) };
    }
}
