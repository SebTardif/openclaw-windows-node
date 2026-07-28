---
name: windows-node-testing
description: "Select the right Windows node validation lane (unit, UI, accessibility, local MCP, gateway, installed Inno, live, performance, clean-runner) with exact repo commands and fail-closed guidance. Use when deciding which tests to run for a change, or when asked what proves a Windows node/tray change works."
---

# Windows Node Testing

This skill catalogs every validation lane available in this repository for the
OpenClaw Windows node/tray, in the order you should reach for them, with the
exact command for each. Use the smallest lane that proves the changed
subsystem, but always run the required closeout lane first.

All lanes are fail-closed by convention: a skipped, errored, or missing proof
must be reported as a blocker, never reworded as a pass.

## Changed-scope lane selection

This repository is single-platform (Windows only) with one CI job, so there
is no need for a changed-path-to-lane classifier here: every change always
runs the same `windows-latest` job. If that ever changes (for example, a
second platform lane is added), follow the upstream monorepo's conceptual
pattern rather than inventing ad hoc YAML path filters: a small,
regex-based path-to-boolean-lane classifier, analogous to
`scripts/ci-changed-scope.mjs` and `.agents/skills/openclaw-testing/SKILL.md`
in the `openclaw/openclaw` monorepo. Do not port that script; it is
monorepo/multi-platform-specific and not applicable to this single-platform
repo today.

## 1. Unit lane (required closeout)

Every code change requires this before anything else:

```powershell
$env:OPENCLAW_REPO_ROOT = (Get-Location).Path
.\build.ps1
dotnet test .\tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj --no-restore
dotnet test .\tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj --no-restore
```

In a fresh worktree, `--no-restore` can silently no-op before `bin\` exists.
Run once without `--no-restore`, or `dotnet build` the test project first.

Add project-specific unit suites when the change touches them, for example:

```powershell
dotnet test .\tests\OpenClaw.Connection.Tests\OpenClaw.Connection.Tests.csproj --no-restore
dotnet test .\tests\OpenClaw.WinNode.Cli.Tests\OpenClaw.WinNode.Cli.Tests.csproj --no-restore
dotnet test .\tests\OpenClaw.SetupEngine.Tests\OpenClaw.SetupEngine.Tests.csproj --no-restore
```

See `docs/TEST_COVERAGE.md` for the full project inventory.

## 2. UI lane

Real WinUI behavior that is awkward to validate through pure unit tests lives
in `OpenClaw.Tray.UITests`, built on `AccessibilityAppFixture` (isolated tray
launch, deep-link navigation, automation-id marker waits).

```powershell
dotnet build tests\OpenClaw.Tray.UITests -c Debug -r win-x64
dotnet test tests\OpenClaw.Tray.UITests --no-build -c Debug -r win-x64 --filter Category!=Accessibility
```

Prefer extending `AccessibilityAppFixture` and its `NavigateAsync`/marker
pattern over inventing a parallel UI automation stack.

For current-head visible screenshot or video evidence, follow
`.agents/skills/windows-computer-use-proof/SKILL.md` and the broader
`.agents/skills/openclaw-proof-validation/SKILL.md` closeout package. The
manual desktop workflow is fixed to
`[self-hosted, windows, openclaw-desktop-proof]`; it requires an active,
interactive desktop and fails closed when the deterministic UI oracle or
curated capture artifact is missing.

## 3. Accessibility lane

Real-process Axe.Windows scans of each page, matching the CI quality gate:

```powershell
dotnet test tests\OpenClaw.Tray.UITests --no-build -c Debug -r win-x64 --filter Category=Accessibility
```

See `docs/ACCESSIBILITY.md`. This lane also hosts deterministic proof tests
(see `.agents/skills/windows-computer-use-proof/SKILL.md`) that reuse the same
fixture to capture a screenshot of a known page.

## 4. Local MCP lane

Local MCP is part of every Windows node command contract. Enable **Local MCP
Server** in Settings, or run the tray with node mode enabled, then:

```powershell
winnode --list-tools
winnode --command <name> --params '<json-object>'
```

For protocol/server-shape changes, also capture raw JSON-RPC `tools/list` and
`tools/call` against `http://127.0.0.1:8765/`.

## 5. Gateway lane

When the behavior is gateway-mediated and a gateway is available:

```powershell
openclaw nodes invoke --command <name> --params '<json-object>'
```

For MXC/`system.run`/exec-approval/sandbox changes, use the formal script,
which sets the required env vars itself and fails if the MXC proof skips:

```powershell
.\scripts\validate-mxc-e2e.ps1
```

`-AllowSkip` only documents a non-MXC-capable host; it is never acceptable as
merge validation for MXC-related work.

