using System.Net;
using System.Text.Json;
using KommitEye.Services;

// Keep existing draft/repair fixtures focused on their phase. Final confirmation
// must refer to a real numbered source line supplied by the host.
sealed class FixtureAuditHandler(HttpMessageHandler draft) : DelegatingHandler(draft)
{
    public int Audits { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        var messages = body.RootElement.GetProperty("messages");
        if (!messages[0].GetProperty("content").GetString()!.StartsWith("Independently verify every assigned"))
            return await base.SendAsync(request, token);
        Audits++;
        using var input = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
        var decisions = input.RootElement.GetProperty("candidates").EnumerateArray().Select(item =>
        {
            var finding = ReviewParser.Parse(item.GetProperty("report").GetString()!).Findings.Single();
            var source = item.GetProperty("sources").EnumerateArray().Single(s => s.GetProperty("Side").GetString() == finding.Side);
            var quote = source.GetProperty("Lines").GetProperty(finding.Line!.Value.ToString()).GetString();
            return new { id = item.GetProperty("id").GetInt32(), action = "keep", evidence = "The cited fixture statement triggers the reported consequence.",
                file = finding.File, side = finding.Side, line = finding.Line, quote, condition = "When executed", consequence = "Fixture failure", contradicted = false };
        }).ToArray();
        return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
            choices = new[] { new { finish_reason = "stop", message = new { content = JsonSerializer.Serialize(decisions) } } }
        })) };
    }
}
