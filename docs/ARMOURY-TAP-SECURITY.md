# Self-contained Armoury write-tap security contract

## Purpose

The Armoury write tap is a temporary, capture-only research instrument embedded in Ally Bindings. It observes the exact HID report buffers that an explicitly confirmed ASUS Armoury process attempts to send to the embedded ROG Ally controller. It replaces the need to install ProcMon, WinDbg, Frida, Wireshark, USBPcap or a kernel filter.

The tap is not part of normal profile operation. It grants no M1/M2 write authority, does not modify Armoury configuration, does not send a HID report and cannot unlock either source-level ASUS write gate.

## Shipped shape

- The public package remains one self-contained `AllyBindings.exe` plus documentation.
- A small x64 native DLL is compiled in CI and embedded as an application resource.
- The DLL is extracted only after explicit capture consent and Windows UAC approval.
- Extraction uses an atomically created cryptographically random per-session directory directly under the trusted Windows Temp root; reparse traversal is rejected. Its protected ACL is Administrators-owned, grants write/delete only to Administrators and Local System, and grants the initiating user read/execute only so a user-context Armoury target can load the DLL and read its one-time configuration. The helper hashes the embedded resource while extracting, verifies the extracted bytes in constant time, and holds the DLL without write/delete sharing through unload.
- The DLL is unloaded and the session directory is deleted before capture completion is accepted.
- No driver, Windows service, scheduled task, startup item, debugger, package manager or third-party application is installed.

## Preconditions

Every condition must pass or capture stops before injection:

1. Windows x64 process and x64 target.
2. Exact supported DMI model and ASUS manufacturer checks already used by the HID capture service.
3. An openable ASUS HID feature-report interface with VID `0x0B05`, PID `0x1B4C`, report ID `0x5A` and descriptor length between 50 and 64 bytes.
4. One or more running processes with an exact allowlisted executable name:
   - `ArmouryCrateSE.Service.exe`
   - `ArmouryCrate.Service.exe`
   - `ArmouryCrateSE.exe`
   - `AsusOptimization.exe`
5. Every selected executable has a valid embedded Authenticode signature with whole-chain, cache-only revocation checking; unknown/offline revocation state fails closed to ETW. The leaf requires exact ASUS common-name and organisation RDNs, code-signing EKU and digital-signature key usage.
6. Every selected process is under a trusted Windows, Program Files or Program Files (x86) root; reparse traversal is rejected, and Windows `AccessCheck` against an impersonation copy of the unelevated parent token must show no content-write, create, delete, DACL-change or ownership-change access on the executable or its ancestor chain up to that trusted root. The verified image file is hash-bound and held without write/delete sharing through injection and unload.
7. At most four exact candidates may be selected; excess or ambiguous process inventory fails closed.
8. The user confirms the displayed controller target and the injection/process risk before UAC and is told to close games and anti-cheat software.

PID `0x1B6E` is not an Ally controller identifier and must not pass the rear-button gate.

## Injection boundary

- Injection uses Windows `OpenProcess`, `VirtualAllocEx`, `WriteProcessMemory` and `CreateRemoteThread` with `LoadLibraryW`; there is no persistent service or driver.
- The elevated same-executable helper revalidates process ID, executable name, signature, architecture and creation time immediately before injection.
- A process restart, identity change, failed signature check, unknown architecture, partial remote write or uncertain module load fails closed.
- The tap has a static Windows `hid.dll` import for target-handle validation, so the Windows PE loader maps the system HID module before tap entry; CI proves it was absent before tap load and released after tap unload.
- The hook DLL may hook only:
  - `hid.dll!HidD_SetFeature`
  - the effective `WriteFile` implementation used by the target process.
- Hook installation failure is reported; it never falls back to broader API hooks or arbitrary process memory scanning.
- Original API arguments are never modified.
- The original API is called exactly once.
- The original return value and thread `LastError` are restored exactly.
- Hook callbacks must not retry, cancel, suppress or synthesize hardware operations.

## Report filter

A record may leave the target process only when all conditions pass:

