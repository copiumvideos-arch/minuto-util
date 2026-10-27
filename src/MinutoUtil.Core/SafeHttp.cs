using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MinutoUtil.Core;

/// <summary>
/// HTTP access to the public internet only. Media URLs come from third-party APIs, so the client refuses
/// to connect to loopback, private, or link-local addresses (SSRF protection) and caps response sizes.
/// </summary>
public static class SafeHttp
{
    /// <summary>
    /// Wikimedia, the Library of Congress and the Internet Archive ask bots for a descriptive User-Agent.
    /// </summary>
    public const string UserAgent = "MinutoUtilBot/0.1 (faceless history Shorts prototype; public domain and CC media only)";

    public static HttpClient CreatePublicClient(TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxAutomaticRedirections = 5,
            ConnectCallback = ConnectPublicOnlyAsync,
        };
        var client = new HttpClient(handler) { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    private static async ValueTask<Stream> ConnectPublicOnlyAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = await Dns.GetHostAddressesAsync(host, ct);
        if (addresses.Length == 0 || addresses.Any(IsNonPublic))
            throw new HttpRequestException($"Blocked connection to non-public host '{host}'.");

        Exception? lastError = null;
        foreach (var address in addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(address, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                lastError = ex;
                if (ex is OperationCanceledException) throw;
            }
        }
        throw new HttpRequestException($"Could not connect to '{host}'.", lastError);
    }

    public static bool IsNonPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] is 0 or 10 or 127
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] is >= 64 and <= 127)
                || b[0] >= 224;
        }

        return address.IsIPv6LinkLocal
            || address.IsIPv6SiteLocal
            || address.IsIPv6Multicast
            || (address.GetAddressBytes()[0] & 0xFE) == 0xFC;
    }

    /// <summary>The body as bytes, refusing anything larger than <paramref name="maxBytes"/>.</summary>
    public static async Task<byte[]> ReadBytesLimitedAsync(HttpResponseMessage response, int maxBytes, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new InvalidDataException($"Response larger than {maxBytes} bytes.");

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new InvalidDataException($"Response larger than {maxBytes} bytes.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    public static async Task<string> ReadLimitedAsync(HttpResponseMessage response, int maxBytes, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new InvalidDataException($"Response larger than {maxBytes} bytes.");

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new InvalidDataException($"Response larger than {maxBytes} bytes.");
            buffer.Write(chunk, 0, read);
        }

        var encoding = Encoding.UTF8;
        var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"');
        if (!string.IsNullOrEmpty(charset))
        {
            try { encoding = Encoding.GetEncoding(charset); }
            catch (ArgumentException) { }
        }
        return encoding.GetString(buffer.GetBuffer(), 0, (int)buffer.Length).TrimStart('﻿');
    }
}
