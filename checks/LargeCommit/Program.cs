using System.Net;
using System.Text.Json;
using KommitEye.Models;
using KommitEye.Services;

if (args.Length != 4) throw new ArgumentException("Expected repository, addition SHA, clean deletion SHA, broken deletion SHA.");
Environment.SetEnvironmentVariable("KOMMITEYE_DATA_DIR", Path.Combine(Path.GetTempPath(), "KommitEye-packet-check-" + Guid.NewGuid()));
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
    var handler = new PacketHandler(disableThinking: i != 1, freeFormAttempts: i == 3 ? 1 : 0);
    using var http = new HttpClient(new FixtureAuditHandler(handler));
    var profile = new ModelProfile { Model = "fixture", Endpoint = "https://fixture.invalid/v1" };
    profile.Parameters.DisableThinking = i != 1;
    var report = await new ModelClient(http).ReviewAsync(profile, session, "test", "Review", default);
    if (!session.ReadyForFinal || handler.Paths.Count != 8 || handler.SynthesisCount != 1 ||
        handler.MaxEvidence > 8500 || (i == 3 ? report != PacketHandler.Finding || handler.Dataset07Requests != 2 : report != "NO_BUGS"))
        throw new Exception($"Unexpected coverage or report for {args[i]}: {report}");
    Console.WriteLine($"PASS {args[i][..7]}: {handler.Paths.Count} packets, largest input evidence {handler.MaxEvidence} characters, {ReviewParser.Parse(report).Findings.Count} findings");
}

using (var session = await GitToolSession.CreateAsync(args[0], args[1], default))
{
    var handler = new PacketHandler(freeFormAttempts: 2);
    using var http = new HttpClient(new FixtureAuditHandler(handler));
    var profile = new ModelProfile { Model = "fixture", Endpoint = "https://fixture.invalid/v1" };
    try
    {
        await new ModelClient(http).ReviewAsync(profile, session, "test", "Review", default);
        throw new Exception("Repeated free-form answer accepted.");
    }
    catch (ReviewFormatException) when (handler.Paths.Count == 7 && handler.Dataset07Requests == 2 && handler.SynthesisCount == 0 && !session.ReadyForFinal)
    {
        Console.WriteLine("PASS repeated free-form response leaves packet 7 incomplete after one retry");
    }
}

