using System.Net;
using System.Text.Json;
using KommitEye.Models;
using KommitEye.Services;

internal sealed class FinalReportReplay(string scenario) : HttpMessageHandler
{
    private int audits;
    private const string CommentClaim = "A line containing only 'x' characters (100 of them) is placed directly between string-literal array elements without a separator, causing a C# syntax error (invalid array-initializer element).";
    private const string Placeholder = "Cannot confirm without reading Program.cs; placeholder.";
    private const string MissingType = "The commit deletes Dataset03.cs, but Program.cs still references Dataset03.Rows.Length and fails to compile.";

    public static async Task Run(string repo, string addition, string cleanDeletion, string restored, string deletion)
    {
        foreach (var (name, sha) in new[] { ("comment", addition), ("clean", cleanDeletion), ("placeholder", restored), ("caller", deletion) })
        {
            var handler = new FinalReportReplay(name);
            using var http = new HttpClient(handler);
            var writer = new ReportWriter();
            var runner = new ReviewRunner(new GitService(), new ModelClient(http), new ConfigurationStore(), new StateStore(), writer);
            var result = await runner.ReviewSingleCommitAsync(new AppSettings { RepositoryPath = Path.GetFullPath(repo), BranchRef = "refs/heads/master" },
                new ModelProfile { Model = "fixture", Endpoint = "https://fixture.invalid/v1" }, sha, default);
            if (handler.audits != (name == "clean" ? 0 : 2) || result.Findings.Count != (name == "caller" ? 1 : 0) ||
                result.NeedsVerification.Count != (name == "placeholder" ? 1 : 0)) throw new Exception("Unexpected verdict: " + name);
            if (name == "caller" && result.Findings.Single() != new Finding("Program.cs", 4, "NEW", MissingType))
                throw new Exception("The corrected location changed the claim or missed the caller.");
            if (name == "placeholder" && (!result.ReportMarkdown.Contains("Needs verification") || !result.ReportMarkdown.Contains(Placeholder) ||
                writer.Browse(sha, outcome: "unknown").Total == 0 || writer.Browse(sha, outcome: "clean").Total != 0))
                throw new Exception("Unverified claim missing from report or marked clean.");
            Console.WriteLine($"PASS final audit {sha[..8]} {name}: {handler.audits} audit calls, {result.Findings.Count} confirmed, {result.NeedsVerification.Count} pending");
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        using var input = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        var data = input.RootElement;
        string answer;
        if (data.TryGetProperty("packet", out var packet))
        {
            var path = packet.ValueKind == JsonValueKind.Null ? "" : packet.GetProperty("Path").GetString();
            answer = (scenario, path) switch {
                ("comment", "Fixtures/Dataset05.cs") => $"BUG\nFILE: {path}\nLINE: 1107\nSIDE: NEW\nDESCRIPTION: {CommentClaim}\nEND",
                ("placeholder", "Program.cs") => $"BUG\nFILE: {path}\nLINE: 4\nSIDE: HUNK\nDESCRIPTION: {Placeholder}\nEND",
                ("caller", "Fixtures/Dataset03.cs") => $"BUG\nFILE: {path}\nLINE: 1\nSIDE: OLD\nDESCRIPTION: {MissingType}\nEND",
                _ => "NO_BUGS"
            };
        }
        else
        {
            audits++;
            var item = data.GetProperty("candidates").EnumerateArray().Single();
            var sources = item.GetProperty("sources").EnumerateArray().ToArray();
            string Line(string file, string side, int n) => sources.First(s => s.GetProperty("File").GetString() == file &&
                s.GetProperty("Side").GetString() == side && s.GetProperty("Lines").TryGetProperty(n.ToString(), out _))
                .GetProperty("Lines").GetProperty(n.ToString()).GetString()!.Trim();
            object decision;
            if (scenario == "comment")
            {
                var quote = Line("Fixtures/Dataset05.cs", "NEW", 1107);
                if (!quote.StartsWith("// xxx") || !sources.Any(s => s.GetProperty("Side").GetString()=="OLD" &&
                    s.GetProperty("Status").GetString()=="not_found")) throw new Exception("Missing addition evidence.");
                decision = audits == 1 ? new { id=1, action="keep", file="Fixtures/Dataset05.cs", side="NEW", line=1107, quote,
                    condition="compiling", consequence="syntax error", evidence="This is a valid comment and does not cause a syntax error.", contradicted=false } :
                    (object)new { id=1, action="unsupported", contradicted=true, file="Fixtures/Dataset05.cs", side="NEW", line=1107, quote, evidence="The alleged code is a // comment." };
            }
            else if (scenario == "placeholder")
            {
                var quote = Line("Program.cs", "NEW", 4);
                decision = audits == 1 ? new { id=1, action="read", file="Program.cs", side="NEW", line=4, evidence="Inspect the caller." } :
                    (object)new { id=1, action="keep", file="Program.cs", side="NEW", line=4, quote,
                        condition="compiling", consequence="unspecified error", contradicted=false, evidence="Source was read." };
            }
            else
            {
                decision = audits == 1 ? new { id=1, action="read", file="Program.cs", side="NEW", line=4, evidence="Locate use of the deleted type." } :
                    (object)new { id=1, action="correct", file="Program.cs", side="NEW", line=4, quote=Line("Program.cs","NEW",4),
                        condition="Compiling the caller after deletion", consequence="Missing type prevents the build",
                        contradicted=false, evidence="Program.cs still uses the deleted declaration." };
            }
            answer = JsonSerializer.Serialize(new[] {decision});
        }
        return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
            choices = new[] { new { finish_reason="stop", message=new { content=answer } } }
        })) };
    }
}
