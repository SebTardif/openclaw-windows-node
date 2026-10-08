using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using OpenClaw.Connection;

namespace OpenClawTray.Services;

/// <summary>
/// Serves one loopback page that does not contain the dashboard credential.
/// The credential is written only after a fresh ownership check, and only to that response.
/// </summary>
internal static class DashboardIssuedBinding
{
    public static bool Matches(GatewayRecord issued, SshTunnelConfig issuedTunnel, GatewayRecord? active) =>
        active is not null
        && string.Equals(active.Id, issued.Id, StringComparison.Ordinal)
        && active.SshTunnel == issuedTunnel;
}

internal static class DashboardCredentialHandoff
{
    // One origin for every launch so the Control UI can keep its local settings.
    public const int Port = 47831;

    private static readonly ConcurrentDictionary<string, Handoff> Live = new();
    private static readonly object ListenerGate = new();
    private static HttpListener? _listener;

    public static string Start(Func<Task<bool>> owned, string destination, string? tlsHost = null)
    {
        ArgumentNullException.ThrowIfNull(owned);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        EnsureListener();
        var handoff = new Handoff(owned, destination, tlsHost);
        Live[handoff.Nonce] = handoff;
        _ = handoff.ExpireUnusedAsync();
        return handoff.BrowserUrl;
    }

    private static void EnsureListener()
    {
        lock (ListenerGate)
        {
            if (_listener is { IsListening: true })
                return;
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            listener.Start();
            _listener = listener;
            _ = ServeAsync(listener);
        }
    }

    private static async Task ServeAsync(HttpListener listener)
    {
        try
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                _ = AnswerAsync(context);
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task AnswerAsync(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "";
            if (path.StartsWith("/d/", StringComparison.Ordinal))
            {
                var nonce = path["/d/".Length..];
                if (!Live.TryGetValue(nonce, out var handoff))
                {
                    response.StatusCode = (int)HttpStatusCode.NotFound;
                    response.Close();
                    return;
                }

                await handoff.DeliverAsync(context);
                return;
            }

            if (!TrySession(path, out var session, out var upstreamPath) &&
                !TrySessionFromReferer(context.Request.Headers["Referer"], path, out session, out upstreamPath))
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                response.Close();
                return;
            }

            if (!await session.Owned())
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                response.Close();
                return;
            }

            if (context.Request.IsWebSocketRequest)
            {
                await session.ProxyWebSocketAsync(context, upstreamPath);
                return;
            }

