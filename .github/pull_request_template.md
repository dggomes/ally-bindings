## What changed

<!-- Explain the user-visible change and why it is needed. -->

## Risk and rollback

<!-- Call out HID/device/update/configuration risks and the rollback path. -->

## Verification

<!-- List exact commands and physical Windows/Ally checks actually run. -->

- [ ] `dotnet format AllyBindings.sln --verify-no-changes`
- [ ] `dotnet test AllyBindings.sln --configuration Release`
- [ ] Relevant safety/package scripts pass
- [ ] User-visible changes are documented under `CHANGELOG.md` → Unreleased
- [ ] No captures, configuration files, credentials or private diagnostics are included
- [ ] Hardware-dependent claims say what physical device/firmware was tested, or explicitly state that validation is pending
