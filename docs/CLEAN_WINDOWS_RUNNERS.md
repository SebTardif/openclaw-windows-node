# Clean Windows runners

This repository supports two Windows-native clean-machine validation paths:

1. A local, operator-owned Windows 11 Hyper-V VM with checkpoints.
2. A disposable Azure Windows lease managed by Crabbox.

Both paths reuse `scripts\validate-installed-inno-smoke.ps1`. They do not use
macOS, Parallels, or a Linux lease as Windows proof.

## Local Hyper-V runner

### Host prerequisites

- Windows 11 Pro, Enterprise, or Education with Hyper-V enabled.
- An elevated Windows PowerShell 5.1 or newer session.
- Hardware virtualization enabled in firmware.
- The verified Microsoft Windows 11 Enterprise Evaluation ISO described below
  and an unused VHDX path.
- At least 8 GB guest memory, 80 GB free disk, and four logical processors by
  default.

The controller creates a Generation 2 VM with secure boot using the canonical
Hyper-V cmdlet template identifier `MicrosoftWindows`, vTPM, automatic
checkpoints disabled, and nested virtualization enabled through
`ExposeVirtualizationExtensions`. Host verification accepts only
whitespace/case-normalized API output for that exact template. When
`SecureBootTemplateId` is exposed, it must be a present, non-empty GUID. Nested
virtualization is required because the installed smoke provisions and
exercises WSL2 inside the guest.

The controller never deletes a VM or VHD. Existing VMs and checkpoints are
refused unless both conditions are met:

- the VM/checkpoint marker matches `-OwnerId`; and
- the command includes `-ConfirmOwnedAction`.

The VM marker is stored in Hyper-V VM notes and beside the VHD under
`.openclaw-clean-windows\<vm-name>`. Checkpoint marker files bind the checkpoint
name, Hyper-V snapshot ID, creation time, VM ID, VHD path, and owner ID.
An active checkpoint normally changes the VM's attached hard disk from the
owner-marked base `.vhdx` to a differencing `.avhdx`. `Assert-OwnedVM` reads
hard disks only from the exact VM object and keeps this controller's strict
exactly-one-active-hard-disk contract. It requires the attached leaf to be an
existing canonical file, reads every level with `Get-VHD`, and follows
`ParentPath` for at most 32 levels with case-insensitive cycle detection. The
chain is accepted only when its terminal canonical path is the exact
owner-marked base VHD. Missing or unreadable levels, unrelated terminal bases,
cycles, excess depth, ambiguous API data, and every additional hard disk are
refused. This does not weaken the VM note/file marker, VM ID, owner ID, or
checkpoint identity checks.

Checkpoint creation first atomically writes a version 2 `pending` intent at the
final marker path, then calls `Checkpoint-VM` and polls only the exact fixed name
for up to 60 seconds at 500 millisecond intervals. The marker becomes `complete`
only after the exact snapshot identity and creation time are observable. A
timeout retains the pending intent for explicit recovery. A pending candidate
must have appeared within 15 minutes of its recorded start. Pending markers
never authorize remove or restore actions. Existing version 1 checkpoint
markers remain compatible only when all legacy final identity fields match.

### Create the VM unattended by default

`Create` defaults to `-CreateMode Unattended`. The known input used by the
local clean runner is shown below. A fresh unattended Create also requires
`-GenerateCredential` as explicit consent to create and persist the per-VM
credentials. That switch is rejected for manual Create, `-ResumeUnattended`,
and `-CleanupUnattend`.

| Property | Verified value |
|---|---|
| ISO | `D:\isos\Win11_Enterprise_Eval_25H2_en-us_x64.iso` |
| ISO size | `7,092,807,680` bytes |
| ISO SHA256 | `A61ADEAB895EF5A4DB436E0A7011C92A2FF17BB0357F58B13BBC4062E535E7B9` |
| `install.wim` image | Index `1`, `Windows 11 Enterprise Evaluation` |
| Switch | `Default Switch` |
| VM | `OpenClaw-Clean-Windows` |
| Owner | `openclaw-clean-runner-bkudiess` |
| VHD | `D:\Hyper-V\OpenClaw-Clean-Windows\os.vhdx` |
| VM resources | 8 processors, 16 GB startup RAM, 120 GB VHD |

