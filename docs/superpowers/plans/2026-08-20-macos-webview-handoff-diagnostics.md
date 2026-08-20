# macOS WebView Handoff Diagnostics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Capture Roblox custom-scheme launches on the Avalonia/WKWebView route that PR #74 misses, while making the activity log prove whether the handoff and native launch stages were reached.

**Architecture:** Keep the existing trusted `RobloxNavigationGate` as the single security boundary. Route all macOS WebView notifications through one small capture helper; add `WebResourceRequested` as a fallback because Avalonia’s macOS adapter raises it before `NavigationStarted`/`NewWindowRequested`, and use the existing later event to cancel the external navigation. Emit only scheme/outcome metadata, never ticket-bearing URI contents.

**Tech Stack:** .NET 8, C#, Avalonia.Controls.WebView 12.1.0, WKWebView, console-style regression tests, macOS bundle/crash diagnostics.

**Spec:** The existing macOS private-server launch behavior and diagnostics requirements in `docs/superpowers/specs/2026-08-20-macos-private-server-launch.md`, extended by the observed Avalonia WebView resource-request route.

## Global Constraints

- Preserve trusted-origin and pending-launch gating; never accept a Roblox scheme without both.
- Keep ticket-bearing Roblox URIs out of activity logs, diagnostics, persistent state, and exception messages.
- Scope the `WebResourceRequested` fallback to macOS so the existing Windows WebView2 path is unchanged.
- Preserve single-shot completion when WebKit reports the same launch through multiple event routes.
- Preserve the fail-closed timeout and existing crash/session-log collection.
- Do not stage or modify the unrelated untracked `docs/superpowers/` files already present in the worktree except the new plan file.

### Task 1: Define the unified route decision and diagnostics contract

**Files:**
- Modify: `src/RobloxAccountManager.Desktop/Services/RobloxNavigationCapturePolicy.cs`
- Test: `tests/RobloxAccountManager.Desktop.Tests/Program.cs`

**Interfaces:**
- Consumes: `RobloxNavigationGate.Evaluate(Uri)` and nullable WebView requests.
- Produces: a route description helper that returns only safe metadata and a reusable decision path for navigation, new-window, and resource-request events.

- [ ] **Step 1: Write the failing tests**

Add assertions that a Roblox custom-scheme request is described without its ticket and that a rejected custom-scheme request reports the gate diagnostic code.

- [ ] **Step 2: Run the focused desktop test and verify it fails**

Run `dotnet run --project tests/RobloxAccountManager.Desktop.Tests/RobloxAccountManager.Desktop.Tests.csproj`.

Expected: compilation fails because the new safe route-description API is not present.

- [ ] **Step 3: Implement the minimal policy helper**

Add a helper that formats `route`, `scheme`, and `outcome` only; do not include `AbsoluteUri`, query, host, path, or exception text for Roblox custom schemes.

- [ ] **Step 4: Run the focused desktop test and verify it passes**

Run the same command and confirm the new assertions and existing launch-policy tests pass.

- [ ] **Step 5: Commit the policy/test change**

Use `git add` for only the policy and desktop test files, then commit with `test: cover macOS navigation route diagnostics`.

### Task 2: Capture the Avalonia macOS resource-request route

**Files:**
- Modify: `src/RobloxAccountManager.Desktop/Services/AvaloniaAccountBrowserSessionService.cs`
- Modify: `src/RobloxAccountManager.Desktop/Views/MainWindow.cs`
- Test: `tests/RobloxAccountManager.Desktop.Tests/Program.cs`

**Interfaces:**
- Consumes: the policy helper from Task 1 and `NativeWebView.WebResourceRequested`.
- Produces: `NavigationDiagnostic` notifications, single-shot pending completion, and macOS-only capture from `WebResourceRequested` before the existing cancellation events.

- [ ] **Step 1: Write the failing regression test**

Add a test that evaluates a trusted pending launch through the `web-resource` route and verifies the result is accepted, then verifies a second route cannot consume the same pending launch.

- [ ] **Step 2: Run the focused desktop test and verify it fails**

Run `dotnet run --project tests/RobloxAccountManager.Desktop.Tests/RobloxAccountManager.Desktop.Tests.csproj`.

Expected: compilation fails because the unified route-capture helper does not yet exist.

- [ ] **Step 3: Implement the minimal route wiring**

Subscribe to `WebResourceRequested` only when the session platform is macOS. Pass `args.Request.Uri` through the same policy as the other two events. Complete the pending task once, and leave the existing `NavigationStarted`/`NewWindowRequested` handlers responsible for canceling the subsequent external navigation.

Raise diagnostics for capture armed, route observed, accepted/rejected outcome, native-launch handoff, and timeout. Keep the messages metadata-only.

- [ ] **Step 4: Run the focused desktop test and verify it passes**

Run the desktop test project and confirm the resource-route and single-shot assertions pass.

- [ ] **Step 5: Commit the route fix**

Use `git add` for the service, main window, and desktop test files, then commit with `fix(macOS): capture WebView resource launch handoff`.

### Task 3: Verify native-stage diagnostics remain fail-closed

**Files:**
- Modify: `src/RobloxAccountManager.Platform.MacOS/MacRobloxDiagnostics.cs` only if a focused gap is found.
- Modify: `src/RobloxAccountManager.Desktop/Views/MainWindow.cs` only if stage logging needs a small adjustment.
- Test: `tests/RobloxAccountManager.Platform.MacOS.Tests/Program.cs` only if a focused regression is added.

**Interfaces:**
- Consumes: existing `MacRobloxDiagnostics.Collect`, crash-report matching, and bundle version reporting.
- Produces: a clear distinction between browser handoff timeout, native `/usr/bin/open` failure, missing bundle, missing process, and crash-report discovery.

- [ ] **Step 1: Add a failing diagnostic assertion only for an observed gap**

Use existing fixture-based diagnostics tests; add one assertion only if the implementation still fails to expose a stage that the VM log needs.

- [ ] **Step 2: Run the macOS platform test and verify the expected failure**

Run `dotnet run --project tests/RobloxAccountManager.Platform.MacOS.Tests/RobloxAccountManager.Platform.MacOS.Tests.csproj`.

- [ ] **Step 3: Implement the minimal diagnostic correction**

Preserve redaction and artifact behavior; do not broaden sensitive log collection.

- [ ] **Step 4: Re-run the macOS platform test and confirm it passes**

- [ ] **Step 5: Commit only if Task 3 changed files**

Use `git add` for the focused diagnostics/test files and commit with `test(macOS): distinguish handoff and native launch failures`.

### Task 4: Full verification and handoff

**Files:**
- No source changes unless verification exposes a regression.

- [ ] **Step 1: Run all console-style tests**

Run the Core, Desktop, Platform.MacOS, and SmokeTests projects with `dotnet run`.

- [ ] **Step 2: Build the release target**

Run `dotnet build RobloxAccountManager.sln --configuration Release --no-restore` if the solution exists; otherwise build the Desktop project and its dependencies in Release.

- [ ] **Step 3: Inspect the final diff and status**

Confirm no ticket-bearing URI appears in new log strings, no unrelated files are staged, and the pre-existing untracked docs remain untouched.

- [ ] **Step 4: Report verification evidence**

Report exact test/build commands and outcomes, plus the VM validation instruction: after clicking Play, the activity log must show `macos-capture-armed`, a `web-resource` route capture, and then either native launch verification or a concrete bundle/process/crash diagnostic instead of an opaque timeout.
