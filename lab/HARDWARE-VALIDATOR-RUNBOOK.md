# Private M1/M2 physical validator runbook

This utility exists for one physical experiment on Daniel's ROG Xbox Ally X. It is **not a public Ally Bindings release**, does not unlock the app's hardware backend, and must not be distributed as a normal installer.

## What it can do

- `inspect`: verify the ASUS system identity and count compatible, openable HID feature-report `0x5A` interfaces. It does not read or change controller settings.
- `write-m1-a-m2-b`: after an exact interactive confirmation, send one fixed 50-byte mapping packet: **M1 → A, M2 → B**.

It cannot select arbitrary mappings, retry continuously, restore defaults, or infer the previous Armoury configuration.
After confirmation it creates an atomic, durable `one-shot-claimed.json` marker before touching HID. The same machine will refuse a second write from this validator until the lab evidence is reviewed and the marker is deliberately cleared by development.

## Before the write

1. Plug the Ally into power.
2. Open Armoury Crate and photograph or export the current M1/M2 assignments.
3. Keep a keyboard/touch recovery path available.
4. Close Armoury Crate completely.
5. Close all games, launchers, overlays, and anti-cheat software.
6. Extract the private CI artifact and verify `SHA256SUMS.txt`.
7. Open Windows Terminal in the extracted directory.

## Inspect first

```powershell
./AllyBindings.HardwareValidator.exe inspect
```

Continue only if it reports:

- the expected RC73XA ROG Xbox Ally X model;
- a supported ROG Ally identity;
- exactly one compatible openable report-`0x5A` interface;
- `INSPECT PASSED`.

Any other result is a hard stop.

## Perform the one-shot write

```powershell
./AllyBindings.HardwareValidator.exe write-m1-a-m2-b
```

The utility displays the exact packet and its pinned SHA-256 (`fb0f2ac8167350edf147fb839be2306ccb15494c824a44badeff7aad083cf38b`), writes a pre-operation audit record, and asks for this exact phrase:

```text
I SAVED SETTINGS; WRITE M1=A M2=B
```

Do not paste or type it unless the preconditions above are true. API acceptance means only that Windows accepted the HID call; it is not physical proof.

## Verify and recover

1. Open `joy.cpl` or another safe local controller tester—never an anti-cheat game.
2. Press M1 and verify that only **A** registers.
3. Press M2 and verify that only **B** registers.
4. Record pass/fail and any duplicate, missing, or stuck input.
5. Open Armoury Crate and restore the photographed assignments, or deliberately use Armoury's own Reset to Default.
6. Verify both paddles again after Armoury applies the restoration.
7. Reboot if input is abnormal, then reapply the saved Armoury assignments.

The audit is stored under:

```text
%LOCALAPPDATA%\AllyBindings\hardware-validation\
```

Retain that JSON with the physical observations. Its `armouryRecoveryConfirmed` field intentionally remains `false`; the utility cannot prove a manual Armoury action. Report the restoration result alongside the file.

## Stop rule

Do not run a second mapping experiment until the first packet, audit, physical M1/M2 result, and Armoury restoration have been reviewed. If the write fails or times out, treat the hardware outcome as unknown and restore through Armoury before doing anything else.
