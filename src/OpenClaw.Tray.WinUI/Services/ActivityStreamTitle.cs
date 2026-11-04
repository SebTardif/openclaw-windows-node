using OpenClaw.Shared;

namespace OpenClawTray.Services;

/// <summary>
/// Title stored for a tool activity row. Support text copies this title and
/// says command arguments are excluded, so the title is the tool and phase only.
/// </summary>
internal static class ActivityStreamTitle
{
    internal static string Format(string sessionKey, AgentActivity activity)
    {
        var name = string.IsNullOrWhiteSpace(activity.ToolName)
            ? activity.Kind.ToString()
            : activity.ToolName.Trim();
        var phase = activity.State?.Trim() ?? "";
        var detail = string.IsNullOrEmpty(phase) ? name : $"{name} {phase}";
        return $"{sessionKey}: {detail}";
    }
}