```powershell
$vmName = "OpenClaw-Clean-Windows"
$ownerId = "openclaw-clean-runner-bkudiess"
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

`ExpectedIsoSha256` defaults to the exact digest in the table. The controller
hashes the original ISO and fails before `New-VM` when it does not match. An
unattended operation refuses an alternate digest because its image contract is
pinned to this Enterprise Evaluation media. Safe manual mode may use a
different explicit expected digest. The Microsoft ISO is attached read-only
and is never rebuilt, copied, or mutated.

For unattended mode, the controller:

1. Requires `-GenerateCredential`, then generates a high-entropy per-VM setup
   credential in process.
2. builds `AutoUnattend.xml` with `XmlDocument` APIs and validates it;
3. creates a small data ISO with the Windows IMAPI2 API, with no ADK,
   `oscdimg`, or third-party utility;
4. mounts the answer ISO read-only, reads and hashes its root
   `AutoUnattend.xml`, reruns strict XML validation, dismounts it, and fails
   closed before any VM or VHD creation if validation fails;
5. attaches the original Microsoft ISO and the validated answer ISO as separate DVD
   drives, then boots from the original ISO;
6. immediately after the initial fresh unattended `Start-VM`, clears the Gen2
   optical `Press any key to boot from CD or DVD` prompt by calling
   `Msvm_Keyboard.TypeKey` with space (`0x20`) through Microsoft's
   `root\virtualization\v2` Hyper-V CIM provider, selecting the VM by its
   trusted VM ID;
7. sends up to nine space-key pulses at 750 ms intervals after a 750 ms delay,
   stops unconditionally within a fixed 7-second boot-only window, requires at
   least one successful CIM delivery, and reports a safe last-error diagnostic
   if none succeeds. It does not inject keys during setup or later reboots.
   Ordinary resume paths do not inject keys. The sole resume exception is an
   owned Off partial VM whose incomplete security configuration was repaired
   and reverified before it was started;
8. waits a bounded time for PowerShell Direct;
9. detaches both ISO drives, bounded-polls Hyper-V until neither exact owned
   path is reported, then removes the answer XML, staging directory, and answer
   ISO;
10. verifies Windows 11 Enterprise Evaluation, build, x64/AMD64 architecture,
   the enabled local administrator, PowerShell Direct, completed setup/OOBE,
   completed image state, and no pending setup command;
11. removes known cached guest answer-file locations;
12. rotates the setup password to a newly generated final credential, opens
    and probes a final-credential PowerShell Direct session, then accepts old
    credential rejection only for a classified authentication failure within
    a bounded check; and
13. returns the nonsecret `CredentialPath`.

The synthetic keyboard step is Microsoft Hyper-V CIM, not desktop automation
and not manual Setup. Host prerequisite checks require `Get-CimInstance`,
`Get-CimAssociatedInstance`, and `Invoke-CimMethod`. The controller never
interpolates `VMName` into WQL.

The answer file uses the supported `install.wim` index selector for image `1`.
The pinned ISO digest binds that index to the verified name
`Windows 11 Enterprise Evaluation`, and post-install verification checks the
actual Enterprise Evaluation edition. It uses en-US and a UEFI/GPT layout:
260 MB FAT32 EFI, 16 MB MSR, then an extending NTFS Windows partition. It
wipes only the fresh VHD created by this `Create` operation. It supplies no
product key, contains no setup scripts or commands, and suppresses disposable
Enterprise Evaluation OOBE.

The answer file necessarily contains the temporary setup password in
plaintext. It exists only in an inheritance-disabled owned media directory
restricted to the current user, SYSTEM, and Administrators. Credentials stored
on the host use current-user DPAPI `PSCredential` CLIXML in a separately
protected owned directory with the same restrictive ACL. No password is
accepted on a command line or written to logs, repository files, result
objects, or proof artifacts.

> [!warning]
> Answer media contains a transient plaintext secret. Even after detaching,
> deleting cached answer files, and rotating the account, the setup password
> may remain recoverable from VM disk sectors or Windows setup logs until those
> sectors are overwritten. A snapshot created before rotation can retain the
> old state. DPAPI ties the credential file to the same Windows host and user.
> Treat the VM, VHD, setup logs, pre-rotation snapshots, and owned partial-state
> directory as sensitive.

No plaintext autologon is configured. PowerShell Direct does not require an
interactive login. A successful unattended install intentionally leaves the
native desktop at the Windows sign-in screen. Later screenshot or computer-use
proof requires an explicit interactive sign-in with the final credential. Do
not add stored autologon to automate desktop proof.

Safe manual setup remains available:

```powershell
.\scripts\clean-windows\Invoke-CleanWindowsHyperV.ps1 `
  -Command Create `
  -CreateMode Manual `
  -VMName $vmName `
  -OwnerId $ownerId `
  -IsoPath $iso `
  -VhdPath $vhd
