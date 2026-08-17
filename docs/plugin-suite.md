# RAM plugin suite

The launcher owns account identity, activity timestamps, HWND/process validation, authenticated plugin IPC, input arbitration, action routing, installation, and lifecycle supervision. RAM Macros, RAM OCR, and RAM AFK are separate medium-integrity processes. They receive only the capabilities explicitly accepted in the Plugins window.

## Focus-safety gate

Production code is statically rejected if it contains `SendInput`, `SetForegroundWindow`, `BringWindowToTop`, or `AttachThreadInput`. Input is posted only to a freshly validated managed-client HWND. The validator checks `IsWindow`, process ID, process start time, minimized state, and client metrics immediately before each message. Window arrangement always uses `SWP_NOACTIVATE` and stores identity-qualified snapshots.

The automated gate is `build/verify-focus-safety.ps1`. A live acceptance run still must use real Roblox clients: keep another app foreground, move/resize clients across mixed-DPI monitors, test keyboard/mouse delivery, and record both foreground transitions and in-game consumption. A queued `PostMessage` is not treated as proof that Roblox consumed the input; an ignored message is reported and skipped.

## Installation trust

Official catalog URLs require a pinned Ed25519 signature and an embedded manifest identity match. HTTPS sideloads require an explicit warning confirmation and per-capability consent. Packages are streamed with hard limits, checked for SHA-256, safe paths, symlinks, and manifest/package consistency, staged atomically, and retain one rollback version.

## Release gate status

The host, SDK, installer, broker, action bridge, lifecycle supervision, and the three standalone plugin cores are implemented and covered by local build, test, and static gates. The official catalog points at future signed release assets; those assets are not published until the pinned signing key is provisioned and the live Roblox acceptance run passes. RAM OCR currently exposes the capture/matching boundary and trigger engine; the Windows Graphics Capture and Windows OCR runtime adapter remains a release task rather than an assumption.

The remaining non-blocking polish is restoring minimized/maximized state on RESET and distributing GRID across multiple work areas. Neither item may introduce activation or a foreground-input fallback.

## Release publishing

Launcher releases are published after a merged PR whose title starts with `release:` or whose labels include `release` (the existing `fix`, `feat`, `chore`, `minor`, and `major` conventions remain supported). Each RAM plugin repository has an independent tag/manual-dispatch workflow that builds `plugin.json`, `plugin.zip`, `plugin.sha256`, and `plugin.sig`; it fails closed unless the Ed25519 private/public signing secrets are configured and the public key matches the launcher trust anchor.
