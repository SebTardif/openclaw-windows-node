using System.Collections.Concurrent;
using System.Net;
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
    private static readonly ConcurrentDictionary<string, Handoff> Live = new();

    public static string Start(Func<Task<bool>> owned, string destination)
    {
        ArgumentNullException.ThrowIfNull(owned);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var handoff = new Handoff(owned, destination);
        Live[handoff.Nonce] = handoff;
        handoff.Start();
        return handoff.BrowserUrl;
    }

    private sealed class Handoff
    {
        private readonly Func<Task<bool>> _owned;
        private readonly string _destination;
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _lifetime = new(TimeSpan.FromMinutes(2));
        private int _delivered;

        public Handoff(Func<Task<bool>> owned, string destination)
        {
            _owned = owned;
            _destination = destination;
            Nonce = Convert.ToHexString(Guid.NewGuid().ToByteArray());
            var port = FreePort();
            BrowserUrl = $"http://127.0.0.1:{port}/d/{Nonce}";
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        }

        public string Nonce { get; }
        public string BrowserUrl { get; }

        public void Start()
        {
            _listener.Start();
            _ = ServeAsync();
        }

        private async Task ServeAsync()
        {
            try
            {
                while (_listener.IsListening && !_lifetime.IsCancellationRequested)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync().WaitAsync(_lifetime.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    _ = AnswerAsync(context);
                }
            }
            catch (HttpListenerException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                Stop();
            }
        }

        private async Task AnswerAsync(HttpListenerContext context)
        {
            var response = context.Response;
            try
            {
                var path = context.Request.Url?.AbsolutePath ?? "";
                if (!path.Equals($"/d/{Nonce}", StringComparison.Ordinal))
                {
                    if (!await _owned())
                    {
                        response.StatusCode = (int)HttpStatusCode.NotFound;
                        response.Close();
                        return;
                    }

                    if (context.Request.IsWebSocketRequest)
                    {
                        await ProxyWebSocketAsync(context);
                        return;
                    }

                    await ProxyAsync(context);
                    return;
                }

                var first = Interlocked.CompareExchange(ref _delivered, 1, 0) == 0 && await _owned();
                if (!first)
                {
                    Interlocked.Exchange(ref _delivered, 1);
                    response.StatusCode = (int)HttpStatusCode.NotFound;
                    response.Close();
                    return;
                }

                // Stay on this listener. A later navigation to the tunnel port
                // would hand the fragment to whoever bound that port.
                var html = "<!doctype html><meta charset=\"utf-8\"><script>location.replace(" +
                    JsonSerializer.Serialize(SameOriginDestination()) + ")</script>";
                var bytes = Encoding.UTF8.GetBytes(html);
                response.StatusCode = (int)HttpStatusCode.OK;
                response.ContentType = "text/html; charset=utf-8";
                response.ContentLength64 = bytes.Length;
                await response.OutputStream.WriteAsync(bytes);
                response.Close();
            }
            catch (Exception)
            {
                try { response.Abort(); } catch (Exception) { }
            }
        }

        private string SameOriginDestination()
        {
            var destination = new Uri(_destination);
            var local = new Uri(BrowserUrl);
            var hash = destination.Fragment;
            var gateway = Uri.EscapeDataString($"ws://127.0.0.1:{local.Port}/");
            hash = string.IsNullOrEmpty(hash)
                ? "#gatewayUrl=" + gateway
                : hash + "&gatewayUrl=" + gateway;
            var path = string.IsNullOrEmpty(destination.AbsolutePath) ? "/" : destination.AbsolutePath;
            return $"http://127.0.0.1:{local.Port}{path}{destination.Query}{hash}";
        }

        private async Task ProxyAsync(HttpListenerContext context)
        {
            var incoming = context.Request.Url;
            var pathAndQuery = (incoming?.PathAndQuery) ?? "/";
            var destination = new Uri(_destination);
            var scheme = destination.Scheme == "https" &&
                (destination.Host is "localhost" or "127.0.0.1")
                ? "http"
                : destination.Scheme;
            var upstream = new Uri($"{scheme}://{destination.Host}:{destination.Port}{pathAndQuery}");
            using var client = new HttpClient();
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.HttpMethod), upstream);
            using var reply = await client.SendAsync(request);
            context.Response.StatusCode = (int)reply.StatusCode;
            var bytes = await reply.Content.ReadAsByteArrayAsync();
            if (reply.Content.Headers.ContentType is { } type)
                context.Response.ContentType = type.ToString();
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }

        private async Task ProxyWebSocketAsync(HttpListenerContext context)
        {
            var socket = await context.AcceptWebSocketAsync(null);
            var destination = new Uri(_destination);
            var incoming = context.Request.Url;
            var upstreamUri = new Uri($"ws://{destination.Host}:{destination.Port}{incoming?.PathAndQuery}");
            using var upstream = new ClientWebSocket();
            try
            {
                await upstream.ConnectAsync(upstreamUri, _lifetime.Token);
                await Task.WhenAll(
                    PumpAsync(socket.WebSocket, upstream),
                    PumpAsync(upstream, socket.WebSocket));
            }
            catch (Exception)
            {
                if (socket.WebSocket.State == WebSocketState.Open)
                    await socket.WebSocket.CloseAsync(WebSocketCloseStatus.InternalServerError, "closed", CancellationToken.None);
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
                    await to.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None);
                    return;
                }

                await to.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, CancellationToken.None);
            }
        }

        private void Stop()
        {
            Live.TryRemove(Nonce, out _);
            _lifetime.Cancel();
            if (_listener.IsListening)
                _listener.Stop();
            _listener.Close();
        }

        private static int FreePort()
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
    }
}
