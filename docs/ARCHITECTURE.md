# Architecture and safety boundaries

## Product boundary

Ally Bindings is a local controller-mapping selector for the case where multiple streamed Xbox titles share one Windows Remote Play process. It does not modify Armoury Crate records or infer the game inside the video stream.

## Runtime shape

```text
XInput controller
      │ snapshots (read-only today)
      ▼
┌─────────────────────────┐
│ Windows host            │
│ XInput monitor          │
│ chord recognizer        │
│ tray / WPF main window  │
│ non-activating overlay  │
└──────────┬──────────────┘
           │ selection
           ▼
┌─────────────────────────┐       ┌──────────────────────────┐
│ AllyBindings.Core       │       │ JSON profile store       │
│ profile cycle machine   │◄─────►│ %LOCALAPPDATA%           │
│ pure mapping transform  │       │ temp + backup + recovery │
│ backend contract        │       └──────────────────────────┘
└──────────┬──────────────┘
           │ apply/restore
           ▼
┌─────────────────────────┐
│ Controller backend      │
│ Preview (implemented)   │
│ Physical/virtual (gate) │
└─────────────────────────┘
```

## Projects

### `AllyBindings.Core`

Cross-platform .NET 8 library with no Windows UI dependency:

- XInput-compatible normalized controller state.
- Named profiles and one-to-one button mappings.
- Configuration validation/normalization.
- Atomic JSON persistence with backup and corrupt-file recovery.
- Pure mapping transform.
- Backend health/apply contract.
- Deterministic controller-chord carousel state machine.
- PII/input-history-free diagnostics projection.

### `AllyBindings.Windows`

Windows 10 2004+ WPF host:

- 20 ms XInput polling through `xinput1_4.dll`.
- Controller index auto-discovery or persisted preferred index.
- View + Menu default chord with hold/release/debounce gating.
- Non-activating, always-on-top profile overlay.
- `Open application` carousel sentinel.
- Tray icon, startup registration and Ctrl+Alt+F12 panic/default hotkey.
- Profile/shortcut editor and truthful backend state.

WPF was chosen over WinUI 3 for this narrow tray utility: fewer deployment/runtime moving parts, native Windows rendering and straightforward cross-target compilation. There is no Electron/browser runtime.

## Profile format

The canonical path is `%LOCALAPPDATA%\AllyBindings\config.json`. The entire user state is one schema-versioned document so profile and shortcut changes commit together.

```json
{
  "schemaVersion": 1,
  "activeProfileId": "elden-ring",
  "controllerIndex": null,
  "runAtStartup": true,
  "shortcut": {
    "buttons": ["View", "Menu"],
    "holdMilliseconds": 250,
    "commitDelayMilliseconds": 900
  },
  "profiles": [
    { "id": "default", "name": "Default", "enabled": true, "bindings": {} },
    { "id": "elden-ring", "name": "Elden Ring", "enabled": true, "bindings": { "A": "B" } }
  ]
}
```

Writes go to a same-directory temporary file, preserve the prior valid file as `.bak`, then replace the canonical file. Invalid JSON is copied to a timestamped `.corrupt-*` file before returning to Default.

## Carousel state machine

The controller shortcut is deliberately pure/testable:

1. Detect all configured buttons down.
2. Require the hold threshold.
3. Emit one selection per press, never key-repeat while held.
4. Require release before another selection.
5. Reset the inactivity timer after release.
6. Commit after the inactivity timeout.
7. Cancel pending selection if the controller disconnects.

Cycle items are enabled profiles followed by `Open application`. The first activation advances from the active profile to the next item.

## Backend contract

```csharp
public interface IControllerBackend : IAsyncDisposable
{
    BackendStatus GetStatus();
    Task<BackendStatus> InitializeAsync(CancellationToken cancellationToken = default);
    Task<BackendApplyResult> ApplyAsync(MappingProfile profile, CancellationToken cancellationToken = default);
    Task<BackendApplyResult> RestoreDefaultAsync(CancellationToken cancellationToken = default);
}
```

Backend results distinguish a selected app profile from a mapping physically applied to controller output. The included preview backend always keeps physical passthrough intact and returns `AppliedToController = false`.

A real backend must additionally stream normalized input through `MappingEngine`, produce exactly one virtual Xbox device and prove fail-open recovery. No physical-device hide action belongs in the generic UI/core layer.

## Safety invariants

1. Never hide a physical controller before a healthy output device exists.
2. Never claim a profile was applied when only app state changed.
3. `Default` always exists and cannot carry remaps.
4. Ctrl+Alt+F12 does not depend on the controller chord/profile.
5. Disconnect cancels uncommitted selections.
6. Startup registration is per-user, opt-in and removable.
7. Launching the app installs no driver and requires no elevation.
8. Diagnostics contain status/config metadata, not controller input history.
9. No code injection, Armoury mutation, macros or network service.

## Release gate

The UI/core/package may ship as preview. A build must not be called a working remapper until the real backend passes every hard-fail condition in `HARDWARE-SPIKE.md` on Daniel's Ally X, including duplicate-input, Command Centre, suspend/resume, forced kill and uninstall rollback.
