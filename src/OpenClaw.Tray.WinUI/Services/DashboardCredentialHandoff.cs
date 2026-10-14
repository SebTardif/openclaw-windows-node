using System.Collections.Concurrent;
using System.Globalization;
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
    private static readonly ConcurrentDictionary<string, OriginBinding> OriginPorts = new();
    private static readonly ConcurrentDictionary<int, HttpListener> Listeners = new();
    private static readonly object ListenerGate = new();
    private static readonly JsonSerializerOptions StoreJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static string Start(
        Func<Task<bool>> owned,
        string destination,
        string? tlsHost = null,
        string? originKey = null,
        string? destinationIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(owned);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var key = string.IsNullOrWhiteSpace(originKey) ? "default" : originKey;
        var identity = string.IsNullOrWhiteSpace(destinationIdentity)
            ? ProxiedOriginIdentity(destination)
            : destinationIdentity;
        var binding = PortFor(key, identity);
        try
        {
            EnsureListener(binding.Port);
        }
        catch (HttpListenerException ex)
        {
            throw new InvalidOperationException(
                $"Dashboard origin 127.0.0.1:{binding.Port} is already in use. Close the program using that port and open the dashboard again.",
                ex);
        }
        var handoff = new Handoff(owned, destination, tlsHost, binding.Port, binding.Session);
        Live[handoff.Nonce] = handoff;
        _ = handoff.ExpireUnusedAsync();
        return handoff.BrowserUrl;
    }

    /// <summary>
    /// Test override for the per-gateway origin file. Production uses the tray data directory.
    /// </summary>
    internal static string? OriginStorePath { get; set; }

    internal static void ResetOriginsForTests() => OriginPorts.Clear();

    /// <summary>
    /// Saved gateway identity, without the token. A retargeted record keeps its id
    /// and must not keep the previous gateway's browser origin.
    /// </summary>
    internal static string DestinationIdentity(GatewayRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var saved = string.IsNullOrWhiteSpace(record.Url) ? "" : SavedUrlIdentity(record.Url);
        if (record.SshTunnel is not { } tunnel)
            return saved;
        return saved + "|" + tunnel.User + "@" + tunnel.Host + ":" + tunnel.SshPort
            + ">" + tunnel.LocalPort + ":" + tunnel.RemotePort;
    }

    private static OriginBinding PortFor(string key, string destinationIdentity)
    {
        var cacheKey = key + "\n" + destinationIdentity;
        if (OriginPorts.TryGetValue(cacheKey, out var cached))
            return cached;

        lock (ListenerGate)
        {
            if (OriginPorts.TryGetValue(cacheKey, out cached))
                return cached;

            var saved = ReadStore();
            var match = saved.FirstOrDefault(entry =>
                entry.Key == key && entry.Destination == destinationIdentity);
            if (match is null)
            {
                // An older file stored only the gateway id. Adopt that port for the
                // first destination seen, then leave it so a later destination
                // cannot inherit the same browser profile.
                var legacy = saved.FirstOrDefault(entry =>
                    entry.Key == key && entry.Destination.Length == 0);
                if (legacy is not null)
                {
                    legacy.Destination = destinationIdentity;
                    if (string.IsNullOrEmpty(legacy.Session))
                        legacy.Session = NewSessionId();
                    match = legacy;
                    WriteStore(saved);
                }
            }

            if (match is null)
            {
                var port = key == "default" || !PortTaken(Port, saved) ? Port : AllocatePort(saved);
                match = new OriginStoreEntry
                {
                    Key = key,
                    Destination = destinationIdentity,
                    Port = port,
                    Session = NewSessionId(),
                };
                saved.Add(match);
                WriteStore(saved);
            }
            else if (string.IsNullOrEmpty(match.Session))
            {
                match.Session = NewSessionId();
                WriteStore(saved);
            }

            cached = new OriginBinding(match.Port, match.Session);
            OriginPorts[cacheKey] = cached;
            return cached;
        }
    }

    private static bool PortTaken(int port, List<OriginStoreEntry> saved) =>
        saved.Exists(entry => entry.Port == port) || OriginPorts.Values.Any(item => item.Port == port);

    private static int AllocatePort(List<OriginStoreEntry> saved)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var port = FreePort();
            if (port == Port || PortTaken(port, saved))
                continue;
            return port;
        }

        throw new InvalidOperationException("No free loopback port is available for a dashboard origin.");
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static string StoreFilePath() =>
        OriginStorePath ?? Path.Combine(AppIdentity.ResolveRoamingDataDirectory(), "dashboard-origins.json");

    private static List<OriginStoreEntry> ReadStore()
    {
        try
        {
            var path = StoreFilePath();
            if (!File.Exists(path))
                return [];
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("Dashboard origin file is not an object.");
            if (doc.RootElement.TryGetProperty("Entries", out var entries) ||
                doc.RootElement.TryGetProperty("entries", out entries))
            {
                return JsonSerializer.Deserialize<OriginStoreFile>(doc.RootElement.GetRawText(), StoreJson)?.Entries ?? [];
            }

            var legacy = new List<OriginStoreEntry>();
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Number &&
                    property.Value.TryGetInt32(out var port))
                {
                    legacy.Add(new OriginStoreEntry { Key = property.Name, Port = port });
                }
            }

            return legacy;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("The saved dashboard origins could not be read.", ex);
        }
    }

    private static void WriteStore(List<OriginStoreEntry> saved)
    {
        var path = StoreFilePath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(new OriginStoreFile { Entries = saved }, StoreJson);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, path, overwrite: true);
    }

    private static string ProxiedOriginIdentity(string destination)
    {
        var uri = new Uri(destination);
        return uri.Scheme + "://" + uri.IdnHost + ":" + uri.Port;
    }

    private static string SavedUrlIdentity(string savedUrl)
    {
        if (!Uri.TryCreate(savedUrl, UriKind.Absolute, out var saved))
            return savedUrl;
        var path = string.IsNullOrEmpty(saved.AbsolutePath) ? "/" : saved.AbsolutePath;
        return saved.Scheme + "://" + saved.IdnHost + ":" + saved.Port + path;
    }

    private static string NewSessionId() => Convert.ToHexString(Guid.NewGuid().ToByteArray());

    private sealed record OriginBinding(int Port, string Session);

    private sealed class OriginStoreFile
    {
        public List<OriginStoreEntry> Entries { get; set; } = [];
    }

    private sealed class OriginStoreEntry
    {
        public string Key { get; set; } = "";
        public string Destination { get; set; } = "";
        public int Port { get; set; }
        public string Session { get; set; } = "";
    }

    private static void EnsureListener(int port)
    {
        lock (ListenerGate)
        {
            if (Listeners.TryGetValue(port, out var existing) && existing.IsListening)
                return;
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            Listeners[port] = listener;
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
            var requestPort = RequestPort(context);
            if (path.StartsWith("/d/", StringComparison.Ordinal))
            {
                var nonce = path["/d/".Length..];
                if (!Live.TryGetValue(nonce, out var handoff) || handoff.Port != requestPort)
                {
                    response.StatusCode = (int)HttpStatusCode.NotFound;
                    response.Close();
                    return;
                }

                await handoff.DeliverAsync(context);
                return;
            }

            if (!TrySession(path, requestPort, out var session, out var upstreamPath) &&
                !TrySessionFromReferer(context.Request.Headers["Referer"], path, requestPort, out session, out upstreamPath))
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

    private static int RequestPort(HttpListenerContext context) =>
        context.Request.LocalEndPoint is IPEndPoint local
            ? local.Port
            : context.Request.Url?.Port ?? 0;

    private static bool TrySession(string path, int requestPort, out Handoff session, out string upstreamPath)
    {
        Handoff? match = null;
        var matchedPrefix = "";
        foreach (var candidate in Live.Values)
        {
            if (candidate.Port != requestPort)
                continue;
            var prefix = "/s/" + candidate.SessionId;
            if (!path.Equals(prefix, StringComparison.Ordinal) &&
                !path.StartsWith(prefix + "/", StringComparison.Ordinal))
                continue;
            if (match is not null && candidate.Generation < match.Generation)
                continue;
            match = candidate;
            matchedPrefix = prefix;
        }

        if (match is null)
        {
            session = null!;
            upstreamPath = "/";
            return false;
        }

        session = match;
        upstreamPath = path[matchedPrefix.Length..];
        if (string.IsNullOrEmpty(upstreamPath))
            upstreamPath = "/";
        return true;
    }

    private static bool TrySessionFromReferer(
        string? referer,
        string requestPath,
        int requestPort,
        out Handoff session,
        out string upstreamPath)
    {
        session = null!;
        upstreamPath = requestPath;
        if (string.IsNullOrWhiteSpace(referer) || !Uri.TryCreate(referer, UriKind.Absolute, out var uri))
            return false;
        if (uri.Port != requestPort)
            return false;
        if (!TrySession(uri.AbsolutePath, requestPort, out session, out _))
            return false;
        if (string.IsNullOrEmpty(upstreamPath))
            upstreamPath = "/";
        return true;
    }

    internal static byte[]? DecodeChunked(byte[] body)
    {
        var output = new MemoryStream();
        var index = 0;
        while (index < body.Length)
        {
            var lineEnd = IndexOfCrlf(body, index);
            if (lineEnd < 0)
                return null;
            var sizeText = Encoding.ASCII.GetString(body, index, lineEnd - index);
            var extension = sizeText.IndexOf(';');
            if (extension >= 0)
                sizeText = sizeText[..extension];
            if (!int.TryParse(sizeText.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size) || size < 0)
                return null;
            index = lineEnd + 2;
            if (size == 0)
                return output.ToArray();
            if (index + size + 2 > body.Length)
                return null;
            output.Write(body, index, size);
            if (body[index + size] != '\r' || body[index + size + 1] != '\n')
                return null;
            index += size + 2;
        }

        return null;
    }

    private static int IndexOfCrlf(byte[] body, int start)
    {
        for (var i = start; i + 1 < body.Length; i++)
        {
            if (body[i] == '\r' && body[i + 1] == '\n')
                return i;
        }

        return -1;
    }

    internal static string? ReadHeader(string headerText, string name)
    {
        foreach (var line in headerText.Split("\r\n"))
        {
            var prefix = name + ":";
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return line[prefix.Length..].Trim();
        }

        return null;
    }

    internal static string RewriteLocation(string location, string sessionPrefix)
    {
        if (location.StartsWith(sessionPrefix, StringComparison.Ordinal))
            return location;
        if (location.StartsWith("/", StringComparison.Ordinal))
            return sessionPrefix + location;
        return location;
    }

    internal static string RewriteRootAbsolute(string html, string sessionPrefix)
    {
        return System.Text.RegularExpressions.Regex.Replace(
            html,
            "(src|href|action)=\"/(?!s/)",
            "$1=\"" + sessionPrefix + "/",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// The Control UI requests assets from the base path the Gateway declares.
    /// That path has to include this session. Referer is not a substitute:
    /// the Gateway sends Referrer-Policy: no-referrer, and this proxy keeps that header.
    /// </summary>
    internal static string RewriteDeclaredResourceBase(string body, string sessionPrefix)
    {
        var prefix = sessionPrefix.TrimEnd('/');
        body = System.Text.RegularExpressions.Regex.Replace(
            body,
            "window\\.__OPENCLAW_CONTROL_UI_BASE_PATH__\\s*=\\s*(\"[^\"]*\"|'[^']*')",
            match => "window.__OPENCLAW_CONTROL_UI_BASE_PATH__=" +
                JsonSerializer.Serialize(CombineResourceBase(prefix, Unquote(match.Groups[1].Value))));
        body = System.Text.RegularExpressions.Regex.Replace(
            body,
            "<base\\s+href\\s*=\\s*(\"[^\"]*\"|'[^']*')",
            match => "<base href=" + JsonSerializer.Serialize(CombineResourceBase(prefix, Unquote(match.Groups[1].Value))),
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return System.Text.RegularExpressions.Regex.Replace(
            body,
            "\"basePath\"\\s*:\\s*\"([^\"]*)\"",
            match => "\"basePath\":" + JsonSerializer.Serialize(CombineResourceBase(prefix, match.Groups[1].Value)));
    }

    internal static string AdaptSetCookie(string setCookie, string sessionPrefix)
    {
        var parts = setCookie.Split(';');
        var kept = new List<string> { parts[0].Trim() };
        var sawPath = false;
        foreach (var raw in parts.Skip(1))
        {
            var part = raw.Trim();
            if (part.Length == 0)
                continue;
            var name = part.Split('=', 2)[0].Trim();
            if (name.Equals("Domain", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Secure", StringComparison.OrdinalIgnoreCase))
                continue;
            if (name.Equals("SameSite", StringComparison.OrdinalIgnoreCase) &&
                part.Contains("None", StringComparison.OrdinalIgnoreCase))
            {
                kept.Add("SameSite=Lax");
                continue;
            }
            if (name.Equals("Path", StringComparison.OrdinalIgnoreCase))
            {
                sawPath = true;
                var value = part.Split('=', 2).Length == 2 ? part.Split('=', 2)[1].Trim() : "/";
                kept.Add("Path=" + CookiePath(value, sessionPrefix));
                continue;
            }

            kept.Add(part);
        }

        if (!sawPath)
            kept.Add("Path=" + sessionPrefix.TrimEnd('/'));
        return string.Join("; ", kept);
    }

    private static string Unquote(string literal)
    {
        if (literal.Length >= 2 && (literal[0] == '"' || literal[0] == '\''))
            return literal[1..^1];
        return literal;
    }

    private static string CombineResourceBase(string sessionPrefix, string declared)
    {
        var prefix = sessionPrefix.TrimEnd('/');
        if (string.IsNullOrEmpty(declared) || declared == "/" || declared == "./")
            return prefix + "/";
        if (declared.StartsWith(prefix + "/", StringComparison.Ordinal) ||
            declared.Equals(prefix, StringComparison.Ordinal))
            return declared.EndsWith('/') ? declared : declared + "/";
        var path = declared.StartsWith('/') ? declared : "/" + declared;
        var combined = prefix + path.TrimEnd('/');
        return combined + "/";
    }

    private static string CookiePath(string path, string sessionPrefix)
    {
        var prefix = sessionPrefix.TrimEnd('/');
        if (string.IsNullOrEmpty(path) || path == "/")
            return prefix;
        if (path.StartsWith(prefix + "/", StringComparison.Ordinal) || path.Equals(prefix, StringComparison.Ordinal))
            return path;
        if (!path.StartsWith('/'))
            path = "/" + path;
        return prefix + path;
    }

    private static List<string> ReadSetCookies(string headerText)
    {
        var cookies = new List<string>();
        foreach (var line in headerText.Split("\r\n"))
        {
            const string name = "Set-Cookie:";
            if (line.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                cookies.Add(line[name.Length..].Trim());
        }

        return cookies;
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
        private readonly int _port;
        private int _delivered;
        private static int _nextGeneration;

        public Handoff(Func<Task<bool>> owned, string destination, string? tlsHost, int port, string sessionId)
        {
            _owned = owned;
            _destination = destination;
            _tlsHost = string.IsNullOrWhiteSpace(tlsHost) ? new Uri(destination).Host : tlsHost;
            _port = port;
            SessionId = sessionId;
            Generation = Interlocked.Increment(ref _nextGeneration);
            Nonce = Convert.ToHexString(Guid.NewGuid().ToByteArray());
            BrowserUrl = $"http://127.0.0.1:{port}/d/{Nonce}";
        }

        public string Nonce { get; }
        public string SessionId { get; }
        public int Generation { get; }
        public string BrowserUrl { get; }
        public int Port => _port;
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
            // The launch nonce is only the one-use /d/ page. The Control UI stores
            // settings under gatewayUrl, including its path, so that path stays
            // on the saved session for this destination.
            var gateway = Uri.EscapeDataString($"ws://127.0.0.1:{_port}/s/{SessionId}/");
            hash = string.IsNullOrEmpty(hash)
                ? "#gatewayUrl=" + gateway
                : hash + "&gatewayUrl=" + gateway;
            var path = string.IsNullOrEmpty(destination.AbsolutePath) ? "/" : destination.AbsolutePath;
            return $"http://127.0.0.1:{_port}/s/{SessionId}{path}{destination.Query}{hash}";
        }

        public async Task ProxyAsync(HttpListenerContext context, string upstreamPath)
        {
            var destination = new Uri(_destination);
            var query = context.Request.Url?.Query ?? "";
            var pathAndQuery = upstreamPath + query;
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(destination.Host, destination.Port);
            if (!BackendIsOwned(tcp) || !await _owned())
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

            var bodyBytes = Array.Empty<byte>();
            if (context.Request.HasEntityBody)
            {
                using var incoming = new MemoryStream();
                await context.Request.InputStream.CopyToAsync(incoming);
                bodyBytes = incoming.ToArray();
            }

            var requestHeader = new StringBuilder();
            requestHeader.Append($"{context.Request.HttpMethod} {pathAndQuery} HTTP/1.1\r\n");
            requestHeader.Append($"Host: {_tlsHost}\r\n");
            requestHeader.Append("Connection: close\r\n");
            foreach (var name in new[] { "Cookie", "Authorization", "Content-Type", "Accept", "Accept-Language" })
            {
                var value = context.Request.Headers[name];
                if (!string.IsNullOrEmpty(value))
                    requestHeader.Append($"{name}: {value}\r\n");
            }
            if (bodyBytes.Length > 0)
                requestHeader.Append($"Content-Length: {bodyBytes.Length}\r\n");
            requestHeader.Append("\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(requestHeader.ToString()));
            if (bodyBytes.Length > 0)
                await stream.WriteAsync(bodyBytes);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            var payload = buffer.ToArray();
            var headerEnd = FindHeaderEnd(payload);
            var body = headerEnd < 0 ? payload : payload[(headerEnd + 4)..];
            var headerText = Encoding.ASCII.GetString(payload, 0, headerEnd < 0 ? payload.Length : headerEnd);
            if (ReadHeader(headerText, "Transfer-Encoding") is { } transfer &&
                transfer.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                var decoded = DecodeChunked(body);
                if (decoded is null)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.BadGateway;
                    context.Response.Close();
                    return;
                }

                body = decoded;
            }
            context.Response.StatusCode = StatusCode(payload);
            CopySecurityHeaders(headerText, context.Response);
            var sessionPrefix = "/s/" + SessionId;
            foreach (var cookie in ReadSetCookies(headerText))
            {
                try
                {
                    context.Response.AppendHeader("Set-Cookie", AdaptSetCookie(cookie, sessionPrefix));
                }
                catch (ArgumentException)
                {
                }
            }
            if (ReadHeader(headerText, "Location") is { } location)
            {
                try
                {
                    context.Response.Headers["Location"] = RewriteLocation(location, sessionPrefix);
                }
                catch (ArgumentException)
                {
                }
            }
            if (ReadContentType(headerText) is { } contentType)
            {
                context.Response.ContentType = contentType;
                var textBody = contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase) ||
                    contentType.Contains("json", StringComparison.OrdinalIgnoreCase);
                if (textBody)
                {
                    var text = Encoding.UTF8.GetString(body);
                    if (contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                        text = RewriteRootAbsolute(text, sessionPrefix);
                    text = RewriteDeclaredResourceBase(text, sessionPrefix);
                    body = Encoding.UTF8.GetBytes(text);
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
            var query = context.Request.Url?.Query ?? "";
            using var probe = new TcpClient();
            await probe.ConnectAsync(destination.Host, destination.Port);
            if (!BackendIsOwned(probe) || !await _owned())
            {
                probe.Close();
                if (browser.WebSocket.State == WebSocketState.Open)
                    await browser.WebSocket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "closed", CancellationToken.None);
                return;
            }

            try
            {
                Stream socketStream = probe.GetStream();
                if (destination.Scheme == "https")
                {
                    var ssl = new SslStream(socketStream, leaveInnerStreamOpen: false);
                    await ssl.AuthenticateAsClientAsync(_tlsHost);
                    socketStream = ssl;
                }

                var key = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
                var upgrade =
                    $"GET {upstreamPath}{query} HTTP/1.1\r\nHost: {_tlsHost}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n";
                await socketStream.WriteAsync(Encoding.ASCII.GetBytes(upgrade));
                if (!await ReadUpgradeAcceptedAsync(socketStream))
                {
                    if (browser.WebSocket.State == WebSocketState.Open)
                        await browser.WebSocket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "closed", CancellationToken.None);
                    return;
                }

                using var upstream = WebSocket.CreateFromStream(
                    socketStream,
                    new WebSocketCreationOptions { IsServer = false });
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

        private static bool BackendIsOwned(TcpClient tcp)
        {
            if (tcp.Client.LocalEndPoint is not IPEndPoint local ||
                tcp.Client.RemoteEndPoint is not IPEndPoint remote)
                return false;
            var accepted = WindowsTcpListenerSnapshot.AcceptedProcessId(remote, local);
            var listener = WindowsTcpListenerSnapshot.MatchListener(
                WindowsTcpListenerSnapshot.Capture().Listeners,
                remote);
            return accepted is not null && listener is not null && accepted == listener.ProcessId;
        }

        private static async Task<bool> ReadUpgradeAcceptedAsync(Stream stream)
        {
            using var header = new MemoryStream();
            var window = new byte[4];
            var one = new byte[1];
            while (await stream.ReadAsync(one) > 0)
            {
                header.WriteByte(one[0]);
                window[0] = window[1];
                window[1] = window[2];
                window[2] = window[3];
                window[3] = one[0];
                if (window[0] == '\r' && window[1] == '\n' && window[2] == '\r' && window[3] == '\n')
                    break;
                if (header.Length > 8192)
                    return false;
            }

            var text = Encoding.ASCII.GetString(header.ToArray());
            return text.Contains(" 101 ", StringComparison.Ordinal);
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
