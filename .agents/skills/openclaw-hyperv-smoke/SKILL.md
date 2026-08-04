---
name: openclaw-hyperv-smoke
description: Use an owned local Windows 11 Hyper-V VM to run clean-machine OpenClaw installed or release-upgrade smoke with fixed checkpoints, PowerShell Direct transport, fail-closed artifacts, and restore-in-finally safety.
---

# OpenClaw Hyper-V smoke

Use this skill for local clean-machine Windows proof on an operator-owned
Hyper-V VM. It is the Windows-native lifecycle analog of a snapshot-based VM
runbook. It ports the lifecycle structure, not another platform's controller
or guest tooling.

For lane selection and required host closeout, start with
`.agents/skills/windows-node-testing/SKILL.md`. For the complete proof package,
use `.agents/skills/openclaw-proof-validation/SKILL.md`. Use
`.agents/skills/windows-computer-use-proof/SKILL.md` only when the guest has an
active interactive desktop and current-head visible evidence is required.
Hyper-V is the local owned clean-VM backend. `.agents/skills/crabbox/SKILL.md`
is the remote disposable-lease backend.

## Inventory before action

Run inventory from an elevated PowerShell session before creating, restoring,
starting, stopping, or copying anything:

```powershell
$vmName = "OpenClaw-Clean-Windows"
$ownerId = "openclaw-clean-runner-<operator>"
$vm = Get-VM -Name $vmName -ErrorAction SilentlyContinue
$snapshots = if ($vm) { @(Get-VMSnapshot -VMName $vmName) } else { @() }
$vm
$snapshots | Select-Object VMName, Name, Id, CreationTime
```

Also confirm:

- Windows 11 Pro, Enterprise, or Education with Hyper-V enabled.
- The shell is elevated and hardware virtualization is enabled.
- The exact Windows 11 Enterprise Evaluation x64 ISO and unused VHDX path are
  available for unattended `Create`. The controller generates the per-VM
  credential. Manual `Prepare` requires a credential or the DPAPI
  `CredentialPath` returned by unattended `Create`.
- The intended VM is Generation 2 x64 with secure boot, vTPM, automatic
  checkpoints disabled, and nested virtualization enabled.
- WSL2 and an Ubuntu distribution can run inside the guest.
- No other developer or smoke lane owns an active conflicting VM operation.

If elevation, ISO, credential, virtualization, or exclusive VM ownership is
missing, report that exact blocker and stop. Do not turn inventory or a
preflight-only result into proof.

Use explicit blocker wording when applicable:

- non-elevated host;
- missing ISO;
- missing credential for the guest administrator;
- active conflicting VM owned by another operator or smoke lane.

## Hyper-V lifecycle mapping

| Snapshot VM concept | Safe Windows Hyper-V equivalent |
|---|---|
| Inventory VM and snapshots | `Get-VM` and `Get-VMSnapshot` |
| Create a checkpoint | `Checkpoint-VM` through the owned controller |
| Restore a checkpoint | `Restore-VMSnapshot` only after owner-marker validation |
| Start or stop the guest | `Start-VM` / `Stop-VM` only inside an owned operation |
| Execute in the guest | PowerShell Direct `Invoke-Command -VMName` or an owned `PSSession` |
| Copy committed source into the guest | One validated `git archive` ZIP through `Copy-Item -ToSession` |
| Retrieve proof artifacts | `Copy-Item -FromSession` |

Do not call lifecycle cmdlets directly to bypass the repository controller.
The controller binds the VM, VHD, checkpoint name, snapshot ID, creation time,
VM ID, and `OwnerId` before mutation.

## Fixed checkpoints and ownership

The controller owns exactly two checkpoint names:

- `clean-windows`: updated Windows before OpenClaw prerequisites.
- `openclaw-prerequisites`: WSL2 platform features, Git, PowerShell 7, staged
  .NET/Node/Windows SDK/WebView2/Visual Studio Build Tools VC runtime packages,
  and a passing
  `scripts\setup-dev.ps1 -CheckOnly`, before smoke state. The installed smoke
  provisions its gateway distribution later.