using (var session = await GitToolSession.CreateAsync(args[0], args[1], default))
using (var http = new HttpClient(new FixtureAuditHandler(new PacketHandler())))
{
    var profile = new ModelProfile { Model = "fixture", Endpoint = "https://fixture.invalid/v1",
        Parameters = new ModelParameters { MaxRounds = 7, ReminderRequests = 1 } };
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

foreach (var (packetLimit, reserve, finalLimit) in new[] { (8, 2, 2), (8, new ModelParameters().CrossFileMaxRounds, 6), (9, 2, 3), (107, 6, 105) })
{
    using var session = await GitToolSession.CreateAsync(args[0], args[3], default);
    var handler = new FinalSearchHandler(finalLimit);
    using var http = new HttpClient(new FixtureAuditHandler(handler));
    var profile = new ModelProfile { Model = "fixture", Endpoint = "https://fixture.invalid/v1",
        Parameters = new ModelParameters { MaxRounds = packetLimit, CrossFileMaxRounds = reserve, ReminderRequests = 120 } };
    try
    {
        var report = await new ModelClient(http).ReviewAsync(profile, session, "test", "Review", default);
        if (finalLimit < 3 || handler.Requests != 3 || !session.ReadyForFinal || report != PacketHandler.Finding)
            throw new Exception("Final review lost packet findings or its independent budget.");
    }
    catch (InvalidDataException e) when (finalLimit == 2 && handler.Requests == 2 && !session.ReadyForFinal &&
        e.Message.Contains("confirmed packet findings")) { }
    Console.WriteLine($"PASS final budget: {packetLimit - 8} unused packet requests plus {reserve} reserved gives {finalLimit}");
}

foreach (var verdict in new[] { "NO_BUGS", PacketHandler.Finding, ConflictingVerdictHandler.Conflict })
{
    using var session = await GitToolSession.CreateAsync(args[0], args[3], default);
    var handler = new ConflictingVerdictHandler(verdict);
    using var http = new HttpClient(new FixtureAuditHandler(handler));
    var profile = new ModelProfile { Model = "fixture", Endpoint = "https://fixture.invalid/v1" };
    try
    {
        var report = await new ModelClient(http).ReviewAsync(profile, session, "test", "Review", default);
        if (report != verdict || verdict == ConflictingVerdictHandler.Conflict || !session.ReadyForFinal ||
            handler.PacketVisits.Count != 8 || handler.Verifications != 1 || handler.SynthesisCount != 1)
            throw new Exception("Incorrect result after verdict verification.");
    }
    catch (ReviewFormatException) when (verdict == ConflictingVerdictHandler.Conflict &&
        handler.Verifications == 1 && !session.ReadyForFinal && handler.SynthesisCount == 0) { }
    Console.WriteLine("PASS conflicting verdict verification: " + verdict.Split('\n')[0] +
        (verdict == ConflictingVerdictHandler.Conflict ? " and NO_BUGS rejected" : " accepted"));
}

foreach (var (packetFailures, finalFailures) in new[] { (1, 1), (2, 0), (0, 2) })
{
    using var session = await GitToolSession.CreateAsync(args[0], args[3], default);
    var handler = new PacketHandler(packetToolText: packetFailures, finalToolText: finalFailures);
    using var http = new HttpClient(new FixtureAuditHandler(handler));
    var profile = new ModelProfile { Model = "fixture", Endpoint = "https://fixture.invalid/v1" };
    try
    {
        var report = await new ModelClient(http).ReviewAsync(profile, session, "test", "Review", default);
        if (packetFailures != 1 || finalFailures != 1 || handler.Dataset07Requests != 2 ||
            handler.SynthesisCount != 2 || !session.ReadyForFinal || report != PacketHandler.Finding)
            throw new Exception("Diagnostic 10 retry lost coverage, the original finding or its retry bound.");
    }
    catch (ReviewFormatException error) when (error.Message.Contains("tool_calls as text twice") && !session.ReadyForFinal &&
        ((packetFailures == 2 && handler.Dataset07Requests == 2 && handler.SynthesisCount == 0) ||
         (finalFailures == 2 && handler.Dataset07Requests == 1 && handler.SynthesisCount == 2))) { }
    Console.WriteLine($"PASS diagnostic 10: packet failures {packetFailures}, final failures {finalFailures}");
}

foreach (var (interruptAfter, changePrompt) in new[] { (4, false), (8, false), (4, true) })
{
    var project = Guid.NewGuid().ToString();
    var handler = new PacketHandler();
    using var cancellation = new CancellationTokenSource();
    using var http = new HttpClient(new FixtureAuditHandler(handler));
    var profile = new ModelProfile { Model = "fixture", Endpoint = "https://fixture.invalid/v1" };
    using (var checkpoint = new PacketCheckpoint(project, args[0], "test", args[3]) {
        Progress = message => { if (message.StartsWith($"Saved packet {interruptAfter}/")) cancellation.Cancel(); }
    })
    using (var session = await GitToolSession.CreateAsync(args[0], args[3], default))
    {
        try
        {
            await new ModelClient(http).ReviewAsync(profile, session, "test", "Review", cancellation.Token, checkpoint: checkpoint);
            throw new Exception("Review failed to stop at the requested packet.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }
    using var resumed = new PacketCheckpoint(project, args[0], "test", args[3]);
    using var freshHttp = new HttpClient(new FixtureAuditHandler(new PacketHandler()));
    using var freshSession = await GitToolSession.CreateAsync(args[0], args[3], default);
    var report = await new ModelClient(changePrompt ? freshHttp : http).ReviewAsync(profile, freshSession, "test",
        changePrompt ? "Changed instructions" : "Review", default, checkpoint: resumed);
    if (!freshSession.ReadyForFinal || report != PacketHandler.Finding)
        throw new Exception("Restart lost a confirmed finding or skipped final review.");
    resumed.Complete();
    Console.WriteLine($"PASS checkpoint after packet {interruptAfter}/8, changed prompt={changePrompt}");
}

var restored = await NovemberReplay.Run(args[0], args[3]);
await FinalReportReplay.Run(args[0], args[1], args[2], restored, args[3]);
await RecordedAuditReplay.Run(args[0], args[3]);

sealed class FinalSearchHandler(int limit) : DelegatingHandler(new PacketHandler())
{
    public int Requests;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        var messages = body.RootElement.GetProperty("messages");
        using var evidence = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
        if (evidence.RootElement.GetProperty("packet").ValueKind != JsonValueKind.Null)
            return await base.SendAsync(request, token);
        Requests++;
        if (!messages[messages.GetArrayLength() - 1].GetProperty("content").GetString()!.Contains($"request {Requests} of {limit}"))
            throw new Exception("Reminder did not use the separate cross-file budget.");
        var json = Requests switch
        {
            1 => """{"choices":[{"finish_reason":"stop","message":{"content":""}}]}""",
            2 => """{"choices":[{"finish_reason":"tool_calls","message":{"tool_calls":[{"id":"search","type":"function","function":{"name":"git_search","arguments":"{\"path\":\"Program.cs\",\"query\":\"Dataset03\"}"}}]}}]}""",
            3 when messages.EnumerateArray().Any(m => m.GetProperty("role").GetString() == "tool" &&
                m.GetProperty("content").GetString()!.Contains("Fixtures.Dataset03.Rows")) =>
                """{"choices":[{"finish_reason":"stop","message":{"content":"NO_BUGS"}}]}""",
            _ => throw new Exception("Missing search evidence or unexpected final request.")
        };
        return new(HttpStatusCode.OK) { Content = new StringContent(json) };
    }
}

sealed class ConflictingVerdictHandler(string verdict) : HttpMessageHandler
{
    public const string Conflict = PacketHandler.Finding + "\n\nNO_BUGS";
    public readonly Dictionary<string, int> PacketVisits = new();
    public int Verifications, SynthesisCount;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        var messages = body.RootElement.GetProperty("messages");
        using var evidence = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
        var packet = evidence.RootElement.GetProperty("packet");
        var answer = "NO_BUGS";
        if (packet.ValueKind == JsonValueKind.Null) SynthesisCount++;
        else
        {
            var path = packet.GetProperty("Path").GetString()!;
            var visit = PacketVisits[path] = PacketVisits.GetValueOrDefault(path) + 1;
            if (path == "Fixtures/Dataset03.cs")
            {
                if (visit == 1) answer = "No errors detected.";
                else if (visit == 2) answer = Conflict;
                else
                {
                    if (visit != 3 || messages[2].GetProperty("content").GetString() != Conflict ||
                        !messages[3].GetProperty("content").GetString()!.Contains("Verify each candidate") ||
                        !packet.GetProperty("References").GetString()!.Contains("Program.cs:4:") ||
                        !body.RootElement.TryGetProperty("tools", out _))
                        throw new Exception("Verdict verification lost the claim, tools or original packet evidence.");
                    Verifications++;
                    answer = verdict;
                }
            }
            else if (visit != 1) throw new Exception("Verdict verification repeated an unrelated packet.");
        }
        var json = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = answer }, finish_reason = "stop" } } });
        return new(HttpStatusCode.OK) { Content = new StringContent(json) };
    }
}

