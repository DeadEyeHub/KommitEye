using System.Net;
using System.Text.Json;
using KommitEye.Models;
using KommitEye.Services;

if (args.Length != 4) throw new ArgumentException("Expected repository, addition SHA, clean deletion SHA, broken deletion SHA.");
for (var i = 1; i < args.Length; i++)
{
    using var session = await GitToolSession.CreateAsync(args[0], args[i], default);
    if (i == 1)
    {
        const string directory = """{"path":"Fixtures"}""";
        try
        {
            await session.ExecuteAsync("git_file", directory, default);
            throw new Exception("Directory was accepted as a file.");
        }
        catch (GitToolQueryException error) when (error.Message.Contains("git_tree", StringComparison.Ordinal)) { }
        var listing = await session.ExecuteAsync("git_tree", directory, default);
        if (!listing.Contains("Dataset03.cs", StringComparison.Ordinal))
            throw new Exception("Suggested directory listing did not contain Dataset03.cs.");
    }
    var handler = new PacketHandler(disableThinking: i != 1);
    using var http = new HttpClient(handler);
    var profile = new ModelProfile { Model = "fixture", Endpoint = "https://fixture.invalid/v1" };
    profile.Parameters.DisableThinking = i != 1;
    var report = await new ModelClient(http).ReviewAsync(profile, session, "test", "Review", default);
    if (!session.ReadyForFinal || handler.Paths.Count != 8 || handler.SynthesisCount != 1 ||
        handler.MaxEvidence > 8500 || (i == 3 ? report != PacketHandler.Finding : report != "NO_BUGS"))
        throw new Exception($"Unexpected coverage or report for {args[i]}: {report}");
    Console.WriteLine($"PASS {args[i][..7]}: {handler.Paths.Count} packets, largest input evidence {handler.MaxEvidence} characters, {ReviewParser.Parse(report).Findings.Count} findings");
}

using (var session = await GitToolSession.CreateAsync(args[0], args[1], default))
using (var http = new HttpClient(new PacketHandler()))
{
    var profile = new ModelProfile { Model = "fixture", Endpoint = "https://fixture.invalid/v1",
        Parameters = new ModelParameters { MaxRounds = 8, ReminderRequests = 1 } };
    try
    {
        await new ModelClient(http).ReviewAsync(profile, session, "test", "Review", default);
        throw new Exception("Accepted review without the cross-file pass.");
    }
    catch (ReviewBudgetException) when (!session.ReadyForFinal)
    {
        Console.WriteLine("PASS exhausted budget before cross-file pass leaves review incomplete");
    }
}

sealed class PacketHandler(bool disableThinking = false) : HttpMessageHandler
{
    public const string Finding = "BUG\nFILE: Program.cs\nLINE: 4\nSIDE: NEW\nDESCRIPTION: Program refers to the deleted Dataset03.Rows.\nEND";
    public readonly HashSet<string> Paths = [];
    public int SynthesisCount;
    public int MaxEvidence;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        var hasOverride = body.RootElement.TryGetProperty("chat_template_kwargs", out var template);
        if (hasOverride != disableThinking || (hasOverride && template.GetProperty("enable_thinking").GetBoolean()))
            throw new Exception("Unexpected thinking mode in the model request.");
        var messages = body.RootElement.GetProperty("messages");
        if (messages.GetArrayLength() > 3 || messages.EnumerateArray().Any(m => m.GetProperty("role").GetString() == "assistant"))
            throw new Exception("Expected a fresh packet context.");
        var text = messages[1].GetProperty("content").GetString()!;
        MaxEvidence = Math.Max(MaxEvidence, text.Length);
        using var evidence = JsonDocument.Parse(text);
        var packet = evidence.RootElement.GetProperty("packet");
        var answer = "NO_BUGS";
        if (packet.ValueKind == JsonValueKind.Null)
        {
            SynthesisCount++;
            if (Paths.Count != 8) throw new Exception("Premature synthesis.");
        }
        else
        {
            var path = packet.GetProperty("Path").GetString()!;
            var patch = packet.GetProperty("Patch").GetString()!;
            if (!Paths.Add(path) || !patch.Contains("1099 consecutive literal rows") || !patch.Contains("static readonly string[] Rows"))
                throw new Exception("Missing structural evidence for " + path);
            var references = packet.GetProperty("References").GetString()!;
            if (path.EndsWith("/Dataset03.cs") && patch.Contains("deleted file mode") &&
                references.Contains("Program.cs:4:") && references.Contains("Fixtures.Dataset03.Rows"))
                answer = Finding;
        }
        var response = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = answer }, finish_reason = "stop" } } });
        return new(HttpStatusCode.OK) { Content = new StringContent(response) };
    }
}