```

Manual mode still verifies the explicit ISO SHA256 before creating the VM. It
attaches only the Microsoft ISO and leaves Windows setup to the operator.

### Resume, recover, or clean owned unattended state

A failed unattended operation never deletes its VM or VHD. Before PowerShell
Direct readiness, it retains the owned state marker, DPAPI setup credential,
answer XML, staging directory, and answer ISO for diagnosis. Inspect the VM,
VHD, DVD paths, and
`.openclaw-clean-windows\OpenClaw-Clean-Windows\unattend.owner.json`.

#### Current restored clean checkpoint

The controlled Windows 11 Enterprise Evaluation build 26200 x64 experiment is
complete, and nested hypervisor presence was confirmed.
The exact `clean-windows` checkpoint has been restored. The checkpoint owner
marker is finalized, and answer media and setup material are absent.
The final rotated `guest.clixml` credential remains available. At this clean checkpoint,
`Microsoft-Windows-Subsystem-Linux` and `VirtualMachinePlatform` are disabled,
and `wsl.exe --status` returns exit 50 with `WSL is not installed`. That is the
expected input to the staged preparation flow.

The current failed preparation attempt installed the pinned App Installer and
validated its source export, then failed because manual repair also requires
the mutable signed `Microsoft.Winget.Source` catalog package. That package was
not present before the `Git.Git` probe. The existing failure rollback is
expected to have restored the exact
`clean-windows` checkpoint, but this hotfix does not claim live confirmation
of that restore. Before retrying, the driver must confirm that exact owned
restore and its finalized checkpoint marker. Then use the normal `Prepare`
command below. Do not use `-RecoverPendingCheckpoint`, `-CleanupUnattend`, or
an ad hoc lifecycle command for this retry.

From an elevated PowerShell session, use normal `Prepare` without
`-RecoverPendingCheckpoint`:

```powershell
.\scripts\clean-windows\Invoke-CleanWindowsHyperV.ps1 -Command Prepare -VMName 'OpenClaw-Clean-Windows' -OwnerId 'openclaw-clean-runner-bkudiess' -VhdPath 'D:\Hyper-V\OpenClaw-Clean-Windows\os.vhdx' -CredentialPath $credentialPath -ConfirmOwnedAction
```

The controller validates exact ownership, restores `clean-windows`, runs the
bounded prerequisite stages, and rolls back to `clean-windows` if preparation
fails. Do not use `-CleanupUnattend`, reinstall Windows, delete the VM or VHD,
issue ad hoc snapshot commands, or remove either owned checkpoint.

#### Recover a different pending checkpoint

`-RecoverPendingCheckpoint` remains a Prepare-only repair for a controller
state that actually has a pending checkpoint marker or the narrowly supported
completed-unattended markerless state. It requires `-ConfirmOwnedAction` and
validates the exact VM note and file markers, final DPAPI credential, VM and VHD
identity, exact checkpoint name, snapshot identity, and bounded creation time.
Duplicate, wrong-name, wrong-VM, old, or future snapshots are refused.

Do not add the recovery switch to the current normal `Prepare` command. If a
future operation reports pending intent, follow the reported typed recovery
path rather than deleting, recreating, or adopting a snapshot.

#### Resume a different owned partial state

Resume only the exact owned VM:

```powershell
.\scripts\clean-windows\Invoke-CleanWindowsHyperV.ps1 `
  -Command Create `
  -ResumeUnattended `
  -VMName $vmName `
  -OwnerId $ownerId `
  -VhdPath $vhd `
  -ConfirmOwnedAction
```

For a different owned partial install that should not continue, detach exact
owned installation media and remove only owned unattended media and the
temporary setup credential. Do not use this cleanup path for the current
completed state described above:

```powershell
.\scripts\clean-windows\Invoke-CleanWindowsHyperV.ps1 `
  -Command Create `
  -CleanupUnattend `
  -VMName $vmName `
  -OwnerId $ownerId `
  -VhdPath $vhd `
  -ConfirmOwnedAction
```

Both operations validate the unattended marker and, when a VM exists, the
ordinary VM note/file ownership markers. They refuse mismatches. Cleanup never
deletes or unregisters a VM or VHD. If failure happened after PowerShell
Direct readiness, successful media cleanup may already have removed the
answer media as required.

Validate answer XML, DPAPI/ACL behavior, IMAPI generation, a read-only mount,
and cleanup without creating a VM or requiring Hyper-V elevation:

```powershell
.\scripts\clean-windows\Test-CleanWindowsUnattendMedia.ps1 `
  -Command ValidateMedia
```

The helper accepts only a new owned child of
`TestResults\CleanWindowsUnattend`, prints a nonsecret JSON proof, dismounts
the ISO, validates its nonce-bound marker, and removes the generated root.

### Prepare checkpoints

```powershell
.\scripts\clean-windows\Invoke-CleanWindowsHyperV.ps1 `
  -Command Prepare `
  -VMName $vmName `
  -OwnerId $ownerId `
  -VhdPath $vhd `
  -CredentialPath $credentialPath `
  -ConfirmOwnedAction
```

`Prepare` creates or validates `clean-windows`, restores it, and runs explicit
idempotent stages. The optional-feature stage checks both exact Windows
features, enables only disabled features with `-NoRestart`, and returns both
resulting states plus `needsRestart`. The host controller restarts only the
exact owned VM and reconnects PowerShell Direct when required. The package
stage uses a sequential authoritative short-circuit: it invokes and normalizes
the fixed `wsl.exe --status` operation before deciding whether `--version` is
safe to invoke. It has three package decisions:

- Zero-exit status and version are ready and require no package operation.
- Explicit absent status (exit `-1` or `50` plus not-installed output) uses
  only `wsl.exe --install --no-distribution`. This branch never invokes version
  or update.
- Zero-exit ready status with any nonzero version exit invokes exactly one
  `wsl.exe --update --web-download`. This branch does not depend on localized
  version text.

An unknown or contradictory status fails before version is invoked. Version is
normalized only in the ready-status branch, where contradictory zero-exit
not-installed text still fails closed.

Install and update accept only exits `0` and `3010`. Either successful
operation always requests the second bounded owned restart. The update result
is authoritative, and the package stage does not re-probe status or version
before that restart. A nonzero update failure reports bounded, sanitized
version and update diagnostics. The trusted helper has fixed typed argument
arrays and supplies no standard input, Store UI, shell command text, or
arbitrary arguments.

After the second reconnect, final verification requires enabled features plus
zero-exit status and version. The pinned WinGet bootstrap then runs before Git,
PowerShell 7, the four staged developer packages, repository copy, or the
read-only `scripts\setup-dev.ps1 -CheckOnly` gate.

#### Pinned App Installer and signed mutable WinGet catalog bootstrap

`Prepare` installs WinGet only for the PowerShell Direct guest user. It uses
`Add-AppxPackage` for current-user registration. It never uses all-users
provisioning, `License1.xml`, Store UI, or an App Installer license file.

The immutable source is the official Microsoft `winget-cli` release
`v1.29.280` under:

```text
https://github.com/microsoft/winget-cli/releases/download/v1.29.280/
```

