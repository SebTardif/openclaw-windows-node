# Live Model and Real Discord Channel Parity Testing

## Overview

`tests\OpenClaw.E2ETests\LiveParity` adds two independent, opt-in E2E lanes on
top of the existing published-gateway E2E fixture:

- **Live model lane**: configures a real, user-selected LLM provider/model in
  the real WSL gateway and drives one bounded chat turn through the native
  tray MCP `app.chat.send` / `app.chat.snapshot` surface, the same surface
  `PublishedGatewayRoundtripProof` uses for its deterministic mock proof.
- **Real channel lane**: configures a real Discord bot (the "system under
  test", SUT) in the real WSL gateway's Discord plugin, then uses a second,
  distinct Discord bot (the "driver") to post a message that mentions the SUT
  bot and drives an inbound-mention to agent to outbound-reply round trip.

The real channel lane is not an outbound-send-only check. It asserts the
full loop, in this exact order: the driver sends a message (outbound from
the driver's own REST call), Discord's own infrastructure delivers that
message as an inbound mention to the real gateway's Discord plugin, the
configured agent produces a reply (outbound from the SUT, through the
gateway), and only then does the driver read that reply back over the
Discord REST API. A successful driver send proves nothing about the gateway
by itself; the assertion is the driver's readback of a later, SUT-authored,
nonce-bearing message, which can only exist if Discord actually delivered
the inbound mention and the agent actually produced an outbound reply.

Both lanes prove real behavior that the repository's deterministic,
dependency-free mocks cannot: an actual model provider actually answering,
and an actual Discord bot actually receiving and reacting to a message.
Passing this live lane alongside the existing deterministic mock proof
(`PublishedGatewayRoundtripProof`, backed by `WslOpenAiResponsesMock`) does
not establish mock-vs-live behavioral parity. The two exercise different
code paths for different purposes: the mock proof is a fast, deterministic,
hermetic regression check of the native chat surface, and this live lane is
a real-dependency check of a specific configured provider or Discord
account. Neither substitutes for or validates the other. Both live lanes
cost real money or spend real third-party rate limit budget, so they are
disabled by default and never run in normal hosted CI. Only their
secretless contract tests (gate skip reasons, profile validation, secret
redaction, bounded polling) run in CI.

## Quick reference

| Variable | Purpose | Contains a secret? |
|---|---|---|
| `OPENCLAW_RUN_E2E` | Base opt-in gate shared with the rest of `OpenClaw.E2ETests` | No |
| `OPENCLAW_RUN_LIVE_MODEL_E2E` | Opt-in gate for the live model lane | No |
| `OPENCLAW_RUN_REAL_CHANNEL_E2E` | Opt-in gate for the real Discord channel lane | No |
| `OPENCLAW_LIVE_MODEL_PROFILE` | Absolute path to the live model profile JSON file | No (a path, not a credential) |
| `OPENCLAW_REAL_CHANNEL_PROFILE` | Absolute path to the real channel profile JSON file | No (a path, not a credential) |
| The environment variable named by a profile's `apiKeyEnvVar` / `tokenEnvVar` field | Holds the actual provider API key or Discord bot token | **Yes** |

The live model lane requires the first four rows above (`OPENCLAW_RUN_E2E`,
`OPENCLAW_RUN_LIVE_MODEL_E2E`, `OPENCLAW_LIVE_MODEL_PROFILE`, and the env var
named by that profile's `apiKeyEnvVar`). The real channel lane additionally
requires `OPENCLAW_RUN_REAL_CHANNEL_E2E`, `OPENCLAW_REAL_CHANNEL_PROFILE`,
and both bot token env vars named by that profile, and it also loads the live
model profile (the agent needs a configured model to answer with).

## Skip vs fail

These lanes intentionally have two different failure modes depending on how
far you have opted in:

1. **Gate disabled** (`OPENCLAW_RUN_E2E` unset, or the lane's own variable
   unset): the test **skips** with an exact, actionable reason naming only
   the missing variable, for example:
   `Live model E2E test disabled. ... Set OPENCLAW_RUN_LIVE_MODEL_E2E=1
   (in addition to OPENCLAW_RUN_E2E=1) to enable.`
2. **Gate enabled, but a profile or credential is missing/invalid**: the
   test **fails**, it does not skip. A profile path variable that is unset,
   relative, or points to a missing file throws naming only that variable. A
   missing credential environment variable throws naming only that variable,
   never its value. A profile with an unknown field, a literal-looking
   secret in a name field, or an out-of-range value throws naming only the
   offending field path.

This means enabling a lane is a deliberate statement of intent: once you have
opted in, a misconfiguration is a build-breaking failure to fix, not a
silent skip to ignore.

## Live model profile schema

`OPENCLAW_LIVE_MODEL_PROFILE` must point to an absolute path to a JSON file
matching this schema. Unknown fields are rejected.

| Field | Type | Required | Notes |
|---|---|---|---|
| `schemaVersion` | number | Yes | Must be `1`. |
| `provider` | string | Yes | Lowercase provider id recognized by the target gateway (letters, digits, hyphens; must start with a letter), for example `openai`. This profile intentionally has no `baseUrl` field: it stages credentials using OpenClaw's SecretRef CLI builder mode (`--ref-provider ... --ref-source env --ref-id ...`), matching the task's own reference example, which sets only the provider's `apiKey`. |
| `model` | string | Yes | Model id recognized by that provider, for example `gpt-4.1-mini`. |
| `apiKeyEnvVar` | string | Yes | Name (not value) of the process environment variable that holds the real API key. Upper snake case; rejected if it looks like a literal credential instead of a name. |
| `replyTimeoutSeconds` | number | No (default `120`) | Bounded `5` to `600`. How long to wait for the assistant's reply before failing. |

Example profile (`OPENCLAW_LIVE_MODEL_PROFILE`), with a placeholder env var
name, never a real key:

```json
{
  "schemaVersion": 1,
  "provider": "openai",
  "model": "gpt-4.1-mini",
  "apiKeyEnvVar": "OPENCLAW_LIVE_PARITY_OPENAI_API_KEY",
  "replyTimeoutSeconds": 120
}
```

Before running the lane, set the real key in that named variable in your own
shell, never in the profile file:

```powershell
$env:OPENCLAW_LIVE_PARITY_OPENAI_API_KEY = "<your real key, not committed anywhere>"
```

## Real channel (Discord) profile schema

`OPENCLAW_REAL_CHANNEL_PROFILE` must point to an absolute path to a JSON file
matching this schema. Unknown fields are rejected. The lane also loads the
live model profile above, because the agent still needs a configured model
to generate its reply.

| Field | Type | Required | Notes |
|---|---|---|---|
| `schemaVersion` | number | Yes | Must be `1`. |
| `guildId` | string | Yes | Discord guild (server) snowflake id, 15 to 25 digits. |
| `channelId` | string | Yes | Discord channel snowflake id, 15 to 25 digits. Scopes the gateway's allowlist to this one channel; every other channel in the guild is implicitly rejected. |
| `driver.tokenEnvVar` | string | Yes | Name of the env var holding the driver bot's token. Must differ from `sut.tokenEnvVar`. |
| `driver.userId` | string | Yes | Driver bot's snowflake user id. Must differ from `sut.userId`. |
| `sut.tokenEnvVar` | string | Yes | Name of the env var holding the SUT (system-under-test) bot's token. This is the bot identity the real gateway is configured to run as. |
| `sut.userId` | string | Yes | SUT bot's snowflake user/application id. |
| `pollTimeoutSeconds` | number | No (default `45`) | Bounded `5` to `120`. How long to poll Discord for the SUT's reply before failing. |

Example profile (`OPENCLAW_REAL_CHANNEL_PROFILE`), with placeholder env var
names and placeholder-shaped (not real) ids:

```json
{
  "schemaVersion": 1,
  "guildId": "123456789012345678",
  "channelId": "234567890123456789",
  "driver": {
    "tokenEnvVar": "OPENCLAW_LIVE_PARITY_DRIVER_BOT_TOKEN",
    "userId": "345678901234567890"
  },
  "sut": {
    "tokenEnvVar": "OPENCLAW_LIVE_PARITY_SUT_BOT_TOKEN",
    "userId": "456789012345678901"
  },
  "pollTimeoutSeconds": 45
}
```

Before running the lane, set both real tokens in their named variables:

```powershell
$env:OPENCLAW_LIVE_PARITY_DRIVER_BOT_TOKEN = "<driver bot token, not committed anywhere>"
$env:OPENCLAW_LIVE_PARITY_SUT_BOT_TOKEN = "<SUT bot token, not committed anywhere>"
```

### Why two distinct bot identities

The lane requires the driver and the SUT to be two different Discord
applications with two different tokens and two different user ids. The
driver posts the inbound mention and polls for a reply; the SUT is the bot
identity the real gateway runs as and replies through. A single bot cannot
prove an inbound-to-outbound round trip: it would just be observing its own
message. This mirrors the public main-repo Discord canary scenario runtime,
which also drives its `driver` bot against a distinct `sut` bot id.

## What the lanes configure in the gateway

Both lanes back up `/home/openclaw/.openclaw/openclaw.json` inside the WSL
gateway distro before making any change, and restore that exact file,
unset any staged environment variables, restart the gateway, and delete the
backup in `DisposeAsync`. Restoration is strict, not best-effort: if it
fails, the failure propagates instead of being silently swallowed, so a
broken restore cannot leave the shared gateway mutated for later tests.

Credentials are staged using OpenClaw's SecretRef CLI builder mode, never a
literal value:

```
openclaw config set models.providers.<provider>.apiKey --ref-provider default --ref-source env --ref-id <API_KEY_ENV_VAR>
openclaw config set channels.discord.token --ref-provider default --ref-source env --ref-id <SUT_TOKEN_ENV_VAR>
```

The resulting `openclaw.json` only ever contains a reference object such as
`{"source": "env", "provider": "default", "id": "<ENV_VAR_NAME>"}`, never the
literal secret. The real channel lane additionally merges a Discord plugin
configuration scoped to exactly the configured guild and channel:

```json
{
  "enabled": true,
  "allowBots": "mentions",
  "groupPolicy": "allowlist",
  "guilds": {
    "<guildId>": {
      "channels": {
        "<channelId>": {
          "requireMention": true,
          "users": ["<driver.userId>"]
        }
      }
    }
  }
}
```

`groupPolicy: "allowlist"` plus a `guilds` entry scopes the gateway to that
one guild; the nested `channels` entry further scopes it to that one
channel; `requireMention: true` means the gateway only reacts to messages
that mention it; `allowBots: "mentions"` means it will react to a
bot-authored message (the driver is a bot) but only when mentioned, not to
every bot message in the channel; and the channel-level `users` list is a
strict allowlist so the gateway only reacts to messages authored by the
driver's user id, not any other account that might post in that channel.

The named secret environment variables are made available to the WSL
gateway's systemd user service with `systemctl --user set-environment`, run
through `RunInWslAsync`'s `environment` parameter and piped stdin script (per
`docs/WSL_EXE_ARGV_PITFALL.md`), never through a command-line argument,
config file, or log line. The gateway is then restarted so the new
configuration and environment take effect, and the lane waits for the tray
operator and Windows node to report Ready before proceeding.

## Cost, rate limits, and safety

- The live model lane spends real API budget on the configured provider for
  every run: one chat completion request per run at minimum, possibly more
  if the provider/agent performs additional tool calls before its final
  reply.
- The real channel lane posts and polls real Discord API calls (message
  send, message list, and best-effort message delete) against a real guild
  and channel, subject to Discord's own rate limits.
- Poll bounds are capped (`pollTimeoutSeconds`, default `45`, max `120`;
  `replyTimeoutSeconds`, default `120`, max `600`) so a stuck run fails in
  bounded time instead of hanging indefinitely.
- Use a dedicated, low-traffic test guild and channel, and dedicated test bot
  applications, never a production Discord community or production API key.
  Anyone who can post in the configured channel while a run is in flight
  could observe the marker message; a private test channel limits exposure.
- The driver's Discord token is only ever used from this Windows test
  process (through `DiscordRestClient`); it is deliberately excluded from
  the set of secrets staged into WSL/the gateway and never reaches that
  environment. Only the SUT token (and the model provider's API key) are
  staged into the gateway.
- Discord REST calls are hard-coded to `https://discord.com/api/v10/`.
  Profiles cannot override the API host, preventing bot credentials from
  being redirected to an untrusted endpoint.

## Cleanup behavior

- **Gateway configuration and environment**: restored strictly in
  `DisposeAsync` as described above. A restore failure is a test failure,
  not a swallowed warning.
- **Discord messages**: the lane makes a best-effort attempt, in a `finally`
  block, to delete both the driver's posted message and the SUT's reply
  using their respective tokens. Deletion failures are swallowed by design
  (consistent with this repository's existing narrow-suppression
  conventions for artifact/cleanup code) so a Discord API hiccup during
  cleanup never masks the round-trip assertion's own pass/fail outcome. Do
  not rely on this for anything beyond tidiness; periodically clear out your
  test channel.

## Security and privacy

- Profiles are strict JSON DTOs. Unknown fields are rejected outright, so a
  profile cannot silently carry an unreviewed extra property, including an
  accidentally pasted literal secret under a made-up field name.
- Fields that name a credential (`apiKeyEnvVar`, `driver.tokenEnvVar`,
  `sut.tokenEnvVar`) are validated to look like an environment variable name
  and are rejected if they look like a literal credential instead (known
  provider token prefixes, the three-segment Discord bot token shape, or a
  long mixed-case-plus-digit run with no separators). This catches a profile
  author who pastes a real key where a name was expected, before that value
  is ever staged anywhere.
- Credentials are resolved only from named process environment variables.
  The lanes never read arbitrary host or user configuration files, and the
  profile path itself must be an absolute path (never resolved by search).
- Every known secret value registered during a run (API keys, both Discord
  tokens) is redacted from thrown exception messages and from WSL command
  stdout/stderr before either is included in any error, on top of the
  existing `E2ESetupFixture.SanitizeForLog` baseline redaction.
- Neither lane ever prints the prompt text, the assistant's reply text, the
  configured model or provider id, a Discord message body, a Discord user or
  message snowflake id, or a token to console output, logs, or the TRX.
  Diagnostics are limited to counts, indices, lengths, and coarse
  true/false/Ready state.
- Content capture is off by default, matching the same default the public
  main-repo live-transport QA suite uses for its own optional evidence
  capture (for example its Discord live-transport runtime's
  `OPENCLAW_QA_DISCORD_CAPTURE_UI_METADATA` opt-in flag, default off, which
  gates whether channel/guild/message ids are even included in its evidence
  artifacts). This lane's own code never writes prompt text, reply text, a
  model/provider id, a Discord message body, or a snowflake id to any log,
  console line, or artifact file it creates, and there is no opt-in flag to
  turn that capture on. Live-lane fixtures disable shared runtime artifact
  copying and drain tray stdout/stderr without persisting them.
  Secret-bearing WSL configuration calls also suppress command and output
  logging. Setup and uninstall lifecycle artifacts remain available, but
  live prompts, replies, model and provider ids, Discord message bodies,
  snowflake ids, and credentials are not written into artifacts or TRX
  diagnostics.
- One residual, unavoidable exposure: staging a secret into the gateway's
  systemd user service environment requires a WSL-internal `bash` process to
  expand `$NAME` into `systemctl --user set-environment NAME="$NAME"` before
  exec. The resolved value is briefly present in that single process's argv,
  local to the WSL VM, and is the mechanism this lane's design calls for
  (`systemctl --user set-environment`, not the config file or CLI arguments
  visible outside the VM). It is not written to any file, log, or the
  Windows side of the boundary.

## Running the lanes

Run the secretless contract tests any time, in any environment, with no
setup:

```powershell
dotnet test .\tests\OpenClaw.E2ETests\OpenClaw.E2ETests.csproj --no-restore --filter "FullyQualifiedName~OpenClaw.E2ETests.LiveParity.LiveParityGateContractTests|FullyQualifiedName~OpenClaw.E2ETests.LiveParity.LiveParityProfileContractTests|FullyQualifiedName~OpenClaw.E2ETests.LiveParity.LiveParitySupportContractTests"
```

Run a live proof lane once you have set up a profile and its referenced
credentials, using the formal validation script (recommended: it builds,
runs the contract tests first, then the selected proof, and fails the whole
run if the requested lane is skipped or does not pass):

```powershell
$env:OPENCLAW_LIVE_MODEL_PROFILE = "D:\path\to\live-model-profile.json"
$env:OPENCLAW_LIVE_PARITY_OPENAI_API_KEY = "<your real key>"
.\scripts\validate-live-parity-e2e.ps1 -Lane LiveModel
```

```powershell
$env:OPENCLAW_LIVE_MODEL_PROFILE = "D:\path\to\live-model-profile.json"
$env:OPENCLAW_REAL_CHANNEL_PROFILE = "D:\path\to\real-channel-profile.json"
$env:OPENCLAW_LIVE_PARITY_OPENAI_API_KEY = "<your real key>"
$env:OPENCLAW_LIVE_PARITY_DRIVER_BOT_TOKEN = "<driver bot token>"
$env:OPENCLAW_LIVE_PARITY_SUT_BOT_TOKEN = "<SUT bot token>"
.\scripts\validate-live-parity-e2e.ps1 -Lane RealChannel
```

`-Lane All` runs both. There is no `-AllowSkip` escape hatch: unlike
`scripts\validate-mxc-e2e.ps1`, a requested live lane that is reported
skipped or missing in the TRX always fails this script, because you have
explicitly asked to spend real budget proving it. The script never prints a
profile path or a credential value; it only checks and reports environment
variable *names* and pass/fail/skip outcomes.

Or drive `dotnet test` directly:

```powershell
$env:OPENCLAW_RUN_E2E = "1"
$env:OPENCLAW_RUN_LIVE_MODEL_E2E = "1"
$env:OPENCLAW_LIVE_MODEL_PROFILE = "D:\path\to\live-model-profile.json"
$env:OPENCLAW_LIVE_PARITY_OPENAI_API_KEY = "<your real key>"
dotnet test .\tests\OpenClaw.E2ETests\OpenClaw.E2ETests.csproj -r win-x64 --filter "FullyQualifiedName~OpenClaw.E2ETests.LiveParity.LiveModelE2ETests"
```

## No hosted scheduled lane

This repository does not add a GitHub-hosted scheduled workflow for either
lane. Both require real, spendable third-party credentials (a model
provider API key, two Discord bot tokens) that would have to live as
repository or organization secrets, reachable by any workflow run on a
GitHub-hosted runner. That is a materially larger blast radius than this
repository's existing MXC E2E proof, which needs no third-party secret and
is explicitly restricted to protected self-hosted runners.

The underlying reason is not "hosted workflows are inconvenient": it is that
package proof (building and testing whatever a contributor pushed, including
a fork) must stay strictly separate from any job that carries a live secret,
because untrusted candidate code must never run with credential access. This
repository's own `.github/workflows/ci.yml` is the package-proof side of
that split: it runs for every PR and never touches a third-party secret. A
hosted live-parity lane would sit on the other side of that split, and this
repository does not yet have the protected infrastructure that side
requires.

The public main OpenClaw repository's own secret-bearing live-transport QA
workflow is the closest real prior art for what that infrastructure looks
like, and a future hosted lane here should mirror its shape rather than
inventing a weaker one:

- **A protected environment gate.** Every job in the main-repo workflow that
  can see a live secret runs under `environment: qa-live-shared`
  (`.github/workflows/qa-live-transports-convex.yml`), so branch/reviewer
  protection rules stand between untrusted code and the secret, not just
  workflow conditionals.
- **An actor/ref authorization gate, separate from the environment gate.**
  An `authorize_actor` job requires maintainer-level (`admin`/`maintain`/
  `write`) repository permission before any manually dispatched run is
  allowed to proceed, and a `validate_selected_ref` job independently
  re-validates that the selected commit is reachable from `main`, a release
  tag, a release branch head, or an open PR head in that repository before
  any credentialed job runs
  (`.github/workflows/qa-live-transports-convex.yml`, the trigger/permission
  block and both gate jobs).
- **Ephemeral, ACL-restricted secret injection through a lease broker**,
  not a static repository secret read directly by test code. Live jobs
  authenticate to a small credential broker
  (`qa/convex-credential-broker/`) and acquire a short-lived,
  `kind`/`ownerId`/`actorRole`-scoped lease
  (`extensions/qa-lab/src/live-transports/shared/credential-lease.runtime.ts`,
  the `acquireQaCredentialLease` function) instead of reading a `secrets.*`
  value straight into the code under test.
- **A heartbeat while the lease is held, and a guaranteed release.** Callers
  under `extensions/qa-lab/src/live-transports/**` heartbeat the lease while
  it is in use and release it in a nested `try`/`finally` written so lease
  release still runs even if heartbeat shutdown itself throws.

Two caveats on that prior art, stated plainly so this design does not
overclaim its maturity: the broker's own documentation states that
"App-level encryption: not included in v1" for the current implementation
(`qa/convex-credential-broker/README.md`), and the main repository still
runs its own live-transport suite locally through a plain wrapper script
(`scripts/test-live.mjs`) rather than only through the hosted workflow, the
same local/hosted split this repository follows with
`scripts\validate-live-parity-e2e.ps1`. Separately, that repository also
keeps a macOS-Parallels-VM-based manual Discord smoke
(`.agents/skills/parallels-discord-roundtrip/`) alongside its automated live
lane; that smoke uses a single bot token to send and read back its own
message on one guest and is useful only as an independent manual oracle for
a human validating basic send/read plumbing on another platform. It is not
an automated two-identity round-trip proof and is not a substitute for, or
equivalent to, either that repository's automated Discord live lane or this
repository's real channel lane.

A hosted, scheduled live-parity lane should only be added here once this
repository has equivalent infrastructure: a protected environment, an
actor/ref gate, ephemeral ACL-restricted secret injection (broker-leased,
not a static repository secret read directly by test code), and lease
heartbeat/release in a `finally` so a crashed run cannot hold a live
credential indefinitely, scoped to a disposable test bot/guild/channel and a
provider account with a hard spend cap. Until then, this lane is
local-only, explicit opt-in, and never runs unattended with real
credentials.

## Known limitations

- This repository does not vendor the gateway's Discord plugin or model
  provider source; both are external packages resolved by the `openclaw` CLI
  into WSL. The Discord configuration shape documented above
  (`channels.discord.{enabled,allowBots,groupPolicy,guilds}`, with
  `guilds.<id>.channels.<id>.{requireMention,users}`) and the SecretRef
  builder-mode CLI shape were confirmed against a real installed gateway
  during development of this lane (source inspection plus non-destructive,
  backed-up, restored `--dry-run` and live `config set` calls against a real
  gateway instance), but a future gateway release could change either shape.
  If the real channel lane fails at the "configure gateway" step on a newer
  gateway version, compare `openclaw config get channels.discord --json`
  against the shape above and adjust
  `tests\OpenClaw.E2ETests\LiveParity\DiscordGatewayConfigJsonBuilder.cs`.
- The live model lane's chat-turn assertion checks for a non-empty Assistant
  reply following the User marker; it does not and cannot assert on the
  reply's content, since a real model's reply is not deterministic. The sent
  message explicitly asks for a short, one-sentence reply to bound token
  cost, but the provider is free to reply at whatever length it chooses.
- The real channel lane's poll matches on channel plus SUT sender id plus a
  nonce substring in message content; if the configured agent's persona
  rewords or declines to include the exact nonce, the lane fails even though
  the round trip technically occurred. Keep the driver's instruction
  ("reply with only this exact marker") in a system/user context the
  configured agent is likely to follow literally.
