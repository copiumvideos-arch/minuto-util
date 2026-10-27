using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MinutoUtil.Core.Publishing;

public sealed record TikTokClient(string ClientKey, string ClientSecret);

/// <summary>Who signed in, for the owner to confirm it is the channel's TikTok account.</summary>
public sealed record TikTokAccount(string OpenId, string DisplayName, string Scopes);

/// <summary>
/// TikTok Login Kit for a desktop app: a one-time browser sign-in with PKCE and a localhost redirect, then silent
/// refreshes. Access tokens last 24 hours, refresh tokens 365 days, and TikTok may hand back a new refresh token on
/// every refresh, which is saved (DPAPI file) or flagged (environment variable on a runner).
/// </summary>
public sealed class TikTokAuth(HttpClient http, TikTokClient client, ITokenStore store, TimeProvider time)
{
    public const string LoginCommand = "minuto-util tiktok-login";

    /// <summary>user.info.basic shows whose account it is; video.upload sends drafts to the inbox. Nothing can be posted publicly.</summary>
    public static readonly IReadOnlyList<string> Scopes = ["user.info.basic", "video.upload"];
    internal static readonly Uri AuthorizeEndpoint = new("https://www.tiktok.com/v2/auth/authorize/");
    internal static readonly Uri TokenEndpoint = new("https://open.tiktokapis.com/v2/oauth/token/");
    internal static readonly Uri UserInfoEndpoint = new("https://open.tiktokapis.com/v2/user/info/?fields=open_id,display_name");

    internal TimeSpan SignInWait { get; init; } = TimeSpan.FromMinutes(10);

    private string? _accessToken;
    private DateTimeOffset _expiresAt;

    public bool HasSignedIn => store.Load() is not null;

    /// <summary>
    /// Opens TikTok's consent page. With a listener, waits for the redirect to <paramref name="redirectUri"/>; with
    /// <paramref name="readPastedUrl"/>, asks the owner to paste the address the browser ended on (the page will not load).
    /// </summary>
    public async Task<TikTokAccount> AuthorizeInteractiveAsync(string redirectUri, Action<Uri> openBrowser, Func<string?>? readPastedUrl, CancellationToken ct)
    {
        var verifier = OAuthRedirect.RandomToken(64);
        var state = OAuthRedirect.RandomToken(24);
        var url = AuthorizationUrl(client.ClientKey, redirectUri, Challenge(verifier), state);

        string code;
        if (readPastedUrl is null)
        {
            code = await OAuthRedirect.WaitForCodeAsync(redirectUri, state, SignInWait, "TikTok", LoginCommand, () => openBrowser(url), ct);
        }
        else
        {
            openBrowser(url);
            code = OAuthRedirect.CodeFromPastedUrl(readPastedUrl(), state, "TikTok");
        }

        var tokens = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier,
        }, ct);
        if (string.IsNullOrEmpty(tokens.RefreshToken))
            throw new PublishAuthException("TikTok did not return a refresh token.");
        var granted = (tokens.Scope ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (!granted.Contains("video.upload"))
            throw new PublishAuthException($"TikTok did not grant video.upload (granted: {tokens.Scope}). Add the Content Posting API product and the scope to the app, then sign in again.");
        Remember(tokens);
        var account = await AccountAsync(tokens.AccessToken, tokens.Scope ?? "", tokens.OpenId ?? "", ct);
        store.Save(tokens.RefreshToken);
        return account;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (_accessToken is not null && time.GetUtcNow() < _expiresAt) return _accessToken;
        var refreshToken = store.Load()
            ?? throw new PublishAuthException($"Not signed in to TikTok. Run '{LoginCommand}' once.");
        var tokens = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }, ct);
        // TikTok may rotate the refresh token: the new one must be used from now on.
        if (!string.IsNullOrEmpty(tokens.RefreshToken) && tokens.RefreshToken != refreshToken) store.Save(tokens.RefreshToken);
        Remember(tokens);
        return _accessToken!;
    }

    /// <summary>TikTok's desktop PKCE: the challenge is the hex SHA-256 of the verifier (not base64url as in RFC 7636).</summary>
    internal static string Challenge(string verifier) => Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    internal static Uri AuthorizationUrl(string clientKey, string redirectUri, string challenge, string state) =>
        new($"{AuthorizeEndpoint}?{OAuthRedirect.Query(new Dictionary<string, string>
        {
            ["client_key"] = clientKey,
            ["scope"] = string.Join(',', Scopes),
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        })}");

    private async Task<TikTokAccount> AccountAsync(string accessToken, string scopes, string openId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await http.SendAsync(request, ct);
        var body = await SafeHttp.ReadLimitedAsync(response, 64_000, ct);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var user = doc.RootElement.GetProperty("data").GetProperty("user");
            return new TikTokAccount(user.TryGetProperty("open_id", out var id) ? id.GetString() ?? openId : openId,
                user.TryGetProperty("display_name", out var name) ? name.GetString() ?? "" : "", scopes);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // The account name is only shown to the owner; the sign-in itself is fine without it.
            Log.Warn($"TikTok user info failed ({(int)response.StatusCode}): {TextUtil.Truncate(body, 200)}");
            return new TikTokAccount(openId, "", scopes);
        }
    }

    private void Remember(TokenResponse tokens)
    {
        _accessToken = tokens.AccessToken;
        _expiresAt = time.GetUtcNow().AddSeconds(Math.Max(0, tokens.ExpiresIn - 300));
    }

    private async Task<TokenResponse> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        form["client_key"] = client.ClientKey;
        form["client_secret"] = client.ClientSecret;
        using var response = await http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form), ct);
        var body = await SafeHttp.ReadLimitedAsync(response, 64_000, ct);
        TokenResponse? tokens = null;
        try { tokens = JsonSerializer.Deserialize<TokenResponse>(body, GoogleAuth.TokenJson); }
        catch (JsonException) { }

        // TikTok can answer an error with 200 and an "error" field; a body with an access token is never logged.
        if (!response.IsSuccessStatusCode || tokens is null || string.IsNullOrEmpty(tokens.AccessToken))
        {
            var error = tokens?.Error ?? "";
            var hint = error is "invalid_grant" ? $" The sign-in expired or was revoked. Run '{LoginCommand}' again." : "";
            throw new PublishAuthException(
                $"TikTok token request failed ({(int)response.StatusCode} {error}): {TextUtil.Truncate(tokens?.ErrorDescription ?? "", 200)}{hint}");
        }
        return tokens;
    }

    private sealed record TokenResponse(string AccessToken, int ExpiresIn, string? RefreshToken, string? OpenId, string? Scope,
        string? Error, string? ErrorDescription);
}