The three top-level assets are pinned before any parsing or extraction:

| Asset | Bytes | SHA-256 |
|---|---:|---|
| `Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle` | 216775738 | `0809fa9f52e395d6e7de692331dce847ac991952675116bb4d8aae2ddcc20946` |
| `DesktopAppInstaller_Dependencies.zip` | 97760717 | `3bbfcaa5cb011c48fac48d896d64a5c7c6898859a9f3d01555c8cd000f4e2962` |
| `DesktopAppInstaller_Dependencies.json` | 322 | `a56ddd79cf9cd056d9546cfeb6958c2b44d20f6221f8518bf17b003717d47a7a` |

The descriptor must contain exactly these ordered x64 dependencies, with no
extra dependency such as UI.Xaml:

1. `Microsoft.VCLibs.140.00` version `14.0.33519.0`
2. `Microsoft.VCLibs.140.00.UWPDesktop` version `14.0.33728.0`
3. `Microsoft.WindowsAppRuntime.1.8` version `8000.616.304.0`

Downloads use a streaming Windows PowerShell 5.1 `HttpClient` path with a
shared 1800-second cancellation bound. Automatic redirects, cookies,
credentials, and authorization headers are disabled. The initial host must be
`github.com`; up to five HTTPS redirects may target only
`release-assets.githubusercontent.com` or `objects.githubusercontent.com`.
Diagnostics include only the safe asset name, host, and status. SAS query
strings are never printed. TLS is restricted to TLS 1.2 in the handler when
available; a required global fallback is restored before returning.

The dependency archive is indexed with `ZipArchive`. Every entry path must be
relative, traversal-free, and unique. Only the three exact x64 package paths
are extracted. Each package is size and SHA-256 checked before its
Authenticode signature and namespace-independent Appx manifest are inspected.
All signers and manifest publishers must be exactly Microsoft. The package
name, version, publisher, and x64 architecture must match the pin.

The bundle must have a Valid Microsoft Authenticode signature and exact
`Microsoft.DesktopAppInstaller` bundle identity version `2026.623.1704.0`.
Only its single nonstub `AppInstaller_x64.msix` entry is extracted. That
payload is pinned to 62421154 bytes and SHA-256
`bdc908068f7563d89ef3405f1a30ae74df8cb0416414ed3613c4d68e2c812ff1`.
Its manifest must identify x64 `Microsoft.DesktopAppInstaller` version
`1.29.280.0`, the three exact minimum dependencies, application id `winget`,
root executable `winget.exe`, and alias `winget.exe`.

No package is installed until every downloaded and extracted artifact passes
all checks. Dependencies install in the pinned order before the bundle.
Already registered exact x64 dependencies are verified and skipped. Older
matching dependencies may be upgraded to the pin. Unexpected identity,
publisher, architecture, or newer versions fail closed.

The community source catalog is a separate mutable Microsoft-signed package,
not an immutable `winget-cli` release asset. Its one fixed official initial
URL is:

```text
https://cdn.winget.microsoft.com/cache/source2.msix
```

The catalog download shares the bootstrap's 1800-second deadline and streams
to `source2.msix` under the same nonce temporary root. It has an explicit 16
MiB maximum instead of a permanent exact size pin. Automatic redirects,
cookies, credentials, and authorization are disabled. Manual redirects are
bounded to five and may remain only on exact
`cdn.winget.microsoft.com` HTTPS port 443 targets without user information or
fragments. Zero-length content, oversized content, and a declared
`Content-Length` that does not match the completed stream fail closed.
Diagnostics and evidence never include redirect query strings.

The runtime SHA-256 is computed after download and retained as evidence, but
is not compared with a permanent hash. Before current-user
`Add-AppxPackage`, the package must have Authenticode status `Valid`, the exact
Microsoft signer subject, and a safely parsed `AppxManifest.xml` identity with
name `Microsoft.Winget.Source`, the exact Microsoft publisher, neutral
architecture, and a valid nonzero `System.Version`. The runtime version is
not a reproducible pin. Neither runtime value is a permanent pin. The
immutable App Installer and dependency size,
hash, version, architecture, signature, and manifest pins remain unchanged.

Before downloading, the bootstrap inspects current-user
`Microsoft.Winget.Source` registrations. Exactly one registration with the
exact name, publisher, neutral architecture, and valid nonzero version is
accepted and skipped. Its evidence records acquisition `existing`, the
observed version, and a null SHA-256 because no downloaded file was observed.
Duplicates or any invalid identity fail instead of being replaced. If the
package is missing, the bootstrap downloads, validates, and installs it, then
polls the exact current-user registration for at most 60 one-second attempts.
The registered version must equal the validated signed manifest version.
Downloaded evidence records acquisition `downloaded`, the observed version,
and the runtime SHA-256.

If exactly one current-user
`Microsoft.DesktopAppInstaller_8wekyb3d8bbwe` registration already has version
`1.29.280.0`, `Prepare` validates its exact identity, root `winget.exe`,
and bounded WindowsApps alias and PATH resolution. It skips the immutable
release downloads but still ensures the signed mutable source catalog and
records its evidence. Newly installed App Installer follows the same catalog
workflow. Only after the catalog registration succeeds does either branch
validate exact `winget --version` output `v1.29.280`, export exactly the source
named `winget`, hydrate only that source with the bounded, typed
`source update --name winget --accept-source-agreements
--disable-interactivity` operation and requires successful noninteractive
resolution of `Git.Git` through `--source winget`. The bootstrap result and
host proof record catalog acquisition, observed version, and runtime SHA-256
or honest null. The prepared `openclaw-prerequisites` checkpoint freezes that
observed registration. No source is reset, removed, or added, and `msstore`
is not touched. The update is scoped only to the exported `winget` source.
Every package install in Prepare,
shared developer setup, and the installed-smoke Inno bootstrap uses explicit
`--source winget`, source and package agreement flags, and disabled
interactivity, so `msstore` is never queried.

