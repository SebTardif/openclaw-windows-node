namespace OpenClawTray.Windows;

public static class CanvasGatewayAuth
{
    private const string CanvasVirtualHostOrigin = "https://openclaw-canvas.local";

    public static bool ShouldAttachGatewayBearer(
        string? documentUri,
        string? requestUri,
        string? trustedGatewayOrigin,
        string? initiatorUri = null)
    {
        if (string.IsNullOrEmpty(requestUri) || string.IsNullOrEmpty(trustedGatewayOrigin))
            return false;

        if (!IsOriginMatch(requestUri, trustedGatewayOrigin))
            return false;

        // A missing Referer must not inherit the top-level document. The only
        // request without an initiator that may carry the bearer is the first
        // A2UI navigation, while the document is still about:blank.
        if (string.IsNullOrEmpty(initiatorUri))
            return IsAboutBlank(documentUri) && IsTrustedA2uiNavigation(requestUri);

        if (IsAboutBlank(initiatorUri))
            return false;

        return IsOriginMatch(initiatorUri, trustedGatewayOrigin) ||
            IsOriginMatch(initiatorUri, CanvasVirtualHostOrigin);
    }

    private static bool IsAboutBlank(string? uri) =>
        !string.IsNullOrEmpty(uri) &&
        (uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase) ||
         uri.StartsWith("about:blank?", StringComparison.OrdinalIgnoreCase) ||
         uri.StartsWith("about:blank#", StringComparison.OrdinalIgnoreCase));

    private static bool IsTrustedA2uiNavigation(string requestUri)
    {
        if (!Uri.TryCreate(requestUri, UriKind.Absolute, out var uri))
            return false;

        return uri.AbsolutePath.StartsWith("/__openclaw__/a2ui/", StringComparison.OrdinalIgnoreCase);
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
