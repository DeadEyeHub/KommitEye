using System.Security.Claims;
using System.Text.Json;
using KommitEye.Services;
using Microsoft.AspNetCore.Authentication;

namespace KommitEye.Server;

public static class AuthorApi
{
    public const string Scheme = "Author";
    public static void Map(WebApplication app, RouteGroupBuilder admin)
    {
        admin.MapGet("/author-settings", (AuthorAccess access) => access.Snapshot());
        admin.MapPost("/author-settings", (AuthorSettingsUpdate request, AuthorAccess access, MailNotifications mail) =>
        {
            access.Update(request.Settings, request.Revision);
            mail.AuthorPortalEnabled = access.Settings.Enabled;
            return Results.Ok();
        });
        admin.MapGet("/report-entries/{id:long}/feedback", (long id, ReportStore reports) =>
            reports.AuthorDetail(id, null) is { } detail ? Results.Ok(detail) : Results.NotFound());

        var auth = app.MapGroup("/api/v1/author/auth").RequireRateLimiting("author-login");
        auth.MapGet("/info", (AuthorAccess access) => new { enabled = access.Settings.Enabled });
        auth.MapPost("/request", async (AuthorEmail request, AuthorAccess access, ReportStore reports, MailNotifications mail, CancellationToken token) =>
        {
            var email = AuthorAccess.Normalize(request.Email);
            var issued = access.Issue(email);
            if (issued is { } code && reports.HasAuthor(email))
                await mail.SendAuthorCodeAsync(email, code.Code, code.Expires, token);
            return Results.Ok(new { message = "If this address has reviews and sending limits allow it, a code has been sent.", resendSeconds = access.Settings.ResendSeconds });
        });
        auth.MapPost("/verify", async (AuthorCode request, HttpContext context, AuthorAccess access, ReportStore reports) =>
        {
            var email = AuthorAccess.Normalize(request.Email);
            var ticket = request.Code is not null && request.Code.Length <= 32 ? access.Authenticate(email, request.Code) : null;
            if (ticket is null || !reports.HasAuthor(email))
                return Results.Json(new { error = "Код неверен или истёк. / Invalid or expired code." }, statusCode: 401);
            var principal = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.Email, email), new Claim("version", ticket.Value.Version)], Scheme));
            await context.SignInAsync(Scheme, principal, new AuthenticationProperties {
                IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(ticket.Value.Hours), AllowRefresh = false });
            return Results.Ok(new { email });
        });
        var api = app.MapGroup("/api/v1/author").RequireAuthorization("Author");
        api.MapGet("/session", (HttpContext context) => new { email = Email(context) });
        api.MapPost("/logout", async (HttpContext context) => { await context.SignOutAsync(Scheme); return Results.NoContent(); });
        api.MapGet("/reviews", (HttpContext context, ReportStore reports, int? offset, string? filter) =>
        {
            var items = reports.AuthorReviews(Email(context), offset ?? 0, filter ?? "");
            return new { items = items.Take(50), hasMore = items.Count > 50 };
        });
        api.MapGet("/reviews/{id:long}", (long id, HttpContext context, ReportStore reports) =>
            reports.AuthorDetail(id, Email(context)) is { } detail ? Results.Ok(detail) : Results.NotFound());
        api.MapPost("/reviews/{id:long}/findings/{finding:int}", (long id, int finding, AuthorResponse request, HttpContext context, ReportStore reports, MailNotifications mail) =>
        {
            reports.Respond(id, finding, Email(context), request.Status, request.Comment ?? "", mail.Settings.NotifyFalsePositives, request.SavedAt);
            return Results.Ok();
        });
    }
    private static string Email(HttpContext context) => context.User.FindFirstValue(ClaimTypes.Email)!;
}
public sealed record AuthorEmail(string Email);
public sealed record AuthorCode(string Email, string Code);
public sealed record AuthorResponse(string Status, string? Comment, string? SavedAt);
public sealed record AuthorSettingsUpdate(long Revision, AuthorPortalSettings Settings);

public sealed class AuthorMailWorker(ReportStore reports, AuthorAccess access, MailNotifications mail, ILogger<AuthorMailWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { Process(); }
            catch (Exception error) { logger.LogWarning("Author notification dispatch failed ({Type}); will retry.", error.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
    public void Process()
    {
        foreach (var notice in reports.PendingPortalNotices())
        {
            var settings = access.Settings;
            var mailSettings = mail.Settings;
            if (!settings.Enabled || (notice.Kind == "review" && !mailSettings.SendToAuthor) ||
                (notice.Kind == "feedback" && !mailSettings.NotifyFalsePositives))
            { reports.CompletePortalNotice(notice.Id); continue; }
            if (!mailSettings.Enabled) return;
            string email;
            try { email = AuthorAccess.Normalize(notice.Email); }
            catch (ArgumentException) { reports.CompletePortalNotice(notice.Id); continue; }
            string recipients, subject, body, url;
            if (notice.Kind == "review")
            {
                var detail = reports.AuthorDetail(long.Parse(notice.Payload), email);
                if (detail is null) { reports.CompletePortalNotice(notice.Id); continue; }
                var review = detail.Review;
                recipients = email;
                subject = $"[KommitEye] {review.Project} / {review.Sha[..Math.Min(8, review.Sha.Length)]} — проверка завершена / review completed";
                body = $"Проект / Project: {review.Project}\nВетка / Branch: {review.Branch}\nКоммит / Commit: {review.Sha}\n{review.Subject}\nНайдено проблем / Findings: {review.FindingCount}\nОткройте кабинет, чтобы прочитать проверку и ответить. / Open your portal to read and respond.";
                url = settings.PublicUrl + "/author?review=" + review.Id;
            }
            else
            {
                using var json = JsonDocument.Parse(notice.Payload);
                var data = json.RootElement;
                var id = data.GetProperty("ReviewId").GetInt64();
                var review = reports.AuthorDetail(id, null)?.Review;
                recipients = mailSettings.Recipients;
                subject = "[KommitEye] Ложное срабатывание / False positive";
                body = $"Проект / Project: {review?.Project}\nКоммит / Commit: {data.GetProperty("Sha").GetString()}\nАвтор / Author: {email}\n\n{data.GetProperty("Description").GetString()}\n\nКомментарий / Comment:\n{data.GetProperty("Comment").GetString()}";
                url = settings.PublicUrl + "/?review=" + id;
                if (mailSettings.Addresses().Count == 0) { reports.CompletePortalNotice(notice.Id); continue; }
            }
            if (mail.EnqueuePortal("portal-" + notice.Id, recipients, subject, body, url)) reports.CompletePortalNotice(notice.Id);
        }
    }
}
