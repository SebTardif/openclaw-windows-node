# Windows Node Testing Guide

## Overview

The Windows Node feature allows the tray app to receive commands from the OpenClaw agent (canvas, screenshots, screen recordings, camera, location, notifications, and controlled command execution). This is **experimental** and must be explicitly enabled in Settings.

## How to Enable

1. Open the tray app
2. Right-click → Settings
3. Scroll to "ADVANCED (EXPERIMENTAL)"
4. Toggle "Enable Node Mode" ON
5. Click Save

## Companion-App Setup Guidance

For app-owned local WSL setup, after OpenClaw onboard completes or is explicitly skipped, setup runs the pinned gateway CLI's non-interactive baseline initializer against the final runtime workspace and then injects fixed Windows-node guidance into that workspace's `AGENTS.md`. The injected block is setup-owned and idempotently replaced between managed markers, preserving user-authored content and file permissions outside those markers and leaving OpenClaw source files unchanged.

**Note on the apply script's WSL invocation.** The `WindowsNodeBootstrapContextStep` apply and rollback scripts are piped to `bash -s` via stdin (`RunInWslAsync(..., inputViaStdin: true)`) rather than the default `bash -c` argv path. This is required because `wsl.exe` performs shell variable expansion on argv before invoking bash, which would drop user-defined `$var` references in the multi-line script (`workspace='...'` followed by `mkdir -p "$workspace"` becomes `mkdir -p ""`). See `docs/WSL_EXE_ARGV_PITFALL.md` for the full writeup.

The guidance helps the first companion-app OpenClaw session route Windows desktop, files, screenshots, camera, notifications, browser proxy, and Windows command tasks through the Windows node / `nodes` tool.

## What You Can Test Now

### Agent-driven UI and MCP validation

For changes touching tray UX, Settings, onboarding, chat/canvas, Command Center, Windows node capabilities, local MCP, gateway pairing/connection, permissions, or diagnostics, use `.agents/skills/openclaw-proof-validation/SKILL.md`.

Short version: run required tests, collect a closeout proof pass with `.\run-app-local.ps1 -Isolated` when UI is involved, use computer-use or developer-provided screenshots/output for the active changed UI state, prove MCP with `winnode` or raw JSON-RPC, prove gateway paths when available, and include current-head concrete output under `## Real behavior proof`. Mid-development computer-use/MCP/rubber-duck validation is fine when explicitly requested or needed to unblock work.

### New command MCP contract

Every new Windows node call must be exposed through local MCP and `winnode`: register the capability, update `McpToolBridge.CommandDescriptions`, update `src/OpenClaw.WinNode.Cli/skill.md`, add focused tests, and prove discovery/invocation with `winnode` or raw MCP JSON-RPC.

### 1. Settings Toggle
- Verify the toggle appears in Settings under "ADVANCED"
- Verify it saves and persists across app restarts

### 2. Node Connection
- Enable Node Mode and save
- Watch for "🔌 Node Mode Active" toast notification
- Check logs at `%LOCALAPPDATA%\OpenClawTray\openclaw-tray.log` for:
  ```
  [INFO] Starting Windows Node connection to ws://...
  [INFO] Node connected, waiting for challenge...
  [INFO] Registered capability: screen (2 commands)
  [INFO] All capabilities registered
  [INFO] Node status: Connected
  ```

### 3. Screen Capture Notification
- When the agent captures your screen, you should see "📸 Screen Captured" toast
- This is throttled to max once per 10 seconds

### 4. Command Center
- Open the tray status detail or launch `openclaw://commandcenter`
- In Node Mode, verify the window shows gateway channel health from node `health` events plus a synthesized local Windows node when operator `node.list` is not connected
- Check diagnostics for pairing approval, pending reapproval, stale health, all-stopped channels, allowlist filtering, browser control host availability for `browser.proxy`, and usage-cost gaps
- When only the synthesized local Windows node is available, verify its locally declared capabilities/commands are labeled unverified and are not counted as approved/effective
- For `pending-reapproval`, verify effective capabilities/commands remain unchanged, pending declarations are listed separately, and the copy action emits `openclaw nodes approve <pendingRequestId>`
- During a changed-command handshake, verify authoritative `pending-reapproval` replaces the generic node-pair approval card and exposes only the node-list trust command; explicitly typed device role-upgrade, Node mode off/hidden, and failure cards remain higher priority
- If the gateway omits a safe pending request ID, verify the copy action emits `openclaw nodes pending`, labels it as discovery only, and does not offer reconnect-after-approval yet
- Approve the request explicitly, reconnect the node, and verify the effective capability/command counts update and the pending reapproval warning clears
- Use "Copy fix" only for safe repair commands; privacy-sensitive commands remain informational unless you explicitly opt in on the gateway

