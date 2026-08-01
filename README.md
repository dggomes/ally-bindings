# Ally Bindings

A small, local-first Windows application for choosing named controller mappings while Xbox Remote Play is running on a ROG Xbox Ally X.

## What works now

- Native .NET 8/WPF app with tray mode and no network listener; optional update checks make one outbound request to GitHub Releases.
- Local, versioned JSON profiles with atomic writes, backup and corrupt-file recovery.
- Profile editor for one-to-one standard XInput mappings plus M1/M2 → controller-button/trigger bindings.
- Configurable controller chord; safe default is **View + Menu**.
- Controller-driven profile carousel in a small, always-on-top overlay.
- Explicit **hold the chord, then press RT** gesture to open the editor; opening the app is no longer a carousel item.
- Experimental, opt-in ASUS HID backend that applies M1/M2 mappings on positively identified Ally models (including Xbox Ally X `RC73XA`).
- Permanent `Default` profile, keyboard recovery shortcut (`Ctrl+Alt+F12`), controller disconnect cancellation and diagnostics export.
- Optional sign-in startup so the controller shortcut works while the window is closed.
- Cross-platform core tests and Windows CI packaging.
- Automatic daily update checks plus a verified one-click GitHub Releases updater; installation always requires confirmation.

## Important backend status

Standard XInput remapping still uses the **preview backend**. It detects the controller, stores profiles and exercises the complete selection UX, but does not yet feed transformed standard-button states to a virtual controller or hide the physical one.

M1/M2 are different: research confirmed they are ASUS firmware-managed controls rather than XInput buttons. The opt-in **ASUS M1/M2 hardware mappings** backend sends the same narrow controller-mapping feature report used by Armoury-style tools. The UI and overlay identify this as a partial backend: the OS accepted the rear-button command, but live firmware state cannot be read back; standard mappings remain preview-only.

Applying a rear-button profile overwrites Armoury Crate's current M1/M2 assignments. Armoury can also overwrite Ally Bindings later. **Restore Default**, `Ctrl+Alt+F12`, and normal exit after an active rear remap write the best-known native M1/M2 modifier packet—not a backup of a custom Armoury assignment. Independent implementations corroborate this packet, but it still needs physical validation on each supported Ally model/firmware. A persisted recovery marker repeats the reset attempt on the next launch after an unclean termination. Keep the backend disabled until you are ready to validate it on the device.

## Controller shortcut

1. Keep Ally Bindings running in the notification area.
2. Hold **View + Menu** for 250 ms.
3. The overlay shows the next enabled profile.
4. Release and repeat the chord to rotate again.
5. Stop for 900 ms to commit the displayed item.
6. To open the editor instead, keep **View + Menu held** after the hold threshold and press **RT**. RT must be pressed after the chord is armed; an already-held trigger does nothing.

The chord and timings are configurable. `A + B` is supported, but the app warns about face-button-only chords because preview mode observes rather than swallows those inputs; they can reach the streamed game.

The app cannot hear a controller chord while it is not running. Enable **Run in the tray when I sign in** if the shortcut must always be available.

## Install a CI build

1. Open the latest successful GitHub Actions `build` run.
2. Download `AllyBindings-win-x64`.
3. Extract the zip to a normal user folder.
4. Run `AllyBindings.exe`.

No driver or privileged installer is invoked. The optional M1/M2 backend uses the existing ASUS HID interface. Configuration is stored at:

```text
%LOCALAPPDATA%\AllyBindings\config.json
```

## Updates

- The app checks the public `dggomes/ally-bindings` GitHub Releases feed at most once every 24 hours by default; automatic checks and preview releases can be toggled independently.
- **Check for updates** runs the same check immediately.
- Installation is never silent: the app shows the release and waits for confirmation.
- The updater requires GitHub's SHA-256 asset digest, rejects unsafe ZIP paths/symlinks/duplicates, stages the package before exit, atomically replaces each file with the executable last, and retains both application-file and configuration backups until the new app explicitly confirms full initialization. Failure or timeout attempts every rollback step, restores the prior configuration schema, and relaunches the previous app; incomplete rollback is reported explicitly.
- Profiles are outside the app folder under `%LOCALAPPDATA%` and are not replaced.
- Releases are not yet Authenticode-signed. SHA-256 verification protects against corruption and mismatched downloads, but not compromise of the GitHub repository/release credentials.

## Build and test

Requires .NET 8 SDK.

```powershell
dotnet test AllyBindings.sln --configuration Release
./scripts/package.ps1
```

Core tests also run on macOS/Linux:

```bash
dotnet test tests/AllyBindings.Core.Tests/AllyBindings.Core.Tests.csproj -c Release
```

The WPF project can be cross-compiled with Windows targeting enabled, but the packaged app must be exercised on Windows.

## Safety model

- No account, telemetry, cloud sync or network listener. Update checks contact only GitHub's HTTPS API/download host and can be disabled.
- No injection into Xbox, Armoury Crate or games. The opt-in M1/M2 backend writes only ASUS's rear-button mapping zone.
- No macros, turbo, scripts or anti-cheat bypasses.
- `Default` is always present and cannot be edited/deleted.
- Configuration failure recovers to Default and preserves a corrupt-file copy.
- Controller disconnect cancels an uncommitted carousel selection.
- M1/M2 writes require both a positively identified Ally model and a matching openable ASUS feature-report interface.
- Disabling the M1/M2 backend refuses to complete if the best-known native reset cannot be written after this app changed the paddles.
- Physical hiding/virtual output will not be enabled before output health, duplicate-input and rollback tests pass on the Ally X.

## Repository map

- `src/AllyBindings.Core` — profile model/store, mapping engine, backend contract, carousel state machine, diagnostics.
- `src/AllyBindings.Windows` — WPF UI, tray, overlay, XInput polling, startup registration and panic hotkey.
- `tests/AllyBindings.Core.Tests` — deterministic core tests.
- `docs/ARCHITECTURE.md` — boundaries and data/control flow.
- `docs/PLAN.md` — delivery state and remaining release gates.
- `docs/HARDWARE-SPIKE.md` — physical Ally validation and rollback matrix.
- `examples/config.sample.json` — readable sample profiles.

## Scope

Ally Bindings is a mapping selector, not an Armoury Crate replacement. Apart from the explicit M1/M2 opt-in, it does not change TDP, fan, RGB, display settings or Armoury game profiles, and it does not guess the streamed Xbox title from video/OCR.
