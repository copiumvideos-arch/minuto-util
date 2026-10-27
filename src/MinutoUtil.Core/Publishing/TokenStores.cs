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

/// <summary>Keeps the refresh token encrypted with Windows DPAPI, readable only by the current Windows user.</summary>
public sealed class DpapiTokenStore(string path, string purpose) : ITokenStore
{
    private readonly byte[] _entropy = Encoding.UTF8.GetBytes($"minuto-util-{purpose}-token");

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

public static class TokenStores
{
    /// <summary>The environment variable when it is set, otherwise the DPAPI file.</summary>
    public static ITokenStore For(string envVar, string tokenFile, string purpose) =>
        Environment.GetEnvironmentVariable(envVar) is { Length: > 0 } value
            ? new EnvironmentTokenStore(envVar, value.Trim())
            : new DpapiTokenStore(ExpandPath(tokenFile), purpose);

    /// <summary>%LOCALAPPDATA% and friends; forward slashes work on both systems.</summary>
    public static string ExpandPath(string path) => System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
}
