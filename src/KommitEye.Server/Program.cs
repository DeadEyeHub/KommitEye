using KommitEye.Platform;
using KommitEye.Server.Hosting;
using System.Security.Claims;
using System.Threading.RateLimiting;
using KommitEye.Models;
using KommitEye.Services;
using KommitEye.Server;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.SignalR;

if (WindowsServiceCommands.Run(args) is { } serviceExitCode)
{ Environment.ExitCode = serviceExitCode; return; }

try
{
    await RunServerAsync(args);
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
{
    var message = "Server startup/access failure: " + error.Message + " Verify the running account can read configuration and modify data; permissions were not changed.";
    Console.Error.WriteLine(message);
    if (OperatingSystem.IsWindows())
        try { System.Diagnostics.EventLog.WriteEntry(".NET Runtime", message, System.Diagnostics.EventLogEntryType.Error); } catch { }
    Environment.ExitCode = 1;
}

static async Task RunServerAsync(string[] args)
{
var hostOptions = ServerHostOptions.Load(args);
if (args.Contains("--initialize-config")) return;
Environment.SetEnvironmentVariable("KOMMITEYE_DATA_DIR", hostOptions.DataDirectory);
if (hostOptions.GitExecutable is { } gitPath)
    Environment.SetEnvironmentVariable("PATH", Path.GetDirectoryName(gitPath) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
AppPaths.EnsureCreated();
// The server is the exclusive owner of its data directory, including the legacy JSON stores.
using var dataLock = new FileStream(Path.Combine(AppPaths.DataDirectory, "server.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.WebHost.UseUrls(hostOptions.Urls!);
using var platform = ServerPlatform.Configure(builder, AppPaths.DataDirectory, hostOptions.CredentialProtection);
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "KommitEye.Session"; options.Cookie.HttpOnly = true; options.Cookie.SameSite = SameSiteMode.Strict;
    options.ExpireTimeSpan = TimeSpan.FromHours(8); options.SlidingExpiration = true;
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorization();
builder.Services.AddSignalR();
builder.Services.AddRateLimiter(options => options.AddPolicy("login", context =>
    RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new()
    { PermitLimit = 8, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));
builder.Services.AddSingleton(new ServerSecurity(AppPaths.DataDirectory));
builder.Services.AddSingleton(new ConfigurationStore { AllowEmptyProjects = true }); builder.Services.AddSingleton<ModelClient>();
builder.Services.AddSingleton<StateStore>(); builder.Services.AddSingleton<ReportWriter>();
builder.Services.AddSingleton(new JobStore(AppPaths.DataDirectory));
builder.Services.AddSingleton<OAuthOperations>();
builder.Services.AddSingleton(sp => sp.GetRequiredService<OAuthOperations>().CreateMail());
builder.Services.AddSingleton<ProjectCoordinator>();
builder.Services.AddSingleton<SettingsApi>();
await using var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; connect-src 'self' ws: wss:; style-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    // Cross-site forms cannot issue commands, including login; CORS is deliberately not enabled.
    if (context.Request.Path.StartsWithSegments("/api") && context.Request.Method is not ("GET" or "HEAD") &&
        context.Request.Headers["X-KommitEye-Client"] != "web")
    { context.Response.StatusCode = 403; return; }
    if (context.Request.Path != "/api/v1/oauth/google/callback" && context.Request.Headers["Sec-Fetch-Site"] == "cross-site" &&
        (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/hubs")))
    { context.Response.StatusCode = 403; return; }
    if (context.Request.Path.StartsWithSegments("/hubs") && context.Request.Headers.Origin is { Count: > 0 } origins &&
        (!Uri.TryCreate(origins[0], UriKind.Absolute, out var origin) || !string.Equals(origin.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase)))
    { context.Response.StatusCode = 403; return; }
    try { await next(); }
    catch (Exception error) when (error is ArgumentException or InvalidOperationException or KeyNotFoundException or GitException or System.ComponentModel.DataAnnotations.ValidationException)
    {
        context.Response.StatusCode = error is KeyNotFoundException ? 404 : error is InvalidOperationException ? 409 : 400;
        await context.Response.WriteAsJsonAsync(new { error = GitService.SanitizeDiagnostic(error.Message) });
    }
    catch (Exception error) when (!context.Response.HasStarted)
    {
        context.Response.StatusCode = 500;
        Console.Error.WriteLine("Server operation failed: " + error.GetType().Name);
        await context.Response.WriteAsJsonAsync(new { error = "Operation failed. Check server configuration and file permissions." });
    }
});
app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter();
app.MapGet("/api/v1/setup", (HttpContext context, ServerSecurity security) => new { required = security.NeedsSetup,
    local = context.Connection.RemoteIpAddress is { } address && System.Net.IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address) });
app.MapPost("/api/v1/setup", async (LoginRequest request, HttpContext context, ServerSecurity security) =>
{
    var address = context.Connection.RemoteIpAddress;
    if (address is null || !System.Net.IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address))
        return Results.Json(new { error = "Open localhost on the server computer to create the first administrator password." }, statusCode: 403);
    security.Setup(request.Password);
    await context.SignInAsync(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], CookieAuthenticationDefaults.AuthenticationScheme)));
    return Results.Ok();
}).RequireRateLimiting("login");
app.MapPost("/api/v1/login", async (LoginRequest request, HttpContext context, ServerSecurity security) =>
{
    if (!security.Verify(request.Password)) return Results.Unauthorized();
    await context.SignInAsync(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], CookieAuthenticationDefaults.AuthenticationScheme)));
    return Results.Ok(new { user = "admin" });
}).RequireRateLimiting("login");
var api = app.MapGroup("/api/v1").RequireAuthorization();
api.MapGet("/mail/log", (OAuthOperations oauth) => Results.Text(oauth.MailLog.Snapshot(true) ?? ""));
api.MapPost("/oauth/{provider}/connect", (string provider, OAuthRequest request, OAuthOperations oauth) => oauth.Begin(provider, request.Configuration));
api.MapGet("/oauth/{id}", (string id, OAuthOperations oauth) => oauth.Get(id));
app.MapGet("/api/v1/oauth/google/callback", (string state, string? code, string? error, OAuthOperations oauth) =>
    oauth.Callback(state, code, error) ? Results.Text("Sign-in received. Return to KommitEye.") : Results.BadRequest());
