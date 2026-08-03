# Controlled RC73XA M1/M2 physical validator runbook

This utility exists for one physical experiment on Daniel's ROG Xbox Ally X (`RC73XA`, `VID_0B05/PID_1B4C`). It is **not an Ally Bindings release**, is never bundled with the normal app, and does not unlock either public hardware-write gate.

The executable is standalone: it does not reference Ally Bindings Core, HidSharp, or the generic HID adapter. It contains only narrow native SetupAPI/HID declarations and no controller-state readback, reset builder, arbitrary mapping builder, parameterised report writer, or retry loop.

## Authoritative build only

Use only an artifact produced by the manual **controlled hardware validator** workflow from an approved commit on `main`:

- workflow trigger: `workflow_dispatch`;
- required environment: `hardware-lab-approval`;
- input must exactly equal the checked-out `main` commit SHA;
- artifact name includes that full commit SHA;
- retention is one day;
- the workflow summary records the outer ZIP SHA-256;
- GitHub signs a build-provenance attestation for the exact ZIP.

Never use a validator built by a pull request, an untrusted branch, or a third party. A checksum bundled inside a substituted ZIP is not provenance. On the workflow run page, verify all five immutable properties before download: event `workflow_dispatch`, branch `main`, exact head SHA, successful `hardware-lab-approval` deployment, and successful run conclusion. The environment approval is an explicit operator gate, not an independent second-person review.

Before extraction, compare the downloaded ZIP against both `OUTER-SHA256.txt` and the independently viewed workflow summary:

```powershell
(Get-FileHash .\AllyBindings-HardwareValidator-win-x64.zip -Algorithm SHA256).Hash.ToLowerInvariant()
Get-Content .\OUTER-SHA256.txt
```

All three values—the computed hash, `OUTER-SHA256.txt`, and workflow summary—must match. After extraction, `SHA256SUMS.txt` must list exactly the other seven packaged files once each; the validator’s safety job verifies this automatically.

Verify GitHub's signed provenance and then run the packaged fail-closed allowlist/checksum verifier. If `gh` is unavailable, install the signed GitHub CLI first with `winget install --id GitHub.cli --exact`:

```powershell
gh attestation verify .\AllyBindings-HardwareValidator-win-x64.zip --repo dggomes/ally-bindings
# The verification output must identify .github/workflows/hardware-validator.yml on refs/heads/main.
Expand-Archive .\AllyBindings-HardwareValidator-win-x64.zip -DestinationPath .\validator
Set-Location .\validator
.\Verify-Package.ps1
```

`Verify-Package.ps1` must print `CONTROLLED VALIDATOR PACKAGE VALID` and the executable SHA-256. `SHA256SUMS.txt` must contain exactly seven unique entries covering every file except itself.

## What it can do

- `inspect`: verify exact ASUS RC73XA DMI and exactly one compatible `VID_0B05/PID_1B4C` HID feature-report `0x5A` interface. It enumerates through SetupAPI and validates VID/PID, caps, and report ID `0x5A` from the same native handle, but never calls `GetFeature` or reads controller settings.
- `write-m1-a-m2-b`: after exact interactive confirmation, issue one fixed SET_FEATURE command: **M1 → A, M2 → B**.

The reviewed 50-byte logical command is:

```text
5AD102082C010200000000000000000001020000000000000000000101000000000000000000010100000000000000000000
```

Logical SHA-256:

```text
fb0f2ac8167350edf147fb839be2306ccb15494c824a44badeff7aad083cf38b
```

The HID descriptor may require a 50–64-byte zero-padded wire packet. The validator constructs, displays, hashes, and audits that **exact final wire buffer** before confirmation, then requires the same descriptor length and bytes at write time.

## Before the write

1. Plug the Ally into power.
2. Open Armoury Crate and photograph/export the current M1/M2 assignments.
3. Keep keyboard/touch recovery available.
4. Close Armoury Crate completely.
5. Close games, launchers, overlays, and anti-cheat software.
6. Verify the workflow event/ref/SHA/environment/conclusion, signed attestation, outer ZIP SHA-256, packaged verifier, and logical packet hash.
7. Open an elevated Windows Terminal in the extracted directory.

## Inspect first

```powershell
./AllyBindings.HardwareValidator.exe inspect
```

Continue only if it reports:

- the expected RC73XA product name;
- exact target approval `True`;
- exactly one `VID_0B05/PID_1B4C` interface;
- feature report length between 50 and 64 bytes;
- the logical hash above;
- `Inspection only. No HID feature report was read or written.`

Any mismatch is a hard stop.

## Perform the one-shot write

```powershell
./AllyBindings.HardwareValidator.exe write-m1-a-m2-b
```

Before prompting, compare the displayed logical bytes/hash and record the displayed exact wire packet/hash. Type this exact phrase only after every precondition is true:

```text
I SAVED SETTINGS; WRITE M1=A M2=B
```

The utility creates write-through append-only audit records and an atomic machine-wide `one-shot-claimed.json` marker before entering the HID operation. The same machine refuses another write across Windows user profiles unless development deliberately clears the marker after reviewing the evidence.

API acceptance means only that Windows accepted SET_FEATURE; it is not physical proof.

## Verify and recover

1. Open `joy.cpl` or another safe local controller tester—never an anti-cheat game.
2. Press M1 and verify only **A** registers.
3. Press M2 and verify only **B** registers.
4. Record duplicate, missing, or stuck inputs.
5. Open Armoury Crate and restore the photographed assignments, or deliberately use Armoury’s Reset to Default.
6. Verify both paddles again after restoration.
7. Reboot and reapply the saved Armoury assignments if input remains abnormal.

Audits are under:

```text
%PROGRAMDATA%\AllyBindings\HardwareValidator\
```

`armouryRecoveryConfirmed` intentionally remains `false`; software cannot prove a manual Armoury action.

## Required result record

Do not call the experiment accepted until this record and the audit JSON exist:

```text
Operator/date:
Approved main commit SHA:
Workflow run URL:
Verified workflow event/ref/head SHA/environment/conclusion:
Attestation verification output saved as:
Downloaded ZIP SHA-256:
Executable SHA-256:
Logical packet SHA-256:
Exact wire packet length/hex/SHA-256:
Displayed DMI model:
Displayed VID/PID/interface count:
HID API outcome:
M1 observed input:
M2 observed input:
Duplicate/stuck/missing inputs:
Armoury restoration performed:
Post-restoration M1/M2 result:
Original Armoury photo/export filename and SHA-256:
Post-write controller-tester screenshot/video filename and SHA-256:
Post-restoration Armoury screenshot filename and SHA-256:
Post-restoration controller-tester screenshot/video filename and SHA-256:
Audit JSON filenames:
Notes:
```

## Stop rule

Do not run a second mapping experiment until the first packet, audits, physical result, and Armoury restoration have been reviewed. If the operation fails, times out, is cancelled, or crashes after confirmation, treat the outcome as unknown and restore through Armoury before doing anything else.