For gateway setup/connect/pairing changes, `OpenClaw.E2ETests` covers the real
WSL Gateway path (`OPENCLAW_RUN_E2E=1`); see `docs/WINDOWS_NODE_TESTING.md` for
the exact `PublishedGatewayNativeChatTests` invocation and expected artifacts.

## 6. Installed Inno lane and release upgrade lane

Proves the installed tray runtime payload end to end, including a real Inno
install/uninstall cycle. This is this repository's closest analog to the
upstream monorepo's "post-publish"/package-boundary lane concept
(`.agents/skills/openclaw-testing/SKILL.md`, which validates an actually
published npm package rather than source): it validates the installed,
packaged artifact rather than source, catching packaging/installer defects
that a source-level build/test pass cannot see.

```powershell
.\scripts\validate-installed-inno-smoke.ps1
```

Refuses to start if existing DEV install/data/distro state is present. Every
phase must report `passed`; a missing or skipped phase is a failure. Artifacts
land under `TestResults\InstalledSmoke\<timestamp>`, with `phase-status.json`
as the phase gate.

Previous-to-current release upgrade proof is a separate, clean-machine-only
lane. Run it only in a disposable Windows VM or clean runner with PowerShell 7,
an exact official previous release tag, and that release's official x64
installer SHA-256:

```powershell
pwsh -File .\scripts\validate-inno-upgrade-smoke.ps1 `
  -PreviousRelease v0.6.12 `
  -PreviousInstallerSha256 <official-x64-installer-sha256> `
  -ConfirmCleanMachineReleaseIdentity
```

Do not remove or bypass `-ConfirmCleanMachineReleaseIdentity` to make a
developer workstation pass. A safety-only preflight is not upgrade proof.
The full lane must pass every acquisition, previous install, state seed,
current upgrade, state-preservation, installed-payload, roundtrip, and cleanup
phase. See `docs/WINDOWS_NODE_TESTING.md` for the exact release identity,
artifact, offline-input, and phase contracts.

## 7. Live lane

Credentialed model-provider and Discord parity tests live under
`tests\OpenClaw.E2ETests\LiveParity`. Run them through the fail-closed wrapper:

```powershell
.\scripts\validate-live-parity-e2e.ps1 -Lane LiveModel
.\scripts\validate-live-parity-e2e.ps1 -Lane RealChannel
```

These lanes require `OPENCLAW_RUN_E2E=1`, their lane-specific opt-in gate, an
absolute profile path, and the credential environment variables named by that
profile. Once explicitly enabled, missing or invalid configuration is a
failure, not a skip. They spend real provider or Discord budget and must never
run in normal hosted CI. Normal CI runs only the secretless gate, profile, and
redaction contract tests, never `LiveModelE2ETests` or
`RealChannelE2ETests`.

See `docs/LIVE_PARITY_TESTING.md` for profiles, gates, redaction, bounded
timeouts, and the never-normal-CI rule. The MXC, published-gateway,
installed-Inno, and interactive desktop proofs remain additional live
behavior lanes. For an isolated manual tray proof and the complete evidence
package, follow `.agents/skills/openclaw-proof-validation/SKILL.md`.

## 8. Performance lane

There is no automated performance/soak lane in this repository today. Per
`docs/TEST_COVERAGE.md`, long-running reconnect/high-frequency-activity/memory
behavior over multi-day sessions is an explicitly documented gap. If a change
claims a performance characteristic, say so plainly and either provide a
manual measurement (with method and numbers) or report the gap as unverified.
Do not invent a performance test to fill this lane.

If a performance lane is ever added, keep it non-gating initially and use
Windows-native, process-level metrics rather than porting Linux-style
thresholds:

- CLI/tray startup time (wall clock from process start to first ready
  signal, for example the Hub window becoming reachable).
- `Process.PeakWorkingSet64` (or `Get-Process`'s `PeakWorkingSet`) for a
  representative session, not Linux RSS. Windows working-set accounting
  differs from Linux RSS; never reuse a Linux RSS threshold or budget as a
  Windows working-set gate.
- Image/build provenance (configuration, runtime identifier, commit) recorded
  alongside any measurement, so a number can be attributed to a specific
  build rather than compared across unlike builds.

Treat any such metrics as informational evidence attached to a PR, not a
pass/fail gate, until this repository has enough historical data to set a
real, Windows-appropriate threshold.

## 9. Clean-runner lane

A "clean runner" is a host with no prior OpenClaw state: no `%APPDATA%\
OpenClawTray[-Dev]`, no `OpenClawGateway[-Dev]` WSL distro, no dev-build
identity marker. Start with `docs/CLEAN_WINDOWS_RUNNERS.md`; it defines the
ownership, lease, artifact, and proof taxonomy for both supported controllers.
Route local Hyper-V clean-machine, installed-smoke, and release-upgrade
requests through `.agents/skills/openclaw-hyperv-smoke/SKILL.md`. It owns the
inventory, fixed-checkpoint, PowerShell Direct, restore-in-finally, and blocker
runbook.

```powershell
# Diagnose/repair missing local prerequisites on a fresh machine or agent host.
.\scripts\setup-dev.ps1 -CheckOnly

