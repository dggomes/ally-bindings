# Ally Bindings

A small, local-first Windows application for choosing named controller mappings while Xbox Remote Play is running on a ROG Xbox Ally X.

## What works now

- Native .NET 8/WPF app with tray mode and no network service.
- Local, versioned JSON profiles with atomic writes, backup and corrupt-file recovery.
- Profile editor for one-to-one standard XInput button mappings.
- Configurable controller chord; safe default is **View + Menu**.
- Controller-driven profile carousel in a small, always-on-top overlay.
- A final **Open application** carousel item that activates the main window after the selection timeout.
- Guaranteed `Default`, keyboard panic shortcut (`Ctrl+Alt+F12`), controller disconnect cancellation and diagnostics export.
- Optional sign-in startup so the controller shortcut works while the window is closed.
- Cross-platform core tests and Windows CI packaging.

## Important backend status

The application currently uses a **preview backend**. It detects the XInput controller, stores profiles and exercises the complete selection UX. The mapping transform is implemented and covered by the core test suite, but the running app does **not** yet feed transformed states to a virtual controller or hide the physical one.

That distinction is visible in the UI and overlay; the app never claims a mapping reached the controller when it did not. Real remapping is gated by the [Ally X hardware validation](docs/HARDWARE-SPIKE.md), because an unvalidated physical-hide/virtual-output chain can duplicate input or strand the controller.

## Controller shortcut

1. Keep Ally Bindings running in the notification area.
2. Hold **View + Menu** for 250 ms.
3. The overlay shows the next enabled profile.
4. Release and repeat the chord to rotate again.
5. Stop for 900 ms to commit the displayed item.
6. Selecting **Open application** opens/activates the main window.

The chord and timings are configurable. `A + B` is supported, but the app warns about face-button-only chords because preview mode observes rather than swallows those inputs; they can reach the streamed game.

The app cannot hear a controller chord while it is not running. Enable **Run in the tray when I sign in** if the shortcut must always be available.

## Install a CI build

1. Open the latest successful GitHub Actions `build` run.
2. Download `AllyBindings-win-x64`.
3. Extract the zip to a normal user folder.
4. Run `AllyBindings.exe`.

No driver or privileged installer is invoked. Configuration is stored at:

```text
%LOCALAPPDATA%\AllyBindings\config.json
```

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

- Local-only; no account, telemetry, cloud sync or network listener.
- No injection into Xbox, Armoury Crate or games.
- No macros, turbo, scripts or anti-cheat bypasses.
- `Default` is always present and cannot be edited/deleted.
- Configuration failure recovers to Default and preserves a corrupt-file copy.
- Controller disconnect cancels an uncommitted carousel selection.
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

Ally Bindings is a mapping selector, not an Armoury Crate replacement. It does not change TDP, fan, RGB, display settings or Armoury game profiles, and it does not guess the streamed Xbox title from video/OCR.
