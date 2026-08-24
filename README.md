# Roblox Account Manager

An open-source Roblox launcher for running multiple accounts with separate local login sessions. The supported release remains Windows while the new Avalonia/macOS frontend is validated on real Apple Silicon and Intel Macs.

## Download and use

[Download the latest `RobloxAccountManager.exe`](https://github.com/CodySimonds65/Roblox-Account-Manager/releases/latest/download/RobloxAccountManager.exe)

1. Add a profile and sign in on Roblox.
2. Select one or more profiles.
3. Choose a game preset or paste a Roblox game URL.
4. Click **Auto-launch selected**.

Profiles support groups, favorites, drag ordering, and multi-select launch queues. Game presets can be searched, edited, duplicated, imported, or exported; private-server links are supported.

The Windows x64 EXE includes .NET; Microsoft WebView2 Runtime is required. Updates are checksum-verified and installed after restart approval.

The client requests administrator approval once at startup; additional account launches do not trigger more UAC prompts.

## Privacy

- Credentials and `.ROBLOSECURITY` tokens are never collected or sent to the developer.
- Profiles, login sessions, and game presets stay on your computer.
- The client contacts Roblox, GitHub for updates, and Microsoft for Sysinternals Handle.
- Removing a profile clears its active local browser session.

## Build it yourself

Requires the .NET 8 SDK or newer. Windows builds also require the Microsoft WebView2 Runtime; macOS builds require macOS 14 or newer.

- **Windows:** Run `build-client.cmd`; the output is `release\RobloxAccountManager.exe`.
- **macOS:** Publish for `osx-arm64` or `osx-x64`, then package the app with `build/macos/package-app.sh`.
- **Source:** The supported Windows/WPF application is under `client\`. The Avalonia desktop under
  `src\RobloxAccountManager.Desktop` is the macOS frontend and intentionally does not run on Windows.
  Shared contracts and platform services remain under `src\`.

macOS support is experimental; use the Windows release for normal use. All source is licensed under Apache 2.0.

## Settings

The top-right **Settings** window manages launcher options and Roblox engine/game settings across three scopes:

- **Global** defaults
- **Game** overrides
- **Per profile** overrides

Profile settings override game settings, and game settings override global settings. Selecting **Default** uses the next available lower-level value. Available controls include graphics quality, FPS, volume, MSAA, texture quality, scaling, and advanced flags.

Roblox updates may change or disable multi-instance behavior. Use the project only where permitted by Roblox and the applicable game rules.
