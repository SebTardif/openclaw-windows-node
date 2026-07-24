---
name: windows-computer-use-proof
description: "Capture repeatable, redacted screenshot/video proof of a real Windows tray UI path with deterministic artifact naming and a machine-readable manifest, then paste the result into a PR's '## Real behavior proof' section. Use when a UI/desktop change needs current-head visible evidence, or when asked for a screenshot/desktop/computer-use proof."
---

# Windows Computer-Use Proof

Use this skill to produce a fail-closed, current-head screenshot (and,
when supported, a short motion artifact) of one deterministic visible path in
the OpenClaw Windows tray, plus a `schemaVersion: 1` JSON manifest describing
exactly what was captured, without ever touching real `%APPDATA%` state.

This skill exists specifically for UI proof capture. For everything else
(which lane to run, rubber-duck review, MCP/gateway proof, PR proof package
shape), use `.agents/skills/openclaw-proof-validation/SKILL.md`, which points
here for the screenshot/video step and does not duplicate this contract.

## Witness, not oracle

The screenshot (and any future motion artifact) is a **witness only**. It is
never the pass/fail signal. The sole oracle is the deterministic UI
automation assertion in `WindowsDesktopProofTests.ConnectionPage_IsReachableAndScreenshotable`:
navigating to `openclaw://hub/connection` and finding the
`ConnectionPageMarker` automation id. That assertion throws (and fails the
test/script) on its own if the app does not behave correctly; the screenshot
capture is wrapped separately so a witness-capture failure never masks or
gets masked by the oracle result.

This mirrors the upstream Mantis QA pattern (`docs/concepts/mantis.md` in the
`openclaw/openclaw` monorepo): *"prefer small, typed oracles over vision
checks... keep vision checks additive to a platform-API oracle where one
exists."* Here, the platform-API-equivalent oracle is the UIAutomation
assertion; the screenshot is the additive evidence layer.

Proof completion still requires the required artifacts (screenshot,
proof-text, TRX) to exist; the oracle passing alone is not enough to call a
run "proof". The distinction only changes how a failure is diagnosed, not
whether artifacts are required.

## Interactive-desktop guard

Both the script and the UI test independently check for an interactive
desktop session (not Session 0, `Environment.UserInteractive` true) before
attempting anything, and fail closed with a clear message if none is
available. Screenshot capture is fundamentally impossible without a desktop,
so this check runs before build/test even starts, in a distinct
`environment-non-interactive` failure phase.

## What it does

`scripts\capture-windows-desktop-proof.ps1`:

1. Builds the current-head tray app (unless `-NoBuild`) and the
   `OpenClaw.Tray.UITests` proof test.
2. Launches an isolated tray instance under a test-owned temp
   `OPENCLAW_TRAY_DATA_DIR` (never the real `%APPDATA%\OpenClawTray`), using
   the existing `AccessibilityAppFixture`.
3. Drives one deterministic route: the deep link
   `openclaw://hub/connection` to the Connection page, waiting for the
   `ConnectionPageMarker` automation id.
4. Captures a still screenshot of only the tray Hub window via the existing
   `OPENCLAW_UI_SCREENSHOT_PATH` hook, and writes proof text lines via
   `OPENCLAW_UI_PROOF_PATH`.
5. Writes a `schemaVersion: 1` manifest listing every artifact
   (`screenshot`, `proof-text`, `trx`, and `motion`) with type, path, byte
   size, and status.
6. Fails closed: non-zero exit, `outcome: "fail"`, and a populated `failure`
   block with a `phase` if the desktop is non-interactive, the app is
   missing, the build fails, the UI automation oracle fails, or any required
   artifact is missing or empty. See "Failure phases" below.

## Command

```powershell
.\scripts\capture-windows-desktop-proof.ps1 -ArtifactRoot <path-outside-AppData>
```

Useful flags:

| Flag | Effect |
|---|---|
| `-RepoRoot <path>` | Defaults to the script's own repo root. Must exist and contain the tray project. |
| `-ArtifactRoot <path>` | Where the manifest/screenshot/TRX are written. Defaults to a timestamped folder under `TestResults\DesktopProof\`. Rejected if it resolves under a real `OpenClawTray`/`OpenClawTray-Dev` folder in `%APPDATA%` or `%LOCALAPPDATA%`. |
| `-Configuration <Debug\|Release>` | Build configuration. Defaults to `Debug`. |
| `-NoBuild` | Skip the build step when the tray and test projects are already built at current head. |
| `-IncludeMotion` | Requests a short motion artifact. No native video/GIF capture primitive exists in this repo today, so this **always fails closed** with a clear message rather than faking success. Omit it; the manifest still reports `motion` as `status: "unavailable"` with a reason. |
| `-DryRun` | Validates repo root, artifact root, and the app-data guard only. Never builds or runs anything. Always exits `2` (distinct from pass `0` / fail `1`) and writes `outcome: "not_run"`, so a dry run can never be mistaken for real proof. |

Exit codes: `0` = pass, `1` = fail, `2` = dry-run (not a proof result).

## Deterministic artifact naming

Without `-ArtifactRoot`, the script uses
`TestResults\DesktopProof\<yyyyMMdd-HHmmss-fff>\` so repeated runs never
collide and sort chronologically. Inside that folder:

- `manifest.json`: the schema-versioned manifest.
- `screenshot.png`: the Hub window capture.
- `proof.txt`: the deterministic proof lines written by the UI test.
- `desktop-proof.trx`: the raw test result.

## App-only capture and redaction

- The screenshot is bounded to the Hub window's own automation
  `BoundingRectangle` and captured directly from that window with the native
  `PrintWindow` API. If that app-scoped capture is unavailable or blank, the
  runner falls back to `CopyFromScreen` only after confirming the Hub is the
  foreground window, and still clips to the Hub bounds. It never captures the
  full desktop or taskbar. Inspect the image before publication and redact any
  unexpected system overlay that appeared inside the app bounds.
- The isolated tray runs against synthetic/deterministic data only (a fresh
  temp data directory and the built-in Connection page route). It never
  reads or writes real gateway tokens, device keys, settings, or prompts.
- Do not publish the full `ArtifactRoot`. The manifest's `publish` field is the
  source of truth for what is safe to attach to a PR. Never publish
  profile/auth directories, raw process logs, or the TRX (which can contain
  host metadata). Curate only `screenshot.png`, `proof.txt`, and
  `manifest.json`; retain `desktop-proof.trx` as a private diagnostic artifact.
- If a future implementation adds motion capture (for example FFmpeg
  `gdigrab`, the Windows analog of the `x11grab` pattern used in the
  monorepo's `desktop-browser-smoke.runtime.ts`), it must be cropped to the
  Hub window's bounds, never the full desktop, and must remain best-effort:
  log and report `status: "unavailable"` when the capture pipeline is not
  present rather than failing the whole run or fabricating a video. Only the
  screenshot is a hard-required artifact; video/GIF is optional evidence.

## Failure phases

`failure.phase` distinguishes why a run failed, so a reviewer can tell an
evidence-capture gap apart from a real app regression at a glance:

| Phase | Meaning |
|---|---|
| `environment-non-interactive` | The host has no interactive desktop session (Session 0 or `Environment.UserInteractive` false). Fails before build/test even starts. |
| `build` | `dotnet build` failed for the proof test project. |
| `app-missing` | The current-head tray executable was not found (usually a missing build). |
| `oracle-failed` | The deterministic UI automation assertion itself failed or did not run (bad `dotnet test` exit code, missing/unparsable TRX, or the proof test outcome was not `Passed`). This is a genuine app-regression signal. |
| `artifact-missing` | The oracle passed, but a required artifact (most commonly the screenshot witness) could not be captured or was empty. The message states the oracle passed; this is an evidence-capture gap (for example a transient foreground-focus restriction), not an app defect, but the run still fails because proof completion requires the artifact. |
| `unsupported` | `-IncludeMotion` was requested but no capture primitive exists. |

## Manifest shape

```json
{
  "schemaVersion": 1,
  "mode": "run",
  "runId": "20240101-120000-000",
  "repo": { "name": "openclaw-windows-node", "commit": "af8b36c7...", "branch": "...", "workingTreeDirty": false },
  "build": { "configuration": "Debug", "runtimeIdentifier": "win-x64", "skipped": false },
  "route": { "pageTag": "connection", "pageMarker": "ConnectionPageMarker" },
  "isolation": { "usedRealAppData": false, "note": "..." },
  "environment": { "available": true, "sessionId": 2, "userInteractive": true },
  "outcome": "pass",
  "exitCode": 0,
  "failure": null,
  "artifacts": [
    { "type": "screenshot", "path": "screenshot.png", "bytes": 12345, "status": "captured", "publish": true },
    { "type": "proof-text", "path": "proof.txt", "bytes": 210, "status": "captured", "publish": true },
    { "type": "trx", "path": "desktop-proof.trx", "bytes": 3456, "status": "captured", "testOutcome": "Passed", "publish": false },
    { "type": "motion", "path": null, "bytes": 0, "status": "unavailable", "publish": false }
  ]
}
```

`outcome` is one of `pass`, `fail`, or `not_run`. `failure` is `null` on pass
and a `{ "phase": "...", "message": "..." }` object otherwise (see "Failure
phases" above). `environment` is always populated, including on `-DryRun`.
Artifact paths are file names relative to the manifest, never absolute host
paths. `repo` deliberately omits the repository's absolute path.
Never treat a `fail` or `not_run` manifest as proof; report it as the blocker
it is.

## Known environment constraint

Screenshot capture requires an interactive Windows desktop session. The
fixture first uses app-scoped `PrintWindow`, which does not need to steal
foreground activation. If Windows returns an unusable image, it falls back to
the existing foregrounded `CopyFromScreen` path. GitHub-hosted
`windows-latest` runners and normal interactive developer sessions provide an
interactive desktop. A hard non-interactive host (Session 0) is caught early
by the `environment-non-interactive` guard above. If both capture methods fail,
the test records that exception around the screenshot witness only, so the
runner reports `artifact-missing` (oracle passed, witness unavailable) rather
than an unexplained test failure. Report that case explicitly as a blocker
rather than skipping or faking the capture.

## PR `## Real behavior proof` content

After a passing run, paste into the PR body:

```markdown
## Real behavior proof

- Ran `.\scripts\capture-windows-desktop-proof.ps1 -ArtifactRoot <path>`.
- Manifest: outcome=pass, exitCode=0, route=connection/ConnectionPageMarker.
- Attached `screenshot.png` (Hub window, Connection page, current head <short-sha>).
- `motion`: unavailable (no native video/GIF capture primitive; not requested).
```

If the run failed or could not be attempted (for example, the known
interactive-desktop constraint above), state that plainly with the manifest's
`failure.phase`/`failure.message` instead of omitting the section.

## Out of scope

This skill captures UI proof against the installed/isolated tray app only.
It never routes through, exposes, or depends on any QA-only private/debug
endpoint on a gateway; those are a separate, unsafe concern for an installed
product and are intentionally not ported here.