api.MapGet("/session", () => new { user = "admin" });
api.MapPost("/logout", async (HttpContext context) => { await context.SignOutAsync(); return Results.NoContent(); });
api.MapGet("/system/info", () => new { version = typeof(EventsHub).Assembly.GetName().Version?.ToString(3), apiVersion = 1, machine = Environment.MachineName, dataDirectory = AppPaths.DataDirectory });
api.MapGet("/projects", (ProjectCoordinator coordinator) => coordinator.SnapshotAsync());
api.MapGet("/usage", (ProjectCoordinator coordinator) => coordinator.Usage());
api.MapPost("/repositories/branches", async (RepositoryRequest request, CancellationToken token) => await new GitService().GetBranchesAsync(request.Path, token));
api.MapPost("/projects/{id}/start-commit", async (string id, StartRequest request, ProjectCoordinator coordinator) =>
{ await coordinator.SetStartCommitAsync(id, request.Sha ?? throw new ArgumentException("SHA required.")); return Results.Ok(); });
api.MapPost("/projects", async (ProjectUpdate request, ProjectCoordinator coordinator) => { RepositoryApi.ValidateMode(request.Settings); await coordinator.SaveProjectAsync(request.Settings, request.Revision); return Results.Ok(); });
api.MapPost("/projects/{id}/delete", async (string id, ProjectDelete request, ProjectCoordinator coordinator) => { await coordinator.DeleteProjectAsync(id, request.Revision); return Results.Ok(); });
api.MapPost("/projects/{id}/start", async (string id, StartRequest request, ProjectCoordinator coordinator) =>
    Results.Accepted(value: await coordinator.StartAsync(id, null, request.RequestKey)));
api.MapPost("/projects/{id}/reviews", async (string id, StartRequest request, ProjectCoordinator coordinator) =>
    Results.Accepted(value: await coordinator.StartAsync(id, request.Sha ?? throw new ArgumentException("SHA required."), request.RequestKey)));