PowerShell 7 is pinned separately to community package version `7.6.4.0`.
`Prepare` requires `--installer-type wix`, `--scope machine`, and
`--source winget`, so WinGet selects `PowerShell-7.6.4-win-x64.msi` instead of
the default MSIX bundle. The audited community manifest SHA-256 for that MSI
is `d11942df52fd12470169797abfa4781d9480efdc81000ba4fa55a5b921ed8dd0`;
WinGet enforces the manifest hash during installation. After installation,
the controller requires `C:\Program Files\PowerShell\7\pwsh.exe`, requires
`pwsh.exe` on PATH to resolve to that exact machine path, and executes it
noninteractively to require engine version `7.6.4`. It does not retry with
MSIX or enable autologon. Native failures include decimal and eight-digit
hexadecimal exit codes plus bounded sanitized output. `0x80073D19` receives a
specific AppX deployment-session/user-logged-off diagnostic.

The remaining developer prerequisites are also controller-owned stages before
source transfer. Each stage uses exactly one bounded native WinGet install
with redirected, sanitized stdout and stderr, package-specific optional scope,
source `winget`, silent/noninteractive agreement flags, and one exact version
and installer type:

| Stage | Package | Version | Installer type | Scope filter |
|---|---|---:|---|---|
| .NET 10 | `Microsoft.DotNet.SDK.10` | `10.0.302` | `burn` | Omitted. The Burn installer is machine-wide, but its manifest has no `Scope`. |
| Node LTS | `OpenJS.NodeJS.LTS` | `24.18.0` | `wix` | `machine` |
| Windows SDK | `Microsoft.WindowsSDK.10.0.26100` | `10.0.26100.7705` | `burn` | `machine` |
| WebView2 | `Microsoft.EdgeWebView2Runtime` | `150.0.4078.83` | `exe` | `machine` |

Before installing, each stage uses the same availability contract as
`setup-dev.ps1`: a 10.x SDK from `dotnet --list-sdks`, both `node` and `npm`,
a numeric Windows SDK Include directory, or a valid WebView2 runtime
registration. Existing prerequisites skip their WinGet operation. A
successful install must immediately pass the same check.
The .NET proof records a null scope filter rather than claiming a manifest
filter that was not applied; its post-install SDK-path/version check still
proves the machine installation.

Exit `3010` and WinGet
`APPINSTALLER_CLI_ERROR_INSTALL_REBOOT_REQUIRED_TO_FINISH` request an exact
owned restart. Installer-initiated reboot and PowerShell Direct socket loss
use bounded transition-aware recovery instead. Every reconnect requires the
same owner-bound Running VM ID, reads boot identity, and invokes package
verify-only without repeating install. Verified software on a newer boot
records `session-loss-reboot`; verified software on the original boot records
`session-recycle-same-boot`. If same-boot verification is initially missing,
the controller polls bounded reconnect and verify-only attempts for installer
completion. A newer-boot verification failure, regressed boot identity,
changed VM ID, or non-Running VM fails closed. Timeout diagnostics preserve
the original install failure and latest verification failure. Each proof
records package, pinned selection, whether install ran, the verified
transition type, and observed verification.

#### Clean committed source transfer

`Prepare` and `Smoke` never recursively copy the host checkout. Before source
transfer, the controller requires `git status --porcelain=v1
--untracked-files=all` to be empty and resolves one full committed `HEAD`.
Ignored host output may still exist, but it cannot enter the payload. The
controller creates one deterministic `git archive --format=zip` from exact
`HEAD`, so `.git`, untracked files, ignored `bin`, `obj`, `TestResults`, and
other stale build output are absent by construction.

The archive contract caps compressed size at 256 MiB, expanded size at 512
MiB, and tracked files at 20,000. It records the source HEAD, tracked-file and
archive-entry counts, byte size, expanded size, and SHA-256. Host validation
requires the archive file count to equal the committed tree count. Entry names
must be unique under Windows case-insensitive comparison, relative,
traversal-free, Windows-safe, and free of `.git`, `bin`, `obj`, and
`TestResults` path segments. Unexpected Git tree or ZIP entry types fail.
Git symbolic-link entries are accepted only when their UTF-8 target is a safe
relative non-traversing path. Extraction must materialize no reparse points.

