using System.Text;
using KommitEye.Models;

namespace KommitEye.Services;

public static class ReportRenderer
{
    public static string Render(string branch, CommitInfo commit, ReviewResult result, bool manualReview)
    {
        var text = new StringBuilder();

        text.AppendLine($"<!-- commit:{commit.Sha} -->")
            .AppendLine($"## `{commit.Sha[..Math.Min(8, commit.Sha.Length)]}` - {Escape(commit.Subject)}{ManualLabel(manualReview)}")
            .AppendLine()
            .AppendLine(Localization.Format("- Branch: {0}", "- Ветка: {0}", Escape(branch)))
            .AppendLine(Localization.Format("- Author: {0}", "- Автор: {0}", Escape(commit.Author)))
            .AppendLine(Localization.Format(
                "- Date: {0:yyyy-MM-dd HH:mm:ss zzz}",
                "- Дата: {0:yyyy-MM-dd HH:mm:ss zzz}",
                commit.Date))
            .AppendLine(Localization.Format("- Result: {0}", "- Результат: {0}", DescribeResult(result)))
            .AppendLine();

        if (result.AnalysisDuration is { } duration)
        {
            var elapsed = FormattableString.Invariant($"{(long)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}.{duration.Milliseconds:000}");
            text.AppendLine(Localization.Format("- Analysis duration: {0}", "- Длительность анализа: {0}", elapsed)).AppendLine();
        }

        foreach (var finding in result.Findings)
        {
            var line = finding.Line?.ToString() ?? Localization.Text("line unknown", "строка неизвестна");
            var side = finding.Side.Equals("OLD", StringComparison.OrdinalIgnoreCase)
                ? Localization.Text(", deleted line", ", удаленная строка")
                : finding.Side.Equals("HUNK", StringComparison.OrdinalIgnoreCase)
                    ? Localization.Text(", changed block", ", измененный блок")
                    : string.Empty;
            text.AppendLine($"### `{Escape(finding.File)}:{line}`{side}")
                .AppendLine()
                .AppendLine(Escape(finding.Description))
                .AppendLine();
        }

        if (result.UnstructuredResponse is not null)
        {
            text.AppendLine(Localization.Text(
                    "### Unstructured model response",
                    "### Неструктурированный ответ модели"))
                .AppendLine()
                .AppendLine(Localization.Text(
                    "The response could not be fully parsed and was preserved:",
                    "Ответ не удалось полностью разобрать, поэтому он сохранен целиком:"))
                .AppendLine()
                .AppendLine("```text")
                .AppendLine(result.UnstructuredResponse.Replace("```", "'''"))
                .AppendLine("```")
                .AppendLine();
        }

        return text.ToString();
    }

    private static string DescribeResult(ReviewResult result)
    {
        if (result.Failure is not null)
            return Localization.Text("review failed; skipped: ", "проверка не выполнена; пропущен: ") + Escape(result.Failure);
        if (result.EmptyDiff)
            return Localization.Text("empty diff; no model request", "пустой diff; запрос к модели не выполнялся");
        if (result.Findings.Count > 0)
            return Localization.Format(
                "potential bugs: {0}",
                "потенциальных ошибок: {0}",
                result.Findings.Count);
        return result.UnstructuredResponse is null
            ? Localization.Text("no bugs found", "ошибок не найдено")
            : Localization.Text("unstructured model response", "неструктурированный ответ модели");
    }

    private static string ManualLabel(bool manualReview) => manualReview
        ? Localization.Text(" (manual review)", " (ручная проверка)")
        : string.Empty;

    private static string Escape(string value) => value.Replace("\r", " ").Replace("\n", " ");

}
