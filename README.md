# Ally Bindings

A small, local-first Windows application for applying named controller mappings while playing Xbox games through **Xbox Remote Play** on a ROG Xbox Ally X.

## The problem

Armoury Crate can associate a game profile with a local executable. Xbox Remote Play presents every streamed Xbox title as the same local Xbox/Remote Play process, so Armoury Crate cannot distinguish *Elden Ring* from *Lies of P*.

Ally Bindings makes the active mapping an explicit choice instead:

1. Open a compact overlay with a hotkey or Ally shortcut.
2. Choose a named profile such as `Elden Ring`, `Lies of P`, or `Default`.
3. The controller mapping changes immediately while the Remote Play stream stays open.

It deliberately does **not** attempt to replace Armoury Crate or control power, TDP, fan, RGB, display, or game launching.

## Status

**Planning / hardware-validation phase. No controller driver or remapping code exists yet.**

The project must first prove that the Ally X controller can be captured and re-emitted safely without breaking the Command Centre button, Armoury Crate, Xbox Remote Play, or local games. See:

- [Implementation plan](docs/PLAN.md)
- [Architecture and safety boundaries](docs/ARCHITECTURE.md)
- [Hardware research spike](docs/HARDWARE-SPIKE.md)

## Intended v1

- Lightweight tray app with a configurable global hotkey.
- Named mapping profiles stored locally as JSON.
- Button, stick-click, trigger, and rear-button remaps.
- An unobtrusive on-screen confirmation after switching.
- A guaranteed `Default` profile and panic-reset hotkey.
- Manual profile selection; no unreliable OCR or guessing of the title inside a Remote Play video stream.

## Non-goals

- Changing Armoury Crate Game Profiles.
- Automating profile selection from the streamed game title in v1.
- Macros, turbo, cheats, anti-cheat bypasses, or competitive-game automation.
- TDP/fan/RGB/display controls.
- Collecting telemetry, cloud sync, accounts, or network access.

## Licence

To be selected before implementation. The app must not copy GPL-licensed code into a differently licensed project; external integrations need their own licence review.
