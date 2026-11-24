using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using KommitEye.Models;
using KommitEye.Services;

internal sealed class CrossFileResumeChecks(bool retry) : DelegatingHandler(new PacketHandler())
{
    private readonly List<object> pieces = [];
    private string? parent, map;
    private int finalRequests;

    internal static async Task RunAsync(string repository, string sha)
    {
        foreach (var scenario in new[] { "same limits", "changed limits", "4.0.5 reserve increased" })
        {
            var project = Guid.NewGuid().ToString();
            const string branch = "test", prompt = "Review";
            var profile = new ModelProfile { Model = "fixture", Endpoint = "https://fixture.invalid/v1" };
            profile.Parameters.MaxRounds = 8;
            var first = new CrossFileResumeChecks(false);
            using (var checkpoint = new PacketCheckpoint(project, repository, branch, sha))
            using (var session = await GitToolSession.CreateAsync(repository, sha, default, profile.Parameters))
            using (var http = new HttpClient(new FixtureAuditHandler(first)))
            {
                try
                {
                    await new ModelClient(http).ReviewAsync(profile, session, branch, prompt, default, checkpoint: checkpoint);
                    throw new Exception("Expected the cross-file phase to exhaust its reserve.");
                }
                catch (InvalidDataException error) when (error.Message.Contains("Cross-file review round budget exhausted (6/6) with confirmed packet findings")
                    && first.finalRequests == 6 && checkpoint.CompletedPackets == 8) { }
            }
            if (scenario.StartsWith("4.0.5"))
            {
                // Recreate the on-disk format and fingerprint written by the previous release.
                var path = Path.Combine(AppPaths.DataDirectory, "review-progress", Hash(new { project, repository, branch, sha }) + ".json");
                var snapshot = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
                snapshot.Remove("PacketRoundsUsed");
                var parameters = JsonSerializer.SerializeToNode(profile.Parameters)!.AsObject();
                foreach (var key in parameters.Select(p => p.Key).Where(k => k.StartsWith("Git") && k.EndsWith("TimeoutSeconds")).ToArray())
                    parameters.Remove(key);
                snapshot["Fingerprint"] = Hash(new { protocol = 2, plan = new { Parent = first.parent, Map = first.map, Packets = first.pieces },
                    profile.Name, profile.Model, profile.Endpoint, Parameters = parameters, prompt, Localization.Language });
                File.WriteAllText(path, snapshot.ToJsonString());
            }
            if (scenario != "same limits") profile.Parameters.CrossFileMaxRounds = 12;
            if (scenario == "changed limits")
            {
                profile.Parameters.MaxRounds = 16;
                profile.Parameters.MaxToolCalls++;
            }
            var messages = new List<string>();
            var second = new CrossFileResumeChecks(true);
            using var resumed = new PacketCheckpoint(project, repository, branch, sha) { Progress = messages.Add };
            using var fresh = await GitToolSession.CreateAsync(repository, sha, default, profile.Parameters);
            using var client = new HttpClient(new FixtureAuditHandler(second));
            var report = await new ModelClient(client).ReviewAsync(profile, fresh, branch, prompt, default, checkpoint: resumed);
            var expectedBudget = profile.Parameters.CrossFileMaxRounds + profile.Parameters.MaxRounds - 8;
            if (report != PacketHandler.Finding || !fresh.ReadyForFinal || second.finalRequests != 1 ||
                !messages.Contains($"Resuming cross-file review with {expectedBudget} requests."))
                throw new Exception("Retry lost the finding or used the wrong phase budget.");
            resumed.Complete();
            Console.WriteLine($"PASS cross-file restart after 6/6: {scenario}, 0 repeated packets, confirmed finding retained");
        }
    }

    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        using var evidence = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        var packet = evidence.RootElement.GetProperty("packet");
        if (packet.ValueKind != JsonValueKind.Null)
        {
            if (retry) throw new Exception("Retry sent an already validated packet to the model.");
            parent = evidence.RootElement.GetProperty("parent_sha").GetString();
            map = evidence.RootElement.GetProperty("map").GetString();
            pieces.Add(new { Path = packet.GetProperty("Path").GetString(), Patch = packet.GetProperty("Patch").GetString() });
            return await base.SendAsync(request, token);
        }
        finalRequests++;
        var json = retry ? """{"choices":[{"finish_reason":"stop","message":{"content":"NO_BUGS"}}]}""" :
            """{"choices":[{"finish_reason":"tool_calls","message":{"tool_calls":[{"id":"lookup","type":"function","function":{"name":"git_metadata","arguments":"{}"}}]}}]}""";
        return new(HttpStatusCode.OK) { Content = new StringContent(json) };
    }
}
