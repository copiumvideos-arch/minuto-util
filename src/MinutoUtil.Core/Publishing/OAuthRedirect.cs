using System.Collections.Specialized;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace MinutoUtil.Core.Publishing;

/// <summary>
/// The browser half of an installed-app OAuth sign-in: a one-shot local listener that receives the redirect with
/// ?code=, checks the state (CSRF), and answers the browser tab with a short page. Shared by Google and TikTok.
/// </summary>
public static class OAuthRedirect
{
    /// <summary>Waits for the redirect on <paramref name="prefix"/> (an HttpListener prefix ending in "/") and returns the code.</summary>
    public static async Task<string> WaitForCodeAsync(string prefix, string state, TimeSpan wait, string service, string loginCommand,
        Action startSignIn, CancellationToken ct)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            throw new PublishAuthException($"Could not listen on {prefix} ({ex.Message}). Close what uses that port, or use '{loginCommand} --paste'.", ex);
        }

        startSignIn();
        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(wait, ct);
            }
            catch (TimeoutException)
            {
                throw new PublishAuthException($"No sign-in within {wait.TotalMinutes:0} minutes. Run '{loginCommand}' again and open the new link.");
            }

            // Favicon requests and the like carry no state; they get the same "not ours" page.
            var result = Check(context.Request.QueryString, state, service);
            await RespondAsync(context, result.Message);
            if (result.Error is { } error) throw new PublishAuthException(error);
            if (result.Code is { } code) return code;
        }
    }

    /// <summary>For a sign-in finished on another machine or without a listener: the owner pastes the address the browser ended on.</summary>
    public static string CodeFromPastedUrl(string? pasted, string state, string service)
    {
        if (string.IsNullOrWhiteSpace(pasted) || !Uri.TryCreate(pasted.Trim(), UriKind.Absolute, out var url))
            throw new PublishAuthException("That is not an address. Copy the whole address from the browser's address bar after signing in.");
        var result = Check(System.Web.HttpUtility.ParseQueryString(url.Query), state, service);
        if (result.Error is { } error) throw new PublishAuthException(error);
        return result.Code ?? throw new PublishAuthException(result.Message);
    }

    private static (string? Code, string? Error, string Message) Check(NameValueCollection query, string state, string service)
    {
        if (query["state"] != state)
            return (null, null, "This sign-in link is not the one Minuto Útil opened. Close this tab.");
        if (query["error"] is { } error)
            return (null, $"{service} sign-in failed: {TextUtil.Truncate(error, 100)}", "Sign-in was cancelled. You can close this tab.");
        if (string.IsNullOrEmpty(query["code"]))
            return (null, $"{service} sent no authorization code.", "No authorization code. Close this tab and try again.");
        return (query["code"], null, $"Minuto Útil is signed in to {service}. You can close this tab.");
    }

    private static async Task RespondAsync(HttpListenerContext context, string message)
    {
        var html = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=utf-8><title>Minuto Útil</title><p style=\"font:16px system-ui;margin:40px\">{WebUtility.HtmlEncode(message)}</p>");
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = html.Length;
        await context.Response.OutputStream.WriteAsync(html);
        context.Response.Close();
    }

    public static int FreeLoopbackPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    public static string RandomToken(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Query(IEnumerable<KeyValuePair<string, string>> parameters) =>
        string.Join('&', parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
}
