# Hardware validation spike

## Goal

Prove a safe mapping path on Daniel's ROG Xbox Ally X **before** building the application UI around it.

The important question is not whether Windows can remap a generic controller. It is whether the Ally X's physical controls — especially rear buttons and the Command Centre button — can be captured, mapped, and restored without duplicate input or broken Armoury functionality.

## Preconditions

- Test on the actual ROG Xbox Ally X, plugged into power.
- Record Windows version, Armoury Crate version, ASUS System Control Interface version, and controller firmware.
- Export/photograph the existing Armoury controller mapping before any change.
- Create a Windows restore point before installing a filter/virtual-controller driver.
- Test only in a non-competitive local game or Xbox Remote Play menu; never an anti-cheat title.

## Candidate backends

### A. Supported ASUS/Armoury service integration — preferred if real

Investigate whether Armoury Crate exposes a local, supported command/API for changing the active control mapping without an executable transition.

**Pass condition:** documented or stable local invocation, mapping applies live, no config-file mutation.

**Likely outcome:** unavailable. Asus currently exposes game-profile behaviour through Armoury Crate, not a public developer API. Do not reverse-engineer private IPC for v1.

### B. Existing maintained remapping backend

Evaluate a maintained Windows input-remapping backend that can:

- read the physical Ally gamepad;
- hide it from consumer applications only after virtual output is ready;
- emit a virtual Xbox/XInput controller;
- preserve/avoid collision with ASUS-specific buttons; and
- uninstall cleanly.

This route is acceptable only after reviewing licence, code-signing/driver provenance, Windows 11 compatibility, maintenance activity, and crash recovery. Do not commit to a legacy/unmaintained virtual-controller stack merely because it has old tutorials.

### C. Windows-supported HID / virtual-device path

Evaluate whether a Windows-supported virtual HID/controller route can meet the same requirements without an unsupported filter driver. This is architecturally attractive but may require more native/driver work than the narrow product warrants.

## Test matrix

| Test | Expected result | Hard fail |
|---|---|---|
| Baseline input enumeration | Physical buttons and rear paddles identified | Ambiguous/duplicate devices not understood |
| Start backend | One controller reaches Xbox input test | Duplicate inputs |
| Apply `Default` | Exact baseline Xbox layout | Missing buttons or stuck input |
| Apply rear-button remap | Only intended virtual output changes | Command Centre ceases working |
| Switch 20 times | No drift, missed transitions, or ghost presses | Any input lockout |
| Start Remote Play | Mapping reaches streamed Xbox UI | Xbox sees two controllers |
| Suspend/resume | Controller recovers safely | Inputs remain hidden/unusable |
| Remote Play reconnect | Mapping remains usable | Requires reboot/reinstall |
| Kill app/backend | Physical controller remains usable or auto-restores promptly | Controller stranded hidden |
| Armoury Crate open/close | No crash/corruption/config reset | Armoury mappings damaged |
| Uninstall backend | Original behaviour restored | Residual hidden controller/driver |

## Decision rule

Proceed to implementation only if a backend passes every hard-fail test and provides a credible recovery path. If none does, the project stops rather than shipping a fragile controller driver experiment.

## Deliverables from the spike

- Redacted device/driver inventory.
- Chosen backend and licence rationale.
- Reproducible install and rollback steps.
- Test results with Windows/Armoury/firmware versions.
- A minimal command-line proof: `apply default`, `apply lies-of-p`, `restore`.
