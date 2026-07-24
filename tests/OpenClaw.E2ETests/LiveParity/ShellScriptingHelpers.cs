namespace OpenClaw.E2ETests.LiveParity;

/// <summary>
/// Tiny shared shell-scripting helper reused across the live-parity WSL
/// staging and proof code, instead of duplicating a private copy per file
/// as elsewhere in this test project.
/// </summary>
internal static class ShellScriptingHelpers
{
    /// <summary>Wraps a value in single quotes for safe interpolation into a bash script.</summary>
    public static string SingleQuote(string value) =>
        "'" + value.Replace("'", "'\"'\"'") + "'";
}
