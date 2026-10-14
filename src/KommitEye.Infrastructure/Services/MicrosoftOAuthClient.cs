using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Identity.Client;

namespace KommitEye.Services;

public sealed class MicrosoftMailSession
{
    public string ClientId { get; set; } = "";
    public string TenantId { get; set; } = "common";
    public string AccountId { get; set; } = "";
    public string Email { get; set; } = "";
    public byte[] Cache { get; set; } = [];
    public bool NeedsAuthorization { get; set; }
}

public interface IMicrosoftOAuthClient
{
    Task<MicrosoftMailSession> ConnectAsync(string clientId, string tenantId, CancellationToken token);
    Task<string> AccessTokenAsync(MicrosoftMailSession session, bool forceRefresh, CancellationToken token);
}

public sealed class MicrosoftOAuthClient(Func<DeviceCodeResult, Task>? deviceCode = null) : IMicrosoftOAuthClient
{
    private static readonly string[] Scopes = ["https://graph.microsoft.com/User.Read", "https://graph.microsoft.com/Mail.Send"];
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };

    public static string Authority(string tenantId)
    {
        tenantId = tenantId.Trim();
        if (tenantId is "common" or "organizations" or "consumers")
            return "https://login.microsoftonline.com/" + tenantId;
        if (Guid.TryParse(tenantId, out var id)) return "https://login.microsoftonline.com/" + id.ToString("D");
        throw new MailProviderException(Localization.Text(
            "Enter the Directory (tenant) ID from Overview, or common for personal/multitenant accounts.",
            "Укажите Directory (tenant) ID из Overview или common для личных аккаунтов и multitenant-приложений."));
    }

    private static IPublicClientApplication Create(MicrosoftMailSession session)
    {
        var app = PublicClientApplicationBuilder.Create(session.ClientId)
            .WithAuthority(Authority(session.TenantId))
            .WithRedirectUri("http://localhost").Build();
        app.UserTokenCache.SetBeforeAccess(args => { if (session.Cache.Length > 0) args.TokenCache.DeserializeMsalV3(session.Cache); });
        app.UserTokenCache.SetAfterAccess(args =>
        {
            if (args.HasStateChanged) session.Cache = args.TokenCache.SerializeMsalV3();
        });
        return app;
    }

    public async Task<MicrosoftMailSession> ConnectAsync(string clientId, string tenantId, CancellationToken token)
    {
        if (!Guid.TryParse(clientId, out _)) throw new MailProviderException(Localization.Text(
            "Enter the Microsoft application (client) ID.", "Укажите идентификатор приложения Microsoft (client ID)."));
        var session = new MicrosoftMailSession { ClientId = clientId, TenantId = tenantId };
        var app = Create(session);
        var result = deviceCode is null
            ? await app.AcquireTokenInteractive(Scopes).WithPrompt(Prompt.SelectAccount).WithUseEmbeddedWebView(false).ExecuteAsync(token)
            : await app.AcquireTokenWithDeviceCode(Scopes, deviceCode).ExecuteAsync(token);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me?$select=mail,userPrincipalName");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", result.AccessToken);
        using var response = await Http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) throw new MailAuthorizationException();
        using var profile = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        session.Email = profile.RootElement.TryGetProperty("mail", out var mail) ? mail.GetString() ?? "" : "";
        if (session.Email.Length == 0) session.Email = profile.RootElement.GetProperty("userPrincipalName").GetString() ?? "";
        if (string.IsNullOrWhiteSpace(session.Email)) throw new MailAuthorizationException();
        session.AccountId = result.Account.HomeAccountId.Identifier;
        return session;
    }

    public async Task<string> AccessTokenAsync(MicrosoftMailSession session, bool forceRefresh, CancellationToken token)
    {
        try
        {
            var app = Create(session);
            var account = await app.GetAccountAsync(session.AccountId);
            if (account is null) throw new MailAuthorizationException();
            return (await app.AcquireTokenSilent(Scopes, account).WithForceRefresh(forceRefresh).ExecuteAsync(token)).AccessToken;
        }
        catch (MsalUiRequiredException) { throw new MailAuthorizationException(); }
    }
}