## What Requires Gateway Support

These features need the gateway to send `node.invoke` commands:

| Command | Description | Expected Behavior |
|---------|-------------|-------------------|
| `canvas.present` | Show WebView2 window | Opens floating window with URL or HTML |
| `canvas.hide` | Hide canvas window | Closes the canvas window |
| `canvas.eval` | Execute JavaScript | Runs JS in canvas, returns result |
| `canvas.snapshot` | Capture canvas | Returns base64 PNG of canvas content |
| `canvas.a2ui.pushJSONL` | Legacy A2UI JSONL push | Routes through same renderer path as `canvas.a2ui.push` |
| `screen.snapshot` | Take screenshot | Captures screen, shows notification, returns base64 |
| `screen.record` | Record short screen clip | Returns MP4/base64 metadata; requires explicit gateway allowlist |
| `system.notify` | Show notification | Displays toast notification |
| `system.run` | Controlled command execution | Uses local exec approval policy; `prompt` decisions show a Windows Allow once / Always allow / Deny dialog |
| `system.run.prepare` | Pre-flight command execution | Parses and validates a `system.run` invocation without executing it |
| `system.which` | Resolve executables | Returns absolute paths for requested binaries |
| `camera.list` | Enumerate cameras | Returns device IDs and names |
| `camera.snap` | Capture photo | Returns base64 image (NV12 fallback) |
| `camera.clip` | Capture video clip | Returns MP4/base64 metadata |
| `location.get` | Get Windows location | Uses Windows location permission/settings |
| `device.info` / `device.status` | Device metadata/status | Returns host/app/locale plus battery/storage/network/uptime payloads |
| `browser.proxy` | Proxy browser-control host requests | Requires Browser proxy bridge enabled, a compatible browser-control host listening on gateway port + 2, and matching browser-control auth |
| `tts.speak` | Speak text aloud | Requires Text-to-speech playback enabled in Settings; gateway mode also requires `tts.speak` in `gateway.nodes.allowCommands` |
| `stt.transcribe` | Bounded microphone transcription | Requires Speech-to-text enabled in Settings; uses local Whisper.net |
| `stt.listen` | Voice-activity microphone transcription | Returns when the user stops speaking or timeout expires |
| `stt.status` | Speech-to-text readiness | Returns Whisper.net model download/readiness state |

### Cancelling an invocation

The gateway may send the `node.invoke.cancel` event with
`payload.invokeId` matching an active `node.invoke.request`. The Windows node
cancels only that invocation and completes its original result with
`ok: false, error: "cancelled"`; unknown or already-completed IDs are ignored.
The legacy `payload.requestId` spelling is also accepted for compatibility.
Operation completion is the linearization point: once capability execution
returns and atomically marks the invocation complete, later cancellation is too
late and the completed result is preserved.

For local MCP, send a JSON-RPC `notifications/cancelled` notification with
`params.requestId` matching the active `tools/call` JSON-RPC ID. Cancellation
must stop queued camera admission, recording delays/frame waits, and active
recording cleanup rather than only abandoning the HTTP waiter.

## Capabilities Advertised

When the node connects, it advertises these capabilities:
- `canvas` - WebView2-based canvas window
- `screen` - Screen snapshot and recording via Windows.Graphics.Capture
- `system` - Notifications, command execution (`system.run`, `system.run.prepare`, `system.which`), exec approval policy
- `camera` - MediaCapture photo/video capture (frame reader fallback)
- `location` - Windows.Devices.Geolocation
- `device` - Host/app metadata and lightweight status
- `browser` - Local `browser.proxy` bridge to a browser-control host on gateway port + 2, when enabled in Settings
- `tts` - Windows speech synthesis or ElevenLabs playback, when enabled in Settings
- `stt` - Local speech-to-text via Whisper.net, when enabled in Settings