VM ownership is recorded in Hyper-V notes and beside the VHD under
`.openclaw-clean-windows\<vm-name>`. Checkpoint markers bind the exact
snapshot identity. A version 2 checkpoint marker is written atomically as
`pending` before `Checkpoint-VM`, then finalized as `complete` only after a
60-second exact-name poll at 500 millisecond intervals observes one snapshot.
Pending recovery accepts only a snapshot created within 15 minutes of intent.
Pending markers never authorize remove or restore operations. Existing
resources require matching finalized markers and `-ConfirmOwnedAction`.

Never delete or unregister a VM, VHD, or checkpoint. Never modify, restore,
start, or stop an unowned or mismatched resource. Never weaken
`-ConfirmOwnedAction`, clean-state guards, or checkpoint identity checks.

An active checkpoint attaches its `.avhdx` leaf instead of the owner-marker
base `.vhdx`. The ownership assertion reads active hard disks only from the
exact VM object and preserves this controller's exactly-one-disk rule. It
requires each leaf and parent to exist and be `Get-VHD` readable, follows
canonical `ParentPath` ancestry for at most 32 levels with case-insensitive
cycle detection, and accepts only a terminal path equal to the exact
owner-marked base. Extra disks, unrelated bases, broken ancestry, cycles,
ambiguous data, and excess depth fail closed. VM note/file markers, VM ID,
owner ID, VHD marker, and checkpoint identity remain mandatory.

## Create unattended by default

Fresh unattended Create requires `-GenerateCredential` as explicit consent.
The switch is rejected for manual Create, `-ResumeUnattended`, and
`-CleanupUnattend`.

```powershell
$iso = "D:\isos\Win11_Enterprise_Eval_25H2_en-us_x64.iso"
$vhd = "D:\Hyper-V\OpenClaw-Clean-Windows\os.vhdx"

$createResult = .\scripts\clean-windows\Invoke-CleanWindowsHyperV.ps1 `
  -Command Create `
  -VMName $vmName `
  -OwnerId $ownerId `
  -IsoPath $iso `
  -VhdPath $vhd `
  -GenerateCredential `
  -SwitchName "Default Switch" `
  -ProcessorCount 8 `
  -StartupMemoryGB 16 `
  -VhdSizeGB 120

$credentialPath = $createResult.CredentialPath
```

The verified input is 7,092,807,680 bytes with SHA256
`A61ADEAB895EF5A4DB436E0A7011C92A2FF17BB0357F58B13BBC4062E535E7B9`.
That digest is the default `ExpectedIsoSha256` contract and is checked before
VM creation. The answer file uses the supported `install.wim` index selector
for image 1. The pinned digest binds it to the verified name
`Windows 11 Enterprise Evaluation`, which is also checked after setup. The
answer uses en-US and a UEFI/GPT disk layout, suppresses disposable Evaluation
OOBE, supplies no product key, and contains no arbitrary commands or scripts.
Secure boot uses the canonical Hyper-V cmdlet template identifier
`MicrosoftWindows`. Verification accepts only whitespace/case-normalized API
output for that exact template. If the host exposes `SecureBootTemplateId`, it
must contain a non-empty GUID.

The original Microsoft ISO and the IMAPI2-built answer ISO remain separate.
Before any VM or VHD creation, production Create mounts the answer ISO
read-only, reads and hashes `AutoUnattend.xml`, reruns strict validation, and
dismounts it. Immediately after the one fresh unattended `Start-VM`, it uses
the trusted VM ID with Microsoft's `root\virtualization\v2`
`Msvm_Keyboard.TypeKey` method to send up to nine space-key pulses, 750 ms
apart after a 750 ms delay, and clear the Gen2 optical boot prompt. Injection
continues through its fixed 7-second boot-only window even after a successful
delivery, then stops unconditionally. At least one delivery must succeed, and
a no-delivery failure includes a safe last-error diagnostic. Injection never
runs during later reboots. Ordinary resume paths do not inject keys. The sole
resume exception is an owned Off partial VM whose incomplete security
configuration was repaired and reverified before it was started. Only then
does the bounded PowerShell Direct wait begin. Both DVD media are detached with
bounded Hyper-V readback polling, and answer XML/staging/ISO files are removed.
The controller verifies the actual
edition, build, x64 architecture, local administrator, completed setup/OOBE,
and image state. It removes known guest answer caches, rotates the temporary
setup password, establishes and probes the final-credential PowerShell Direct
session, and accepts old-password rejection only for a classified
authentication failure in a bounded check. It persists only the new credential
as current-user DPAPI CLIXML with a restrictive ACL.

