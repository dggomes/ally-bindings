# Changelog

All notable changes to Ally Bindings are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). This project uses preview versioning while hardware behavior is still being validated.

## [Unreleased]

## [v0.3.0-preview.12] - 2026-08-02

### Fixed

- Classify every schema observation from the ETW event's own QPC timestamp inside helper-generated, acknowledged and closed action windows; transition sampling and ETW classification share one lock so thread scheduling cannot move buffered events across phases.
- Finish cancellation and teardown of an active ETW capture before any Ally Bindings native reset can begin.
- Require positive elevated-helper exit after graceful cancellation or forced process-tree termination; an unconfirmed exit faults the capture barrier and keeps native resets blocked until restart.
- Bound and sanitize exact-candidate source-field metadata before IPC serialization.
- Preserve coalesced newline-delimited pipe frames with one persistent bounded reader on each side of the authenticated helper channel.

### Safety

- Withdraw `v0.3.0-preview.11` permanently and remove its assets; delete its obsolete release run, protect the withdrawn tags against deletion/movement with an active repository ruleset, and make future release jobs load the authoritative denylist from `origin/main`.
- Replace lexical-only privacy assertions with an explicit metadata DTO contract that recursively rejects byte containers and payload/hash/timestamp fields.

### Tests

- Cover closed QPC windows and idle gaps, strict transition-command parsing, coalesced pipe frames, all command-marker split boundaries, intentional scalar-report-ID dual observations, behavioral reset/capture gate ordering, and recursive schema serialization payload exclusion.

## [v0.3.0-preview.11] - 2026-08-02 — WITHDRAWN

### Added

- Record a bounded metadata-only inventory of decoded USB ETW event/property shapes after two physical Ally X captures produced clean events but no exact `5A D1 02 08 2C` candidates.
- Detect full, command-only and adjacent-property-split ASUS rear-map markers in memory while exporting framing metadata only—never generic USB payload bytes or payload hashes.
- Bucket schema and marker counts by the three authenticated capture phases so transport shapes can be correlated with Armoury's apply/reset actions without retaining timestamps.
- Stamp capture manifests with the full informational version, including preview and commit metadata, instead of the compatibility-only `0.3.0.0` assembly version.

### Safety

- Keep schema discovery permanently ineligible for hardware unlock; exact target-scoped reports and a proven reset vector are still required.
- Cap property count, schema/marker cardinality, metadata lengths, event count, decoded bytes, and IPC response size. Overflow makes discovery explicitly incomplete and fail-closed.

### Tests

- Cover full, command-only, every adjacent-field split, scalar report-ID, non-adjacent rejection, overflow and zero-authority isolation while retaining the existing no-write and no-raw-trace gates.

## [v0.3.0-preview.10] - 2026-08-02

### Fixed

- Allow the elevated ETW helper to connect to the unelevated app through an explicit current-user SID ACL. `.NET`'s `PipeOptions.CurrentUserOnly` intentionally rejects cross-elevation connections even for the same Windows account.
- Deny network logons at the pipe ACL and retain both endpoint PID checks plus exact executable-path authentication after connection.

### Tests

- Exercise helper authentication through the same protected SID ACL used by the app and keep forged PID/executable peers fail-closed.

## [v0.3.0-preview.9] - 2026-08-02

### Added

- Persist a bounded, privacy-safe lifecycle diagnostic when the elevated USB ETW helper starts, authenticates, verifies providers, creates its session, enables providers, becomes ready, receives a command, stops, or fails.
- Capture the helper exit code plus bounded, path-redacted error types, HRESULTs and messages without storing usernames, absolute paths, stack traces, USB payloads, controller reports, configuration values, or raw ETW data.
- Offer explicit `Copy diagnostics` and `Open folder` actions after a capture failure, removing the need to run PowerShell on the handheld without silently exposing diagnostics through the clipboard.

### Tests

- Require rejected helper peers to persist a valid schema-2 diagnostic with the expected structured failure and privacy declaration.
- Keep diagnostic lifecycle, atomic writes, clipboard handoff, and Explorer disclosure in the capture safety gate.

## [v0.3.0-preview.8] - 2026-08-02

### Upgrade note

- If an earlier build reports `The path is not of a legal form` while updating, close Ally Bindings and run the standalone preview.8 EXE once. That failed install stops before replacing `AllyBindings.exe`; configuration remains in `%LOCALAPPDATA%\AllyBindings`.

### Fixed

- Restrict in-app updates to the self-contained `AllyBindings.exe` instead of rewriting the documentation bundle. The executable is now one atomic replacement with a real same-volume backup path; passing a null backup path could fail on an existing package file such as `CHANGELOG.md` on some installations.
- Use the same valid-backup replacement path when restoring application files and configuration, preventing the secondary incomplete-rollback errors shown after the original failure.
- Use unpredictable transaction-scoped temporary names and never relaunch after an incomplete rollback.
- Bind the staged executable to a SHA-256 calculated directly from the already-verified archive. The installer holds a deny-write/delete read handle, rehashes that exact handle, and copies from it so post-verification staging changes fail closed.

### Tests

- Exercise atomic replacement of an already-existing executable, verify its exact package hash, and assert that installed documentation is left untouched.
- Mutate the staged executable after package verification and verify that installation is rejected before the existing executable or configuration changes.

## [v0.3.0-preview.7] - 2026-08-02

### Fixed

