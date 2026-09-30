using System;
using System.Net.Http;

namespace OpenClawTray.Helpers;

// A loopback readiness URL carries the gateway token in the query, so that
// host skips the process proxy. A remote URL keeps the proxy.
internal static class ChatReadinessClient
{
    internal static SocketsHttpHandler CreateHandler(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return new SocketsHttpHandler
        {
            UseProxy = !url.IsLoopback,
        };
    }

    internal static HttpClient Create(string url)
    {
        var parsed = Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri
            : new Uri("http://127.0.0.1/");
        return new HttpClient(CreateHandler(parsed), disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(3),
        };
    }
}
