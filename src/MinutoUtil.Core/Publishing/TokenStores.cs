using System.Security.Cryptography;
using System.Text;

namespace MinutoUtil.Core.Publishing;

public sealed class PublishAuthException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Where a platform's refresh token lives. Tokens are never printed or logged.</summary>
public interface ITokenStore
{
    string? Load();
    void Save(string refreshToken);
}

/// <summary>
/// Keeps the refresh token encrypted with Windows DPAPI, readable only by the current Windows user. The entropy is
/// "&lt;channel id&gt;-&lt;purpose&gt;-token" ("minuto-util-youtube-token" for Minuto Útil, as before there were channels), so
/// one channel's file does not open as another channel's or another platform's.
/// </summary>
public sealed class DpapiTokenStore(string path, string purpose, string channelId) : ITokenStore
{
    private readonly byte[] _entropy = TokenStores.Binding(channelId, purpose);

    public string Path { get; } = path;

    public string? Load()
    {
        if (!File.Exists(Path)) return null;
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException($"{Path} is DPAPI-encrypted (Windows only). On Linux set the refresh token environment variable.");
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(Path), _entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException ex)
        {
            throw new PublishAuthException($"Could not decrypt {Path} (made by another Windows user?). Sign in again.", ex);
        }
    }

    public void Save(string refreshToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The token file uses Windows DPAPI.");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        File.WriteAllBytes(Path, ProtectedData.Protect(Encoding.UTF8.GetBytes(refreshToken), _entropy, DataProtectionScope.CurrentUser));
    }
}

/// <summary>
/// The refresh token from an environment variable (a GitHub Actions secret on the Linux runner). It cannot be written
/// back, so a token the platform replaced (TikTok rotates them) is kept for this process only and a warning says the
/// secret needs updating. The value itself is never logged.
/// </summary>
public sealed class EnvironmentTokenStore(string variable, string value) : ITokenStore
{
    private string _token = value;

    public string Variable { get; } = variable;

    public string? Load() => _token;

    public void Save(string refreshToken)
    {
        if (refreshToken == _token) return;
        _token = refreshToken;
        Log.Warn($"the platform returned a new refresh token; {Variable} is now out of date. Sign in again on Windows and update the secret.");
    }
}

/// <summary>
/// The cloud runner's store: the refresh token AES-256-GCM encrypted in a file on the repository's state branch, with
/// a key held only as a GitHub secret. The first run starts from the environment variable (the secret); a token the
/// platform replaces is written to the file, so TikTok's rotation and the 365-day renewal survive between jobs. A
/// file the key cannot open (the key was replaced) falls back to the secret with a warning. The key comes from the
/// channel's &lt;prefix&gt;_STATE_KEY (<paramref name="keyEnvVar"/>, named in the warning) and the associated data binds the
/// file to its channel and platform.
/// </summary>
public sealed class SealedFileTokenStore(string path, byte[] key, string purpose, string? seed, string channelId, string keyEnvVar) : ITokenStore
{
    private const int NonceSize = 12, TagSize = 16;

    public string Path { get; } = path;

    public string? Load()
    {
        if (!File.Exists(Path)) return seed;
        var data = File.ReadAllBytes(Path);
        try
        {
            if (data.Length <= NonceSize + TagSize) throw new CryptographicException("too short");
            var plain = new byte[data.Length - NonceSize - TagSize];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(data.AsSpan(0, NonceSize), data.AsSpan(NonceSize + TagSize), data.AsSpan(NonceSize, TagSize), plain, Aad);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            Log.Warn($"{System.IO.Path.GetFileName(Path)} cannot be opened with {keyEnvVar}; using the refresh token secret instead");
            return seed;
        }
    }

    public void Save(string refreshToken)
    {
        if (File.Exists(Path) && Load() == refreshToken) return;
        var plain = Encoding.UTF8.GetBytes(refreshToken);
        var data = new byte[NonceSize + TagSize + plain.Length];
        RandomNumberGenerator.Fill(data.AsSpan(0, NonceSize));
        using (var aes = new AesGcm(key, TagSize))
            aes.Encrypt(data.AsSpan(0, NonceSize), plain, data.AsSpan(NonceSize + TagSize), data.AsSpan(NonceSize, TagSize), Aad);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        File.WriteAllBytes(Path + ".tmp", data);
        File.Move(Path + ".tmp", Path, overwrite: true);
        Log.Info($"{purpose}: the new refresh token is saved encrypted in the state folder");
    }

    // Binds the file to its channel and platform: a YouTube file renamed to tiktok-token.enc, or another channel's file,
    // does not decrypt.
    private byte[] Aad => TokenStores.Binding(channelId, purpose);

    /// <summary>The 32-byte key from its base64 environment variable, or null when it is not set.</summary>
    public static byte[]? KeyFromEnvironment(string keyEnvVar)
    {
        if (Environment.GetEnvironmentVariable(keyEnvVar) is not { Length: > 0 } text) return null;
        try
        {
            var key = Convert.FromBase64String(text.Trim());
            if (key.Length == 32) return key;
        }
        catch (FormatException)
        {
        }
        throw new PublishAuthException($"{keyEnvVar} must be 32 random bytes in base64.");
    }
}

public static class TokenStores
{
    /// <summary>
    /// On the cloud runner (the channel's state folder and key set) the sealed file seeded by the environment variable;
    /// otherwise the environment variable when it is set, otherwise the DPAPI file.
    /// </summary>
    public static ITokenStore For(ChannelOptions channel, string envVar, string tokenFile, string purpose)
    {
        var seed = Environment.GetEnvironmentVariable(envVar) is { Length: > 0 } value ? value.Trim() : null;
        if (Environment.GetEnvironmentVariable(channel.StateDirEnvVar) is { Length: > 0 } stateDir
            && SealedFileTokenStore.KeyFromEnvironment(channel.StateKeyEnvVar) is { } key)
            return new SealedFileTokenStore(System.IO.Path.Combine(stateDir, $"{purpose}-token.enc"), key, purpose, seed, channel.Id, channel.StateKeyEnvVar);
        return seed is not null ? new EnvironmentTokenStore(envVar, seed) : new DpapiTokenStore(ExpandPath(tokenFile), purpose, channel.Id);
    }

    /// <summary>
    /// "&lt;channel id&gt;-&lt;purpose&gt;-token": the DPAPI entropy and the AES-GCM associated data. For minuto-util it is
    /// byte for byte what both used before there were channels, so the live sign-ins and sealed files still open.
    /// </summary>
    public static byte[] Binding(string channelId, string purpose) =>
        Encoding.UTF8.GetBytes($"{Channel.ValidateId(channelId)}-{purpose}-token");

    /// <summary>%LOCALAPPDATA% and friends; forward slashes work on both systems.</summary>
    public static string ExpandPath(string path) => System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
}
