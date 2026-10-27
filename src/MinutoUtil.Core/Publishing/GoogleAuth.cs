using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MinutoUtil.Core.Publishing;

/// <summary>The "Desktop app" OAuth client from Google Cloud. Its secret is not truly secret for installed apps, but it stays out of the repo.</summary>
public sealed record OAuthClient(string ClientId, string ClientSecret)
{
    public static OAuthClient Load(string path)
    {
        if (!File.Exists(path))
            throw new PublishAuthException($"OAuth client file not found: {path}. Copy the Desktop app client_secret.json there (see README).");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("installed", out var installed))
            throw new PublishAuthException($"{path} is not a Desktop app OAuth client (no \"installed\" section). Create the client with type \"Desktop app\".");
        return new OAuthClient(
            installed.GetProperty("client_id").GetString() ?? "",
            installed.GetProperty("client_secret").GetString() ?? "");
    }
}

/// <summary>
/// Google OAuth for an installed app (copied from Flopômetro and adapted): a one-time browser sign-in (loopback redirect
/// with PKCE), then silent access-token refreshes. Only upload and read-only scopes are requested, so the token cannot
/// delete or edit anything on the channel; read-only is there to check which channel the sign-in belongs to.
/// </summary>
public sealed class GoogleAuth(HttpClient http, OAuthClient client, ITokenStore store, TimeProvider time)
{
    public const string LoginCommand = "minuto-util youtube-login";

    public static readonly IReadOnlyList<string> Scopes =
    [
        "https://www.googleapis.com/auth/youtube.upload",
        "https://www.googleapis.com/auth/youtube.readonly",
    ];
    internal static readonly Uri AuthorizeEndpoint = new("https://accounts.google.com/o/oauth2/v2/auth");
    internal static readonly Uri TokenEndpoint = new("https://oauth2.googleapis.com/token");

    /// <summary>How long the sign-in link stays usable.</summary>
    internal TimeSpan SignInWait { get; init; } = TimeSpan.FromMinutes(10);

    private string? _accessToken;
    private DateTimeOffset _expiresAt;

    public bool HasSignedIn => store.Load() is not null;

    /// <summary>
    /// Opens the Google consent page and waits for the redirect back to this machine. <paramref name="verify"/> gets the
    /// new access token before anything is saved (the channel check); if it throws, the sign-in is thrown away.
    /// </summary>
    public async Task AuthorizeInteractiveAsync(Action<Uri> openBrowser, Func<string, CancellationToken, Task> verify, CancellationToken ct)
    {
        var redirectUri = $"http://127.0.0.1:{OAuthRedirect.FreeLoopbackPort()}/";
        var verifier = OAuthRedirect.RandomToken(64);
        var state = OAuthRedirect.RandomToken(24);
        var challenge = OAuthRedirect.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        var code = await OAuthRedirect.WaitForCodeAsync(redirectUri, state, SignInWait, "YouTube", LoginCommand,
            () => openBrowser(AuthorizationUrl(client.ClientId, redirectUri, challenge, state)), ct);

        var tokens = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier,
        }, ct);
        if (tokens.RefreshToken is null)
            throw new PublishAuthException("Google did not return a refresh token. Remove Minuto Útil's access at myaccount.google.com/permissions and sign in again.");
        await verify(tokens.AccessToken, ct);
        store.Save(tokens.RefreshToken);
        Remember(tokens);
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (_accessToken is not null && time.GetUtcNow() < _expiresAt) return _accessToken;
        var refreshToken = store.Load()
            ?? throw new PublishAuthException($"Not signed in to YouTube. Run '{LoginCommand}' once.");
        var tokens = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }, ct);
        Remember(tokens);
        return _accessToken!;
    }

    internal static Uri AuthorizationUrl(string clientId, string redirectUri, string challenge, string state) =>
        new($"{AuthorizeEndpoint}?{OAuthRedirect.Query(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = string.Join(' ', Scopes),
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["access_type"] = "offline",
            ["prompt"] = "consent select_account",
        })}");

    private void Remember(TokenResponse tokens)
    {
        _accessToken = tokens.AccessToken;
        // Refresh a minute early so a long upload never starts with a token about to expire.
        _expiresAt = time.GetUtcNow().AddSeconds(Math.Max(0, tokens.ExpiresIn - 60));
    }

    private async Task<TokenResponse> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        form["client_id"] = client.ClientId;
        form["client_secret"] = client.ClientSecret;
        using var response = await http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form), ct);
        var body = await SafeHttp.ReadLimitedAsync(response, 64_000, ct);
        if (!response.IsSuccessStatusCode)
        {
            // Never log the body of a successful response: it holds tokens. Error bodies do not.
            var hint = body.Contains("invalid_grant", StringComparison.Ordinal)
                ? $" The sign-in expired or was revoked. Run '{LoginCommand}' again."
                : "";
            throw new PublishAuthException($"Google token request failed ({(int)response.StatusCode}): {TextUtil.Truncate(body, 200)}{hint}");
        }
        return JsonSerializer.Deserialize<TokenResponse>(body, TokenJson)
            ?? throw new PublishAuthException("Google returned an empty token response.");
    }

    internal static readonly JsonSerializerOptions TokenJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private sealed record TokenResponse(string AccessToken, int ExpiresIn, string? RefreshToken);
}
