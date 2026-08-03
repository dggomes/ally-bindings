# Built-in Armoury write capture

## What it is

Ally Bindings can run a temporary, target-scoped diagnostic capture to observe the M1/M2 HID feature reports that Armoury Crate sends to the embedded ROG Ally controller.

You do not need to install ProcMon, WinDbg, Frida, Wireshark, USBPcap, a driver or a Windows service. The capture helper is part of `AllyBindings.exe`.

This mode observes Armoury. It does not apply mappings itself and cannot enable the source-locked ASUS write backend.

## Before capture

1. Plug the Ally into power.
2. Open Armoury Crate SE and navigate to the controller M1/M2 assignment page.
3. Close games, launchers with anti-cheat components, G-Helper and Handheld Companion.
4. Export or photograph your existing M1/M2 assignments.
5. Leave Armoury running.

## Capture

1. Open **Capture & update** in Ally Bindings.
2. Choose **Capture Armoury M1/M2**.
3. Review the displayed ROG Ally identity and the explicit injection/process-risk disclosure.
4. Accept the one-time Windows administrator prompt. Nothing is installed.
5. In Armoury, apply the three configurations requested by Ally Bindings:
   - M1=A and M2=B;
   - M1=X and M2=Y;
   - Reset to Default.
6. Return to Ally Bindings and choose **Done** after each Armoury operation.
7. Keep the generated ZIP and its displayed SHA-256 together.

The app enumerates running processes matching nine exact allowlisted ASUS Armoury executable names, then temporarily extracts its embedded x64 capture DLL into a random protected directory. Capture is rejected if more than twelve candidates pass path, signature and identity verification; otherwise, the app may inject the DLL into every verified candidate because Armoury versions can distribute controller writes across different components. It records only exact target `5A D1` rear-mapping writes, then unloads the DLL and deletes the directory. A successful finish positively confirms helper exit and hook unload from every attached candidate.

## What is retained

The ZIP may contain private controller-configuration bytes:

- exact 50–64-byte `5A D1` rear-mapping buffers for ASUS VID `0x0B05`, PID `0x1B4C`;
- API kind, report length and API result;
- capture phase and per-phase ordinal, without raw timestamps or QPC values;
- allowlisted ASUS process name, without process ID, executable path or user-specific identifiers;
- bounded aggregate counts showing which covered HID APIs, categorical handle-validation outcomes and target filter stages were reached, without rejected bytes, hashes or exact nonmatching lengths;
- redacted device/model identity;
- manifest and file hashes.

It does not contain general USB traffic, keyboard input, XInput history, arbitrary process memory, device paths or non-target HID reports. Nothing is uploaded automatically.

## Failure states

If the native tap is unavailable, Ally Bindings shows the bounded rejection reason before starting anything else. The metadata-only, system-wide ETW fallback starts only after you explicitly choose **Start ETW fallback**; cancelling that dialog starts no ETW session.

- **No ASUS process found:** launch Armoury Crate SE, leave it open and retry.
- **Signature rejected:** update/repair Armoury rather than bypassing the check.
- **Target architecture rejected:** the diagnostic supports x64 Armoury processes only.
- **No matching writes:** keep Armoury open and apply each requested change; the process boundary may need further review.
- **TEARDOWN UNCONFIRMED:** restart Windows. Restarting Ally Bindings alone does not clear the persisted native-write barrier.
- **Cancelled:** partial artifacts are deleted and cannot be accepted as evidence.

Every result remains **REVIEW REQUIRED**. Captured bytes never unlock hardware writes automatically.