The CIM key is not manual Setup or desktop automation. A missing successful
injection is a blocker, not a reason to fall back to interactive setup.

The temporary answer ISO contains a plaintext setup secret. VM disk sectors,
setup logs, and pre-rotation snapshots can retain it until overwritten. DPAPI
ties the returned credential file to the same Windows host and user. Treat
partial state as sensitive.

No autologon is enabled. PowerShell Direct does not need an interactive login.
The desktop remains at sign-in. Screenshot proof later requires an explicit
interactive sign-in, never a stored autologon credential.

Use `-CreateMode Manual` only when an operator intentionally needs the old
interactive Windows setup path. Manual mode still verifies the ISO digest and
later commands may use an in-memory `-Credential`.

Safe helper proof does not create a VM and does not require elevation:

```powershell
.\scripts\clean-windows\Test-CleanWindowsUnattendMedia.ps1 -Command ValidateMedia
```

It generates, mounts, validates, dismounts, and deletes nonce-bound owned test
media while printing only nonsecret JSON.

The current Windows 11 Enterprise Evaluation build 26200 x64 state has nested
hypervisor presence confirmed and is restored to the exact `clean-windows`
checkpoint. Its checkpoint owner marker is finalized, answer media and setup
material are absent, and the final rotated `guest.clixml` credential remains
available. Both WSL optional features are disabled at that checkpoint, and
`wsl.exe --status` returns exit 50 with `WSL is not installed`. This is the
expected input to normal `Prepare`.

The obsolete `Prepare` reached source transfer after WinGet, Git, and
PowerShell succeeded, but its recursive checkout copy included more than 3
GiB of ignored host `bin`/`obj` output. After about 58 minutes it failed
because exit-zero Git line-ending warning stderr surfaced as a failed
PowerShell Direct job. That run is not clean proof. Its existing `finally`
was expected to restore the exact `clean-windows` checkpoint, but this hotfix
does not claim live confirmation. The driver must confirm that exact owned
restore and finalized checkpoint marker before the next attempt. Retry with
normal `Prepare`. Do not use `-RecoverPendingCheckpoint`,
`-CleanupUnattend`, or an ad hoc lifecycle command.

The fifteenth real `Prepare` passed WSL, signed WinGet/catalog, Git,
PowerShell 7 Wix, the 6 MiB committed source archive, and Git
staging/provenance. Its old monolithic setup-dev install job then lost the
Hyper-V socket immediately after Git detection, so it did not identify or
verify the package transition. Its existing `finally` is expected to restore
`clean-windows`, but this change does not claim live confirmation. Confirm the
exact owned finalized checkpoint before normal retry.

The sixteenth real `Prepare` reached the staged developer-prerequisite
controller but failed before any package installation because Windows
PowerShell 5.1 treated an inline parenthesized `if` command argument as a
command named `if`. The controller now assigns that operation label before
invocation, and an executable Windows PowerShell 5.1 regression covers both
install and verify-only worker paths. Its existing `finally` is expected to
restore `clean-windows`, but this change does not claim live confirmation.
Confirm the exact owned finalized checkpoint before normal retry.

The seventeenth real `Prepare` reached the .NET 10 package stage, where WinGet
reported `0x8A150010` because the manifest has no `Scope` and the controller
had applied `--scope machine` universally. The .NET Burn selection now omits
the scope filter and records null scope evidence; Node, Windows SDK, and
WebView2 retain exact machine scope. The .NET verification still requires an
installed 10.x SDK. Its existing `finally` is expected to restore
`clean-windows`, but this change does not claim live confirmation. Confirm the
exact owned finalized checkpoint before normal retry.

The eighteenth real `Prepare` installed and verified .NET 10 and Node LTS.
The Windows SDK installer then recycled the PowerShell Direct target process
without rebooting Windows. The old recovery reconnected on the original boot
but waited only for a boot-identity advance and timed out. Recovery now accepts
only package verification on the exact owner-bound Running VM ID. It records
`session-loss-reboot` for a newer verified boot or
`session-recycle-same-boot` for verified service/session recycling, and polls
same-boot verify-only attempts without repeating install. Its existing
`finally` restored `clean-windows`; this implementation does not independently
operate or inspect the VM.

