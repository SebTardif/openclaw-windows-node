using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace OpenClaw.E2ETests.LiveParity;

/// <summary>
/// Minimal Discord REST v10 client for the real channel parity lane. Used
/// directly from the Windows test process, once with the driver bot token
/// (posting the mention plus nonce, polling for the SUT's reply) and once
/// with the SUT bot token (best-effort deletion of its own reply). Never
/// logs message content, ids, or the bot token; callers are responsible for
/// keeping any diagnostics coarse (counts/state only).
/// </summary>
internal sealed class DiscordRestClient : IDisposable
{
    private const string DiscordApiBaseUrl = "https://discord.com/api/v10/";
    private readonly HttpClient _http;

    public DiscordRestClient(string botToken)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(DiscordApiBaseUrl, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(20),
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", botToken);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("OpenClawLiveParityE2E/1.0");
    }

    public async Task<string> PostMessageAsync(string channelId, string content)
    {
        using var body = new StringContent(JsonSerializer.Serialize(new { content }), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync($"channels/{channelId}/messages", body).ConfigureAwait(false);
        var responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Discord POST message failed with HTTP {(int)response.StatusCode}.");
        }

        using var doc = JsonDocument.Parse(responseText);
        return doc.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Discord POST message response did not include an id.");
    }

    public async Task<IReadOnlyList<DiscordMessage>> ListMessagesAfterAsync(string channelId, string afterMessageId, int limit = 50)
    {
        using var response = await _http.GetAsync(
            $"channels/{channelId}/messages?after={afterMessageId}&limit={limit}").ConfigureAwait(false);
        var responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Discord GET messages failed with HTTP {(int)response.StatusCode}.");
        }

        using var doc = JsonDocument.Parse(responseText);
        var results = new List<DiscordMessage>();
        foreach (var message in doc.RootElement.EnumerateArray())
        {
            var id = message.GetProperty("id").GetString() ?? string.Empty;
            var authorId = message.TryGetProperty("author", out var author) && author.TryGetProperty("id", out var authorIdElement)
                ? authorIdElement.GetString() ?? string.Empty
                : string.Empty;
            var content = message.TryGetProperty("content", out var contentElement) ? contentElement.GetString() ?? string.Empty : string.Empty;
            results.Add(new DiscordMessage(id, authorId, content));
        }

        return results;
    }

    /// <summary>
    /// Best-effort cleanup only: swallows failures and returns false rather
    /// than throwing, so a cleanup problem never masks the proof's own
    /// pass/fail outcome. Callers must not rely on this for anything other
    /// than tidiness.
    /// </summary>
    public async Task<bool> TryDeleteMessageAsync(string channelId, string messageId)
    {
        try
        {
            using var response = await _http.DeleteAsync($"channels/{channelId}/messages/{messageId}").ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        // slopwatch-ignore: SW003 Best-effort Discord message cleanup must not hide the test outcome.
        catch
        {
            return false;
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// One Discord message as read back through the REST API: id, author id,
/// and content. A reference-type record (not a struct) so it can be used
/// as the nullable match result type with <see cref="BoundedPoller"/>.
/// </summary>
internal sealed record DiscordMessage(string Id, string AuthorId, string Content);