Local MCP clients also see MCP-only `app.*` commands such as `app.navigate`, `app.status`, `app.chat.snapshot`/`app.chat.send`/`app.chat.reset`, and `app.chat.queue.list`/`app.chat.queue.cancel`. Connection diagnostics and setup tools live under `app.connection.*`; use `app.connection.status` to inspect active gateway, operator/node credential state, MCP runtime status, browser proxy caveat, pending approval commands, and recent diagnostics, and `app.connection.gateways` to list saved gateway records without token values. These are local testing and automation hooks registered with the tray's MCP server and are not advertised to the gateway WebSocket.

## Security Features

- **URL Validation**: Canvas blocks `file://`, `javascript:`, localhost, private IPs, IPv6 localhost
- **Screen Capture Notification**: User is notified when screen snapshots are captured
- **Screen Recording Allowlist**: `screen.record` must be explicitly allowed by the gateway and does not leave a hidden local MP4 copy on Windows
- **Command Center Redaction**: recent node invoke activity records command name, status, duration, node id, and privacy class only; it does not store base64 payloads, screenshots, recordings, tokens, or command arguments
- **Node Mode Toggle**: Must be explicitly enabled by user
- **Command Validation**: Only alphanumeric commands with dots/hyphens allowed

## Troubleshooting

### Node doesn't connect
- Check the active gateway in Connection settings. Gateway records live in `%APPDATA%\OpenClawTray\gateways.json`; post-pairing device tokens live under `%APPDATA%\OpenClawTray\gateways\<gateway-id>\device-key-ed25519.json`.
- Check logs for connection errors
- Verify gateway is running and accessible
- If only a bootstrap token exists, finish pairing or approve the device; paired device tokens take precedence on future connects.

### No "Node Mode Active" notification
- Ensure Windows notifications are enabled for the app
- Check if notification settings in the app are enabled

### `browser.proxy` reports no browser-control host
- Confirm the Browser proxy bridge toggle is enabled in Settings, then save and reconnect or re-pair if the gateway keeps an older command snapshot.
- The bridge is local-only: it calls `http://127.0.0.1:<gateway-port+2>` from Windows. For a gateway on `ws://127.0.0.1:18789`, the browser-control host must listen on `127.0.0.1:18791`.
- In managed SSH tunnel mode, keep Browser proxy bridge enabled so the tray forwards local gateway port + 2 to remote gateway port + 2. Settings shows a selectable preview of the exact `ssh -N -L ...` command.
- If using a manual SSH tunnel, add both forwards, for example: `ssh -N -L 18789:127.0.0.1:18789 -L 18791:127.0.0.1:18791 <user>@<host>`. If the SSH daemon is not listening on port 22, include `-p <ssh-port>`. If local and remote gateway ports differ, forward `<local-gateway-port+2>` to `127.0.0.1:<remote-gateway-port+2>`.
- Advanced split/remote topologies can pin the browser-control listener with the active gateway record's `BrowserControlPort` field in `%APPDATA%\OpenClawTray\gateways.json`. This value is a local TCP port on Windows and is scoped to that gateway record. Configure it only for a trusted browser-control forward, because `browser.proxy` sends the saved shared gateway token to the selected local listener for browser-control authentication. When a gateway uses SSH, tunnel-derived `localPort + 2` browser-control routing is used only when that gateway's managed tunnel has `IncludeBrowserProxyForward` enabled; otherwise set `BrowserControlPort` to a trusted manual forward.
- A local SSH forward is not enough if the remote browser-control host is not running. Command Center port diagnostics should show whether the local gateway and browser-control ports are listening and which process owns them.
- If Command Center shows the browser-control port listening but `browser.proxy` returns an auth error, verify the Windows Settings gateway token matches the browser-control host token/password. QR/bootstrap pairing can connect the node without saving a shared gateway token, but browser-control auth may still require one.
- A local smoke can verify the host dependency without proving gateway invoke auth: start the upstream browser-control host with a temporary no-secret config, confirm `http://127.0.0.1:<gateway-port+2>/` and `/tabs` return HTTP 200, then stop the captured host process. The full parity smoke is not complete until `openclaw nodes invoke --command browser.proxy` succeeds through the active gateway.