The nineteenth real run completed `Prepare`, created
`openclaw-prerequisites`, and passed `Verify`. Installed smoke passed preflight
but then failed; recursive remote directory retrieval also failed before logs
could be retained. The prepared checkpoint remains valid. After the artifact
archive fix, retry only the typed `Installed` smoke lane from
`openclaw-prerequisites`; do not rerun Prepare or Verify.

The twentieth real run again passed `Prepare` and `Verify`; artifact archive
retrieval succeeded and retained the failed Installed build proof. The
prepared checkpoint is valid. The failure showed the extracted repository
owned by `BUILTIN\Administrators` instead of `OpenClawAdmin`, and direct
`dotnet tool restore | Out-Host` promoted benign native stderr to a terminating
error. Source staging now normalizes and verifies every exact extracted entry
owner SID before Git init, without reparse traversal or wildcard Git trust.
Version discovery now uses exact bounded `dotnet.exe` stdout/stderr capture
and parses only stdout JSON. Retry only Installed Smoke after this fix; do not
rerun Prepare or Verify.

The twenty-first Installed retry still failed its build, but artifact
retrieval then collided with files from the prior run because the configured
host artifact path was reused directly. `-HostArtifactRoot` is now only a
base. Every Smoke invocation allocates a unique timestamp-plus-nonce child,
prints and records that actual path, and never overwrites or deletes an older
run. The current build failure remains unknown. Retry only Installed Smoke
with the same base and inspect the newly reported child directory.

The next isolated artifact run identified the remaining clean-image
prerequisite: publish requires Visual Studio Build Tools. A later real Prepare
proved that `Microsoft.VisualStudio.Component.VC.Redist.14.Latest` can register
without laying down the loose CRT files consumed by publish. The prepared
checkpoint therefore requires both that component and the individual
`Microsoft.VisualStudio.Component.VC.Tools.x86.x64` component. Rerun normal
`Prepare` from `clean-windows` to install and verify both, recreate the prepared
checkpoint, then run `Verify` and Installed `Smoke`.

The next real retry passed `Prepare` and `Verify`, including both Build Tools
components and loose CRT files. Installed Smoke then lost its PowerShell Direct
target after preflight while the nested validation process continued. Immediate
artifact packaging collided with the still-open roundtrip log. The controller
now reconnects to the exact owned Running VM, waits boundedly for the existing
lane completion marker without rerunning validation, and packages only after
the validation writers close.

The completion-aware retry recovered that same validation process and preserved
complete artifacts. It then exposed two later defects: clean nested WSL CLI
installation exceeded the old five-minute budget on both attempts, and cleanup
used `Start-Process` after the PowerShell Direct target recycled. The CLI step
now retains the official HTTPS installer and pinned version, adds bounded
initial curl transport controls plus structured JSON progress, uses 15 minutes
per clean-install attempt, and reports sanitized stdout/stderr tails. Installed
and Upgrade smoke now use a COM-independent bounded
`System.Diagnostics.Process` helper for Inno operations; Installed smoke uses it
for E2E `dotnet.exe` build/test as well. The helper captures separate artifacts
and terminates only its exact observed PID tree on timeout. The prepared
checkpoint remains valid, so retry only Installed Smoke.

From an elevated PowerShell session, run normal `Prepare` without
`-RecoverPendingCheckpoint`:

```powershell
.\scripts\clean-windows\Invoke-CleanWindowsHyperV.ps1 -Command Prepare -VMName 'OpenClaw-Clean-Windows' -OwnerId 'openclaw-clean-runner-bkudiess' -VhdPath 'D:\Hyper-V\OpenClaw-Clean-Windows\os.vhdx' -CredentialPath $credentialPath -ConfirmOwnedAction
```

`-RecoverPendingCheckpoint` remains a Prepare-only repair for a future
operation that actually reports pending intent, or for the narrowly supported
completed-unattended markerless state. It requires `-ConfirmOwnedAction`,
validates exact VM, VHD, credential, snapshot identity, and bounded creation
time, and never deletes or recreates a snapshot. Do not add it to the current
normal command.

Do not use `-CleanupUnattend`, reinstall, delete the VM or VHD, issue ad hoc
snapshot commands, or remove either owned checkpoint.

Partial failure never deletes a VM or VHD. Before PowerShell Direct readiness,
owned answer media and the DPAPI setup credential remain for diagnosis. For a
different owned partial state that should not continue, Cleanup validates the
exact unattended and VM markers before detaching or deleting only owned setup
material.

