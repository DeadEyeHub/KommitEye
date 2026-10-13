using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace KommitEye.Server;

// Render the report writer's Markdown subset. All model/Git text stays inert:
// no raw HTML, external resources, links or executable content are accepted.
public static class HtmlReport
{
    private static string Inline(string value)
    {
        value = Regex.Replace(value, @"\\([\\`*_{}\[\]()#+\-.!>])", "$1");
        return Regex.Replace(WebUtility.HtmlEncode(value), "`([^`]+)`", "<code>$1</code>");
    }
    public static string Render(string title, string markdown)
    {
        var body = new StringBuilder();
        var code = false; var list = false;
        foreach (var line in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith("```"))
            {
                if (list) { body.AppendLine("</ul>"); list = false; }
                body.AppendLine(code ? "</code></pre>" : "<pre><code>"); code = !code; continue;
            }
            if (code) { body.AppendLine(WebUtility.HtmlEncode(line)); continue; }
            if (Regex.IsMatch(line, @"^<!-- commit:[a-fA-F0-9]+ -->$")) continue;
            if (line.StartsWith("- "))
            {
                if (!list) { body.AppendLine("<ul>"); list = true; }
                body.Append("<li>").Append(Inline(line[2..])).AppendLine("</li>"); continue;
            }
            if (list) { body.AppendLine("</ul>"); list = false; }
            if (string.IsNullOrWhiteSpace(line)) continue;
            var heading = Regex.Match(line, @"^(#{1,6}) (.*)$");
            if (heading.Success)
            {
                var level = heading.Groups[1].Length;
                body.Append($"<h{level}>").Append(Inline(heading.Groups[2].Value)).AppendLine($"</h{level}>");
            }
            else body.Append("<p>").Append(Inline(line)).AppendLine("</p>");
        }
        if (code) body.AppendLine("</code></pre>");
        if (list) body.AppendLine("</ul>");
        return "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
            "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'\">" +
            "<title>" + WebUtility.HtmlEncode(title) + "</title><style>" +
            "body{font:16px/1.6 system-ui,sans-serif;color:#172c2d;background:#fff;max-width:960px;margin:40px auto;padding:0 24px;overflow-wrap:anywhere}" +
            "h1,h2,h3{line-height:1.3}h2{margin-top:40px;padding-top:20px;border-top:1px solid #ccd7d3}h3{margin-top:24px}" +
            "pre{white-space:pre-wrap;padding:16px;background:#f3f5f2}code{font-family:ui-monospace,monospace}li{margin:4px 0}" +
            "@media print{body{max-width:none;margin:0}h2,h3{break-after:avoid}}" +
            "</style></head><body>" + body + "</body></html>";
    }
}
