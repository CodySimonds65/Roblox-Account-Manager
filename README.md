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

Requires Windows, the .NET 8 SDK or newer, and Microsoft WebView2 Runtime.

```text
build-client.cmd
```

Output: `release\RobloxAccountManager.exe`. The source is under `client\` and licensed under Apache 2.0.

### macOS development build

The cross-platform implementation is under `src/`. It requires macOS 14 or newer because each account uses a persistent, uniquely identified WKWebView data store. Build the shared contracts, macOS adapter, and Avalonia frontend with:

```text
dotnet run --project tests/RobloxAccountManager.Core.Tests/RobloxAccountManager.Core.Tests.csproj -c Release
dotnet build src/RobloxAccountManager.Platform.MacOS/RobloxAccountManager.Platform.MacOS.csproj -c Release
dotnet publish src/RobloxAccountManager.Desktop/RobloxAccountManager.Desktop.csproj -c Release -r osx-arm64 --self-contained true
bash build/macos/package-app.sh bin/Release/net8.0/osx-arm64/publish "Roblox Account Manager.app" 0.0.0
```

Use `osx-x64` for Intel. The development build is not a replacement for the Windows release until the real-Mac two/four-client matrix and signed, notarized PKG checks pass. Cloning or re-signing Roblox requires explicit consent; Accessibility is optional and only controls focus/tiling of external client windows.

The package step creates a normal macOS application bundle. Open `Roblox Account Manager.app` in Finder, or launch it from Terminal with:

```text
open "Roblox Account Manager.app"
```

Unsigned development bundles may require **System Settings → Privacy & Security → Open Anyway**. The release workflow publishes separate `osx-arm64` and `osx-x64` component PKGs signed with Developer ID Installer, notarized, stapled, and checksum-paired. The package has no installer scripts and installs the signed app under `/Applications`.

While Apple signing credentials are being configured, the repository also provides a manually triggered temporary unsigned release path. Its assets include `-unsigned` in the filename and are never presented as certified. Users must explicitly approve the installer and app through macOS **Privacy & Security → Open Anyway**. These packages are for testing only; use the signed workflow for normal distribution.

macOS launching is fail-closed until `RAM_TRUSTED_ROBLOX_TEAM_ID` is set to the 10-character Team Identifier captured from a verified official Roblox installation. Signed release packaging stores that identity in the signed app resources, separately from RAM's own Apple signing identity; an unconfigured development build can manage browser sessions and inspect clients but will not pass an authentication ticket to an app bundle.

## Settings

The top-right **Settings** window manages launcher options and Roblox engine/game settings across three scopes:

- **Global** defaults
- **Game** overrides
- **Per profile** overrides

Profile settings override game settings, and game settings override global settings. Selecting **Default** uses the next available lower-level value. Available controls include graphics quality, FPS, volume, MSAA, texture quality, scaling, and advanced flags.

Roblox updates may change or disable multi-instance behavior. Use the project only where permitted by Roblox and the applicable game rules.