PowerShell Direct transfers exactly that one bounded ZIP. The guest verifies
its size and SHA-256 and repeats the complete ZIP entry validation before
resetting the guest repository root. After built-in `Expand-Archive`, it
requires the exact tracked-file count and no generated directories or reparse
points. It writes `openclaw-source-provenance.json` with the original host
HEAD and archive evidence. Before Git initialization, a bounded breadth-first
walk that never recurses through reparse points changes only the owner of the
exact repository root and every extracted file/directory, including
provenance, to the current guest administrator SID. It preserves access rules
and inheritance, then rereads every owner SID and fails on any mismatch. Git
trust therefore comes from correct Windows ownership, not a wildcard
`safe.directory`. The guest then initializes and commits the disposable Git
repository. Before staging, it sets repository-local
`core.autocrlf=false` and `core.safecrlf=true`. Every fixed Git operation runs
through a bounded native-process helper with redirected stdout and stderr, so
exit-zero warnings remain bounded diagnostics instead of failing a PowerShell
Direct job. Nonzero exits fail with sanitized bounded diagnostics. The
controller hashes every extracted source file before and after staging,
excluding only `.git`, and requires identical digests plus an empty final
`git status --porcelain`. This proves staging did not rewrite LF working-tree
bytes and that the source provenance is part of the clean commit. Both guest
and host archives are removed in `finally`; cleanup failure fails the
operation.

Installed builds call `scripts\Get-OpenClawVersion.ps1`, which resolves the
exact `dotnet.exe` Application and runs fixed `tool restore` and
`tool run dotnet-gitversion -- /output json` arguments through bounded
`Start-Process` capture. Stdout and stderr remain separate; exit-zero warning
stderr is diagnostic only, while timeout/nonzero failures include sanitized
bounded output. Only stdout may be parsed, and it must be one JSON object with
the requested version property. Capture files are removed and temporary
dotnet environment overrides are restored on success or failure.

After source staging, Prepare invokes only `scripts\setup-dev.ps1 -CheckOnly`
through a bounded redirected native process. The clean controller never runs
the script's package-installing mode. CheckOnly must pass the repository
prerequisite and build preflight without changing package or Git trust state.

Downloads, extraction, and native output captures live under one nonce child
of the guest temporary directory. The root is removed on success or failure.
A cleanup failure makes the bootstrap fail.

`Prepare` does not install Ubuntu or any other distribution. The installed
smoke provisions its app-owned distribution later. Guest commands, restart
reconnection, and PowerShell Direct readiness have bounded timeouts. Failed
jobs include bounded sanitized reason, child error, and output diagnostics.
`-Credential` remains available for operator-managed manual mode. Unattended
mode should use the returned DPAPI `-CredentialPath`. Checkpoint writes are
transactional and eventually consistent. Recovery is never automatic and is
limited to the explicit owned path documented above.

### Verify and smoke

```powershell
.\scripts\clean-windows\Invoke-CleanWindowsHyperV.ps1 `
  -Command Verify `
  -VMName $vmName `
  -OwnerId $ownerId `
  -VhdPath $vhd `
  -CredentialPath $credentialPath `
  -ConfirmOwnedAction

.\scripts\clean-windows\Invoke-CleanWindowsHyperV.ps1 `
  -Command Smoke `
  -VMName $vmName `
  -OwnerId $ownerId `
  -VhdPath $vhd `
  -CredentialPath $credentialPath `
  -HostArtifactRoot "TestResults\CleanWindows\HyperV" `
  -ConfirmOwnedAction
```

The Verify summary resolves exactly `git.exe`, `dotnet.exe`, `node.exe`, and
`npm.cmd` as Windows `Application` commands, then executes each resolved path
through bounded native stdout/stderr capture. It never invokes ambiguous
`npm` or `npm.ps1`, so the guest execution policy cannot redirect the check to
the PowerShell shim. Every command must exit zero and return its expected
version shape; npm specifically requires semantic version output. Capture
cleanup is mandatory.

The controller transfers one validated clean-HEAD source archive with
PowerShell Direct `Copy-Item -ToSession`, runs the typed `Installed` validation
lane by default, and never recursively copies the remote artifact directory.
Before checkpoint restore, the guest validates the exact lane root under
`GuestRoot\artifacts`, rejects reparse points and unsafe relative paths, bounds
file count and expanded size, and creates a nonce ZIP with size and SHA-256
proof. The controller copies that one file with `Copy-Item -FromSession`,
rechecks size/hash and every ZIP entry, extracts under `HostArtifactRoot`, and
requires lane-specific phase/log artifacts. Guest and host archive copies are
removed in `finally`. If the validation or packaging session broke, retrieval
reconnects boundedly only to the exact owner-bound Running VM, removes only
owned nonce archive residue, and retries packaging once. Healthy-session
integrity failures are not retried. Primary validation and artifact failures
are both retained. A failed validation also reports a
bounded sanitized phase-status snapshot and log tail when available. Only
after retrieval does the VM stop and restore to `openclaw-prerequisites`.

After `scripts\validate-inno-upgrade-smoke.ps1` is integrated, run the typed
upgrade lane with an exact official release tag and x64 installer SHA-256:

```powershell
.\scripts\clean-windows\Invoke-CleanWindowsHyperV.ps1 `
  -Command Smoke `
  -ValidationLane Upgrade `
  -PreviousRelease v0.6.12 `
  -PreviousInstallerSha256 "<official-x64-sha256>" `
  -VMName $vmName `
  -OwnerId $ownerId `
  -VhdPath $vhd `
  -CredentialPath $credentialPath `
  -HostArtifactRoot "TestResults\CleanWindows\HyperV\Upgrade" `
  -ConfirmOwnedAction
```

The controller restores and verifies its owned `openclaw-prerequisites`
checkpoint before it invokes `pwsh`. It builds a PowerShell argument array and
adds `-ConfirmCleanMachineReleaseIdentity` itself. It does not accept arbitrary
script text, arbitrary arguments, an offline installer path, or a direct-install
fallback.

### Restore explicitly

```powershell
.\scripts\clean-windows\Invoke-CleanWindowsHyperV.ps1 `
  -Command Restore `
  -VMName $vmName `
  -OwnerId $ownerId `
  -VhdPath $vhd `
  -CheckpointName openclaw-prerequisites `
  -ConfirmOwnedAction
