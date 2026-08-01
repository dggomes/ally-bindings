# Changelog

All notable changes to Ally Bindings are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). This project uses preview versioning while hardware behavior is still being validated.

## [Unreleased]

### Added

- M1/M2 profile fields and a clean-room ASUS rear-button report builder.
- A source-locked ASUS backend that refuses both custom and recovery writes pending physical protocol validation.
- A passive Armoury Crate capture workflow using device-address-filtered USBPcap traffic.
- Exact HID `SET_REPORT(feature)` extraction with setup bytes, interface, bus/device identity, declared/captured lengths, timestamps and complete payloads.
- Action-window validation for `M1=A / M2=B`, `M1=X / M2=Y`, and Armoury's Reset to Default.
- Conclusive/inconclusive capture verdicts; missing, duplicate, extra, malformed, reordered, mismatched, out-of-window or truncated evidence fails closed.
- Private capture bundles containing the raw PCAP, extracted JSON, manifest, action markers and SHA-256 evidence.
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
- Passive capture refuses broad root-hub collection, requires confirmation of the selected ASUS USB device address before creating a PCAP and revalidates the identity after capture.
- Native resets require the recovery gate explicitly; custom mappings require both custom and recovery authorization so a mapping can never be enabled without its rollback path.
- Unexpected extra HID payload bytes are retained and cause exact-vector comparison to fail instead of being discarded.
- USBPcap is launched through a tracked `start /wait` wrapper and a kill-on-close Windows Job Object established before capture begins.

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
