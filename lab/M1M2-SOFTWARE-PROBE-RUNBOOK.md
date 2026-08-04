# M1/M2 Software Probe Runbook

This package tests whether the ROG Xbox Ally X rear paddles can drive Xbox Remote Play through ordinary Windows keyboard input and a temporary virtual Xbox controller.

## Safety boundary

The probe:

- never opens an ASUS HID interface;
- contains no `HidD_SetFeature` import or hardware-write command;
- never installs or updates a driver;
- never hides a physical controller;
- records only F11/F12 timing, capability status and fixed-choice checkpoint outcomes;
- releases virtual A/B and disconnects the temporary controller on every normal exit.

Armoury Crate remains solely responsible for M1/M2 assignment changes. The probe never generates assignment input.

## 1. Verify the package

Open Windows PowerShell in the extracted folder:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\Verify-Package.ps1
```

Do not continue unless it prints `PACKAGE VERIFIED`.

`SHA256SUMS.txt` and the adjacent ZIP checksum detect accidental corruption. They ship with the artifact and do **not** prove publisher identity; compare the ZIP SHA-256 with a digest obtained through a separately trusted channel before treating it as authentic.

## 2. Inspect capabilities

```powershell
.\AllyBindings.M1M2Probe.exe inspect
```

Record whether ViGEmBus and HidHide are already installed. The probe does not install either component.

## 3. Start an evidence session

```powershell
$session = (& .\AllyBindings.M1M2Probe.exe start)[0]
$session
```

The session is stored under `%LOCALAPPDATA%\AllyBindings\software-probe\<session-id>` unless `start --root` is supplied.

The packaged `Run-Software-Probe.ps1` is the preferred guided path. It writes a stable resume launcher to `%LOCALAPPDATA%\AllyBindings\Resume-M1M2-Software-Probe.ps1` so the same session survives the cold-boot stage.

## 4. Preserve the current Armoury state

Before changing anything, save screenshots of:

- M1 primary and secondary;
- M2 primary and secondary;
- Armoury Crate version;
- ASUS System Control Interface version;
- controller/MCU firmware version.

Then record:

```powershell
.\AllyBindings.M1M2Probe.exe checkpoint --session $session --name armoury-baseline-saved --result pass
```

Screenshots are deliberately not copied into the evidence ZIP.

## 5. Ask Armoury to assign F12/F11

In Armoury Crate, clear both secondary assignments. Use Armoury's own virtual keyboard to click F12 for M1 primary and F11 for M2 primary. The probe deliberately has no `SendInput` or assignment-emitter command.

Confirm Armoury shows M1=F12 and M2=F11, then record:

```powershell
.\AllyBindings.M1M2Probe.exe checkpoint --session $session --name f11-f12-assigned --result pass
```

If Armoury rejects either key, record `fail`, restore the screenshots, and stop. Do not use the retired hardware validator.

## 6. Prove keyboard events

```powershell
.\AllyBindings.M1M2Probe.exe listen --session $session --seconds 30
```

Press and release M1 twice, M2 twice, then hold each once. Confirm clean F12/F11 down/up events and record the result:

Then open Notepad and run a suppression pass:

```powershell
.\AllyBindings.M1M2Probe.exe listen --session $session --seconds 15 --suppress
```

During those 15 seconds press M1/M2 and type `probe` with unrelated keys. F11/F12 must be swallowed while `probe` still appears. This proves the filter does not suppress broad keyboard input.

```powershell
.\AllyBindings.M1M2Probe.exe checkpoint --session $session --name keyboard-capture --result pass
```

The hook ignores and never retains every other key.

## 7. Test Xbox Remote Play

`bridge` requires an already-installed, healthy ViGEmBus. It does not install it.

### Virtual controller only

1. Open Xbox Remote Play and navigate to a screen where A and B have obvious effects.
2. Confirm touch or an external keyboard can reopen ASUS Command Center; this is the recovery input.
3. Start the bridge, then disable the Embedded Controller through ASUS Command Center during the timed window:

```powershell
.\AllyBindings.M1M2Probe.exe bridge --session $session --seconds 120
```

During the two-minute window, M1/F12 becomes virtual A and M2/F11 becomes virtual B. Confirm Remote Play responds and record:

```powershell
.\AllyBindings.M1M2Probe.exe checkpoint --session $session --name remote-play-virtual-only --result pass
```

Re-enable the Embedded Controller immediately after this pass—even if the bridge fails or is cancelled. The guided runner enforces this acknowledgement in `finally`.

### Coexistence

With the Embedded Controller enabled, run `bridge` again. Check for duplicate input, wrong controller slot, or ignored virtual input, then record:

```powershell
.\AllyBindings.M1M2Probe.exe checkpoint --session $session --name remote-play-coexistence --result pass
```

If coexistence fails, record `fail`. Do not install or configure HidHide as part of this package. Record whether a later, separately approved HidHide test is required:

```powershell
.\AllyBindings.M1M2Probe.exe checkpoint --session $session --name hidhide-required --result unknown
```

## 8. Cold-boot persistence

1. Disable startup for Armoury, HandheldCompanion, and Ally Bindings.
2. Disable Windows Fast Startup for the test.
3. Note the session path, or use the stable resume launcher created by `Run-Software-Probe.ps1`.
4. Run `shutdown /s /t 0`; do not restart.
5. Boot without a remapper running.
6. Run `%LOCALAPPDATA%\AllyBindings\Resume-M1M2-Software-Probe.ps1`, choose stage 7, and test M1/M2.

Record `pass` if F12/F11 survived or `fail` if they disappeared:

```powershell
.\AllyBindings.M1M2Probe.exe checkpoint --session $session --name cold-boot-persistence --result pass
```

Re-enable any startup settings changed for the test.

## 9. Restore Armoury

Restore all four original primary/secondary assignments from the screenshots and verify both paddles. Then record:

```powershell
.\AllyBindings.M1M2Probe.exe checkpoint --session $session --name armoury-restored --result pass
```

Do not finalize until this checkpoint is accurate. Finalization is rejected unless all eight checkpoints are recorded, no result is `unknown`, and both baseline preservation and Armoury restoration are `pass`. Use `skipped` for tests you deliberately did not run.

## 10. Finalize evidence

```powershell
.\AllyBindings.M1M2Probe.exe finalize --session $session
```

Record the displayed ZIP path and SHA-256 separately. The ZIP contains only:

- `session.json`;
- `manifest.json` with per-file hashes;
- `README.txt` explaining the privacy boundary.

## Recovery

- `Ctrl+C` cooperatively stops the message loop; ordinary process teardown also removes the keyboard hook.
- The bridge drains queued events, then independently attempts virtual A release, B release and disconnect before returning.
- The probe never disables the Embedded Controller; if you disabled it through ASUS Command Center, re-enable it there.
- Restore M1/M2 only through Armoury using the saved screenshots.
- If a button appears stuck, close the probe and disconnect/reconnect the virtual-controller session before continuing.