## Prepare

```powershell
.\scripts\clean-windows\Invoke-CleanWindowsHyperV.ps1 `
  -Command Prepare `
  -VMName $vmName `
  -OwnerId $ownerId `
  -VhdPath $vhd `
  -CredentialPath $credentialPath `
  -ConfirmOwnedAction
```

`Prepare` creates or validates `clean-windows`, restores it, and uses explicit
idempotent stages. The optional-feature stage checks both exact Windows
features, enables only disabled features with `-NoRestart`, and returns both
states plus `needsRestart`. The host controller restarts and reconnects only
the exact owned VM when required. The package stage uses a fixed native helper
that captures bounded `wsl.exe --status` and `--version` output without sending
native stderr to the PowerShell job error stream. Ready status and version
require no operation. Explicit absent status uses only
`wsl.exe --install --no-distribution`. Ready status with any nonzero version
exit invokes exactly one fixed `wsl.exe --update --web-download`, without
localized-text classification. Install and update accept only exits `0` and
`3010`, always request a second bounded restart, and do not re-probe before
that restart. Update failures include bounded, sanitized version and update
diagnostics. The helper supplies no interactive input, Store UI, shell text,
or arbitrary arguments. The final verification stage for WSL requires
zero-exit status and version before the WinGet stage.

After final WSL verification, a fixed PowerShell Direct scriptblock ensures
the current guest user has official Microsoft WinGet `v1.29.280`. It uses only
the immutable GitHub release, manually follows at most five HTTPS redirects to
the two GitHub asset hosts, streams with a shared 1800-second timeout, and
checks pinned sizes and SHA-256 values before parsing. It safely indexes the
dependency zip, extracts only the three pinned x64 packages, and requires
exact Microsoft Authenticode publishers and namespace-independent Appx
manifest identities. It similarly validates the bundle and its single
nonstub `AppInstaller_x64.msix` payload before any installation.

The `Microsoft.Winget.Source` catalog is intentionally mutable and is not part
of those reproducible pins. The bootstrap first checks for exactly one valid
current-user registration. It accepts only the exact name and Microsoft
publisher, neutral architecture, and a valid nonzero version. A valid existing
registration is skipped with acquisition `existing`, its observed version,
and a null hash. Duplicates and invalid registrations fail closed.

When missing, the catalog is streamed from the fixed official initial URL
`https://cdn.winget.microsoft.com/cache/source2.msix` under the existing nonce
temporary root. It shares the 1800-second deadline, has a 16 MiB maximum,
rejects zero length and `Content-Length` mismatches, and manually follows at
most five HTTPS port 443 redirects that remain on exact
`cdn.winget.microsoft.com` targets without user information or fragments.
Cookies, credentials, and authorization are disabled. Before current-user
installation it requires Authenticode `Valid`, the exact Microsoft signer,
and a safely parsed manifest with exact `Microsoft.Winget.Source` name and
publisher, neutral architecture, and a valid nonzero `System.Version`. It
records the runtime SHA-256 and observed version as acquisition `downloaded`.
Neither value is a permanent pin, and redirect query strings are never
evidence.

The bootstrap uses current-user `Add-AppxPackage`, with no `License1.xml`,
all-users provisioning, or Store UI.
Exact existing App Installer version `1.29.280.0` skips immutable release
downloads, but both the existing and newly installed App Installer branches
ensure the signed mutable catalog first. Only then do they validate the direct
executable, WindowsApps alias, `v1.29.280`, exact JSON export of the source
named `winget`, a bounded typed update of only that exact source, and
noninteractive `Git.Git` resolution through that source. The bootstrap and
host proof record acquisition, observed catalog version, and runtime SHA-256
or honest null. The prepared `openclaw-prerequisites` checkpoint freezes the
observed registration. Every later package install uses explicit
`--source winget`, both
agreement flags, and disabled interactivity, so `msstore` is never queried.
The bootstrap does not reset, remove, add, or touch `msstore`.
All downloads, extraction, and captures use one nonce guest-temp root. Cleanup
failure is a failed bootstrap. Only then do Git, PowerShell 7, the five staged
developer packages, checkout copy, and `scripts\setup-dev.ps1 -CheckOnly`
run.