- Recognize the ROG Xbox Ally X full DMI product name observed on physical hardware: `ROG Xbox Ally X RC73XA_RC73XA`.
- Keep model detection fail-closed by allowlisting that exact case-insensitive name rather than accepting arbitrary strings that contain a supported model token.

### Tests

- Added positive coverage for the exact full product name and negative coverage for shortened, mismatched-model, extra-suffix and unrelated-prefix variants.

## [v0.3.0-preview.6] - 2026-08-02

### Upgrade note

- Every updater-enabled public build before preview.6 (`v0.2.0-preview.1` through `v0.3.0-preview.5`) fails before the replacement binary starts and therefore cannot self-install this repair. Close Ally Bindings and run the standalone preview.6 EXE once; configuration remains in `%LOCALAPPDATA%\AllyBindings`. In-app updates from preview.6 onward use the repaired handoff.

### Fixed

- Closed the downloaded update file before hashing and extracting it. Preview.5 kept its exclusive download handle alive and then failed when its own verifier reopened `update.zip`.
- Reused one bounded-retry read handle for update hashing and extraction, making the handoff tolerant of short-lived antivirus or indexing locks without weakening digest verification.
- Clean up stale incomplete-download directories on a later app launch while preserving installer/rollback directories and refusing to traverse reparse points.

### Tests

- Added a Windows end-to-end download-to-verification regression test that requires the prepared ZIP to be exclusively reopenable, the entire staging tree removable after preparation returns, and a stale abandoned download to be cleaned on startup.
- Added a transient exclusive-lock test for package verification. The updater handoff gate now runs in both PR and tagged-release CI.
- Preview.5 now has a public assetless withdrawal tombstone, and release CI permanently rejects its denylisted tag rather than permitting corrected assets to be published under it.

## [v0.3.0-preview.5] - 2026-08-02

### Changed

- Reworked the controller workspace for real 16:9 handheld use: the window now sizes from the available landscape work area, mapping rails expand up to a readable width, and the central Ally illustration consumes the remaining space instead of leaving a blank panel.
- Increased mapping-label legibility while preserving the 1040×736 compact layout and 900×600 supported minimum.

### Fixed

- Removed the unnecessary internal controller-map scrollbar at 1600×900 and added Windows UI Automation gates for landscape expansion, readable mapping rails, and scroll-free presentation.

## [v0.3.0-preview.4] - 2026-08-02

### Fixed

- Recognize the real ROG Xbox Ally X firmware DMI product form `RC73XA_RC73XA` while retaining the exact ASUS manufacturer gate and rejecting mixed, prefixed, suffixed, or unrelated model identities.

## [v0.3.0-preview.3] - 2026-08-02

### Fixed

- Wrapped and automation-verified the full **Capture & update** navigation labels so the persistent rail remains readable at the 1040×736 Ally viewport.
- Release verification now enforces the exact draft asset allowlist and validates product/file metadata on both standalone and packaged executables after draft and public redownloads.

## [v0.3.0-preview.2] - 2026-08-02

### Added

- Full physical-trigger source mappings with XInput-compatible trigger activation and analog trigger-to-trigger intensity preservation; configuration schema 3 prevents older builds from silently stripping those mappings after rollback.

### Changed

- Rebuilt the controller workspace around an Armoury-style oversized Ally diagram with all 18 physical controls visible as labelled, touch-sized mapping actions.
- Every mapping action and every matching control on the controller illustration now opens the controller-operable binding modal; the illustrated controls expose 48-DIP targets at the Ally viewport and dense clusters route touch/pointer input to the nearest physical control.
- Preview releases recapture the tagged binary's real WPF screens, replace the packaged documentation images before signing off the archive, and reverify the public assets after publication.
- GitHub Actions dependencies are pinned to immutable commits, and `main` requires the full Windows/core checks through a pull request before release tags can be cut.

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
- Prevented active-window controller actions and configured shortcut chords from firing simultaneously; entering the editor now cancels any pending cycle and requires a full button release before rearming.
- Serialized controller dialogs and made failed recovery confirmation fail closed instead of allowing shutdown.
- Kept panic/default restoration available during passive Armoury capture; restore propagates cancellation through discovery/start/completion, exit awaits privacy cleanup, and save/apply stay blocked.
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
[v0.3.0-preview.2]: https://github.com/dggomes/ally-bindings/releases/tag/v0.3.0-preview.2
[v0.3.0-preview.3]: https://github.com/dggomes/ally-bindings/releases/tag/v0.3.0-preview.3
[v0.3.0-preview.4]: https://github.com/dggomes/ally-bindings/releases/tag/v0.3.0-preview.4
[v0.3.0-preview.5]: https://github.com/dggomes/ally-bindings/releases/tag/v0.3.0-preview.5
[v0.3.0-preview.6]: https://github.com/dggomes/ally-bindings/releases/tag/v0.3.0-preview.6
[v0.3.0-preview.7]: https://github.com/dggomes/ally-bindings/releases/tag/v0.3.0-preview.7
[v0.3.0-preview.8]: https://github.com/dggomes/ally-bindings/releases/tag/v0.3.0-preview.8
[v0.3.0-preview.9]: https://github.com/dggomes/ally-bindings/releases/tag/v0.3.0-preview.9
[v0.3.0-preview.10]: https://github.com/dggomes/ally-bindings/releases/tag/v0.3.0-preview.10
[v0.3.0-preview.11]: https://github.com/dggomes/ally-bindings/releases/tag/v0.3.0-preview.11
[v0.3.0-preview.12]: https://github.com/dggomes/ally-bindings/releases/tag/v0.3.0-preview.12