### Canvas window doesn't appear
- Check logs for `canvas.present` command received
- Verify URL is not blocked by security validation

### Camera permission denied
- If you see "Camera access blocked", enable camera access for desktop apps in Windows Privacy settings
- Packaged MSIX builds will show the system consent prompt automatically

### Local sandbox validation
- Sandbox integration tests are intended for local Windows development machines and may skip when the required local sandbox prerequisites are unavailable.
- Build the tray app before running local sandbox validation so the required sandbox helper binaries are present in the app output.
- For MXC-related merge validation, prefer the formal script below because it sets the required gates and fails if MXC is skipped.

  ```powershell
  .\scripts\validate-mxc-e2e.ps1
  ```

### Full Gateway `system.run` MXC runtime proof
- The focused E2E below provisions a fresh WSL Gateway, starts an isolated tray instance, sets a local exec approval rule through MCP, invokes `system.run` through the real Gateway `node.invoke` path, and verifies tray MXC diagnostics show contained `mxc-direct-appc` execution for both allowed execution and denied writes to the tray data directory.
- Run it when validating the Gateway/Windows node runtime path, not just direct MCP or shared library behavior.
- GitHub-hosted Actions runners do not provide a working MXC/AppContainer runtime. The regular cloud E2E matrix should report these MXC proofs as skipped while still running the rest of setup-connect. Run the proof on a local MXC-enabled Windows machine. Only set `OPENCLAW_RUN_MXC_E2E=1` in GitHub Actions when using an MXC-enabled self-hosted runner.
- Use `.\scripts\validate-mxc-e2e.ps1` for normal local validation. It sets `OPENCLAW_RUN_E2E` and `OPENCLAW_RUN_MXC_E2E`, runs the real Gateway MXC proofs, and fails if the MXC proof skips. `-AllowSkip` is only for documenting a non-MXC host, not for merge validation of MXC-related work.
- When reproducing this manually against an existing Gateway, make sure `gateway.nodes.allowCommands` includes `system.run`, `system.run.prepare`, and `system.which`, then approve any `pending-reapproval` request with `openclaw nodes approve <pendingRequestId>`. The node can advertise `system.run` while the Gateway still blocks it until both gates are updated.

  ```powershell
  .\build.ps1
  $env:OPENCLAW_REPO_ROOT = (Get-Location).Path
  $env:OPENCLAW_RUN_E2E = "1"
  dotnet test .\tests\OpenClaw.E2ETests\OpenClaw.E2ETests.csproj `
    --no-restore `
    --filter "FullyQualifiedName~RealGateway_SystemRun_ExecutesThroughWindowsNodeMxcSandbox" `
    --logger "console;verbosity=normal" `
    -r win-x64
  ```

- Expected proof markers:
  - Gateway response contains `OPENCLAW_GATEWAY_SYSTEM_RUN_MXC_OK` with `exitCode=0`.
  - The denied-write proof targets a fresh file under the isolated tray data directory, returns non-zero, and leaves that file absent.
  - `openclaw-tray.log` contains `[mxc] system.run sandbox request` with `executor=mxc-direct-appc`, `contained=True`, and `shell=cmd`.
  - `openclaw-tray.log` contains `[mxc] system.run sandbox result` with `containment=mxc` for both the successful execution and the denied write.
- E2E artifacts are written under `TestResults\E2E\<run-id>` and skip known secret-bearing files such as gateway records and settings.

### Published Gateway native chat and Windows node proof

`PublishedGatewayNativeChatTests.RealPublishedGateway_DeviceInfo_AndNativeChat_Roundtrip`
is the standard non-MXC runtime smoke. It keeps the pinned published/LKG gateway from
the setup fixture and proves all of these paths in one isolated run:

1. The tray operator and Windows node are Ready.
2. `openclaw health --deep --json` succeeds, and the tray-generated dashboard URL
   returns an HTTP 2xx response without token/authentication errors.
3. A real gateway `node.invoke` of `device.info` returns Windows device metadata.
4. Local MCP `app.chat.send` drives the native tray chat provider.
5. The published gateway calls a dependency-free OpenAI Responses-compatible mock
   inside the isolated WSL distro.