PowerShell installation is pinned to community package version `7.6.4.0` and
uses exact `--installer-type wix --scope machine --source winget` arguments.
This selects the machine-safe `PowerShell-7.6.4-win-x64.msi`; the audited
community manifest SHA-256 is
`d11942df52fd12470169797abfa4781d9480efdc81000ba4fa55a5b921ed8dd0`, and
WinGet enforces that manifest hash. The controller never falls back to MSIX
and never enables autologon. It then requires the exact machine path
`C:\Program Files\PowerShell\7\pwsh.exe`, PATH resolution to that path, and
reported engine version `7.6.4`. Installation and version probes use bounded
native captures with sanitized diagnostics. Failures include decimal and
eight-digit hexadecimal codes, with an explicit diagnostic for the known
AppX session error `0x80073D19`.

Before source transfer, the controller installs one exact package per bounded
native WinGet operation: `.NET 10 SDK` as
`Microsoft.DotNet.SDK.10` `10.0.302` `burn`, Node LTS as
`OpenJS.NodeJS.LTS` `24.18.0` `wix`, Windows SDK as
`Microsoft.WindowsSDK.10.0.26100` `10.0.26100.7705` `burn`, and WebView2 as
`Microsoft.EdgeWebView2Runtime` `150.0.4078.83` `exe`, then Visual Studio
Build Tools as `Microsoft.VisualStudio.2022.BuildTools` `17.14.37` `exe`.
The Build Tools selection uses machine scope and only
`--custom "--add Microsoft.VisualStudio.Component.VC.Redist.14.Latest --add Microsoft.VisualStudio.Component.VC.Tools.x86.x64 --norestart"`.
These are two individual components: the registered redistributable update and
the x64/x86 tools files that supply the loose CRT DLLs required by publish. It
does not add workloads, the IDE, recommended, or optional component sets. The
.NET Burn manifest
has no `Scope`, so that typed selection omits `--scope` and records null scope
evidence while its SDK verification proves installation. The other three
typed selections retain exact machine scope. Every operation uses source
`winget`, silent/noninteractive agreement flags, redirected bounded
diagnostics, and no MSIX fallback.

Build Tools verification requires the exact standard
`Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe` path. It
executes that application with
`-latest -products * -requires Microsoft.VisualStudio.Component.VC.Redist.14.Latest Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`,
requires exit zero and exactly one canonical existing install root, then
requires nonempty x64 `vcruntime140*.dll` and `msvcp140*.dll` beneath a
non-reparse `VC\Redist\MSVC\<version>\x64\Microsoft.VC*.CRT` directory safely
contained by that root. Evidence reports only the two individual components,
install root, and VC Redist version. Verify repeats the same verify-only worker,
so it rejects a stale prepared checkpoint missing either component or the
runtime files.

Each stage first applies setup-dev's real availability check and skips an
already-present package. Exit zero requires immediate verification. A
reboot-required result triggers the exact owned restart. Installer-initiated
reboot or PowerShell Direct socket loss requires bounded reconnect to the same
owner-bound Running VM ID followed by verify-only. A verified newer boot
records `session-loss-reboot`; a verified original boot records
`session-recycle-same-boot`. Missing same-boot software gets bounded
reconnect/verify-only polling, while newer-boot missing software, regressed
boot identity, changed VM identity, and non-Running state fail closed. The
controller never repeats install and preserves original installation plus
verification diagnostics. After source staging, only
`scripts\setup-dev.ps1 -CheckOnly` runs; Prepare never invokes its mutating
install mode.

Source transfer then requires an empty `git status --porcelain=v1
--untracked-files=all` and creates one deterministic ZIP from exact committed
`HEAD` with `git archive`. The controller records and bounds HEAD, tracked-file
and archive-entry counts, compressed and expanded sizes, and SHA-256. Host and
guest both reject absolute, traversal, duplicate case-insensitive,
Windows-unsafe, `.git`, `bin`, `obj`, and `TestResults` entries, unexpected
tree or ZIP types, unsafe symbolic-link targets, and archive count/hash/size
mismatches. The guest verifies before resetting its repo root, extracts with
built-in tooling, rejects all resulting reparse points and generated
directories, writes `openclaw-source-provenance.json`, and initializes the
disposable Git commit. Before Git initialization, a bounded breadth-first
walk sets only the owner of the exact root and every extracted entry to the
current guest administrator SID through Windows ACL APIs, preserving access
rules/inheritance and verifying every owner afterward. Reparse points and
wrong owners fail closed; no wildcard `safe.directory` is used. Guest staging
then sets repository-local
`core.autocrlf=false` and `core.safecrlf=true`, then runs only fixed Git
operations through bounded redirected native processes. Exit-zero stderr is
returned as sanitized bounded warning evidence; nonzero exits fail closed.
Pre/post source-tree SHA-256 digests must match, LF working-tree bytes remain
unchanged, and final porcelain status must be empty. Exactly one archive
crosses PowerShell Direct. Guest and host copies are removed in `finally`,
and cleanup failure is fatal.

