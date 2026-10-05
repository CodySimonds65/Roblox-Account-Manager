# Windows UI refresh implementation plan

**Goal:** Make the existing WPF launcher feel like a restrained Windows utility,
with clear actions and more usable workspace at its supported minimum size.

**Architecture:** Keep the WPF windows, named controls, event handlers, stores,
WebView2 host, and native client host. Change the shared visual resources and
existing screen layouts. No dependency or native-input changes are needed.

**Design:** Neutral charcoal surfaces, subtle separators, four-pixel control
corners, Segoe UI text, and blue reserved for launch/save/install-from-URL.
Prefer a compact utility layout over the current dashboard-style cards; a full
WinUI migration would add deployment and host risk without improving these flows.
Retaining the current layout and changing only the accent would leave the
crowding and poor action hierarchy unresolved.

## Constraints

- Windows WPF implementation only; PR #90 review is recorded separately.
- Preserve selection, presets, launch queue, browser, client hosting, and settings behavior.
- Keep account deletion and plugin consent confirmations.
- Preserve existing user artifacts and use `fix/windows-ui-launch-recovery` for the combined Windows PR.
- Validate minimum and default window sizes, keyboard focus, and disabled states.
- Use rendered WPF layout to assess visuals; do not claim native Roblox runtime validation.

## Tasks

- [x] **1. Shared controls — `client/App.xaml`.** Replace purple palette with
  neutral surfaces and restrained blue. Make default buttons neutral and expose
  an explicit `PrimaryButton` style. Reduce checkbox internal padding, make
  corners consistent, and provide visible keyboard focus and disabled states.
  Check existing consumers and add the primary style to affirmative dialog actions.
- [x] **2. Launcher — `client/MainWindow.xaml`.** Narrow sidebar to 232 DIP,
  replace decorative identity/status badges with plain headings and descriptive
  text. Use a two-row preset toolbar with flexible columns, visible search hint,
  labeled preset tools, and a single launch action. Give the browser most of the
  available height while retaining the activity splitter. Put activity controls
  on a separate wrapping row so they cannot cover its title at minimum width.
- [x] **3. Dialogs — `client/SettingsDialog.xaml`, `client/PluginsWindow.xaml`,
  `client/PluginsWindow.xaml.cs`, `client/CompatibilityDialog.xaml`.** Use underlined
  settings tabs and compact option rows; move maintenance options into a named
  section. Add plugin empty states, wrap descriptions, and put installed actions
  on their own wrapping row. Render diagnostics as separator rows with semantic
  status text and wrapping details. Keep footer copy and actions in separate rows.
- [x] **4. Verification.** Build the WPF project in Release. Render the main
  window at 1380x860 and 1080x700, plus settings, plugins and diagnostics at default
  and minimum sizes. Inspect images and fix clipping, focus and hover issues.
  Run `pwsh -NoProfile -File build/test-all.ps1`, then independent code review and
  `git diff --check`. Record evidence and limitations in this plan.

## Outcome

Validated on 2026-10-05 on `fix/windows-ui-launch-recovery`, based on `origin/main`.
The separate macOS changes under PR #90 are excluded from this Windows PR.

- `pwsh -NoProfile -File build/test-all.ps1` passed all Core, Desktop, macOS
  scenario, and Windows tests (31 Windows tests), plus Windows platform and SDK builds.
- `pwsh -NoProfile -File build/verify-focus-safety.ps1` passed.
- `dotnet run --project build/WindowsUiPreview -- .` generated 11 previews;
  shared typography and launch-control layout checks passed. The main, settings,
  plugins, and compatibility previews were inspected at minimum supported sizes.
- Independent source review found no blockers; `git diff --check` passed.

The previews omit native browser/game hosts and window title bars. Plugin
fixtures cover catalog rows and the empty state, not installed-plugin actions.
Live four-account Roblox behavior remains pending confirmation on Test 4.