# Local operator-owned Hyper-V VM and checkpoint controller.
# Create is unattended by default. Resume and cleanup are switches on Create.
.\scripts\clean-windows\Invoke-CleanWindowsHyperV.ps1 -Command <Create|Prepare|Verify|Smoke|Restore> ...

# Safe answer XML, DPAPI ACL, IMAPI2 ISO, mount, and cleanup proof. No VM.
.\scripts\clean-windows\Test-CleanWindowsUnattendMedia.ps1 -Command ValidateMedia

# Install the pinned Crabbox client, then run a disposable Azure Windows lease.
.\scripts\clean-windows\Install-Crabbox.ps1
.\scripts\clean-windows\Invoke-CrabboxWindowsSmoke.ps1 -Mode <NativeDesktopComponent|Wsl2Component|CombinedInstalledSmoke> ...
```

`NativeDesktopComponent` proves only native Windows desktop capability.
`Wsl2Component` proves only the WSL2 component. Separate component leases must
never be combined into a full installed-app claim. `CombinedInstalledSmoke`
requires one explicit x64 Azure Windows image whose same lease proves native
desktop, WSL2, Ubuntu, and the selected installed or upgrade smoke.

Hyper-V operations require elevation, hardware virtualization, and exact
VM/checkpoint ownership markers plus `-ConfirmOwnedAction`. Crabbox requires
interactive Azure authentication, an approved image, Azure RBAC, and exact
lease-id capture and cleanup. Missing elevation, RBAC, image preparation, or
lease proof is an external blocker, never a reason to weaken ownership or
success-shape separate component evidence.

If `Checkpoint-VM` returned before an exact fixed-name snapshot became visible,
do not issue ad hoc checkpoint commands. Route pending-intent or the narrowly
documented completed-unattended markerless state through the Hyper-V skill's
Prepare-only `-RecoverPendingCheckpoint -ConfirmOwnedAction` procedure.
An active checkpoint `.avhdx` is not accepted by name or extension alone. The
controller reads the exact VM object's sole active hard disk and uses `Get-VHD`
to require a bounded, cycle-free canonical `ParentPath` chain whose terminal
ancestor is the exact owner-marked base VHD. All VM, owner, ID, VHD, and
checkpoint markers remain required. Use the Hyper-V skill's exact elevated
recovery procedure only when the controller reports pending intent.

The current owned VM is restored to its exact finalized `clean-windows`
checkpoint and retains the final rotated credential. Route it through normal
`Prepare` without `-RecoverPendingCheckpoint`. Prepare uses bounded
optional-feature, conditional restart, WSL package, conditional restart, and
final verification stages before tool setup. After WSL verification it runs a
fixed current-user WinGet/App Installer bootstrap pinned to official Microsoft
`winget-cli` `v1.29.280`. The bootstrap uses immutable HTTPS assets, manual
allowlisted redirects, pinned sizes and SHA-256 hashes, exact Microsoft
signatures and manifests, x64-only dependency ordering, and a clean nonce
guest-temp root. It does not use `License1.xml`, all-users provisioning, Store
UI.

The separate `Microsoft.Winget.Source` package is mutable catalog data, not an
immutable App Installer pin. Both App Installer branches first inspect the
current-user registration and accept only one exact Microsoft publisher,
neutral architecture, valid nonzero version identity. A valid registration is
skipped with acquisition `existing`, observed version, and null hash.
Duplicates or invalid registrations fail. If missing, the bootstrap streams
`https://cdn.winget.microsoft.com/cache/source2.msix` under the nonce
temporary root with the shared bounded deadline, a 16 MiB maximum, manual
HTTPS port 443 redirects restricted to exact
`cdn.winget.microsoft.com` targets, and no cookies, credentials, or
authorization. It rejects zero length and `Content-Length` mismatch, then
requires Authenticode `Valid`, the exact Microsoft signer, and exact
`Microsoft.Winget.Source` name, publisher, neutral architecture, and valid
nonzero manifest version before current-user installation and bounded
registration polling. The runtime SHA-256 and version are evidence, never
permanent pins. Existing registration evidence honestly has no downloaded
hash, and redirect query strings are not recorded.