Installed and upgrade version discovery resolves only `dotnet.exe` as a
Windows Application and runs fixed tool restore/GitVersion arguments through
bounded redirected `Start-Process` capture. Exit-zero stderr is diagnostic,
nonzero/timeout errors are sanitized and bounded, GitVersion parses stdout
only as one JSON object, and capture plus temporary dotnet environment state
are cleaned up/restored.

Installed and Upgrade smoke process execution is separate from version
discovery. Inno install/uninstall uses the shared COM-independent
`System.Diagnostics.Process` helper with typed arguments, redirected captures,
raw `/DIR=` and `/LOG=` values without embedded quotes, and bounded timeout
tree termination. Installed E2E `dotnet.exe` build/test
uses the same helper. The nested clean WSL CLI install keeps the official HTTPS
path and pinned version, requests structured progress, bounds initial curl
connect/stall/retry behavior, and allows 15 minutes per attempt. Timeout and
nonzero failures preserve sanitized bounded stdout and stderr tails. Gateway
configuration then budgets 45 seconds per emitted cold
`openclaw config set` command plus startup headroom (7.5 minutes for the
default nine commands) and records command-count plus sanitized tail evidence
on failure.

`openclaw gateway start` can outlive its bounded controller command after
systemd has already created the socket. Recovery requires the installed
`openclaw-gateway.service` to be `active/running` and its exact nonzero
`MainPID` to match a PID reported for the configured listening port. It then
continues to the normal HTTP health gate without a duplicate start. Process
names alone are not ownership proof; mismatched or foreign listeners fail
closed.

Budget clean-distro QR/bootstrap, exact device and node approval, bounded
approval drains, and final gateway status commands for two minutes each. Keep
approval request IDs in `OPENCLAW_APPROVAL_REQUEST_ID`, never replay an
approval within one operation after timeout, and retain only bounded sanitized
stdout/stderr diagnostics.

`Prepare` does not install Ubuntu or another distribution. Installed smoke
provisions its app-owned distribution later. Checkpoint observation, guest
commands, restarts, and reconnects are bounded. Failed jobs report bounded,
sanitized reason and child diagnostics before removal. Timeouts retain pending
intent for explicit recovery. Recovery is never automatic.

## Verify

```powershell
.\scripts\clean-windows\Invoke-CleanWindowsHyperV.ps1 `
  -Command Verify `
  -VMName $vmName `
  -OwnerId $ownerId `
  -VhdPath $vhd `
  -CredentialPath $credentialPath `
  -ConfirmOwnedAction
```

`Verify` restores the owned `openclaw-prerequisites` checkpoint and checks
Gen2/x64 host configuration, nested virtualization, guest readiness, WSL2,
and OpenClaw prerequisites without claiming a distribution or installed-app
proof. Its summary resolves `git.exe`, `dotnet.exe`, `node.exe`, and
`npm.cmd` only as Windows Applications and runs each exact path through
bounded redirected native capture. It never selects `npm.ps1`; exit zero and
the expected version shape are required, including semantic version output
from `npm.cmd`.

## Smoke: Installed lane

```powershell
.\scripts\clean-windows\Invoke-CleanWindowsHyperV.ps1 `
  -Command Smoke `
  -ValidationLane Installed `
  -VMName $vmName `
  -OwnerId $ownerId `
  -VhdPath $vhd `
  -CredentialPath $credentialPath `
  -HostArtifactRoot "TestResults\CleanWindows\HyperV\Installed" `
  -ConfirmOwnedAction
```

