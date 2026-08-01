# Architecture and safety boundaries

## Product decision

Ally Bindings is **a controller-mapping selector**, not an Armoury Crate replacement. It solves a narrow problem: one local Remote Play executable needs multiple user-selectable control layouts.

The app will not attempt to modify Armoury Crate's internal game-profile records. Asus does not publish a supported automation interface for selecting a profile at runtime, and config-file/service reverse engineering would be fragile across Armoury updates.

## Design principles

1. **Local only.** No account, telemetry, cloud service, or network listener.
2. **Explicit selection.** The app does not pretend it knows which streamed game is playing.
3. **Fail open.** If the remapping backend crashes or cannot start, the physical controller must remain usable as a normal controller.
4. **One obvious escape hatch.** A hardware-independent panic hotkey restores `Default` immediately.
5. **No privileged installation without consent.** Any input/filter driver is optional, clearly named, signed where possible, and installed only after Daniel approves it on the Ally X.
6. **Do not break Ally controls.** The Command Centre button, touch controls, sleep/wake, and Armoury Crate must be explicitly regression-tested.

## Components

```text
┌───────────────────────────┐
│ WinUI 3 tray + overlay UI │
│ profiles / hotkeys / log  │
└─────────────┬─────────────┘
              │ profile selection
┌─────────────▼─────────────┐
│ Mapping engine            │
│ pure, testable transforms │
└───────┬───────────┬───────┘
        │           │
  physical input    virtual output
        │           │
┌───────▼───┐   ┌───▼────────────────┐
│ Ally input│   │ virtual Xbox pad    │
│ adapter   │   │ adapter             │
└───────────┘   └──────────┬─────────┘
                            │
                    Xbox Remote Play
```

### UI host

- **Target:** .NET 8+, C#, WinUI 3 / Windows App SDK.
- **Reason:** native Windows feel, low idle footprint, straightforward tray/global-hotkey support, no Electron runtime.
- **Responsibilities:** profile list/editor, hotkey registration, overlay/toast, safe startup/shutdown, diagnostics export.

### Profile store

Profiles are local JSON under `%LOCALAPPDATA%\AllyBindings\profiles.json`, atomically written with a timestamped backup. Example:

```json
{
  "schemaVersion": 1,
  "activeProfile": "default",
  "profiles": [
    {
      "id": "lies-of-p",
      "name": "Lies of P",
      "bindings": {
        "rearLeft": "leftStickClick",
        "rearRight": "rightStickClick"
      }
    }
  ]
}
```

V1 supports only one-to-one standard gamepad mappings. Chords, turbo, rapid-fire, delays, scripts, and macros are deliberately excluded.

### Mapping engine

A pure C# library receives a normalized controller state and a selected `Profile`, returning a normalized output state. It knows nothing about ASUS, drivers, UI, or Remote Play. This is the main testable unit.

### Hardware adapters

The input/output layer is intentionally an interface until proven on the physical Ally X:

```csharp
interface IControllerBackend : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken);
    Task ApplyAsync(MappingProfile profile, CancellationToken cancellationToken);
    Task RestorePassthroughAsync(CancellationToken cancellationToken);
    BackendStatus GetStatus();
}
```

The hardware spike decides whether a safe backend exists. Candidate approaches are evaluated in `HARDWARE-SPIKE.md`; none is committed to yet.

## Why automatic Xbox-title detection is out of scope

Remote Play transfers a video/audio stream. Windows usually knows the local client process, not the Xbox title rendered inside that stream. Window-title scraping, OCR, or accessibility-tree guessing would be brittle and could switch a layout incorrectly in the middle of play.

A one-action profile picker is the reliable UX. A later auto-detection module is acceptable only if the Xbox client exposes a stable, documented local title signal and it can be tested against sleep/reconnect/title changes.

## Security and compatibility boundaries

- Never inject into Xbox, Armoury Crate, Xbox Game Bar, or any game process.
- Never alter anti-cheat-related processes or claim competitive-game compatibility.
- Never hide a physical controller before a virtual output is confirmed healthy.
- Show the active backend and whether physical passthrough is intact.
- Require an explicit reboot/driver-install acknowledgement only if a selected backend needs it.
- Keep all mapping data readable and exportable.

## Acceptance criteria for release

1. From Remote Play, a profile switches in at most two user actions and takes effect in under one second.
2. No duplicate input reaches Remote Play.
3. `Default` restores a standard controller layout.
4. Command Centre and the configured panic-reset path work before, during, and after a profile switch.
5. Suspend/resume and Remote Play reconnect return to a usable controller state.
6. Closing/crashing the app leaves a usable controller path.
7. The app has no outbound network calls.
