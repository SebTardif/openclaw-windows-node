using System.Net;

namespace OpenClawTray.Helpers;

public static class GatewayDashboardUrlBuilder
{
    public static string Build(
        string gatewayUrl,
        string? path,
        string? sharedGatewayToken,
        bool appendSharedGatewayToken,
        string? ownedLoopbackOrigin = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayUrl);

        var endpoint = new Uri(gatewayUrl
            .Replace("ws://", "http://", StringComparison.OrdinalIgnoreCase)
            .Replace("wss://", "https://", StringComparison.OrdinalIgnoreCase), UriKind.Absolute);
        if (endpoint.Scheme is not ("http" or "https"))
            throw new ArgumentException("Dashboard requires an HTTP endpoint.", nameof(gatewayUrl));

        if (!string.IsNullOrWhiteSpace(ownedLoopbackOrigin))
            endpoint = ReplaceAuthorityWithOwnedLoopback(endpoint, ownedLoopbackOrigin);

        var baseUrl = endpoint.GetLeftPart(UriPartial.Path).TrimEnd('/');
        var url = string.IsNullOrWhiteSpace(path)
            ? baseUrl + endpoint.Query
            : $"{baseUrl}/{path.TrimStart('/')}";
        var fragment = endpoint.Fragment;

        if (appendSharedGatewayToken && !string.IsNullOrEmpty(sharedGatewayToken))
        {
            var fields = fragment.TrimStart('#').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(field => !Uri.UnescapeDataString(field.Split('=', 2)[0])
                    .Equals("token", StringComparison.OrdinalIgnoreCase));
            fragment = "#" + string.Join("&", fields.Append("token=" + Uri.EscapeDataString(sharedGatewayToken)));
        }

        return url + fragment;
    }

    private static Uri ReplaceAuthorityWithOwnedLoopback(Uri endpoint, string ownedLoopbackOrigin)
    {
        if (!Uri.TryCreate(ownedLoopbackOrigin, UriKind.Absolute, out var loopback) ||
            !string.Equals(loopback.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            !IPAddress.TryParse(loopback.IdnHost, out var address) ||
            !IPAddress.IsLoopback(address) ||
            loopback.Port is < 1 or > 65535)
        {
            throw new ArgumentException(
                "Dashboard tunnel origin must be an HTTP loopback endpoint.",
                nameof(ownedLoopbackOrigin));
        }

        return new UriBuilder(endpoint)
        {
            Scheme = Uri.UriSchemeHttp,
            Host = loopback.Host,
            Port = loopback.Port,
        }.Uri;
    }
}
