namespace OpenClawTray.Windows;

public static class CanvasGatewayAuth
{
    private const string CanvasVirtualHostOrigin = "https://openclaw-canvas.local";

    public static bool ShouldAttachGatewayBearer(string? documentUri, string? requestUri, string? trustedGatewayOrigin)
    {
        if (string.IsNullOrEmpty(documentUri) || string.IsNullOrEmpty(requestUri) || string.IsNullOrEmpty(trustedGatewayOrigin))
            return false;

        if (!IsOriginMatch(requestUri, trustedGatewayOrigin))
            return false;

        return IsOriginMatch(documentUri, trustedGatewayOrigin) ||
            IsOriginMatch(documentUri, CanvasVirtualHostOrigin);
    }

    private static bool IsOriginMatch(string uri, string origin)
    {
        return uri.StartsWith(origin, StringComparison.OrdinalIgnoreCase) &&
            (uri.Length == origin.Length ||
             uri[origin.Length] == '/' ||
             uri[origin.Length] == '?' ||
             uri[origin.Length] == '#');
    }
}