Only after catalog registration does the bootstrap verify an exact JSON export
for the source named `winget`, perform one bounded typed update of only that
source, and resolve `Git.Git` through that source before package installation.
The bootstrap and host proof record catalog acquisition, version, and hash or
null. The prepared `openclaw-prerequisites` checkpoint freezes the observed
registration. It does not reset, remove, add, or touch `msstore`.
All executable WinGet installs use explicit `--source winget`, agreement flags,
and disabled interactivity, so `msstore` is never queried. It does not install
Ubuntu or another distribution. The installed smoke provisions its app-owned
distribution later.

The PowerShell 7 package is an additional exact selection contract:
`Microsoft.PowerShell` version `7.6.4.0`, installer type `wix`, machine scope,
and source `winget`. The audited community manifest identifies
`PowerShell-7.6.4-win-x64.msi` with SHA-256
`d11942df52fd12470169797abfa4781d9480efdc81000ba4fa55a5b921ed8dd0`;
WinGet enforces that manifest hash. The controller must not select or retry
the default MSIX bundle, because PowerShell Direct does not provide the active
AppX user session it needs. Post-install proof requires the exact
`C:\Program Files\PowerShell\7\pwsh.exe` path and engine version `7.6.4`.
Error `0x80073D19` is reported as the known AppX
deployment-session/user-logged-off regression.

After a failed `Prepare`, the driver must first confirm that the rollback
restored the exact owned `clean-windows` checkpoint and finalized marker. The
next attempt is normal `Prepare`, without `-RecoverPendingCheckpoint` or
cleanup. In the package stage, zero-exit status plus nonzero version invokes
the one fixed `wsl.exe --update --web-download` operation. Accepted update
exits `0` and `3010` always require the owned reconnect before final status,
version, and feature verification. The current failed Prepare is expected to
have rolled back after the default PowerShell MSIX failed with `0x80073D19`,
but this hotfix does not claim live confirmation. Unit coverage for the
bootstrap and pinned PowerShell installer uses extracted functions and mocks
only. Do not use a VM or real Appx/network operations for that focused lane.

Fresh unattended Hyper-V `Create` requires `-GenerateCredential`, verifies the
official ISO SHA256, builds a separate answer ISO with Windows IMAPI2, and
mounts and validates that ISO before any VM or VHD creation. It then waits a
bounded time for PowerShell Direct, but first clears the Gen2 optical boot
prompt through Microsoft Hyper-V `root\virtualization\v2`
`Msvm_Keyboard.TypeKey` using the trusted VM ID. It sends multiple 750 ms
space-key pulses within a fixed 7-second boot-only window and does not stop
after the first successful delivery. The bounded sequence runs after fresh
unattended `Start-VM` and never during later reboots. Ordinary resume paths do
not inject keys. The sole resume exception is an owned Off partial VM whose
incomplete security configuration was repaired and reverified before it was
started. The controller then detaches both ISO drives, removes answer media,
verifies completed Windows 11 Enterprise Evaluation setup, and rotates the
generated setup credential. It establishes the final-credential session
before accepting only a classified authentication rejection from the old
credential. It returns a current-user DPAPI `CredentialPath`; route
`Prepare`, `Verify`, and `Smoke` through that path instead of passing
plaintext. Owned partial installs may use `-ResumeUnattended` or
`-CleanupUnattend`, but either requires `-ConfirmOwnedAction` and never deletes
the VM or VHD.

Unattended setup deliberately leaves the desktop at the sign-in screen and
does not enable autologon. Native desktop screenshot proof requires a later
explicit interactive sign-in. The no-VM media helper is focused setup-media
proof only and is not clean-machine, installed-app, or desktop proof.

GitHub-hosted `windows-latest` CI runners are clean by construction for normal
automated suites, but they do not replace the controller-specific clean-machine
proof above. When validating locally, prefer isolated/dev flows (see
`run-app-local.ps1`) over touching real `%APPDATA%` state.

## Fail-closed guidance

- A lane that is skipped, times out, or cannot run must be reported as a named
  blocker, never omitted or reworded as a pass.
- A test that no-ops (0 tests discovered, or "no-op" build messages) is not
  proof. Confirm a non-zero test count actually ran.
- Prefer the script/tool's own fail-closed exit code and manifest over
  eyeballing console output. Scripts in this repo (`validate-mxc-e2e.ps1`,
  `validate-installed-inno-smoke.ps1`, `validate-inno-upgrade-smoke.ps1`,
  `validate-live-parity-e2e.ps1`, `capture-windows-desktop-proof.ps1`) are
  designed to exit non-zero and write a failure record rather than a
  success-shaped status when any required artifact or check is missing.
- Never point a validation lane's isolated data directory, WSL distro, or
  artifact root at real `%APPDATA%`/`%LOCALAPPDATA%` OpenClaw folders.
