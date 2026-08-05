# Full virtual-controller validation — ROG Xbox Ally X

This is a **physical validation procedure**, not a release claim. The build mirrors one pinned physical XInput controller into one virtual Xbox 360 controller and treats global non-injected F12/F11 events as M1/M2 while enabled. Windows' low-level keyboard hook cannot prove which device originated an event: physical keyboard F11/F12 are therefore unavailable during remapping, and the test must confirm whether Armoury emits accepted non-injected events. The build never hides the physical controller and never writes ASUS HID configuration.

## Hard stop conditions

Stop the test, disable virtual remapping and record the failure if any of these occur:

- normal controller input disappears;
- Edge/xCloud sees an unusable second-player topology;
- Command Centre or Armoury loses controller access;
- F11/F12 escapes into the foreground app while virtual remapping is enabled;
- virtual output remains connected after Ally Bindings exits;
- the fixed recovery gesture cannot disable virtual output.

Do **not** install or configure HidHide as a workaround. Released HidHide builds do not provide the process-owned session-blacklist recovery contract required for this product. Persistent device hiding is outside this validation.

## Prerequisites

- ROG Xbox Ally X running Windows 11.
- A current Ally Bindings validation ZIP from this branch.
- ViGEmBus already installed and healthy. Ally Bindings does not install or update it. ViGEmBus is retired upstream, so this is a compatibility gate rather than the intended permanent dependency.
- Armoury Crate SE working normally.
- Edge signed into the intended xCloud account.
- No HidHide or other controller-hiding rule active for the Ally controller.

Record before starting:

- Ally Bindings build/commit: `________________`
- Windows build: `________________`
- Armoury Crate SE version: `________________`
- ViGEmBus version: `________________`
- Existing controller-hiding software/rules: `none / details: ________________`

## Stage 0 — establish the fail-open baseline

1. Leave **full virtual-controller remapping** off.
2. Exit Ally Bindings completely.
3. Confirm the built-in controller operates Edge/xCloud and Command Centre.
4. Reopen Ally Bindings and confirm **Backend: Preview**.
5. Confirm **Restore default** selects Default without changing ASUS hardware.

**Pass:** physical controls work before the virtual path is enabled.

## Stage 1 — assign distinct paddle signals in Armoury

Use Armoury's own on-screen/virtual keyboard; no physical keyboard is required.

1. In the Armoury profile used for Edge/xCloud, assign **M1 → F12**.
2. Assign **M2 → F11**.
3. Clear both secondary paddle assignments.
4. Save in Armoury.
5. Do not change ASUS mappings from Ally Bindings.

Record a screenshot of the two Armoury assignments.

## Stage 2 — enable the complete mirror

1. Launch Ally Bindings.
2. Open **Capture & update → Safety and startup**.
3. Enable **full virtual-controller remapping** and save.
4. Confirm the status names a **Virtual Xbox 360** backend and a pinned physical XInput index.
5. Do not start Edge yet. Exercise every ordinary control in Ally Bindings:
   - D-pad;
   - A/B/X/Y;
   - LB/RB;
   - LT/RT with analogue travel;
   - both stick clicks;
   - both stick axes;
   - View/Menu.

**Pass:** the app remains controller-navigable and reports no virtual or paddle-hook fault.

## Stage 3 — verify profile transforms

Create two enabled test profiles using the controller UI:

- **Mirror:** no remaps; M1 → A; M2 → B.
- **Swap:** A → X; X → A; M1 → Right Trigger; M2 → Left Bumper.

Then:

1. Select **Mirror**.
2. Hold/release **View + Menu** to rotate to **Swap**; stop for the configured commit delay.
3. Verify the overlay and backend status agree on the active profile.
4. While holding a physical control, change profiles and verify the held output is rerendered rather than sticking.
5. Disconnect and reconnect the controller; verify output neutralises during the disconnect and resumes only from the pinned slot.

**Pass:** complete standard-state mirroring and both paddle overlays follow the selected profile without stuck output.

## Stage 4 — Edge/xCloud coexistence gate

1. Start an xCloud title in Edge with **Mirror** selected.
2. Exercise every standard control and both paddles.
3. Repeat with **Swap**.
4. Check whether the title sees:
   - one usable controller;
   - duplicate actions;
   - a second-player controller;
   - a changed player slot after reconnect.
