# RAM plugin suite

The launcher owns account identity, activity timestamps, HWND/process validation, authenticated plugin IPC, input arbitration, action routing, installation, and lifecycle supervision. RAM Macros, RAM OCR, and RAM AFK are separate medium-integrity processes. They receive only the capabilities explicitly accepted in the Plugins window.

## Focus-safety gate

Production code is statically rejected if it contains `SendInput`, `SetForegroundWindow`, `BringWindowToTop`, or `AttachThreadInput`. Input is posted only to a freshly validated managed-client HWND. The validator checks `IsWindow`, process ID, process start time, minimized state, and client metrics immediately before each message. Window arrangement always uses `SWP_NOACTIVATE` and stores identity-qualified snapshots.

The automated gate is `build/verify-focus-safety.ps1`. A live acceptance run still must use real Roblox clients: keep another app foreground, move/resize clients across mixed-DPI monitors, test keyboard/mouse delivery, and record both foreground transitions and in-game consumption. A queued `PostMessage` is not treated as proof that Roblox consumed the input; an ignored message is reported and skipped.

## Installation trust

Official catalog URLs require a pinned Ed25519 signature and an embedded manifest identity match. HTTPS sideloads require an explicit warning confirmation and per-capability consent. Packages are streamed with hard limits, checked for SHA-256, safe paths, symlinks, and manifest/package consistency, staged atomically, and retain one rollback version.