            await session.ProxyAsync(context, upstreamPath);
        }
        catch (Exception)
        {
            try { response.Abort(); } catch (Exception) { }
        }
    }

    private static bool TrySession(string path, out Handoff session, out string upstreamPath)
    {
        foreach (var candidate in Live.Values)
        {
            var prefix = "/s/" + candidate.Nonce;
            if (!path.Equals(prefix, StringComparison.Ordinal) &&
                !path.StartsWith(prefix + "/", StringComparison.Ordinal))
                continue;
            session = candidate;
            upstreamPath = path[prefix.Length..];
            if (string.IsNullOrEmpty(upstreamPath))
                upstreamPath = "/";
            return true;
        }

        session = null!;
        upstreamPath = "/";
        return false;
    }

    private static bool TrySessionFromReferer(string? referer, string requestPath, out Handoff session, out string upstreamPath)
    {
        session = null!;
        upstreamPath = requestPath;
        if (string.IsNullOrWhiteSpace(referer) || !Uri.TryCreate(referer, UriKind.Absolute, out var uri))
            return false;
        if (!TrySession(uri.AbsolutePath, out session, out _))
            return false;
        if (string.IsNullOrEmpty(upstreamPath))
            upstreamPath = "/";
        return true;
    }

    internal static string RewriteRootAbsolute(string html, string sessionPrefix)
    {
        return System.Text.RegularExpressions.Regex.Replace(
            html,
            "(src|href|action)=\"/(?!s/)",
            "$1=\"" + sessionPrefix + "/",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    internal static void CopySecurityHeaders(string headerText, HttpListenerResponse response)
    {
        string[] names =
        [
            "Content-Security-Policy",
            "X-Frame-Options",
            "X-Content-Type-Options",
            "Referrer-Policy",
            "Permissions-Policy",
            "Cross-Origin-Opener-Policy",
            "Cross-Origin-Resource-Policy",
            "Cross-Origin-Embedder-Policy",
        ];
        foreach (var line in headerText.Split("\r\n"))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            var name = line[..colon].Trim();
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
                continue;
            try
            {
                response.Headers[name] = line[(colon + 1)..].Trim();
            }
            catch (ArgumentException)
            {
            }
        }
    }

    internal static string? ReadContentType(string headerText)
    {
        foreach (var line in headerText.Split("\r\n"))
        {
            const string name = "Content-Type:";
            if (line.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                return line[name.Length..].Trim();
        }

        return null;
    }

    private sealed class Handoff
    {
        private readonly Func<Task<bool>> _owned;
        private readonly string _destination;
        private readonly string _tlsHost;
        private int _delivered;

        public Handoff(Func<Task<bool>> owned, string destination, string? tlsHost)
        {
            _owned = owned;
            _destination = destination;
            _tlsHost = string.IsNullOrWhiteSpace(tlsHost) ? new Uri(destination).Host : tlsHost;
            Nonce = Convert.ToHexString(Guid.NewGuid().ToByteArray());
            BrowserUrl = $"http://127.0.0.1:{Port}/d/{Nonce}";
        }

        public string Nonce { get; }
        public string BrowserUrl { get; }
        public Task<bool> Owned() => _owned();

        public async Task ExpireUnusedAsync()
        {
            await Task.Delay(TimeSpan.FromMinutes(2));
            if (Interlocked.CompareExchange(ref _delivered, 0, 0) == 0)
                Live.TryRemove(Nonce, out _);
        }

        public async Task DeliverAsync(HttpListenerContext context)
        {
            var response = context.Response;
            var first = Interlocked.CompareExchange(ref _delivered, 1, 0) == 0 && await _owned();
            if (!first)
            {
                Interlocked.Exchange(ref _delivered, 1);
                response.StatusCode = (int)HttpStatusCode.NotFound;
                response.Close();
                return;
            }

            var html = "<!doctype html><meta charset=\"utf-8\"><script>location.replace(" +
                JsonSerializer.Serialize(SameOriginDestination()) + ")</script>";
            var bytes = Encoding.UTF8.GetBytes(html);
            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
            response.Close();
        }

        private string SameOriginDestination()
        {
            var destination = new Uri(_destination);
            var hash = destination.Fragment;
            var gateway = Uri.EscapeDataString($"ws://127.0.0.1:{Port}/s/{Nonce}/");
            hash = string.IsNullOrEmpty(hash)
                ? "#gatewayUrl=" + gateway
                : hash + "&gatewayUrl=" + gateway;
            var path = string.IsNullOrEmpty(destination.AbsolutePath) ? "/" : destination.AbsolutePath;
            return $"http://127.0.0.1:{Port}/s/{Nonce}{path}{destination.Query}{hash}";
        }

        public async Task ProxyAsync(HttpListenerContext context, string upstreamPath)
        {
            var destination = new Uri(_destination);
            var query = context.Request.Url?.Query ?? "";
            var pathAndQuery = upstreamPath + query;
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(destination.Host, destination.Port);
            var clientPort = tcp.Client.LocalEndPoint is IPEndPoint local ? local.Port : 0;
            var accepted = WindowsTcpListenerSnapshot.AcceptedProcessId(destination.Port, clientPort);
            var listener = WindowsTcpListenerSnapshot.Capture().Listeners
                .FirstOrDefault(item => item.Port == destination.Port);
            if (accepted is null || listener is null || accepted != listener.ProcessId || !await _owned())
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                context.Response.Close();
                return;
            }

            await using var raw = tcp.GetStream();
            Stream stream = raw;
            if (destination.Scheme == "https")
            {
                var ssl = new SslStream(raw, leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsClientAsync(_tlsHost);
                stream = ssl;
            }

            var requestText =
                $"{context.Request.HttpMethod} {pathAndQuery} HTTP/1.1\r\nHost: {_tlsHost}\r\nConnection: close\r\n\r\n";
            var requestBytes = Encoding.ASCII.GetBytes(requestText);
            await stream.WriteAsync(requestBytes);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            var payload = buffer.ToArray();
            var headerEnd = FindHeaderEnd(payload);
            var body = headerEnd < 0 ? payload : payload[(headerEnd + 4)..];
            context.Response.StatusCode = StatusCode(payload);
            var headerText = Encoding.ASCII.GetString(payload, 0, headerEnd < 0 ? payload.Length : headerEnd);
            CopySecurityHeaders(headerText, context.Response);
            if (ReadContentType(headerText) is { } contentType)
            {
                context.Response.ContentType = contentType;
                if (contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                {
                    var html = Encoding.UTF8.GetString(body);
                    body = Encoding.UTF8.GetBytes(RewriteRootAbsolute(html, "/s/" + Nonce));
                }
            }
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        }

        public async Task ProxyWebSocketAsync(HttpListenerContext context, string upstreamPath)
        {
            var browser = await context.AcceptWebSocketAsync(null);
            var destination = new Uri(_destination);
            var scheme = destination.Scheme == "https" ? "wss" : "ws";
            var query = context.Request.Url?.Query ?? "";
            var upstreamUri = new Uri($"{scheme}://{destination.Host}:{destination.Port}{upstreamPath}{query}");
            using var upstream = new ClientWebSocket();
            var tlsHost = _tlsHost;
            upstream.Options.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            {
                if (certificate is not System.Security.Cryptography.X509Certificates.X509Certificate2 cert)
                    return false;
                var other = errors & ~System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch;
                return other == System.Net.Security.SslPolicyErrors.None && cert.MatchesHostname(tlsHost);
            };
            try
            {
                await upstream.ConnectAsync(upstreamUri, CancellationToken.None);
                if (!await _owned())
                {
                    upstream.Abort();
                    if (browser.WebSocket.State == WebSocketState.Open)
                        await browser.WebSocket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "closed", CancellationToken.None);
                    return;
                }

                await Task.WhenAll(
                    PumpAsync(browser.WebSocket, upstream),
                    PumpAsync(upstream, browser.WebSocket));
            }
            catch (Exception)
            {
                if (browser.WebSocket.State == WebSocketState.Open)
                    await browser.WebSocket.CloseAsync(WebSocketCloseStatus.InternalServerError, "closed", CancellationToken.None);
            }
        }

        private static async Task PumpAsync(WebSocket from, WebSocket to)
        {
            var buffer = new byte[8192];
            while (from.State == WebSocketState.Open && to.State == WebSocketState.Open)
            {
                var result = await from.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (to.State == WebSocketState.Open)
                        await to.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None);
                    return;
                }

                await to.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, CancellationToken.None);
            }
        }

        private static int FindHeaderEnd(byte[] payload)
        {
            for (var i = 0; i + 3 < payload.Length; i++)
            {
                if (payload[i] == '\r' && payload[i + 1] == '\n' && payload[i + 2] == '\r' && payload[i + 3] == '\n')
                    return i;
            }

            return -1;
        }

        private static int StatusCode(byte[] payload)
        {
            var line = Encoding.ASCII.GetString(payload.AsSpan(0, Math.Min(payload.Length, 32)));
            var parts = line.Split(' ');
            return parts.Length > 1 && int.TryParse(parts[1], out var code) ? code : 502;
        }
    }
}
