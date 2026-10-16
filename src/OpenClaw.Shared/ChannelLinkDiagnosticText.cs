using System.Text.RegularExpressions;

namespace OpenClaw.Shared;

/// <summary>
/// Text for the Channels page linking diagnostic. The picture still shows
/// the QR. This text must not include the image payload.
/// </summary>
public static class ChannelLinkDiagnosticText
{
    private const int MaxResponseLength = 1000;

    /// <summary>
    /// Builds the selectable diagnostic block. QR image values are removed
    /// from the parameters, the error, and the raw response.
    /// </summary>
    public static string Format(string method, string channelId, string? parametersJson, string? error, string? rawResponse)
    {
        var parts = new List<string>
        {
            $"Method:   {method}",
            $"Channel:  {channelId}",
            $"Params:   {Redact(parametersJson)}",
        };
        if (!string.IsNullOrEmpty(error))
            parts.Add($"Error:    {Redact(error)}");
        if (!string.IsNullOrEmpty(rawResponse))
        {
            var redacted = Redact(rawResponse);
            var trimmed = redacted.Length > MaxResponseLength
                ? redacted[..MaxResponseLength] + "…"
                : redacted;
            parts.Add($"Response: {trimmed}");
        }

        return string.Join("\n", parts);
    }

    /// <summary>
    /// Removes pairing-image values. JSON fields named qrDataUrl or
    /// currentQrDataUrl are replaced, and any data:image payload is too.
    /// </summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var withoutFields = Regex.Replace(
            text,
            @"(""(?i:qrDataUrl|currentQrDataUrl)""\s*:\s*"")[^""]*("")",
            "$1<omitted>$2");
        return Regex.Replace(
            withoutFields,
            @"data:image\\?/[A-Za-z0-9.+-]+;base64,[A-Za-z0-9+/=]+",
            "<omitted>");
    }
}
