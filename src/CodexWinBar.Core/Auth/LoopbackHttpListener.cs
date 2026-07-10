using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CodexWinBar.Core.Auth;

/// <summary>Response the sign-in flow asks the listener to send back to the browser.</summary>
public sealed record LoopbackResponse
{
    /// <summary>HTTP status code; 302 when <see cref="RedirectLocation"/> is set.</summary>
    public int StatusCode { get; init; } = 200;

    /// <summary>Location header for a redirect response.</summary>
    public string? RedirectLocation { get; init; }

    /// <summary>HTML body for a non-redirect response.</summary>
    public string HtmlBody { get; init; } = string.Empty;

    /// <summary>Creates a 302 redirect response.</summary>
    public static LoopbackResponse Redirect(string location) => new() { StatusCode = 302, RedirectLocation = location };

    /// <summary>Creates an HTML page response.</summary>
    public static LoopbackResponse Html(int statusCode, string body) => new() { StatusCode = statusCode, HtmlBody = body };
}

/// <summary>
/// A callback handler's decision: the HTTP reply to send, and whether this request completes the
/// wait. <see cref="Complete"/> is false for a request that fails validation, so the listener keeps
/// waiting for the genuine callback instead of aborting on a stray or racing request.
/// </summary>
public sealed record CallbackResult(LoopbackResponse Response, bool Complete)
{
    /// <summary>The request is the genuine callback (valid or a real provider error); end the wait.</summary>
    public static CallbackResult Done(LoopbackResponse response) => new(response, true);

    /// <summary>The request is not a valid callback; answer it but keep waiting for the real one.</summary>
    public static CallbackResult KeepWaiting(LoopbackResponse response) => new(response, false);
}

/// <summary>
/// Minimal single-shot HTTP listener on 127.0.0.1 for OAuth loopback redirects. Built on TcpListener
/// rather than HttpListener so no http.sys URL ACLs apply and port 0 (ephemeral) binding works.
/// </summary>
public sealed class LoopbackHttpListener : IDisposable
{
    private const int MaxRequestBytes = 16 * 1024;
    private readonly TcpListener listener;
    private readonly string callbackPath;

    /// <summary>Binds to 127.0.0.1 on <paramref name="port"/> (0 picks an ephemeral port) immediately.</summary>
    /// <param name="port">Port to bind, or 0 for an ephemeral port.</param>
    /// <param name="callbackPath">Absolute path (e.g. "/auth/callback") that completes the wait.</param>
    public LoopbackHttpListener(int port, string callbackPath)
    {
        this.callbackPath = callbackPath;
        this.listener = new TcpListener(IPAddress.Loopback, port);
        this.listener.Start();
    }

    /// <summary>The bound port (resolves the actual port when constructed with 0).</summary>
    public int Port => ((IPEndPoint)this.listener.LocalEndpoint).Port;

    /// <summary>
    /// Serves requests until a GET of the callback path is accepted as final. For each callback hit
    /// <paramref name="respond"/> decides the HTTP reply and whether it completes the wait: a hit that
    /// fails validation (wrong/missing state, no code) is answered but the wait CONTINUES, so a stray
    /// browser prefetch or a local process racing the callback cannot abort a genuine sign-in. Other
    /// paths get 404 and the wait continues.
    /// </summary>
    /// <returns>The decoded query parameters of the callback that completed the wait.</returns>
    public async Task<IReadOnlyDictionary<string, string>> WaitForCallbackAsync(
        Func<IReadOnlyDictionary<string, string>, CallbackResult> respond,
        CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            using var client = await this.listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            var stream = client.GetStream();
            string? target;
            try
            {
                target = await ReadRequestTargetAsync(stream, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException)
            {
                continue;
            }

            if (target is null)
            {
                continue;
            }

            var queryIndex = target.IndexOf('?', StringComparison.Ordinal);
            var path = queryIndex < 0 ? target : target[..queryIndex];
            if (!string.Equals(path, this.callbackPath, StringComparison.Ordinal))
            {
                await WriteResponseAsync(stream, LoopbackResponse.Html(404, "Not found."), ct).ConfigureAwait(false);
                continue;
            }

            var query = ParseQuery(queryIndex < 0 ? string.Empty : target[(queryIndex + 1)..]);
            var result = respond(query);
            await WriteResponseAsync(stream, result.Response, ct).ConfigureAwait(false);
            if (result.Complete)
            {
                return query;
            }
        }
    }

    public void Dispose() => this.listener.Stop();

    /// <summary>Reads the request line and drains headers; returns the request target of a GET, else null.</summary>
    private static async Task<string?> ReadRequestTargetAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxRequestBytes];
        var total = 0;
        while (buffer.AsSpan(0, total).IndexOf("\r\n\r\n"u8) < 0)
        {
            if (total >= buffer.Length)
            {
                throw new InvalidDataException("HTTP request exceeded the size limit.");
            }

            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            total += read;
        }

        var text = Encoding.ASCII.GetString(buffer, 0, total);
        var lineEnd = text.IndexOf("\r\n", StringComparison.Ordinal);
        var requestLine = lineEnd < 0 ? text : text[..lineEnd];
        var parts = requestLine.Split(' ');
        return parts.Length >= 2 && parts[0] == "GET" ? parts[1] : null;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, LoopbackResponse response, CancellationToken ct)
    {
        var reason = response.StatusCode switch
        {
            200 => "OK",
            302 => "Found",
            400 => "Bad Request",
            404 => "Not Found",
            _ => "Status",
        };
        var body = Encoding.UTF8.GetBytes(response.HtmlBody);
        var builder = new StringBuilder()
            .Append("HTTP/1.1 ").Append(response.StatusCode).Append(' ').Append(reason).Append("\r\n")
            .Append("Content-Type: text/html; charset=utf-8\r\n")
            .Append("Content-Length: ").Append(body.Length).Append("\r\n")
            .Append("Connection: close\r\n");
        if (response.RedirectLocation is not null)
        {
            builder.Append("Location: ").Append(response.RedirectLocation).Append("\r\n");
        }

        builder.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(builder.ToString()), ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            var key = separator < 0 ? pair : pair[..separator];
            var value = separator < 0 ? string.Empty : pair[(separator + 1)..];
            result[Decode(key)] = Decode(value);
        }

        return result;
    }

    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
}