```

## Remote Crabbox Azure runner

### Install Crabbox user-locally

```powershell
.\scripts\clean-windows\Install-Crabbox.ps1
```

The installer uses only the official immutable
[`openclaw/crabbox` GitHub release](https://github.com/openclaw/crabbox/releases),
selects the current Windows architecture, and verifies the ZIP against:

- the GitHub release asset SHA-256 digest;
- `checksums.txt`; and
- the matching `provenance.json` payload.

It installs under `%LOCALAPPDATA%\OpenClaw\Crabbox`, writes an installation
manifest, and does not change PATH or execution policy.

The official v0.40.0 Windows executable is not Authenticode-signed. Its official
release integrity is supplied by immutable GitHub asset metadata, checksums, and
provenance. The manifest records the actual Authenticode status. If local policy
requires an Authenticode-signed Windows executable, stop here until Crabbox
publishes one. Do not describe digest verification as a Windows code signature.

### Authenticate interactively

```powershell
az login
& $crabbox azure login --location <approved-location>
& $crabbox doctor --provider azure --target windows
```

Set `$crabbox` to the `executablePath` from the installation manifest. Azure
login is intentionally interactive. The scripts do not automate login, inspect
Crabbox configuration, or print credentials.

### Proof taxonomy

| Mode | Contract | Valid claim |
|---|---|---|
| `CombinedInstalledSmoke` | One x64 native Windows desktop lease from an explicit prebaked Azure image, with WSL2 and Ubuntu verified on that same host | Full installed DEV or previous-to-current release upgrade smoke selected by `ValidationLane` |
| `NativeDesktopComponent` | Native Windows plus managed desktop/VNC | Native desktop component proof only |
| `Wsl2Component` | POSIX execution inside Crabbox-managed WSL2 | WSL2 component proof only |

Separate native and WSL2 component leases do not prove one end-to-end host and
must never be combined into a full-smoke claim.

### Combined installed smoke

The combined smoke requires an operator-prepared x64 Azure Windows image. The
image must already contain operational WSL2, an Ubuntu WSL2 distribution, and
the fixed OpenClaw development prerequisites. Acquire it in native
`normal` mode with desktop capability so Windows and WSL2 are proven on the
same host:

```powershell
.\scripts\clean-windows\Invoke-CrabboxWindowsSmoke.ps1 `
  -CrabboxPath $crabbox `
  -Provider azure `
  -Mode CombinedInstalledSmoke `
  -AzureImage "<managed-image-urn-or-resource-id>" `
  -RequireFullInstalledSmoke
```

The runner requires an explicit `-AzureImage`; it does not success-shape the
default Azure Marketplace image as combined-capable. It temporarily supplies
that non-secret image selector through `CRABBOX_AZURE_IMAGE`, then restores the
caller's environment.

Acquisition is explicitly x64, native Windows, desktop-enabled, and backed by a
managed OS disk:

```text
--provider azure --target windows --windows-mode normal --arch amd64
--azure-os-disk managed --desktop
```

Before the installed smoke starts, the remote script fails closed unless the
same host reports all of the following:

- x64 native Windows and the Crabbox desktop capability;
- operational `wsl.exe`;
- a prepared Ubuntu distribution; and
- an Ubuntu kernel that identifies WSL2.

Only after that gate does `crabbox run` execute
the selected `Installed` or `Upgrade` validation lane. The remote artifact
folder is compressed, downloaded with `crabbox cp`, expanded locally, and
checked against the lane-specific `phase-status.json` contract.

The upgrade lane is valid only with `CombinedInstalledSmoke`; component modes
fail closed instead of downgrading it to package-only or split-host proof:

```powershell
.\scripts\clean-windows\Invoke-CrabboxWindowsSmoke.ps1 `
  -CrabboxPath $crabbox `
  -Provider azure `
  -Mode CombinedInstalledSmoke `
  -ValidationLane Upgrade `
  -PreviousRelease v0.6.12 `
  -PreviousInstallerSha256 "<official-x64-sha256>" `
  -AzureImage "<managed-image-urn-or-resource-id>" `
  -RequireFullInstalledSmoke
```

The controller safely supplies the exact release and SHA-256 to
`validate-inno-upgrade-smoke.ps1`, runs it with PowerShell 7, and adds
`-ConfirmCleanMachineReleaseIdentity` only after the combined image contract
passes. It retrieves the complete upgrade artifact root and requires:

- `exitCode` 0 and `cleanupCompleted` true;
- `preflight`, `acquire-previous`, `prepare-current`, `install-previous`,
  `seed-state`, `upgrade-current`, `state-preservation`, `installed-payload`,
  `roundtrip`, and `cleanup` all exactly `passed`;
- `installed-runtime-proof\phase-status.json` with exit code 0; and
- the upgrade done/log and previous/current Inno installer logs.

The automated clean lane intentionally does not expose
`-PreviousInstallerPath`. It uses the official exact tag plus expected SHA-256
contract unless a separate reviewed offline workflow is designed later.