This runs `scripts\validate-installed-inno-smoke.ps1`. Its
`phase-status.json` must report `passed` for `preflight`, `build`, `install`,
`installed-payload`, `roundtrip`, and `cleanup`. A missing phase, skipped
proof, nonzero exit, missing completion marker, or cleanup failure is not
proof.

## Smoke: Upgrade lane

```powershell
.\scripts\clean-windows\Invoke-CleanWindowsHyperV.ps1 `
  -Command Smoke `
  -ValidationLane Upgrade `
  -PreviousRelease v0.6.12 `
  -PreviousInstallerSha256 "<official-x64-installer-sha256>" `
  -VMName $vmName `
  -OwnerId $ownerId `
  -VhdPath $vhd `
  -CredentialPath $credentialPath `
  -HostArtifactRoot "TestResults\CleanWindows\HyperV\Upgrade" `
  -ConfirmOwnedAction
```

The controller invokes `scripts\validate-inno-upgrade-smoke.ps1` through a
typed argument array and adds `-ConfirmCleanMachineReleaseIdentity`. It does
not accept arbitrary script text or a direct-install fallback. The exact
official previous tag and x64 installer SHA-256 are required.

`phase-status.json` must report a zero exit, `cleanupCompleted: true`, and
`passed` for `preflight`, `acquire-previous`, `prepare-current`,
`install-previous`, `seed-state`, `upgrade-current`, `state-preservation`,
`installed-payload`, `roundtrip`, and `cleanup`. Required artifacts include
`upgrade-smoke.log`, `upgrade-smoke.done`, `inno-install-previous.log`,
`inno-install-current.log`, and
`installed-runtime-proof\phase-status.json`.

`-SafetyPreflightOnly` proves only that a host appears safe. Never report it as
upgrade proof.

## Restore

```powershell
.\scripts\clean-windows\Invoke-CleanWindowsHyperV.ps1 `
  -Command Restore `
  -VMName $vmName `
  -OwnerId $ownerId `
  -VhdPath $vhd `
  -CheckpointName openclaw-prerequisites `
  -ConfirmOwnedAction
```

Use the exact owned checkpoint name for reproduction. Do not restore while
another operator or smoke lane owns the VM.

## Transport and cleanup contract

The controller copies exactly one validated clean-HEAD Git archive with
`Copy-Item -ToSession`, executes with PowerShell Direct
`Invoke-Command -VMName` or an owned session, and retrieves one guest-created
artifact ZIP file with `Copy-Item -FromSession`. It never recursively copies a
remote directory. Guest packaging validates the exact owned lane root, safe
relative paths, no reparse points, bounded count/size, and SHA-256. The host
revalidates size/hash/ZIP paths before extraction and requires lane-specific
phase/log files. `-HostArtifactRoot` is a base: each invocation atomically
creates a contained, non-reparse, previously absent
`yyyyMMdd-HHmmss-fff-<8hex>` child for extraction and manifest output.
Collisions retry without changing old runs; the actual path is printed,
returned on success, and recorded even for pre-retrieval failures. Both
archive copies are removed in `finally`, but prior run directories are never
overwritten or deleted. On the exact PowerShell Direct transport-loss
signature, the controller reconnects only to the unchanged owner-bound Running
VM and uses short host-driven polls for the already-running lane's completion
marker and phase status. Subsequent target-process recycling may trigger
additional exact-VM reconnects within the same absolute deadline. It never
reruns validation, and non-transport integrity failures fail immediately.
Recovered success proceeds to packaging; recovered failure preserves bounded
phase/log diagnostics. Packaging starts only after those completion files prove
artifact writers have closed. If the packaging session broke, artifact retrieval
reconnects under the same guard, removes only owned nonce archive residue, and
retries packaging once. Healthy-session integrity failures are not retried. The host manifest
records validation and artifact recovery separately. Commands have bounded
timeouts.

Every smoke attempt must stop the owned guest and restore
`openclaw-prerequisites` in a `finally` path on success or failure. Verify the
host artifact directory and phase gates before making a proof claim. A failed
artifact copy, missing manifest, failed restore, or running conflicting VM is
a named blocker. Primary smoke and artifact retrieval failures remain visible
together; available phase status and log tail diagnostics are sanitized and
bounded.

See `docs/CLEAN_WINDOWS_RUNNERS.md` for the authoritative controller runbook
and `docs/WINDOWS_NODE_TESTING.md` for installed and release-upgrade phase
details.
