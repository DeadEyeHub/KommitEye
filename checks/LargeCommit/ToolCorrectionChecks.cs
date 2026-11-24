using System.Net;
using System.Text.Json;
using KommitEye.Models;
using KommitEye.Services;

internal sealed class ToolCorrectionChecks(bool correct) : HttpMessageHandler
{
    private int requests;

    internal static async Task RunAsync(string repo, string sha)
    {
        foreach (var correct in new[] { true, false })
        {
            var handler = new ToolCorrectionChecks(correct);
            using var http = new HttpClient(handler);
            using var tools = await GitToolSession.CreateAsync(repo, sha, default);
            try
            {
                var report = await new ModelClient(http).ReviewAsync(new ModelProfile { Model = "fixture", Endpoint = "https://fixture.invalid/v1" },
                    tools, "test", "Review", default);
                if (!correct || !tools.ReadyForFinal || report != "NO_BUGS") throw new Exception("Unexpected tool correction verdict.");
            }
            catch (InvalidDataException e) when (!correct && handler.requests == 4 && !tools.ReadyForFinal &&
                e.Message.Contains("Packet 1/8") && e.Message.Contains("git_changed_files({})")) { }
            Console.WriteLine($"PASS parent SHA correction: corrected={correct}, requests={handler.requests}");
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        var messages = body.RootElement.GetProperty("messages");
        using var evidence = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
        requests++;
        if (requests == 3 && !messages.EnumerateArray().Any(m => m.GetProperty("role").GetString() == "user" &&
            m.GetProperty("content").GetString()!.Contains("git_changed_files({})")))
            throw new Exception("Missing actionable correction for the rejected call.");
        if (requests is 1 or 3)
        {
            // An unrelated successful call must not clear the rejected git_changed_files request.
            var name = requests == 3 && !correct ? "git_metadata" : "git_changed_files";
            var arguments = requests == 1 ? JsonSerializer.Serialize(new { commit = evidence.RootElement.GetProperty("parent_sha").GetString() }) : "{}";
            var call = new { id = "call-" + requests, type = "function", function = new { name, arguments } };
            return Response(new { choices = new[] { new { finish_reason = "tool_calls", message = new { tool_calls = new[] { call } } } } });
        }
        return Response(new { choices = new[] { new { finish_reason = "stop", message = new { content = "NO_BUGS" } } } });
    }

    private static HttpResponseMessage Response(object body) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body)) };
}
