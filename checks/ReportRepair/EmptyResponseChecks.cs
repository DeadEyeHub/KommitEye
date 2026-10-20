using System.Net;
using System.Text;
using System.Text.Json;
using KommitEye.Models;
using KommitEye.Services;

static class EmptyResponseChecks
{
    public static async Task RunAsync(string repo, string sha, string finding)
    {
        foreach (var mode in new[] { "reasoning", "reasoning_content", "empty", "whitespace", "null", "missing", "repeat", "budget", "repair-budget", "deadline" })
        {
            var profile = new ModelProfile { Model = "fixture", Endpoint = "https://fixture.invalid/v1" };
            profile.Parameters.MaxRounds = mode == "budget" ? 2 : 3;
            var clock = new EmptyClock();
            var handler = new EmptyHandler(mode, finding, clock);
            using var http = new HttpClient(handler);
            using var tools = await GitToolSession.CreateAsync(repo, sha, default);
            var logs = new List<string>();
            var requests = 0;
            var usage = 0;
            Exception? failure = null;
            string? result = null;
            try
            {
                result = await new ModelClient(http, clock).ReviewAsync(profile, tools, "main", "Review", default,
                    log: logs.Add, usage: value => { if (value is not null) usage++; }, beforeRequest: () => requests++);
            }
            catch (Exception error) { failure = error; }
            if (mode is "repeat" or "budget") Check(failure is EmptyReviewResponseException, mode + ": separate empty error");
            else if (mode == "repair-budget") Check(failure is ReviewFormatException, "format repair obeys shared request limit");
            else if (mode == "deadline") Check(failure is ReviewComplexityException, "recovery obeys original deadline");
            else Check(failure is null && result == finding, mode + ": recovered original findings: " + failure);
            Check(handler.Requests == (mode == "budget" ? 2 : 3) && requests == handler.Requests, mode + ": bounded requests");
            Check(logs.Any(x => x.Contains("Review round 2/") && x.Contains("empty-id") && x.Contains("finish_reason")), mode + ": round diagnostics");
            Check(logs.Any(x => x.Contains("Empty report recovery")), mode + ": recovery diagnostics");
            Check(usage > 0, mode + ": usage retained");
            Console.WriteLine("PASS: empty response " + mode);
        }

        // Verify the runner persists a failure and never restarts a fresh analysis or saves NO_BUGS.
        await new StateStore().SaveAsync(new RepositoryState(), default);
        var failedHandler = new EmptyHandler("repeat", finding, new EmptyClock());
        using var failedHttp = new HttpClient(failedHandler);
        var runner = new ReviewRunner(new GitService(), new ModelClient(failedHttp), new ConfigurationStore(), new StateStore(), new ReportWriter());
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failures = 0;
        var successes = 0;
        runner.Reviewed += review => { if (review.Failed) failures++; else successes++; };
        runner.Log += line => { if (line.Contains("failure saved in report")) saved.TrySetResult(); };
        runner.Start(new AppSettings { RepositoryPath = repo, BranchRef = "refs/heads/main", PullEnabled = false, PollIntervalSeconds = 60 },
            new ModelProfile { Model = "fixture", Endpoint = "https://fixture.invalid/v1" });
        try { await saved.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
        finally { await runner.StopAsync(); }
        Check(failedHandler.Requests == 3 && failures == 1 && successes == 0, "empty failure must not restart analysis");
        var report = new ReportWriter().Read(new ReportWriter().List().Single().Name)!;
        Check(report.Contains("without a final report") && !report.Contains("no bugs found"), "empty failure persisted without false NO_BUGS");
        Console.WriteLine("PASS: empty response runner failure without false NO_BUGS");
    }

    static void Check(bool condition, string name) { if (!condition) throw new Exception(name); }
}

sealed class EmptyHandler(string mode, string finding, EmptyClock clock) : HttpMessageHandler
{
    public int Requests;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        if (!body.RootElement.TryGetProperty("tools", out _)) throw new Exception("Empty response sent to isolated format repair");
        Requests++;
        if (Requests == 1) return Json("""{"choices":[{"message":{"content":null,"tool_calls":[{"id":"diff","type":"function","function":{"name":"git_diff","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}""");
        if (Requests >= 3)
        {
            var messages = body.RootElement.GetProperty("messages");
            if (!messages.EnumerateArray().Any(x => x.GetProperty("role").GetString() == "tool" && x.GetProperty("tool_call_id").GetString() == "diff") ||
                !messages.ToString().Contains("previous response finished without") || messages.ToString().Contains("im_end"))
                throw new Exception("Recovery lost tool context or replayed failed reasoning");
            if (mode == "deadline")
            {
                clock.Expire();
                token.ThrowIfCancellationRequested();
                throw new Exception("Review deadline was reset");
            }
            if (mode != "repeat") return Json(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = mode == "repair-budget" ? finding[..^4] : finding } } } }));
        }
        var field = mode == "reasoning_content" ? "reasoning_content" : "reasoning";
        var delta = new Dictionary<string, object?> { [field] = "loop loop <|im_end|>" };
        if (mode is "empty" or "repeat") delta["content"] = "";
        if (mode == "whitespace") delta["content"] = " \r\n\t";
        if (mode == "null") delta["content"] = null;
        if (mode is "null" or "missing") return Json(JsonSerializer.Serialize(new { id = "empty-id", usage = new { prompt_tokens = 10, completion_tokens = 5, total_tokens = 15 }, choices = new[] { new { finish_reason = "stop", message = delta } } }));
        var packet = JsonSerializer.Serialize(new { id = "empty-id", choices = new[] { new { index = 0, delta, finish_reason = "stop" } } });
        var sse = "data: " + packet + "\n\ndata: {\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5,\"total_tokens\":15}}\n\ndata: [DONE]\n\n";
        return new(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
    }
    static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
}

sealed class EmptyClock : TimeProvider
{
    private Timer? _timer;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        => _timer = new Timer(callback, state);
    public void Expire() => _timer!.Fire();
    private sealed class Timer(TimerCallback callback, object? state) : ITimer
    {
        public void Fire() => callback(state);
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
