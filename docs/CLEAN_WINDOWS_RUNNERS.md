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
- A Windows 11 ISO, an unused VHDX path, and a guest administrator credential.
- At least 8 GB guest memory, 80 GB free disk, and four logical processors by
  default.

The controller creates a Generation 2 VM with secure boot, vTPM, automatic
checkpoints disabled, and nested virtualization enabled through
`ExposeVirtualizationExtensions`. Nested virtualization is required because the
installed smoke provisions and exercises WSL2 inside the guest.

The controller never deletes a VM or VHD. Existing VMs and checkpoints are
refused unless both conditions are met:

- the VM/checkpoint marker matches `-OwnerId`; and
- the command includes `-ConfirmOwnedAction`.

The VM marker is stored in Hyper-V VM notes and beside the VHD under
`.openclaw-clean-windows\<vm-name>`. Checkpoint marker files bind the checkpoint
name, Hyper-V snapshot ID, creation time, VM ID, VHD path, and owner ID.

### Create the VM

```powershell
$vmName = "OpenClaw-Clean-Windows"
$ownerId = "openclaw-clean-runner-bkudiess"
$iso = "D:\isos\Windows11.iso"
$vhd = "D:\Hyper-V\OpenClaw-Clean-Windows\os.vhdx"
$guestAccount = Get-Credential

.\scripts\clean-windows\Invoke-CleanWindowsHyperV.ps1 `
  -Command Create `
  -VMName $vmName `
  -OwnerId $ownerId `
  -IsoPath $iso `
  -VhdPath $vhd
```

Complete Windows setup interactively in the new VM. Install Windows updates and
confirm the supplied credential can open a PowerShell Direct session. Do not
install OpenClaw prerequisites manually.

### Prepare checkpoints

```powershell
.\scripts\clean-windows\Invoke-CleanWindowsHyperV.ps1 `
  -Command Prepare `
  -VMName $vmName `
  -OwnerId $ownerId `
  -VhdPath $vhd `
  -Credential $guestAccount `
  -ConfirmOwnedAction
```

`Prepare` creates `clean-windows`, restores it, enables WSL2 prerequisites, runs
`scripts\setup-dev.ps1` inside the guest, and creates
`openclaw-prerequisites`. Guest commands and PowerShell Direct readiness have
bounded timeouts.

### Verify and smoke

```powershell
.\scripts\clean-windows\Invoke-CleanWindowsHyperV.ps1 `
  -Command Verify `
  -VMName $vmName `
  -OwnerId $ownerId `
  -VhdPath $vhd `
  -Credential $guestAccount `
  -ConfirmOwnedAction

.\scripts\clean-windows\Invoke-CleanWindowsHyperV.ps1 `
  -Command Smoke `
  -VMName $vmName `
  -OwnerId $ownerId `
  -VhdPath $vhd `
  -Credential $guestAccount `
  -HostArtifactRoot "TestResults\CleanWindows\HyperV" `
  -ConfirmOwnedAction
```

The controller transports the checkout with PowerShell Direct
`Copy-Item -ToSession`, runs the typed `Installed` validation lane by default,
and retrieves artifacts with `Copy-Item -FromSession`. The VM is stopped and restored to
`openclaw-prerequisites` in a `finally` path on success or failure.

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
  -Credential $guestAccount `
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
