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
9. immediately detaches both ISO drives and removes the answer XML, staging
   directory, and answer ISO;
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

### Resume or clean an owned partial unattended install

A failed unattended operation never deletes its VM or VHD. Before PowerShell
Direct readiness, it retains the owned state marker, DPAPI setup credential,
answer XML, staging directory, and answer ISO for diagnosis. Inspect the VM,
VHD, DVD paths, and
`.openclaw-clean-windows\OpenClaw-Clean-Windows\unattend.owner.json`.

#### Resume the current owned secure-boot partial state

The current partial state already contains the owned Generation 2 VM, VHD,
original Windows ISO, answer ISO, setup DPAPI credential, VM ownership markers,
and unattended ownership marker. Creation stopped before the first guest start
because the secure-boot template input was not the canonical cmdlet identifier.
This state is resumable. Do not use `-CleanupUnattend` for this state.

From an elevated PowerShell session, run exactly:

```powershell
.\scripts\clean-windows\Invoke-CleanWindowsHyperV.ps1 -Command Create -ResumeUnattended -VMName 'OpenClaw-Clean-Windows' -OwnerId 'openclaw-clean-runner-bkudiess' -VhdPath 'D:\Hyper-V\OpenClaw-Clean-Windows\os.vhdx' -ConfirmOwnedAction
```

The command validates the VM note marker, VM marker file, and unattended
ownership state. It first verifies host configuration. Only after verification
fails and the exact owned VM is Off may it repair secure boot with
`MicrosoftWindows`, restore the verified Windows DVD boot device, and create a
missing key protector or enable a missing vTPM. It preserves an existing key
protector and enabled vTPM, then re-verifies before starting the VM. Immediately
after that repaired pre-first-start VM is running, it uses the same bounded
Hyper-V CIM key pulses to clear the optical boot prompt and continue the
unattended flow. It does not delete or replace the VM, VHD, attached media, or
credentials.

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
secure-boot partial state described above:

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

`Prepare` creates `clean-windows`, restores it, enables WSL2 prerequisites, runs
`scripts\setup-dev.ps1` inside the guest, and creates
`openclaw-prerequisites`. Guest commands and PowerShell Direct readiness have
bounded timeouts. `-Credential` remains available for operator-managed manual
mode. Unattended mode should use the returned DPAPI `-CredentialPath`.

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