6. `app.chat.snapshot` reports the exact synthetic User marker followed later in
   timeline order by the exact deterministic Assistant reply with `turnActive=false`.
7. The gateway config is restored, the exact transient mock systemd unit is stopped,
   and the app/node return to Ready.

The fixture backs up the existing published-gateway config before adding the mock
provider. It restores the file in async disposal even when the proof fails. Mock logs
contain only method/path, byte and item counts, model id, request count, and synthetic
test markers. They never contain request bodies, prompts, authorization headers, or
tokens.

```powershell
$env:OPENCLAW_REPO_ROOT = (Get-Location).Path
$env:OPENCLAW_RUN_E2E = "1"
dotnet build .\src\OpenClaw.Tray.WinUI\OpenClaw.Tray.WinUI.csproj -c Debug -r win-x64
dotnet test .\tests\OpenClaw.E2ETests\OpenClaw.E2ETests.csproj `
  --no-restore `
  --filter "FullyQualifiedName~OpenClaw.E2ETests.Setup.PublishedGatewayNativeChatTests" `
  --logger "console;verbosity=normal" `
  -r win-x64
```

The test must report exactly one executed, passed test. CI's `setup-connect` shard
also reads the TRX and fails when this named proof is missing, skipped, or not passed.
Artifacts are under `TestResults\E2E\<run-id>`:

- `published-gateway-mock.log`: sanitized mock request summaries.
- `published-gateway-service-status.log`: systemd active/substate and restart status.
- `e2e-fixture.log`, setup/uninstall JSONL, and isolated tray logs.

### Live model and real Discord channel parity

The `LiveParity` folder in `OpenClaw.E2ETests` adds two independent, opt-in
lanes that reuse this same published gateway and native tray MCP setup to
prove real behavior beyond the deterministic mock above: a configured LLM
provider actually answering one bounded chat turn, and a real Discord bot
actually receiving a mention and replying through the gateway. Both lanes:

- Require an explicit absolute profile path (`OPENCLAW_LIVE_MODEL_PROFILE` /
  `OPENCLAW_REAL_CHANNEL_PROFILE`) plus `OPENCLAW_RUN_E2E=1` and their own
  variable (`OPENCLAW_RUN_LIVE_MODEL_E2E` / `OPENCLAW_RUN_REAL_CHANNEL_E2E`).
  When explicitly enabled they never skip for a missing profile or
  credential; they fail closed and name only the missing variable.
- Stage credentials into the WSL gateway using OpenClaw's SecretRef CLI
  (`--ref-provider default --ref-source env --ref-id <NAME>`), never a
  literal secret value, and restore the gateway config/environment in
  `DisposeAsync`.
- Never print prompt, reply, model, provider, token, or Discord message
  content to console/log/TRX output; only counts and coarse state.

This lane requires real API/Discord credentials and network access it never
runs in normal CI. See `docs/LIVE_PARITY_TESTING.md` for the full profile
schema, exact environment variables, and `scripts\validate-live-parity-e2e.ps1`.

### Installed DEV Inno smoke

Run the installed-app proof directly from Windows PowerShell:

```powershell
.\scripts\validate-installed-inno-smoke.ps1
```

The command creates its own timestamped artifact directory, builds an x64 DEV Inno
installer, silently installs it into a throwaway directory, verifies the installed
executable hash matches the installer payload, and runs the same named
published-gateway proof with `OPENCLAW_E2E_TRAY_EXE` pinned to the installed
executable. It then silently uninstalls and verifies there is no DEV registration,
installed tray, DEV data directory, or `OpenClawGateway-Dev` distro left behind.

This proves the installed tray runtime payload, including its operator, local MCP,
Windows node, and native chat paths. The fixture's headless WSL gateway setup is
still orchestrated in-process by the source-built E2E test harness. The smoke does
not exercise the installed UI setup entrypoint itself.

The smoke refuses to start if existing DEV install/data/distro state is present. It
does not inspect, overwrite, uninstall, or clean release identity state. Every phase
must report `passed`; a missing or skipped install, installed-payload check, roundtrip,
or cleanup is a failure.

