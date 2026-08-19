# RAM plugin suite

The launcher owns account identity, activity timestamps, HWND/process validation, authenticated plugin IPC, input arbitration, action routing, installation, and lifecycle supervision. RAM Macros, RAM OCR, and RAM AFK are separate medium-integrity processes. They receive only the capabilities explicitly accepted in the Plugins window.

## Foreground automation and safety

Roblox does not consume the legacy background `PostMessage` path for gameplay input. Production automation therefore uses one host-owned `ForegroundAutomationCoordinator` and the guarded `SendInput` injector. Macro, AFK, and OCR actions are serialized through one desktop-wide session: the selected client is foregrounded, its process/HWND/start-time identity is revalidated before every event, and the original foreground client/tab is restored once the batch finishes when Windows still permits it.

This is intentionally visible automation. The user may see a short focus/foreground change and mouse macros may move the cursor. A user Alt-Tab or click takeover cancels the session and RAM does not fight to reclaim focus. Stale/exited clients, denied activation, plugin disconnects, and shutdown fail closed; held-input cleanup is attempted only while the validated target remains safe.

Official plugins request `host.input.foreground.real`. Legacy `host.input.background` and `host.input.background.messages` remain wire-compatible but fail with `foreground-required`; they never upgrade to real input and no production `PostMessage` delivery remains. The automated gate is `build/verify-focus-safety.ps1`, which restricts activation to the coordinator and injection to the guarded injector while rejecting activation/input APIs elsewhere. Window arrangement continues to use `SWP_NOACTIVATE`.

A live acceptance run must use real Roblox clients: keep another app foreground, test two managed clients in order, verify user takeover cancellation, move/resize across mixed-DPI monitors, and record foreground transitions, cursor behavior, held-input release, and in-game consumption. A successful `SendInput` call proves delivery to Windows only—not a particular game action.

## Installation trust

Official catalog URLs require a pinned Ed25519 signature and an embedded manifest identity match. HTTPS sideloads require an explicit warning confirmation and per-capability consent. Packages are streamed with hard limits, checked for SHA-256, safe paths, symlinks, and manifest/package consistency, staged atomically, and retain one rollback version.

## Release gate status

The host, SDK, installer, foreground coordinator, action bridge, lifecycle supervision, and the three standalone plugin cores are implemented and covered by local build, test, and static gates. The official catalog points at future signed release assets; those assets are not published until the pinned signing key is provisioned and the live Roblox acceptance run passes. RAM OCR exposes the capture/matching boundary and trigger engine; a platform OCR adapter can be supplied without changing the foreground-session contract.

The remaining non-blocking polish is restoring minimized/maximized state on RESET and distributing GRID across multiple work areas. Neither item may introduce activation or a foreground-input fallback.

## Cross-platform manifests

Schema 1 remains Windows-compatible. Schema 2 selects an exact runtime entrypoint and accepts only `win-x64`, `osx-arm64`, and `osx-x64` keys:

```json
{
  "schemaVersion": 2,
  "id": "io.github.example.plugin",
  "name": "Example",
  "version": "2.0.0",
  "contractVersion": "1.0",
  "publisher": "Example",
  "description": "Portable plugin",
  "capabilities": ["host.accounts.read"],
  "entryPoints": {
    "win-x64": "windows/plugin.exe",
    "osx-arm64": "macos-arm64/plugin",
    "osx-x64": "macos-x64/plugin"
  }
}
```

A plugin without an entrypoint for the current RID stays visible but is unavailable. macOS does not silently substitute synthetic input, screen reading, or global input; those requests return the stable `platform-not-supported` result. Account snapshots may include `platform` and `windowIdentifier`; numeric Windows handles remain for legacy consumers.

## Release publishing

Launcher releases are published after a merged PR whose title starts with `release:` or whose labels include `release` (the existing `fix`, `feat`, `chore`, `minor`, and `major` conventions remain supported). Each RAM plugin repository has an independent tag/manual-dispatch workflow that builds `plugin.json`, `plugin.zip`, `plugin.sha256`, and `plugin.sig`; it fails closed unless the Ed25519 private/public signing secrets are configured and the public key matches the launcher trust anchor.
