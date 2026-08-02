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
- Read-only M1/M2 snapshots are a separate, explicit, unelevated workflow. After positive model/interface confirmation, the app issues one bounded HidSharp `GET_FEATURE` request for ASUS report `0x5A` at baseline and after each prompted Armoury change. The snapshot service has no ETW/helper/pipe/elevation dependency and no call path to `SET_FEATURE`, controller writes or write-gate mutation. It revalidates the exact target before every read, reads every compatible interface once, accepts only descriptor lengths from 50–64 bytes, times out after three seconds and never retries. Exact successful report bytes are private controller-configuration diagnostics; bundles are labeled review-required and `hardwareUnlockEvidence=false`.
- USB ETW capture is an explicit, temporarily elevated in-app operation using Windows' built-in USB ETW providers with `FullDataBusTrace`. Before any self-elevation, the normal process holds its exact on-disk executable open without write/delete sharing for its lifetime, closing the replacement race even in a user-writable install folder. The USB event stream is system-wide while active, but no capture driver or raw ETL/PCAP is installed or retained; only exact bounded ASUS candidate reports plus bounded metadata-only UCX control-transfer provider/event/field/type/length/count shapes are written for private review. Generic transfer values are never serialized, and neither data class can unlock writes or clear recovery state.
- Capture bundles are private diagnostics and are never uploaded automatically. Only one ZIP is retained per successful capture; its displayed SHA-256 must be recorded independently for review because a file in user-writable storage is not immutable provenance.
- ASUS custom and recovery writes remain source-locked pending physical protocol validation.

The complete safety boundary is documented in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).