Artifacts are written to `TestResults\InstalledSmoke\<timestamp>`. The command prints
the exact artifact path. `phase-status.json` is the phase gate; the folder also
includes `installed-smoke.log`, `installed-smoke.done`, `installed-smoke.pid`, Inno
install/uninstall logs, one log per phase, the TRX, and the nested E2E artifacts.

### Previous-release Inno upgrade smoke

The production Inno identity cannot be safely isolated from a contributor's installed
release. Run this PowerShell 7 lane only on a disposable clean Windows machine or VM:

```powershell
pwsh -File .\scripts\validate-inno-upgrade-smoke.ps1 `
  -PreviousRelease v0.6.12 `
  -PreviousInstallerSha256 <official-x64-installer-sha256> `
  -ConfirmCleanMachineReleaseIdentity
```

Real proof for integration is expected on the clean Hyper-V/Crabbox Windows image.
Do not remove or bypass a local guard to make a contributor workstation pass.
All installer operations are noninteractive, and guard diagnostics identify the
conflicting state category and path without reading or printing registry values,
settings, gateway credentials, or device keys.

The script rejects existing release and DEV installs, data directories, protocol and
startup registry entries, shortcuts, tray processes, startup tasks, and app-owned WSL
distros before it acquires or installs anything. It downloads only the exact x64 asset
from the official `openclaw/openclaw-windows-node` GitHub release over HTTPS, checks
the GitHub asset digest plus any supplied digest, and requires a valid OpenClaw
Foundation signature on the installed previous-release tray. A missing release,
checksum, signature, exact version, or proof phase is a failure.

For deterministic offline runs, pass `-PreviousInstallerPath`, `-PreviousVersion`,
and optionally `-PreviousInstallerSha256`. Locally built unsigned previous payloads
also require `-AllowUnsignedPreviousPayload`. Use `-CurrentInstallerPath`,
`-CurrentPayloadPath`, and `-CurrentVersion` to override the current source build.
The previous and current SemVer values and installer/runtime hashes must differ, so
reinstalling the same payload cannot pass as an upgrade.

The lane installs the previous release into a run-owned directory, seeds representative
settings, gateway registry, and per-gateway identity state, and upgrades in place with
the current source-built release-identity installer. It proves the registered version
transition, unchanged state hashes, current installed runtime hash and ProductVersion,
then reuses `validate-installed-inno-smoke.ps1 -ProofInstalledPayloadOnly` for the
published-gateway health, Windows `device.info`, local MCP native chat, and deterministic
chat roundtrip. Uninstall must preserve the synthetic external gateway state before the
harness deletes only its run-owned state and verifies no release registration, payload,
protocol, startup, or WSL residue remains.

To check only whether the current host is safe without downloading, installing,
uninstalling, or changing OpenClaw state:

```powershell
pwsh -File .\scripts\validate-inno-upgrade-smoke.ps1 `
  -SafetyPreflightOnly `
  -ConfirmCleanMachineReleaseIdentity
