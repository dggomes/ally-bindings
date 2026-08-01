# Contributing to Ally Bindings

Ally Bindings is a safety-sensitive controller utility. Contributions are welcome, but changes that touch HID transport, device filtering, virtual-controller output, physical-device hiding, updates or rollback receive stricter review than ordinary UI work.

## Before opening work

- Search existing [issues](https://github.com/dggomes/ally-bindings/issues) and pull requests.
- For behavior changes, open an issue first unless the fix is small and obvious.
- Never include controller captures, configuration files, user paths, credentials or other private diagnostics in a public issue.
- Do not enable ASUS writes or physical-device hiding without reproducible on-device evidence and a documented rollback path.

## Development setup

Requirements:

- .NET 8 SDK
- PowerShell 7 for packaging scripts
- Windows 10 2004+ or Windows 11 for the WPF application and Windows-only integration tests

```powershell
git clone https://github.com/dggomes/ally-bindings.git
cd ally-bindings
dotnet restore AllyBindings.sln
dotnet test AllyBindings.sln --configuration Release
./scripts/package.ps1
```

Core tests run on Windows, macOS and Linux. The WPF application can be cross-compiled with Windows targeting enabled, but packaging, update/rollback and USBPcap lifecycle behavior must be verified on Windows.

## Pull requests

1. Create a focused branch from `main`.
2. Add or update tests for behavior changes.
3. Run formatting, tests and the relevant safety scripts.
4. Keep generated build output, captures and local configuration out of Git.
5. Explain user-visible behavior, risk, rollback and verification in the PR description.
6. Keep hardware-dependent claims explicit: state whether they were tested on a physical Ally and include redacted evidence where safe.

Recommended preflight:

```powershell
dotnet format AllyBindings.sln --verify-no-changes
dotnet test AllyBindings.sln --configuration Release
./scripts/test-capture-safety.ps1
./scripts/package.ps1
```

## Safety rules

- Capture paths must remain passive and device-address filtered.
- Never silently fall back to whole-root-hub USB capture.
- Never inject into Armoury Crate, Xbox or games.
- Never mutate Armoury databases.
- Never install a driver automatically.
- Never hide the physical controller before a healthy output path and fail-open recovery are proven.
- Never claim a selected app profile was physically applied without read-back or end-to-end evidence.
- Preserve the permanent Default profile and independent keyboard recovery path.

## Style

- Use nullable reference types and existing C# conventions.
- Keep controller/protocol logic in `AllyBindings.Core` where practical and deterministic.
- Keep Windows process/UI orchestration in `AllyBindings.Windows`.
- Prefer explicit failure over a permissive fallback in device, update and rollback code.
- Update `CHANGELOG.md` under **Unreleased** for user-visible changes.

## Reporting security issues

Do not open a public issue for a vulnerability. Follow [SECURITY.md](SECURITY.md).
