using KommitEye.Models;
using KommitEye.Services;

static class OutcomeChecks
{
    public static void Run(string directory)
    {
        Directory.CreateDirectory(Path.Combine(directory, "reports"));
        var samples = new[] {
            ("potential bugs: 2", "bug"), ("потенциальных ошибок: 1", "bug"),
            ("no bugs found", "clean"), ("ошибок не найдено", "clean"),
            ("review failed; skipped: timeout", "failed"), ("проверка не выполнена; пропущен: timeout", "failed"),
            ("empty diff; no model request", "empty"), ("пустой diff; запрос к модели не выполнялся", "empty"),
            ("unstructured model response", "unknown"), ("неструктурированный ответ модели", "unknown"),
            ("potential bugs: not a number", "unknown"), ("unrecognized", "unknown") };
        for (var i = 0; i < samples.Length; i++)
        {
            var tick = (char)96;
            File.WriteAllText(Path.Combine(directory, "reports", $"legacy-{i}.md"),
                $"<!-- commit:{i:x40} -->\n## {tick}{i:x8}{tick} - Fixture\n- Результат: {samples[i].Item1}\n\n### Model text\n- Result: no bugs found\n");
        }
        var store = new ReportStore(directory);
        for (var i = 0; i < samples.Length; i++)
            Check(store.Browse(reportName: $"legacy-{i}.md").Items.Single().Outcome == samples[i].Item2, "legacy outcome " + i);
        var commit = new CommitInfo(new string('f', 40), "Fixture", DateTimeOffset.UtcNow, "Outcome fixture");
        var bug = new ReviewResult();
        bug.Findings.Add(new("file.cs", 1, "NEW", "Bug"));
        void Save(ReviewResult result) => store.Save("current.md", "current.md", directory, "main", commit,
            result, false, ReportRenderer.Render("main", commit, result, false), default);
        foreach (var (result, expected) in new[] {
            (bug, "bug"), (new ReviewResult(), "clean"), (new ReviewResult { Failure = "timeout" }, "failed"),
            (new ReviewResult { UnstructuredResponse = "no bugs found" }, "unknown"),
            (new ReviewResult { EmptyDiff = true }, "empty") })
        {
            Save(result);
            var page = store.Browse(reportName: "current.md", outcome: expected);
            Check(page.Total == 1 && page.Items.Single().Outcome == expected, "current outcome " + expected);
            Check(store.Browse(reportName: "current.md", outcome: expected == "bug" ? "clean" : "bug").Total == 0,
                "repeat replaces outcome without retaining previous findings");
        }
        var bugs = store.Browse(outcome: "bug", limit: 1);
        Check(bugs.Total == 2 && bugs.Items.Count == 1 && store.Browse(outcome: "bug", offset: 1, limit: 1).Items.Count == 1,
            "outcome filter applies before count and pagination");
        Check(store.Browse(query: "Fixture", reportName: "legacy-0.md", outcome: "bug").Total == 1,
            "outcome combines with text and branch filters");
        Check(store.Browse(sort: "outcome").Total == samples.Length + 1, "outcome sorting");
        try { store.Browse(outcome: "invalid"); throw new Exception("Invalid outcome accepted"); }
        catch (ArgumentException) { }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
}
