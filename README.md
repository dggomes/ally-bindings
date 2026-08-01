# Ally Bindings

[![Build](https://github.com/dggomes/ally-bindings/actions/workflows/build.yml/badge.svg)](https://github.com/dggomes/ally-bindings/actions/workflows/build.yml)
[![Latest release](https://img.shields.io/github/v/release/dggomes/ally-bindings?include_prereleases&label=release)](https://github.com/dggomes/ally-bindings/releases)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![Platform: Windows](https://img.shields.io/badge/platform-Windows-0078D4.svg)](#requirements)

A lightweight, local-first Windows controller-profile selector for Xbox Remote Play on the **ROG Xbox Ally X**.

Ally Bindings lets you save named mappings, rotate them from the controller, and see the pending choice in a small overlay. It is intentionally conservative: the current public build exercises the complete profile-selection experience without hiding the physical controller, creating a virtual controller, or writing unvalidated ASUS controller settings.

> **Project status: preview.** The latest published release is `v0.01`. Standard-button remapping is preview-only, and M1/M2 writes are locked until a passive Armoury Crate capture has been reviewed on physical hardware. See [Current capabilities](#current-capabilities) before installing.

Ally Bindings is an independent project and is not affiliated with ASUS, ROG, Microsoft or Xbox.

## Why this exists

Xbox Remote Play presents multiple streamed games through the same Windows process, so process-based controller profiles cannot reliably tell them apart. Ally Bindings makes the profile choice explicit and controller-driven:

1. Hold **View + Menu**.
2. Release and repeat the chord to rotate through enabled profiles.
3. Pause for 900 ms to commit the displayed profile.
4. Keep the chord held and press **RT** to open the editor.

The chord and timings are configurable. The app can remain in the notification area and start automatically when you sign in.

## Current capabilities

| Capability | Status |
|---|---|
| Named local profiles | **Working** |
| Profile editor and validation | **Working** |
| Controller chord and overlay | **Working** |
| Tray mode and sign-in startup | **Working** |
| Panic/default shortcut (`Ctrl+Alt+F12`) | **Working** |
| Automatic and manual update checks | **Working** |
| Verified GitHub Releases updater with rollback | **Working** |
| Passive Armoury M1/M2 protocol capture | **Next release; write-locked** |
| Standard XInput remapping | **Preview only** |
| ASUS M1/M2 controller-setting writes | **Disabled pending physical validation** |
| Physical-device hiding / virtual controller output | **Not enabled** |

Selecting a profile currently updates Ally Bindings' state and UX. It does **not** yet claim that transformed standard-button input reached Remote Play.

## Requirements

- Windows 11 x64. Windows 10 2004+ is the build target, but the physical validation target is Windows 11 on the ROG Xbox Ally X.
- An XInput-compatible controller; the built-in Ally X controller is the intended target.
- No administrator rights for normal launch.
- Optional passive protocol capture: separately installed, signed [USBPcap 1.5.4.0](https://github.com/desowin/usbpcap/releases/tag/1.5.4.0), followed by a reboot. Capture elevation is requested by USBPcap, not by normal Ally Bindings use.

## Install

### Published preview

1. Open [Releases](https://github.com/dggomes/ally-bindings/releases).
2. Download the latest `AllyBindings-<version>-win-x64.zip` asset.
3. Extract it to a normal user-writable folder such as `%LOCALAPPDATA%\Programs\AllyBindings`.
4. Run `AllyBindings.exe`.

Releases are currently **not Authenticode-signed**, so Windows SmartScreen may show an unknown-publisher warning. GitHub publishes a SHA-256 digest for each release asset, and Ally Bindings verifies that digest before installing an update.

### Pull-request build

For unreleased testing, open the latest successful [Build workflow](https://github.com/dggomes/ally-bindings/actions/workflows/build.yml), select the run and download the `AllyBindings-win-x64` artifact. Pull-request artifacts are development builds, not releases.

## First run

1. Open Ally Bindings and keep the permanent **Default** profile enabled.
2. Create or edit named profiles.
3. Leave **Enable experimental ASUS M1/M2 hardware mappings** off; it is locked in the current build anyway.
4. Test profile rotation with the default **View + Menu** chord.
5. Optionally enable **Run in the tray when I sign in**.

Configuration is stored at:

```text
%LOCALAPPDATA%\AllyBindings\config.json
```

Writes are atomic, the previous valid configuration is backed up, and malformed configuration is preserved as a timestamped `.corrupt-*` file before the app returns to a safe Default profile.

## Controller shortcut

- Hold **View + Menu** for 250 ms to select the next enabled profile.
- Release and repeat to continue rotating.
- Stop for 900 ms to commit the displayed item.
- To open the editor, keep **View + Menu held** after the hold threshold and press **RT**. RT must rise after the chord is armed; an already-held trigger does nothing.
- If the controller disconnects before commit, the pending selection is cancelled.

Face-button-only chords such as `A + B` are supported but discouraged while the app is in preview mode because observed inputs are not swallowed and may reach the streamed game.

## Passive Armoury protocol capture

The next release includes a deliberately passive logger for proving what Armoury Crate sends when changing M1/M2 assignments. This must happen **before** Ally Bindings enables any ASUS controller-setting write.

The logger:

- discovers exactly one ASUS N-KEY / ROG Ally USB device address;
- asks the user to confirm it and revalidates the same identity after capture;
- invokes USBPcap with `--devices <address>` and refuses whole-root-hub capture;
- binds the capture process tree to a Windows kill-on-close job before USBPcap is allowed to start;
- extracts outbound HID `SET_REPORT(feature)` payloads without truncating unexpected bytes;
- records action windows for `M1=A / M2=B`, `M1=X / M2=Y`, and Armoury's Reset to Default;
- labels missing, mismatched, reordered or truncated evidence **INCONCLUSIVE**;
- keeps custom and recovery writes source-locked regardless of the capture verdict;
- creates a ZIP with the raw PCAP, extracted JSON, manifest and SHA-256 hash.

### One-time capture procedure

1. Photograph or export any custom Armoury M1/M2 assignments.
2. Install the signed [USBPcap 1.5.4.0 release](https://github.com/desowin/usbpcap/releases/tag/1.5.4.0) and reboot if requested. Do not disable Windows security controls or use test-signing mode.
3. In Ally Bindings, open **Safety & diagnostics** and choose **Capture Armoury M1/M2 protocol (passive)**.
4. Accept USBPcap's UAC prompt and verify the displayed device is the ASUS N-KEY / ROG Ally controller.
5. Follow the prompts to apply `M1=A / M2=B`, then `M1=X / M2=Y`, then Armoury's **Reset to Default**.
6. Press `q` in the black USBPcap console to stop cleanly.
7. Keep the generated ZIP under `%LOCALAPPDATA%\AllyBindings\captures\` and share it only deliberately.

The PCAP is filtered to one composite USB device, but it may still contain traffic from other interfaces on that device. Treat the bundle as **private diagnostic data**. No verdict automatically unlocks writes; the capture requires human review and a later source change.

More detail: [hardware validation runbook](docs/HARDWARE-SPIKE.md).

## Updates

- Automatic update checks run at most once every 24 hours by default and can be disabled.
- **Check for updates** runs the same public GitHub Releases check immediately.
- Installation is never silent: the release is shown and confirmation is required.
- The updater requires GitHub's SHA-256 asset digest, rejects unsafe ZIP paths, symlinks and duplicate entries, stages files before exit, replaces the executable last, and retains application/configuration backups until the new app confirms successful initialization.
- Failure or timeout attempts every rollback step and relaunches the previous app. Incomplete rollback is reported explicitly.
- Profiles live outside the application folder and are not replaced.

The updater protects against corruption and mismatched downloads. Until releases are Authenticode-signed, it does not protect against compromise of the repository or its release credentials.

## Safety and privacy

- No account, telemetry, cloud sync or network listener.
- Update checks contact only GitHub's HTTPS API and release-download hosts.
- No injection into games, Xbox or Armoury Crate.
- No Armoury database mutation, macros, turbo or anti-cheat bypasses.
- No driver is installed or updated by Ally Bindings.
- The permanent Default profile cannot be edited or deleted.
- `Ctrl+Alt+F12` provides a keyboard recovery/default path.
- The current build sends no custom or recovery ASUS M1/M2 report.
- Passive capture fails closed on device ambiguity and never falls back to a broad USB root-hub capture.
- Normal diagnostics contain configuration/status metadata, not input history. Capture bundles are separate, explicit private artifacts.

Please report security concerns privately using the process in [SECURITY.md](SECURITY.md), not a public issue.

## Build and test

Requires the .NET 8 SDK.

```powershell
git clone https://github.com/dggomes/ally-bindings.git
cd ally-bindings
dotnet test AllyBindings.sln --configuration Release
./scripts/package.ps1
```

Core tests also run on macOS and Linux:

```bash
dotnet test tests/AllyBindings.Core.Tests/AllyBindings.Core.Tests.csproj -c Release
```

The WPF app can be cross-compiled with Windows targeting enabled, but Windows packaging, updater rollback and USBPcap lifecycle checks must pass on a Windows runner.

## Repository map

- `src/AllyBindings.Core` — profile/configuration model, persistence, mapping engine, backend contracts, carousel state machine and USBPcap parser.
- `src/AllyBindings.Windows` — WPF UI, tray, overlay, XInput polling, startup registration, updater and passive capture orchestration.
- `tests/AllyBindings.Core.Tests` — deterministic core and protocol-parser tests.
- `docs/ARCHITECTURE.md` — boundaries, data flow and safety invariants.
- `docs/HARDWARE-SPIKE.md` — physical Ally validation and rollback matrix.
- `docs/PLAN.md` — delivery state and release gates.
- `examples/config.sample.json` — readable sample configuration.

## Project documents

- [Changelog](CHANGELOG.md)
- [Contributing](CONTRIBUTING.md)
- [Security policy](SECURITY.md)
- [Architecture](docs/ARCHITECTURE.md)
- [Hardware validation](docs/HARDWARE-SPIKE.md)
- [MIT licence](LICENSE)

## Scope

Ally Bindings is a profile selector—not an Armoury Crate replacement. It does not change TDP, fan, RGB, display or Armoury game-profile settings, and it does not infer the streamed Xbox title from video or OCR.