- `HidD_GetAttributes(handle)` succeeds.
- Vendor ID is exactly `0x0B05`.
- Product ID is exactly `0x1B4C`.
- Buffer length is between 50 and 64 bytes inclusive.
- First byte is report ID `0x5A`.
- Second byte is rear-mapping command `0xD1`.
- Captured bytes are copied before the original call returns.

No device path, arbitrary process memory, keyboard input, XInput history or non-target HID payload may be retained. `WriteFile` calls used by the tap's own named-pipe transport fail the exact HID-handle gate and are not queued.

## Bounds

- Maximum candidate processes: 4.
- Maximum reports per process: 256.
- Maximum report bytes: 64.
- Maximum capture duration: 10 minutes.
- Maximum control-message length: 4 KiB.
- Maximum evidence JSON and ZIP sizes are explicitly bounded.
- Overflow, malformed records, version mismatch, sequence gaps, pipe impersonation or dropped reports mark the run inconclusive.

## IPC authentication

- Parent ↔ elevated-helper communication reuses the current-user/local-only same-executable pipe and mutual process-ID authentication.
- Each injected process gets an independent 32-byte capability from `RandomNumberGenerator.GetBytes(32)`; it is not derived from a filename or process identifier.
- The elevated helper creates each pipe with Local System, Administrators and initiating-user access only, plus a network SID deny rule.
- `GetNamedPipeClientProcessId` must equal the process ID revalidated for that tap instance.
- The native hello record includes protocol version, process ID and helper capability; the helper independently verifies x64 architecture before injection.
- A mismatched client is disconnected and invalidates the run.

## Staged evidence

The helper assigns reports to helper-acknowledged QPC phases:

1. Baseline.
2. Armoury applies M1=A / M2=B.
3. Armoury applies M1=X / M2=Y.
4. Armoury Reset to Default.

The bundle contains only:

- redacted target identity;
- allowlisted process name only; process identity, PID, path and executable signature remain helper-internal;
- API kind, report length, bounded report bytes, return status and error code;
- helper-side phase and per-phase ordinal; raw timestamps and QPC values are not exported;
- manifest, evidence JSON and hashes.

Every manifest permanently states:

- `hardwareWriteAttemptedByAllyBindings: false`
- `hardwareUnlockEvidence: false`
- `reviewRequired: true`
- `driverInstalled: false`
- `externalCaptureToolRequired: false`
- `rawSystemTraceWritten: false`

A capture can inform a later source change but cannot mutate `ArmouryProtocolValidation` at runtime.

## Teardown

- Completion and cancellation send an authenticated stop request to every tap.
- Every DLL holds a synchronized handle to the exact elevated helper process; helper death independently triggers hook disable/drain and `FreeLibraryAndExitThread` self-unload.
- Each DLL disables hooks, waits for active callbacks to drain, closes IPC and unloads itself.
- The helper positively verifies target-module unload and its own exit.
- The helper overwrites no target or application file and deletes the random extraction directory.
- Failure to confirm hook unload, helper exit or temporary-file deletion is prominently reported as `TEARDOWN UNCONFIRMED`; the run is rejected and a persisted write barrier survives app restarts. Only a Windows restart, which proves the affected process objects exited, clears it.
- A target-process exit is safe: the DLL and hooks disappear with the process and the capture records that termination.

## Release gates

CI must prove:

- the native DLL builds only for x64 with mitigations enabled (`/guard:cf`, `/DYNAMICBASE`, `/NXCOMPAT`, `/HIGHENTROPYVA`, `/CETCOMPAT` where supported);
- imports and exports match the narrow contract;
- no capture DLL exists as a loose release-package file;
- the resource exists inside the published executable;
- MinHook licence and version are bundled;
- source-level custom and recovery write gates remain false;
- no PID `1B6E` remains in the ASUS rear-button allowlist;
- synthetic target tests preserve buffers, return values and `LastError`, reject wrong VID/PID/report ID/length and unload cleanly;
- cancellation, spoofed-pipe and helper-crash tests fail closed.

Physical RC73XA validation is still required before treating captured packets as protocol authority.