```

A safety-only result does not count as upgrade proof. Full artifacts are under
`TestResults\UpgradeSmoke\<timestamp>`, including installer logs, previous/current
hash and version evidence, preservation hashes, delegated runtime proof, cleanup
status, and `phase-status.json`.

The `Windows Inno Upgrade Smoke` workflow is manual-only and runs on a disposable
GitHub-hosted Windows runner. It requires an exact prior tag, expected installer
SHA-256, and the `CLEAN-OPENCLAW-VM` confirmation. Normal pull request and release CI
do not run this release-identity lane. The workflow and deterministic mock-backed
gateway/chat proof are secretless; a live agent turn is deliberately outside the
package-upgrade gate.

This lane ports lifecycle oracles from the main OpenClaw repository rather than its
npm, Bash, Docker, or systemd mechanics. The reference points are the
[`openclaw-cross-os-release-checks-reusable.yml`](https://github.com/openclaw/openclaw/blob/main/.github/workflows/openclaw-cross-os-release-checks-reusable.yml)
workflow, its
[`openclaw-cross-os-release-checks.ts`](https://github.com/openclaw/openclaw/blob/main/scripts/openclaw-cross-os-release-checks.ts)
harness, and the
[`release-upgrade-user-journey` scenario](https://github.com/openclaw/openclaw/blob/main/scripts/e2e/lib/release-upgrade-user-journey/scenario.sh).
The Windows port keeps immutable baseline/candidate identities, a clean profile,
seeded state, a real candidate Inno install, fresh installed version/hash and ARP
checks, gateway/dashboard/node/chat health, state preservation, and always-run
failure artifacts. It has no updater-to-direct-install fallback while claiming an
upgrade. Forward upgrade plus teardown does not claim product rollback; teardown is
only fail-safe cleanup of state that the harness proved absent and then created.
### Windows desktop proof (screenshot/manifest capture)

Run a repeatable, fail-closed screenshot proof of the tray's Connection page:

```powershell
.\scripts\capture-windows-desktop-proof.ps1 -ArtifactRoot <path-outside-product-data>
```

This launches an isolated current-head tray under a temp
`OPENCLAW_TRAY_DATA_DIR`, drives the deterministic `connection` deep link,
captures a Hub-window-only screenshot, and writes a `schemaVersion: 1`
manifest. It never reads or writes real `%APPDATA%\OpenClawTray` state and
fails closed if the app, route marker, or any required artifact is missing.
See `.agents/skills/windows-computer-use-proof/SKILL.md` for the full
contract, manifest schema, and PR proof content. A manual-only
`workflow_dispatch` GitHub Actions lane (`.github/workflows/windows-desktop-proof.yml`)
runs the same script only on `[self-hosted, windows,
openclaw-desktop-proof]`. That fixed label must identify a runner launched
interactively in an unlocked, `qwinsta` `Active` desktop session, never as a
Windows service, in Session 0, or in a disconnected RDP session. Generic
GitHub-hosted Windows runners are intentionally unsupported because they do
not guarantee compositor-backed app-window capture. The workflow uploads its
curated artifacts unconditionally.

## Remaining Work (Roadmap)

1. ~~**system.run + exec approvals**~~ ✅ Implemented
    - `system.run` with PowerShell/cmd support
    - `system.run.prepare` pre-flight command
    - `system.which` command lookup
    - `system.execApprovals` allowlist flow with base-hash optimistic concurrency for remote edits
    - `system.run` environment override sanitizer blocks path/toolchain injection and secret-looking variables
2. ~~**screen.record**~~ ✅ Implemented
    - Graphics Capture video recording (MP4/base64)
3. ~~**camera.clip**~~ ✅ Implemented
    - Short webcam video capture (MediaCapture + encoding)
4. ~~**A2UI pushJSONL alias + device status**~~ ✅ Implemented
    - Legacy `canvas.a2ui.pushJSONL`
    - Safe `device.info` / `device.status`
5. ~~**Command Center diagnostics**~~ ✅ Implemented
    - Channel/node/usage/pairing/allowlist diagnostics and recent invoke timeline
6. **Packaging & consent prompts**
    - MSIX packaging with camera/screen capabilities for system prompts
7. **Test matrix & polish**
    - Canvas/screen/camera regression tests
    - Handle timeouts/disconnects, reduce verbose logging

## Files Involved

- `src/OpenClaw.Shared/WindowsNodeClient.cs` - Node protocol client
- `src/OpenClaw.Shared/Capabilities/*.cs` - Capability handlers
- `src/OpenClaw.Tray.WinUI/Services/Connection/GatewayRegistry.cs` - persistent gateway records
- `src/OpenClaw.Tray.WinUI/Services/Connection/GatewayConnectionManager.cs` - operator/node connection lifecycle
- `src/OpenClaw.Tray.WinUI/Services/Connection/CredentialResolver.cs` - device-token/shared/bootstrap credential precedence
- `src/OpenClaw.Tray.WinUI/Services/NodeService.cs` - Orchestrates capabilities
- `src/OpenClaw.Tray.WinUI/Services/ScreenCaptureService.cs` - screen snapshots
- `src/OpenClaw.Tray.WinUI/Services/ScreenRecordingService.cs` - screen recordings
- `src/OpenClaw.Tray.WinUI/Services/CameraCaptureService.cs` - camera photo/video capture
- `src/OpenClaw.Tray.WinUI/Windows/CanvasWindow.xaml` - WebView2 canvas
