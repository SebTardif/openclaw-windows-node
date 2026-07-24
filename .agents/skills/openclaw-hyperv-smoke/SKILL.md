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
does the bounded PowerShell Direct wait begin. Both DVD media are detached and answer
XML/staging/ISO files are removed. The controller verifies the actual
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

The current partial state already has the owned Generation 2 VM, VHD, original
Windows ISO, answer ISO, setup DPAPI credential, VM markers, and unattended
marker. Creation stopped before the first guest start because secure-boot
configuration used a non-canonical template input. This state is resumable.
Do not use `-CleanupUnattend` for this state.

```powershell
.\scripts\clean-windows\Invoke-CleanWindowsHyperV.ps1 -Command Create -ResumeUnattended -VMName 'OpenClaw-Clean-Windows' -OwnerId 'openclaw-clean-runner-bkudiess' -VhdPath 'D:\Hyper-V\OpenClaw-Clean-Windows\os.vhdx' -ConfirmOwnedAction
```

The command validates VM note/file ownership and unattended ownership. It
first verifies host configuration. Only if verification fails and the exact
owned VM is Off does it repair the security configuration with
`MicrosoftWindows` and the already-owned Windows DVD. It preserves an existing
key protector and enabled vTPM, re-verifies, and then starts the VM. It runs
the same bounded Hyper-V CIM key pulses immediately after that repaired
pre-first-start VM is running, clears the optical boot prompt, and continues
the unattended flow. The VM, VHD, media, and credentials are preserved.

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
  -CredentialPath $credentialPath `
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