api.MapPost("/projects/{id}/stop", async (string id, ProjectCoordinator coordinator) => { await coordinator.StopAsync(id); return Results.Ok(); });
api.MapGet("/projects/{id}/log", (string id, bool? details, ProjectCoordinator coordinator) => Results.Text(coordinator.Log(id, details == true)));
api.MapPost("/projects/{id}/log/clear", (string id, bool? details, ProjectCoordinator coordinator) => { coordinator.ClearLog(id, details == true); return Results.Ok(); });
api.MapGet("/jobs", (JobStore jobs) => jobs.List().Take(200));
api.MapGet("/jobs/{id}", (string id, JobStore jobs) => jobs.List().FirstOrDefault(j => j.Id == id) is { } job ? Results.Ok(job) : Results.NotFound());
api.MapGet("/reports", (ReportWriter reports) => reports.List());
api.MapGet("/report-entries", (string? q, string? sort, bool? descending, int? offset, int? limit, string? report, string? outcome, ReportWriter reports) =>
{
    try { return Results.Ok(reports.Browse(q, sort ?? "saved", descending ?? true, offset ?? 0, limit ?? 50, report, outcome)); }
    catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
});
api.MapGet("/report-entries/{id:long}", (long id, string? format, bool? download, ReportWriter reports) =>
{
    var markdown = reports.ReadReview(id);
    if (markdown is null) return Results.NotFound();
    var name = $"review-{id}";
    if (format == "fragment") return Results.Text(HtmlReport.Render(name, markdown, fragment: true), "text/html; charset=utf-8");
    if (format == "html") return Results.File(System.Text.Encoding.UTF8.GetBytes(HtmlReport.Render(name, markdown)),
        "text/html; charset=utf-8", download == true ? name + ".html" : null);
    return Results.File(System.Text.Encoding.UTF8.GetBytes(markdown), "text/plain; charset=utf-8",
        download == true ? name + ".md" : null);
});
api.MapGet("/reports/{name}", (string name, string? format, bool? download, ReportWriter reports) =>
{
    if (name != Path.GetFileName(name) || !name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return Results.BadRequest();
    var markdown = reports.Read(name);
    if (markdown is null) return Results.NotFound();
    if (format == "fragment") return Results.Text(HtmlReport.Render(name, markdown, fragment: true), "text/html; charset=utf-8");
    if (format == "html") return Results.File(System.Text.Encoding.UTF8.GetBytes(HtmlReport.Render(name, markdown)), "text/html; charset=utf-8", download == true ? Path.ChangeExtension(name, ".html") : null);
    return Results.File(System.Text.Encoding.UTF8.GetBytes(markdown), "text/plain; charset=utf-8", download == true ? name : null);
});
api.MapGet("/projects/{id}/commits", async (string id, int? offset, string? head, ProjectCoordinator coordinator) =>
{
    var project = (await coordinator.SnapshotAsync()).Projects.Single(p => p.ProjectId == id);
    var git = new GitService();
    head = string.IsNullOrEmpty(head) ? await git.GetBranchHeadAsync(project.RepositoryPath, project.BranchRef, default) : await git.ResolveCommitAsync(project.RepositoryPath, head, default);
    var commits = await git.GetCommitPageAsync(project.RepositoryPath, head, Math.Max(0, offset ?? 0), 101, default);
    return new { head, commits = commits.Take(100), hasMore = commits.Count > 100 };
});
api.MapGet("/projects/{id}/branches", async (string id, ProjectCoordinator coordinator) =>
{
    var project = (await coordinator.SnapshotAsync()).Projects.Single(p => p.ProjectId == id);
    return await new GitService().GetBranchesAsync(project.RepositoryPath, default);
});
SettingsApi.Map(api);
RepositoryApi.Map(api);
var coordinator = app.Services.GetRequiredService<ProjectCoordinator>();
var hub = app.Services.GetRequiredService<IHubContext<EventsHub>>();
coordinator.Changed += value => { _ = Broadcast(value); };
async Task Broadcast(ServerEvent value) { try { await hub.Clients.All.SendAsync("changed", value); } catch (Exception e) { app.Logger.LogDebug(e, "Event client disconnected"); } }
app.MapHub<EventsHub>("/hubs/events").RequireAuthorization();
app.UseDefaultFiles(); app.UseStaticFiles();
app.MapFallbackToFile("index.html");
await app.StartAsync();
await coordinator.StartConfiguredProjectsAsync(app.Lifetime.ApplicationStopping);
await app.WaitForShutdownAsync();
}

public sealed class EventsHub : Hub;
public sealed record LoginRequest(string Password);
public sealed record ProjectUpdate(long Revision, AppSettings Settings);
public sealed record ProjectDelete(long Revision);
public sealed record StartRequest(string RequestKey, string? Sha);
public sealed record OAuthRequest(string Configuration);
public sealed record RepositoryRequest(string Path);
