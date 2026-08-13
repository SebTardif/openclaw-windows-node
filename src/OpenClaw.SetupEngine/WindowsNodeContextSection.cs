namespace OpenClaw.SetupEngine;

internal static class WindowsNodeContextSection
{
    public const string BeginMarker = "<!-- BEGIN OPENCLAW WINDOWS NODE CONTEXT: managed by OpenClaw Windows setup -->";
    public const string EndMarker = "<!-- END OPENCLAW WINDOWS NODE CONTEXT -->";

    public const string Payload = """
This WSL gateway may be paired with the OpenClaw Windows tray node. For Windows desktop, Windows files, screenshots, camera, notifications, browser proxy, or Windows commands, use the `nodes` tool (`status` / `describe`) and target the Windows node instead of assuming the WSL shell can do it.

For Windows shell work, use `exec host=node` / `system.run`; normal gateway exec runs in WSL. Before claiming that camera, browser proxy, or system.run is unavailable, inspect the Windows Hub capability state with local MCP `app.connection.status` when available. Its `capabilities` entries distinguish the Settings toggle, Windows permission, Gateway declaration, pairing/approval, local MCP exposure, and runtime readiness, and provide the repair path. If the local MCP diagnostic is unavailable, ask the user to copy Capability diagnostics from Windows Hub Command Center. Do not infer camera permission from the Settings toggle alone.
""";

    public static string ManagedBlock => $"{BeginMarker}\n{Payload.TrimEnd()}\n{EndMarker}";
}
