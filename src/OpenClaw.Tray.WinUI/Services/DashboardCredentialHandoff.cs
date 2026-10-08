using System.Collections.Concurrent;
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
    private static Handoff? _active;

    public static string Start(Func<Task<bool>> owned, string destination)
    {
        ArgumentNullException.ThrowIfNull(owned);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        EnsureListener();
        var handoff = new Handoff(owned, destination);
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

            var active = _active;
            if (active is null || !await active.Owned())
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                response.Close();
                return;
            }

            if (context.Request.IsWebSocketRequest)
            {
                await active.ProxyWebSocketAsync(context);
                return;
            }

            await active.ProxyAsync(context);
        }
        catch (Exception)
        {
            try { response.Abort(); } catch (Exception) { }
        }
    }

    private sealed class Handoff
    {
        private readonly Func<Task<bool>> _owned;
        private readonly string _destination;
        private int _delivered;

        public Handoff(Func<Task<bool>> owned, string destination)
        {
            _owned = owned;
            _destination = destination;
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

            _active = this;
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
            var gateway = Uri.EscapeDataString($"ws://127.0.0.1:{Port}/");
            hash = string.IsNullOrEmpty(hash)
                ? "#gatewayUrl=" + gateway
                : hash + "&gatewayUrl=" + gateway;
            var path = string.IsNullOrEmpty(destination.AbsolutePath) ? "/" : destination.AbsolutePath;
            return $"http://127.0.0.1:{Port}{path}{destination.Query}{hash}";
        }

        public async Task ProxyAsync(HttpListenerContext context)
        {
            var destination = new Uri(_destination);
            var pathAndQuery = context.Request.Url?.PathAndQuery ?? "/";
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(destination.Host, destination.Port);
            if (!await _owned())
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                context.Response.Close();
                return;
            }

            await using var raw = tcp.GetStream();
            Stream stream = raw;
            if (destination.Scheme == "https")
            {
                // The owned forward presents the gateway certificate, not a 127.0.0.1 name.
                var ssl = new SslStream(raw, leaveInnerStreamOpen: false, (_, _, _, _) => true);
                await ssl.AuthenticateAsClientAsync(destination.Host);
                stream = ssl;
            }

            var requestText =
                $"{context.Request.HttpMethod} {pathAndQuery} HTTP/1.1\r\nHost: {destination.Host}\r\nConnection: close\r\n\r\n";
            var requestBytes = Encoding.ASCII.GetBytes(requestText);
            await stream.WriteAsync(requestBytes);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            var payload = buffer.ToArray();
            var headerEnd = FindHeaderEnd(payload);
            var body = headerEnd < 0 ? payload : payload[(headerEnd + 4)..];
            context.Response.StatusCode = StatusCode(payload);
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        }

        public async Task ProxyWebSocketAsync(HttpListenerContext context)
        {
            var browser = await context.AcceptWebSocketAsync(null);
            var destination = new Uri(_destination);
            var scheme = destination.Scheme == "https" ? "wss" : "ws";
            var upstreamUri = new Uri($"{scheme}://{destination.Host}:{destination.Port}{context.Request.Url?.PathAndQuery}");
            using var upstream = new ClientWebSocket();
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