5. Open Command Centre over the session and verify its controller navigation still works.
6. Open Armoury after leaving the title and verify it still detects the controller.

Record:

- One usable controller in Edge: `PASS / FAIL`
- Duplicate action observed: `NO / YES — details: ________________`
- Second-player/slot issue: `NO / YES — details: ________________`
- Command Centre functional: `PASS / FAIL`
- Armoury functional: `PASS / FAIL`

**Decision:**

- If coexistence is clean, keep physical hiding out of the product.
- If Edge receives two controllers or the wrong slot, mark this architecture **blocked**. Do not enable persistent HidHide; the next design must first prove a released, process-owned, crash-cleared hiding session or choose a maintained alternative backend/topology.

## Stage 5 — controller-only recovery

With **Swap** active and Edge open:

1. Hold **View + Menu** for at least 750 ms.
2. Newly press and continuously hold **LT** for at least 1.25 seconds.
3. Confirm the overlay reports **Emergency bypass**.
4. Confirm virtual remapping is off and Default is selected.
5. Confirm physical standard controls still operate.
6. Release every control before attempting the gesture again.
7. Repeat once with small natural stick drift; then verify deliberate stick movement or an extra face button cancels the gesture.
8. Begin the gesture but release LT early. Confirm no profile selection commits accidentally.

**Pass:** recovery is possible without keyboard, touch, app focus or tray access and an aborted attempt does not rotate profiles.

## Stage 6 — lifecycle matrix

Re-enable the mirror before each row. After each disruptive action, verify physical controls first, then app state.

| Scenario | Expected result | Pass/fail |
|---|---|---|
| Normal app exit | Virtual target neutralises/disconnects; hook stops; physical remains | |
| Force-kill Ally Bindings | OS removes process-owned virtual target/hook; physical was never hidden | |
| Physical controller disconnect | Virtual report becomes neutral; profile cycle/recovery reset | |
| Reconnect | Only the pinned physical XInput slot resumes output | |
| Sleep/resume | No stuck virtual buttons; physical remains available | |
| Windows sign-out/restart | App starts according to preference; no hiding survives because none is configured | |
| ViGEm unavailable at startup | App persists virtual remapping off, selects Default, writes the emergency-disable latch and starts Preview | |
| Paddle-hook startup/runtime fault | Virtual output disconnects and setting turns off | |

A forced kill cannot run app cleanup. The safety property here is therefore architectural: the build creates no persistent hiding rule, and the virtual controller/hook are owned by the terminated process. Controller recovery and detected virtual/paddle faults additionally leave `%LOCALAPPDATA%\AllyBindings\virtual-remapping-disabled`, which prevents restart from reconnecting virtual output until a deliberate successful re-enable.

## Stage 7 — restore the Ally

1. Disable full virtual-controller remapping.
2. Select Default.
3. Exit Ally Bindings.
4. In Armoury, restore the preferred M1/M2 assignments or defaults.
5. Confirm Edge, Command Centre and Armoury behave as they did in Stage 0.
6. Keep ViGEmBus only if it was already intentionally installed for another application; Ally Bindings does not own or uninstall it.

## Evidence and verdict

Save:

- the Armoury M1/F12 and M2/F11 screenshot;
- Ally Bindings backend/status screenshots before enable, while live, and after recovery;
- the completed coexistence and lifecycle tables;
- any Edge second-player/duplicate-input evidence;
- redacted diagnostics if a runtime fault occurs.

Final verdict:

- Full mirror: `PASS / FAIL`
- Paddle overlay: `PASS / FAIL`
- Controller-only recovery: `PASS / FAIL`
- Edge single-controller coexistence: `PASS / FAIL`
- Command Centre/Armoury compatibility: `PASS / FAIL`
- Safe to proceed without HidHide: `YES / NO`

A release candidate requires every verdict to pass, including `Safe to proceed without HidHide: YES`. If coexistence fails, development returns to topology design rather than applying persistent hiding.

## Release approval evidence

Public release CI is fail-closed until reviewed physical evidence is committed at
`docs/evidence/full-virtual-controller-release-approval.json`. Start from the adjacent
`.example.json`, record the exact physically tested commit and environment versions,
set every verdict above to its passing value, and include the SHA-256 of the redacted
evidence bundle. The tested commit must be an ancestor of the tagged release commit.
The approval file is a release control, not a substitute for retaining the underlying
screenshots, tables and diagnostics.
