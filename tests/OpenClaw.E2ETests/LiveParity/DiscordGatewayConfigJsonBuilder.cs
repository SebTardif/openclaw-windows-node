using System.Text.Json;

namespace OpenClaw.E2ETests.LiveParity;

/// <summary>
/// Maps the real gateway's Discord plugin config shape for a single-account
/// setup: `channels.discord.{enabled,allowBots,groupPolicy,guilds}`. This
/// shape was verified directly against the installed gateway's Discord
/// plugin TypeScript source (not merely inferred), specifically:
/// <list type="bullet">
/// <item>`extensions/discord/src/monitor/message-handler.preflight.ts` -
/// `allowBots` accepts the string literal "mentions" (bot-authored messages
/// are only dispatched when they mention the SUT bot); a bare `true` means
/// "allow all bot messages unconditionally" which is broader than the task
/// requires, and any other value means "drop all bot messages".</item>
/// <item>`extensions/discord/src/monitor/allow-list.ts` -
/// `DiscordGuildEntryResolved.channels` is a map keyed by channel id (or
/// name/slug); each entry accepts `requireMention` and `users` (a strict
/// allowlist of sender ids, evaluated by <c>resolveDiscordMemberAllowed</c>).
/// Setting `users` at the channel level takes precedence over any
/// guild-level `users` list, which is exactly the "strict allowlist for the
/// driver, scoped to the selected guild/channel" the live parity lane
/// needs.</item>
/// <item>`extensions/discord/src/group-policy.ts` /
/// `isDiscordGroupAllowedByPolicy` - with `groupPolicy: "allowlist"`, a
/// message is only accepted when its guild is present under `guilds` *and*,
/// when that guild declares a non-empty `channels` map (as this builder
/// always does), the specific channel is also present there. This means
/// configuring exactly one guild/channel pair scopes the gateway to that
/// pair alone; every other channel in the guild is implicitly rejected.</item>
/// </list>
/// The shape was further confirmed end-to-end with a live, non-destructive
/// `openclaw config set channels.discord &lt;json&gt; --strict-json --merge
/// --dry-run` (and a backed-up, trap-restored non-dry-run run) against the
/// real gateway CLI installed in this dev sandbox's WSL distribution: the
/// resulting `openclaw.json` matched this builder's output byte-for-byte
/// (aside from the token, which is set separately) and `--merge` correctly
/// preserved the `channels.discord.token` SecretRef written by the prior
/// `config set channels.discord.token --ref-provider ...` call rather than
/// clobbering it, so the token-then-merge call order used by
/// <see cref="WslSecretGatewayScope"/> is safe. Unlike the model-provider
/// path, the gateway reported this change requires a restart to take
/// effect ("Restart the gateway to apply"), which the scope already does.
/// See docs/LIVE_PARITY_TESTING.md "Known limitations" for what remains
/// unverified (this repository does not vendor the plugin source; a future
/// gateway release could change the shape).
/// </summary>
internal static class DiscordGatewayConfigJsonBuilder
{
    /// <summary>
    /// Builds the JSON body for `openclaw config set channels.discord &lt;json&gt;
    /// --strict-json --merge`. Deliberately omits the `token` key: the
    /// token is set separately via a SecretRef CLI call so its value never
    /// appears in this JSON body or in any config-set argument.
    /// </summary>
    public static string Build(RealChannelProfile profile) => JsonSerializer.Serialize(new
    {
        enabled = true,
        allowBots = "mentions",
        groupPolicy = "allowlist",
        guilds = new Dictionary<string, object>
        {
            [profile.GuildId] = new
            {
                channels = new Dictionary<string, object>
                {
                    [profile.ChannelId] = new
                    {
                        requireMention = true,
                        users = new[] { profile.Driver.UserId },
                    },
                },
            },
        },
    });
}