Prepare stable machine capability in a trusted source lease, then capture it as
an Azure managed image or native checkpoint. Azure native Windows checkpoints
require `windows.mode=normal`, a managed OS disk, and
`--no-reboot=false`; use `--strategy image` when the result will be selected by
`-AzureImage`. Keep source code, credentials, and scenario artifacts out of the
image. See Crabbox's image bake and checkpoint runbooks linked below.

The primary acceptance consumer for this clean baseline is the noninteractive
previous-to-current Inno upgrade smoke once its sibling branch is integrated.
Run that command from the freshly synchronized checkout after restoring the
Hyper-V checkpoint or acquiring the Azure image. The captured Windows user
profile must predate any OpenClaw install or protocol registration, including
`HKCU:\Software\Classes\openclaw`. If that state exists, the image is not clean:
fail the proof and rebuild the owned checkpoint/image. Never delete or pre-clean
arbitrary user registry state to make the upgrade smoke pass.

Azure Windows ARM64 WSL2 is unsupported because those sizes lack nested
virtualization. The combined runner therefore always passes `--arch amd64`.

### Boundary with GitHub Actions runners

These controllers run clean-machine proof directly. They do not register a
GitHub Actions runner, and a long-lived static Crabbox Windows host is not
clean-machine proof. A remote host is clean proof only when it is acquired from
the documented pre-registration image or checkpoint, used for one bounded
validation activity, and destroyed afterward.

If this infrastructure is later connected to GitHub Actions, preserve this
fail-closed lifecycle:

1. Resolve trusted job metadata.
2. Hydrate a disposable VM from the pre-registration Hyper-V checkpoint or
   Azure image.
3. Establish SSH and mark the host ready only after capability verification.
4. Obtain a short-lived JIT runner token and register with
   `config.cmd --ephemeral --disableupdate`.
5. Run one bounded activity.
6. Always finalize by destroying the VM and its credentials.

Never bake credentials, runner registration or identity, repository contents,
or private caches into a checkpoint or image. The current Actions hydration
workflow is Linux-only, and the experimental Windows Testbox workflow contains
POSIX assumptions and unresolved metadata/authentication behavior. Do not port
it literally, swallow hydration or phone-home failures, or claim it provides a
Windows lifecycle until the metadata and authentication contract is settled
and every failure reaches finalization.

A future CI integration should pass a clean Windows runner label into the
existing reusable workflows and retain a stable aggregate check, rather than
hardcoding the label throughout the workflow graph. Collect Windows and image
key timing data before attempting dynamic packing. Retry only classified
provider or infrastructure transients; product and test failures remain final.

### Native Windows desktop component

```powershell
.\scripts\clean-windows\Invoke-CrabboxWindowsSmoke.ps1 `
  -CrabboxPath $crabbox `
  -Provider azure `
  -Mode NativeDesktopComponent
```

This acquires `--windows-mode normal --desktop` and verifies native Windows and
the desktop capability. It does not run the installed smoke and is not WSL2 or
full installed-app proof. The desktop capability alone is also not visible UI
evidence; collect a screenshot or VNC proof separately when making a UI claim.

### WSL2 component

```powershell
.\scripts\clean-windows\Invoke-CrabboxWindowsSmoke.ps1 `
  -CrabboxPath $crabbox `
  -Provider azure `
  -Mode Wsl2Component
```

This uses explicit `--provider azure --target windows --windows-mode wsl2`
flags and runs a narrow WSL2-on-Windows capability probe. It is not native
Windows, installer, WinUI, registry, or desktop proof.

### Native desktop plus WSL2 on one host

Crabbox's managed `wsl2` mode rejects `--desktop`, and a lease cannot gain a
missing capability after acquisition. The supported combined strategy is
therefore an x64 image prebaked with WSL2 and acquired in `normal --desktop`
mode. `CombinedInstalledSmoke` detects that same-host capability before the
smoke and fails closed when the image contract is absent.

Official Crabbox references:

- [Windows VNC and WSL2](https://github.com/openclaw/crabbox/blob/main/docs/features/vnc-windows.md)
- [Azure provider](https://github.com/openclaw/crabbox/blob/main/docs/providers/azure.md)
- [Image bake runbook](https://github.com/openclaw/crabbox/blob/main/docs/features/image-bake-runbook.md)
- [Checkpoints](https://github.com/openclaw/crabbox/blob/main/docs/features/checkpoints.md)
- [`warmup`](https://github.com/openclaw/crabbox/blob/main/docs/commands/warmup.md)
- [`run`](https://github.com/openclaw/crabbox/blob/main/docs/commands/run.md)
- [`cp`](https://github.com/openclaw/crabbox/blob/main/docs/commands/cp.md)
- [`results`](https://github.com/openclaw/crabbox/blob/main/docs/commands/results.md)
- [`stop`](https://github.com/openclaw/crabbox/blob/main/docs/commands/stop.md)

## Lease records and cleanup

The Crabbox runner extracts exactly one canonical `cbx_<12 hex>` lease ID from
warmup output. Its manifest records the actual provider, target, proof mode,
proof class, x64/managed-disk acquisition contract, selected image for combined
proof, raw lease ID, command output paths, result ID when available,
artifact-copy result, and stop result.

Only the newly captured lease is stopped. Cleanup runs from `finally` after
success or failure, then runs an explicit provider/target/mode `crabbox list
--json` and requires the raw lease ID to be absent. A successful proof with an
unverified stop is still a failed runner invocation. When a run fails, inspect
`crabbox-smoke-manifest.json` and confirm the recorded stop result before
retrying.
