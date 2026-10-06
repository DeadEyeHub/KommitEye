using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Util.Store;

namespace GitReviewer.Services;

public sealed class GoogleMailSession
{
    public string AccountId { get; set; } = "";
    public string Email { get; set; } = "";
    public ClientSecrets Client { get; set; } = new();
    public TokenResponse Token { get; set; } = new();
    public bool NeedsAuthorization { get; set; }
}

public interface IGoogleOAuthClient
{
    Task<GoogleMailSession> ConnectAsync(ClientSecrets client, CancellationToken token);
    Task<string> AccessTokenAsync(GoogleMailSession session, bool forceRefresh, CancellationToken token);
    Task RevokeAsync(GoogleMailSession session, CancellationToken token);
}

public sealed class GoogleOAuthClient(Google.Apis.Http.IHttpClientFactory? httpClientFactory = null) : IGoogleOAuthClient
{
    public const string SendScope = "https://www.googleapis.com/auth/gmail.send";
    private static readonly string[] Scopes = ["openid", "email", SendScope];
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<GoogleMailSession> ConnectAsync(ClientSecrets client, CancellationToken token)
    {
        var memory = new MemoryStore();
        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            new GoogleAuthorizationCodeFlow.Initializer { ClientSecrets = client, Prompt = "consent select_account", HttpClientFactory = httpClientFactory },
            Scopes, "gmail", true, token, memory);
        using var flow = credential.Flow;
        if (string.IsNullOrWhiteSpace(credential.Token.RefreshToken) ||
            (!string.IsNullOrWhiteSpace(credential.Token.Scope) && !credential.Token.Scope.Split(' ').Contains(SendScope)))
            throw new MailAuthorizationException();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://openidconnect.googleapis.com/v1/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await credential.GetAccessTokenForRequestAsync(cancellationToken: token));
        using var response = await Http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) throw new MailAuthorizationException();
        using var identity = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var info = identity.RootElement;
        var sub = info.GetProperty("sub").GetString();
        var email = info.GetProperty("email").GetString();
        if (string.IsNullOrWhiteSpace(sub) || string.IsNullOrWhiteSpace(email) || !info.GetProperty("email_verified").GetBoolean() ||
            email.Any(char.IsControl) || !MimeKit.MailboxAddress.TryParse(email, out var address) || address.Address != email)
            throw new MailAuthorizationException();
        return new GoogleMailSession { AccountId = sub, Email = email, Client = client, Token = credential.Token };
    }

    public async Task<string> AccessTokenAsync(GoogleMailSession session, bool forceRefresh, CancellationToken token)
    {
        using var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        { ClientSecrets = session.Client, Scopes = Scopes, DataStore = new MemoryStore(), HttpClientFactory = httpClientFactory });
        var credential = new UserCredential(flow, "gmail", session.Token);
        try
        {
            if (forceRefresh && !await credential.RefreshTokenAsync(token)) throw new MailAuthorizationException();
            return await credential.GetAccessTokenForRequestAsync(cancellationToken: token);
        }
        catch (TokenResponseException error) when (error.Error?.Error is "invalid_grant" or "invalid_client" or "unauthorized_client" or "access_denied")
        { throw new MailAuthorizationException(); }
        finally { session.Token = credential.Token; }
    }

    public async Task RevokeAsync(GoogleMailSession session, CancellationToken token)
    {
        using var body = new FormUrlEncodedContent(new Dictionary<string, string>
        { ["token"] = session.Token.RefreshToken ?? session.Token.AccessToken });
        using var response = await Http.PostAsync("https://oauth2.googleapis.com/revoke", body, token);
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.BadRequest)
            throw new MailProviderException(Localization.Text("Google could not revoke access.", "Google не смог отозвать доступ."));
    }

    // Explicit memory-only store prevents the SDK's default plaintext FileDataStore.
    private sealed class MemoryStore : IDataStore
    {
        private readonly Dictionary<string, object> _values = [];
        public Task StoreAsync<T>(string key, T value) { _values[typeof(T).FullName + key] = value!; return Task.CompletedTask; }
        public Task DeleteAsync<T>(string key) { _values.Remove(typeof(T).FullName + key); return Task.CompletedTask; }
        public Task<T> GetAsync<T>(string key) => Task.FromResult(_values.TryGetValue(typeof(T).FullName + key, out var value) ? (T)value : default!);
        public Task ClearAsync() { _values.Clear(); return Task.CompletedTask; }
    }
}