sealed class PacketHandler(bool disableThinking = false, int freeFormAttempts = 0, int packetToolText = 0, int finalToolText = 0) : HttpMessageHandler
{
    private const string Diagnostic10 = """
        {"reasoning":"The host has already supplied the patch and coverage for this change. The diff deletes the fixture files Dataset01..08.cs under Fixtures/. The related line shows Program.cs still references LargeCommitFixture.Fixtures.Dataset03.Rows.Length. I must confirm whether this causes a compile error by checking whether Dataset03 still exists or whether the referenced member still exists after this change.\n","tool_calls":[{"name":"git_changed_files","arguments":{"commit":"5996471121f7c628912a81e342bb37daf0a5c6b1","offset":0}}]}
        """;
    public const string Finding = "BUG\nFILE: Program.cs\nLINE: 4\nSIDE: NEW\nDESCRIPTION: Program refers to the deleted Dataset03.Rows.\nEND";
    public readonly HashSet<string> Paths = [];
    public int SynthesisCount;
    public int MaxEvidence;
    public int Dataset07Requests;
    private string? firstDataset07Evidence;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        var hasOverride = body.RootElement.TryGetProperty("chat_template_kwargs", out var template);
        if (hasOverride != disableThinking || (hasOverride && template.GetProperty("enable_thinking").GetBoolean()))
            throw new Exception("Unexpected thinking mode in the model request.");
        var messages = body.RootElement.GetProperty("messages");
        if (messages.EnumerateArray().Count(m => !m.GetProperty("content").GetString()!.StartsWith("Review budget reminder:")) > 3 ||
            messages.EnumerateArray().Any(m => m.GetProperty("role").GetString() == "assistant"))
            throw new Exception("Expected a fresh packet context.");
        var text = messages[1].GetProperty("content").GetString()!;
        MaxEvidence = Math.Max(MaxEvidence, text.Length);
        using var evidence = JsonDocument.Parse(text);
        var packet = evidence.RootElement.GetProperty("packet");
        var answer = "NO_BUGS";
        if (packet.ValueKind == JsonValueKind.Null)
        {
            SynthesisCount++;
            if (finalToolText > 0)
            {
                if (!evidence.RootElement.GetProperty("related_lines").GetString()!.Contains("Fixtures.Dataset03.Rows"))
                    throw new Exception("Cross-file evidence lost the Dataset03 reference.");
                if (SynthesisCount <= finalToolText) answer = Diagnostic10;
            }
            if (Paths.Count != 8) throw new Exception("Premature synthesis.");
            if (evidence.RootElement.GetProperty("follow_up").GetArrayLength() != 0)
                throw new Exception("Discarded packet contributed a follow-up.");
        }
        else
        {
            var path = packet.GetProperty("Path").GetString()!;
            var patch = packet.GetProperty("Patch").GetString()!;
            var firstVisit = Paths.Add(path);
            if (path.EndsWith("/Dataset07.cs", StringComparison.Ordinal))
            {
                Dataset07Requests++;
                if (firstVisit) firstDataset07Evidence = text;
                else if (text != firstDataset07Evidence || Paths.Count != 7 || Dataset07Requests != 2 || (freeFormAttempts == 0 && packetToolText == 0))
                    throw new Exception("Unexpected packet retry or changed retry evidence.");
                if (Dataset07Requests <= freeFormAttempts)
                    answer = "FOLLOW_UP: Discard this unvalidated question.\n{\"file\":\"Fixtures/Dataset07.cs\",\"diff\":\"No bugs detected.\"}";
                if (Dataset07Requests <= packetToolText) answer = Diagnostic10;
            }
            else if (!firstVisit) throw new Exception("An already accepted packet was repeated.");
            if (!patch.Contains("1099 consecutive literal rows") || !patch.Contains("static readonly string[] Rows"))
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
