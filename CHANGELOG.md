# Changelog

All notable changes to Ally Bindings are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). This project uses preview versioning while hardware behavior is still being validated.

## [Unreleased]

## [v0.3.0-preview.1] - 2026-08-01

### Added

- A controller-first, touch-friendly WPF shell with large persistent sections and 48-DIP minimum primary controls.
- Complete gamepad UI navigation: D-pad focus movement, A select, B back, LB/RB section switching, X save and Y profile selection.
- Controller-aware in-app confirmations for capture, updates and recovery gates; only the Windows UAC secure-desktop prompt remains outside app control.
- A controller on-screen keyboard for profile names and button-based timing steppers, removing keyboard-only profile-management paths.
- A visual controller binding editor with physical-button callouts around an Ally-style controller silhouette.
- Original Ally Bindings artwork embedded as the executable, window and notification-area icon.
- An always-visible manual **Update app** action plus a second maintenance-page update action.
- Same-user named-pipe activation so launching the EXE reveals an existing sign-in-started tray instance.
- Windows integration coverage for background startup and second-instance activation, plus structural UI discoverability/touch/controller assertions.
- CI-captured screenshots of all four workspaces, published in the README and release package.

### Changed

- Replaced the long spreadsheet-style editor with focused **Profiles**, **Controller**, **Shortcut**, and **Capture & update** workspaces.
- Moved Armoury M1/M2 capture from a below-the-fold diagnostics card to the first, accented card in a dedicated persistent section.
- Normal launches show and focus the main window; only explicit sign-in/background launches remain tray-only.
- Version/status surfaces preserve the full prerelease SemVer instead of displaying only the numeric assembly version.
- Release publishing now stages assets as a draft, downloads and independently verifies their hashes/package allowlist/metadata, then publishes the prerelease.

### Fixed

- Fixed the published capture feature appearing absent at Ally-scale display heights because it was buried below the initial viewport.
- Fixed a normal second launch reporting “already running” without opening the existing tray instance.
- Prevented active-window controller actions and configured shortcut chords from firing simultaneously.
- Serialized controller dialogs and made failed recovery confirmation fail closed instead of allowing shutdown.
- Kept panic/default restoration available during passive Armoury capture; restore cancels capture while save/apply stay blocked.
- Confined modal focus, made long controller dialogs scrollable, and made the profile keyboard fit the supported minimum window.
- Fixed initial no-controller status, dark-workspace contrast, mapping accessibility names and live status announcements.

## [v0.2.0-preview.2] - 2026-08-01

### Added

- M1/M2 profile fields and a clean-room ASUS rear-button report builder.
- A source-locked ASUS backend that refuses both custom and recovery writes pending physical protocol validation.
- A passive Armoury Crate capture workflow using Windows' built-in USB ETW providers inside Ally Bindings, with no USBPcap/Wireshark install.
- Real-time `FullDataBusTrace` filtering that retains only bounded metadata-decoded 50–64-byte binary fields containing the ASUS rear-mapping candidate prefix, timestamps, provider/event/field metadata and report hashes; no raw ETL/PCAP is written.
- Action-window validation for `M1=A / M2=B`, `M1=X / M2=Y`, and Armoury's Reset to Default.
- Review-required capture diagnostics; unvalidated ETW candidates cannot unlock writes or clear recovery state, and missing, duplicate, extra, malformed, reordered, mismatched, out-of-window, lost or truncated evidence fails closed.
- Private capture bundles containing filtered JSON, manifest, action markers and SHA-256 evidence.
- RT confirmation while the shortcut chord remains held to open the editor.
- Automatic daily update checks and independently configurable preview-release checks.
- Verified one-click updater with staged replacement, application/configuration backups and rollback.
- Public GitHub Releases update feed with no client token requirement.
- CI checks for capture-only safety, updater installation and rollback behavior.
- Public contribution, security, issue-reporting, licensing and changelog documentation.

### Changed

- Opening the editor is no longer an item in the profile carousel.
- New and migrated configurations default to automatic update checks.
- Release publishing uses the repository-scoped GitHub Actions token, publishes SHA-256 asset digests and sources release notes from the matching changelog section.
- Documentation now separates working functionality, preview behavior and hardware-validation gates.

### Security

- ASUS custom and recovery writes no longer expose caller-controlled approval parameters.
- Passive capture confirms the ROG Ally model and compatible ASUS HID interfaces before self-elevating the same executable for ETW, then revalidates that identity afterwards.
- Native resets require the recovery gate explicitly; custom mappings require both custom and recovery authorization so a mapping can never be enabled without its rollback path.
- Missing providers, lost/oversized/dropped ETW events, duplicate/extra report vectors and target identity changes remain review-required and cannot affect write or recovery state.
- The temporary ETW session stops cooperatively on completion, cancellation, timeout or parent disconnect; its fixed name lets the next capture reclaim a session orphaned by a hard process crash.
- ETW loss is queried while the session is live, provider enablement is synchronously acknowledged, and action/report correlation uses the shared Windows performance-counter clock.
- The elevated helper verifies that its pipe server is the same executable image, updater assets are bound exactly to their release tag, and release tags must descend from `main`.
- Capture output is atomically committed as a single private ZIP with an externally displayable SHA-256; loose duplicate artifacts are no longer retained, and cleanup failures surface an explicit privacy warning.
- Windows CI adversarially tests forged pipe-server PIDs, a same-user server running from the wrong executable, and executable replacement while the integrity lock is held.

## [v0.01] - 2026-08-01

### Added

- First public preview release.
- Native .NET 8 WPF application with notification-area mode.
- Local schema-versioned JSON profile store with atomic writes, backup and corrupt-file recovery.
- Permanent Default profile and named profile editor.
- Read-only XInput controller monitoring.
- Configurable controller chord with a View + Menu default.
- Non-activating profile-selection overlay and delayed commit state machine.
- Controller-disconnect cancellation.
- `Ctrl+Alt+F12` panic/default shortcut.
- Optional per-user launch at sign-in.
- Diagnostics export without controller input history.
- Cross-platform core tests and Windows self-contained packaging.

### Known limitations

- Standard mappings use the preview backend and do not transform physical controller output.
- M1/M2 are not available as independent XInput buttons.
- Releases are not Authenticode-signed.

[v0.01]: https://github.com/dggomes/ally-bindings/releases/tag/v0.01
[v0.2.0-preview.2]: https://github.com/dggomes/ally-bindings/releases/tag/v0.2.0-preview.2
[v0.3.0-preview.1]: https://github.com/dggomes/ally-bindings/releases/tag/v0.3.0-preview.1
