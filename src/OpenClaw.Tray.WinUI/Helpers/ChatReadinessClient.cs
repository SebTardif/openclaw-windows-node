using System;
using System.Net.Http;

namespace OpenClawTray.Helpers;

// The legacy web-chat readiness URL carries the gateway token in the query.
// UseProxy stays false so HTTP_PROXY and ALL_PROXY cannot receive that URL.
internal static class ChatReadinessClient
{
    internal static SocketsHttpHandler CreateHandler()
        => new()
        {
            UseProxy = false,
        };

    internal static HttpClient Create()
        => new(CreateHandler(), disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(3),
        };
}
