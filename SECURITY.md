# Security policy

## Supported versions

Ally Bindings is currently a preview project. Security fixes are made on the latest release line and `main`; older preview builds may not receive patches.

| Version | Supported |
|---|---|
| Latest release / `main` | Yes |
| Older preview releases | No guarantee |

## Report a vulnerability privately

Please use GitHub's private vulnerability-reporting flow:

1. Open the repository's **Security** tab.
2. Choose **Report a vulnerability**.
3. Include affected version/commit, impact, reproduction steps and any proposed mitigation.

Do **not** attach raw USB captures, local configuration files, tokens, personal paths or device identifiers unless they are essential. If private diagnostic data is required, describe what exists first so a safe transfer method can be agreed.

Do not open a public issue for:

- a path that can send an unintended HID/controller-setting report;
- device-filter bypass or unexpectedly broad USB capture;
- updater integrity, path traversal or rollback failure;
- controller lockout, persistent physical-device hiding or unsafe recovery;
- credential or private-data exposure.

You should receive an acknowledgement through GitHub. Public disclosure and release timing will be coordinated after the issue is understood and a fix is available.

## Security model

- Normal operation installs no driver and requires no elevation.
- There is no account, telemetry, cloud sync or network listener.
- Update checks use the public GitHub Releases API and HTTPS asset downloads.
- Release asset SHA-256 verification detects corruption or mismatched downloads; releases are not yet Authenticode-signed.
- Passive capture is an explicit operation using a separately installed USBPcap driver, filtered to one confirmed USB device address.
- Capture bundles are private diagnostics and are never uploaded automatically.
- ASUS custom and recovery writes remain source-locked pending physical protocol validation.

The complete safety boundary is documented in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).
