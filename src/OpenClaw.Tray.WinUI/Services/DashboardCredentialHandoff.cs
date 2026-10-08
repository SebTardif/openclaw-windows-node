using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
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
                var allowed = path.Equals($"/d/{Nonce}", StringComparison.Ordinal) &&
                    Interlocked.CompareExchange(ref _delivered, 1, 0) == 0 &&
                    await _owned();
                if (!allowed)
                {
                    if (path.Equals($"/d/{Nonce}", StringComparison.Ordinal))
                        Interlocked.Exchange(ref _delivered, 1);
                    response.StatusCode = (int)HttpStatusCode.NotFound;
                    response.Close();
                    return;
                }

                var html = "<!doctype html><meta charset=\"utf-8\"><script>location.replace(" +
                    JsonSerializer.Serialize(_destination) + ")</script>";
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
