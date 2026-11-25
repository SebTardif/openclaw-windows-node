namespace OpenClawTray.Pages;

/// <summary>
/// Decides whether a late <c>agents.files.get</c> body still belongs to the
/// request that asked for it. The list path already checks the agent id.
/// The body path has to do the same, plus the load token and request path.
/// </summary>
public static class LegacyAgentFileApply
{
    public readonly record struct Request(string AgentId, string RequestPath, int Token);

    public static bool IsCurrent(Request? inFlight, string? currentAgentId, int currentToken)
    {
        if (inFlight is not { } request)
            return false;
        if (currentToken != request.Token)
            return false;
        if (string.IsNullOrEmpty(currentAgentId) || string.IsNullOrEmpty(request.AgentId))
            return false;
        return string.Equals(currentAgentId, request.AgentId, StringComparison.OrdinalIgnoreCase);
    }

    public static bool ReturnedNameMatches(string requestPath, string? returnedName)
    {
        if (string.IsNullOrEmpty(requestPath) || string.IsNullOrEmpty(returnedName))
            return false;
        if (string.Equals(Normalize(requestPath), Normalize(returnedName), StringComparison.OrdinalIgnoreCase))
            return true;
        if (returnedName.Contains('/') || returnedName.Contains('\\'))
            return false;
        return string.Equals(Leaf(requestPath), returnedName, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static string Leaf(string path)
    {
        var normalized = path.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        return slash >= 0 ? normalized[(slash + 1)..] : normalized;
    }
}
