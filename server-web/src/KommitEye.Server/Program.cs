using System.Security.Claims;
using System.Threading.RateLimiting;
using KommitEye.Models;
using KommitEye.Services;
using KommitEye.Server;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;

var dataArgument = Array.IndexOf(args, "--data-dir");
if (dataArgument >= 0)
{
    if (dataArgument + 1 >= args.Length) throw new ArgumentException("--data-dir requires a path.");
    Environment.SetEnvironmentVariable("KOMMITEYE_DATA_DIR", Path.GetFullPath(args[dataArgument + 1]));
}
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KOMMITEYE_DATA_DIR")))
    Environment.SetEnvironmentVariable("KOMMITEYE_DATA_DIR", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KommitEye.Server"));
AppPaths.EnsureCreated();
// The server is the exclusive owner of its data directory, including the legacy JSON stores.
using var dataLock = new FileStream(Path.Combine(AppPaths.DataDirectory, "server.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
if (string.IsNullOrEmpty(builder.Configuration["urls"])) builder.WebHost.UseUrls("http://0.0.0.0:5080");
builder.Services.AddWindowsService(options => options.ServiceName = "KommitEye");
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(AppPaths.DataDirectory, "session-keys"))).ProtectKeysWithDpapi();
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
builder.Services.AddSingleton<ConfigurationStore>(); builder.Services.AddSingleton<ModelClient>();
builder.Services.AddSingleton<StateStore>(); builder.Services.AddSingleton<ReportWriter>();
builder.Services.AddSingleton(new JobStore(AppPaths.DataDirectory));
builder.Services.AddSingleton<OAuthOperations>();
builder.Services.AddSingleton(sp => sp.GetRequiredService<OAuthOperations>().CreateMail());
builder.Services.AddSingleton<ProjectCoordinator>();
builder.Services.AddSingleton<SettingsApi>();
var app = builder.Build();
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
app.MapPost("/api/v1/login", async (LoginRequest request, HttpContext context, ServerSecurity security) =>
{
    if (!security.Verify(request.Password)) return Results.Unauthorized();
    await context.SignInAsync(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], CookieAuthenticationDefaults.AuthenticationScheme)));
    return Results.Ok(new { user = "admin" });
}).RequireRateLimiting("login");
var api = app.MapGroup("/api/v1").RequireAuthorization();
api.MapPost("/oauth/{provider}/connect", (string provider, OAuthRequest request, OAuthOperations oauth) => oauth.Begin(provider, request.Configuration));
api.MapGet("/oauth/{id}", (string id, OAuthOperations oauth) => oauth.Get(id));
app.MapGet("/api/v1/oauth/google/callback", (string state, string? code, string? error, OAuthOperations oauth) =>
    oauth.Callback(state, code, error) ? Results.Text("Sign-in received. Return to KommitEye.") : Results.BadRequest());
api.MapGet("/session", () => new { user = "admin" });
api.MapPost("/logout", async (HttpContext context) => { await context.SignOutAsync(); return Results.NoContent(); });
api.MapGet("/system/info", () => new { version = typeof(ProjectCoordinator).Assembly.GetName().Version?.ToString(), apiVersion = 1, machine = Environment.MachineName, dataDirectory = AppPaths.DataDirectory });
api.MapGet("/projects", (ProjectCoordinator coordinator) => coordinator.SnapshotAsync());
api.MapGet("/usage", (ProjectCoordinator coordinator) => coordinator.Usage());
api.MapPost("/repositories/branches", async (RepositoryRequest request, CancellationToken token) => await new GitService().GetBranchesAsync(request.Path, token));
api.MapPost("/projects/{id}/start-commit", async (string id, StartRequest request, ProjectCoordinator coordinator) =>
{ await coordinator.SetStartCommitAsync(id, request.Sha ?? throw new ArgumentException("SHA required.")); return Results.Ok(); });
api.MapPost("/projects", async (ProjectUpdate request, ProjectCoordinator coordinator) => { RepositoryApi.ValidateMode(request.Settings); await coordinator.SaveProjectAsync(request.Settings, request.Revision); return Results.Ok(); });
api.MapPost("/projects/{id}/start", async (string id, StartRequest request, ProjectCoordinator coordinator) =>
    Results.Accepted(value: await coordinator.StartAsync(id, null, request.RequestKey)));
api.MapPost("/projects/{id}/reviews", async (string id, StartRequest request, ProjectCoordinator coordinator) =>
    Results.Accepted(value: await coordinator.StartAsync(id, request.Sha ?? throw new ArgumentException("SHA required."), request.RequestKey)));
api.MapPost("/projects/{id}/stop", async (string id, ProjectCoordinator coordinator) => { await coordinator.StopAsync(id); return Results.Ok(); });
api.MapGet("/projects/{id}/log", (string id, bool? details, ProjectCoordinator coordinator) => Results.Text(coordinator.Log(id, details == true)));
api.MapPost("/projects/{id}/log/clear", (string id, bool? details, ProjectCoordinator coordinator) => { coordinator.ClearLog(id, details == true); return Results.Ok(); });
api.MapGet("/jobs", (JobStore jobs) => jobs.List().Take(200));
api.MapGet("/jobs/{id}", (string id, JobStore jobs) => jobs.List().FirstOrDefault(j => j.Id == id) is { } job ? Results.Ok(job) : Results.NotFound());
api.MapGet("/reports", () => Directory.GetFiles(AppPaths.ReportsDirectory, "*.md").Select(path => new { name = Path.GetFileName(path), modified = File.GetLastWriteTimeUtc(path), size = new FileInfo(path).Length }).OrderByDescending(r => r.modified));
api.MapGet("/reports/{name}", (string name) =>
{
    if (name != Path.GetFileName(name) || !name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return Results.BadRequest();
    var path = Path.Combine(AppPaths.ReportsDirectory, name);
    return File.Exists(path) ? Results.File(path, "text/plain; charset=utf-8") : Results.NotFound();
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
await app.RunAsync();

public sealed class EventsHub : Hub;
public sealed record LoginRequest(string Password);
public sealed record ProjectUpdate(long Revision, AppSettings Settings);
public sealed record StartRequest(string RequestKey, string? Sha);
public sealed record OAuthRequest(string Configuration);
public sealed record RepositoryRequest(string Path);
