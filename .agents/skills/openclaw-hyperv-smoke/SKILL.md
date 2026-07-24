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
- A Windows 11 x64 ISO, unused VHDX path, and guest administrator credential
  are available for `Create` and `Prepare`.
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
| Copy checkout into the guest | `Copy-Item -ToSession` |
| Retrieve proof artifacts | `Copy-Item -FromSession` |

Do not call lifecycle cmdlets directly to bypass the repository controller.
The controller binds the VM, VHD, checkpoint name, snapshot ID, creation time,
VM ID, and `OwnerId` before mutation.

## Fixed checkpoints and ownership

The controller owns exactly two checkpoint names:

- `clean-windows`: updated Windows before OpenClaw prerequisites.
- `openclaw-prerequisites`: WSL2 platform features, Git, PowerShell 7, and
  `scripts\setup-dev.ps1` prerequisites, before smoke state. The installed
  smoke provisions its gateway distribution later.

VM ownership is recorded in Hyper-V notes and beside the VHD under
`.openclaw-clean-windows\<vm-name>`. Checkpoint markers bind the exact
snapshot identity. Existing resources require matching markers and
`-ConfirmOwnedAction`.

Never delete or unregister a VM, VHD, or checkpoint. Never modify, restore,
start, or stop an unowned or mismatched resource. Never weaken
`-ConfirmOwnedAction`, clean-state guards, or checkpoint identity checks.

## Create

```powershell
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

Complete Windows setup and updates interactively. Confirm the credential can
open PowerShell Direct. Do not install OpenClaw prerequisites manually.

## Prepare

```powershell
.\scripts\clean-windows\Invoke-CleanWindowsHyperV.ps1 `
  -Command Prepare `
  -VMName $vmName `
  -OwnerId $ownerId `
  -VhdPath $vhd `
  -Credential $guestAccount `
  -ConfirmOwnedAction
```

`Prepare` creates `clean-windows`, restores it, enables nested WSL2
prerequisites, installs Git and PowerShell 7, copies the checkout through
PowerShell Direct, runs `scripts\setup-dev.ps1`, and creates
`openclaw-prerequisites`.

## Verify

```powershell
.\scripts\clean-windows\Invoke-CleanWindowsHyperV.ps1 `
  -Command Verify `
  -VMName $vmName `
  -OwnerId $ownerId `
  -VhdPath $vhd `
  -Credential $guestAccount `
  -ConfirmOwnedAction
```

`Verify` restores the owned `openclaw-prerequisites` checkpoint and checks
Gen2/x64 host configuration, nested virtualization, guest readiness, WSL2,
and OpenClaw prerequisites without claiming a distribution or installed-app
proof.

## Smoke: Installed lane

```powershell
.\scripts\clean-windows\Invoke-CleanWindowsHyperV.ps1 `
  -Command Smoke `
  -ValidationLane Installed `
  -VMName $vmName `
  -OwnerId $ownerId `
  -VhdPath $vhd `
  -Credential $guestAccount `
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
  -Credential $guestAccount `
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

The controller copies the checkout with `Copy-Item -ToSession`, executes with
PowerShell Direct `Invoke-Command -VMName` or an owned session, and retrieves
artifacts with `Copy-Item -FromSession`. Commands have bounded timeouts.

Every smoke attempt must stop the owned guest and restore
`openclaw-prerequisites` in a `finally` path on success or failure. Verify the
host artifact directory and phase gates before making a proof claim. A failed
artifact copy, missing manifest, failed restore, or running conflicting VM is
a named blocker.

See `docs/CLEAN_WINDOWS_RUNNERS.md` for the authoritative controller runbook
and `docs/WINDOWS_NODE_TESTING.md` for installed and release-upgrade phase
details.
