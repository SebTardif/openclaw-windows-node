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
`Copy-Item -ToSession`, runs the installed Inno smoke, and retrieves artifacts
with `Copy-Item -FromSession`. The VM is stopped and restored to
`openclaw-prerequisites` in a `finally` path on success or failure.

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

### Native Windows desktop lease

```powershell
.\scripts\clean-windows\Invoke-CrabboxWindowsSmoke.ps1 `
  -CrabboxPath $crabbox `
  -Provider azure `
  -Mode NativeDesktop `
  -RequestUiProof
```

The runner uses explicit `--provider azure --target windows --windows-mode
normal` flags and warms the lease with `--desktop`. `crabbox run` syncs the
current tracked and relevant untracked checkout, then runs
`validate-installed-inno-smoke.ps1`. The remote artifact folder is compressed,
downloaded with `crabbox cp`, expanded locally, and required to contain
`phase-status.json`.

The `--desktop` capability makes a native desktop available, but the automated
installed smoke is not itself visible UI evidence. Use the lease with
`crabbox screenshot`, `crabbox vnc`, or `crabbox desktop launch` before the
runner exits only when adding an explicit interactive proof workflow.

### WSL2 capability lease

```powershell
.\scripts\clean-windows\Invoke-CrabboxWindowsSmoke.ps1 `
  -CrabboxPath $crabbox `
  -Provider azure `
  -Mode Wsl2
```

This uses explicit `--provider azure --target windows --windows-mode wsl2`
flags and runs a narrow WSL2-on-Windows capability probe. It is not native
Windows, installer, WinUI, registry, or desktop proof.

### Native desktop plus WSL2 on one host

Current managed Crabbox leases do not provide this combined contract:

- `--windows-mode wsl2` rejects `--desktop`;
- a lease cannot gain a missing capability after acquisition; and
- separate native and WSL2 leases do not prove one end-to-end host.

`-RequireCombinedNativeDesktopAndWsl2OnOneLease` therefore fails closed. A
combined proof requires an operator-managed or future managed Windows image
that is prebaked with WSL2, supports a visible native desktop, and advertises
both capabilities on the same lease. Until that image contract exists, report
native and WSL2 runs separately and do not merge their claims.

Official Crabbox references:

- [Windows VNC and WSL2](https://github.com/openclaw/crabbox/blob/main/docs/features/vnc-windows.md)
- [Azure provider](https://github.com/openclaw/crabbox/blob/main/docs/providers/azure.md)
- [`warmup`](https://github.com/openclaw/crabbox/blob/main/docs/commands/warmup.md)
- [`run`](https://github.com/openclaw/crabbox/blob/main/docs/commands/run.md)
- [`cp`](https://github.com/openclaw/crabbox/blob/main/docs/commands/cp.md)
- [`results`](https://github.com/openclaw/crabbox/blob/main/docs/commands/results.md)
- [`stop`](https://github.com/openclaw/crabbox/blob/main/docs/commands/stop.md)

## Lease records and cleanup

The Crabbox runner extracts exactly one canonical `cbx_<12 hex>` lease ID from
warmup output. Its manifest records the actual provider, target, mode, raw lease
ID, command output paths, result ID when available, artifact-copy result, and
stop result.

Only the newly captured lease is stopped. Cleanup runs from `finally` after
success or failure. A successful proof with a failed stop is still a failed
runner invocation. When a run fails, inspect `crabbox-smoke-manifest.json` and
confirm the recorded stop result before retrying.
