# macOS PKG Root Layout and CI Validation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Amend PR #67 so release packages use the explicit root layout while unsigned Intel CI does not fail on a known hosted-runner limitation.

**Architecture:** Keep package construction unchanged: the default `package-pkg.sh` path creates a root package containing `Applications/Roblox Account Manager.app`. Separate package-format validation from host-environment validation: both architectures exercise root and component packages on disposable target volumes, unsigned host-root installation runs only on ARM64, and the signed release workflow owns both-architecture host-root coverage.

**Tech Stack:** Bash, macOS `pkgbuild`/`installer`/`pkgutil`, GitHub Actions, GitHub CLI, .NET 8 contract tests.

**Spec:** `docs/superpowers/specs/2026-08-20-macos-pkg-root-layout.md`

## Global Constraints

- Preserve the branch policy: use `fix/` naming, never `agent/` or `codex/`.
- Stage only files belonging to PR #67; never stage `artifacts/` or `release-breaking/`.
- Keep component packages as isolated-volume compatibility fixtures, not published artifacts.
- Do not claim the unsigned Intel host-root case is fixed; it is an environment limitation.
- Run the two .NET test projects sequentially because parallel builds contend for the shared Core output assembly.
- Do not merge until PR checks are green and the rebuilt package passes manual x64 VM validation.

---

### Task 1: Reconfirm the failing boundary

**Files:** Read `.github/workflows/macos-validation.yml`, `build/macos/smoke-install-pkg.sh`, `build/macos/smoke-install-root-pkg.sh`, and `build/macos/package-pkg.sh`.

**Interfaces:** Consume PR #67's failed macOS compatibility run; produce evidence that isolated x64 installation succeeds while unsigned Intel host-root installation fails.

- [ ] Inspect the failed job with `gh run view 32362978348 --repo CodySimonds65/Roblox-Account-Manager --job 96406345232 --log-failed`.
- [ ] Confirm the log shows root payload layout, successful isolated x64 root/component receipt-repair tests, then host installation success followed by an absent `/Applications/Roblox Account Manager.app`.
- [ ] Preserve this as the red baseline; do not change package construction in this task.

---

### Task 2: Correct unsigned validation coverage

**Files:** Modify `.github/workflows/macos-validation.yml:60-78` and `.github/workflows/macos-unsigned-release.yml:149-163`.

**Interfaces:** Consume the root artifact `$pkg` and existing isolated-volume tests; produce green unsigned validation for both architectures without treating the Intel hosted runner as a real-machine result.

- [ ] Run the baseline contract check: assert that validation currently calls `smoke-install-root-pkg.sh "$pkg"` without an Intel skip; expect a nonzero `RED` result.
- [ ] Keep both isolated-volume calls unconditional: `bash build/macos/smoke-install-pkg.sh "$pkg" "$newer_pkg"` and `bash build/macos/smoke-install-pkg.sh "$component_pkg" "$component_newer_pkg"`.
- [ ] In `macos-validation.yml`, wrap `bash build/macos/smoke-install-root-pkg.sh "$pkg"` in `if [[ "${{ matrix.rid }}" == "osx-arm64" ]]; then ... else ... fi`; emit `Skipping unsigned host-root PKG smoke on Intel; isolated target-volume coverage passed.` in the Intel branch.
- [ ] In `macos-unsigned-release.yml`, retain root publication via `bash build/macos/package-pkg.sh --unsigned "$app" "$pkg" "${{ matrix.rid }}" "$package_version"` and apply the same ARM64-only host-root condition to `release_pkg`.
- [ ] Verify the green contract: no unsigned publish command contains `--component`, both unsigned workflows contain the explicit Intel skip, validation still host-tests the root `$pkg` on ARM64, and `git diff --check` passes.
- [ ] Commit only these two workflow files as `ci: scope unsigned macOS host smoke to arm64`.

---

### Task 3: Verify signed release coverage remains authoritative

**Files:** Read `.github/workflows/macos-release.yml:145-174`, `build/macos/package-pkg.sh:244-313`, and `build/macos/smoke-install-root-pkg.sh:21-55`.

**Interfaces:** Consume signed Developer ID identities; produce both `osx-arm64` and `osx-x64` signed release jobs using root packages and host-root smoke.

- [ ] Confirm the signed workflow invokes `package-pkg.sh "$app" "$pkg" ...` without `--component`.
- [ ] Confirm the signed matrix still contains `macos-15`/`osx-arm64` and `macos-15-intel`/`osx-x64`.
- [ ] Confirm signed release host-root smoke remains unconditional; the unsigned Intel skip must not be copied into this workflow.
- [ ] Confirm `package-pkg.sh` continues to enforce payload `Applications/Roblox Account Manager.app`, `install-location="/"`, and no `Scripts` directory.

---

### Task 4: Run verification and update PR #67

**Files:** Verify the three macOS workflow files; do not stage unrelated artifacts.

**Interfaces:** Consume the focused CI correction on `fix/macos-pkg-root-layout`; produce a pushed PR revision with green checks.

- [ ] Run `git diff --check` and `git status --short --branch`; confirm only intended workflow files are modified and `artifacts/`/`release-breaking/` remain untracked.
- [ ] Run sequentially: `dotnet run --project tests/RobloxAccountManager.Core.Tests/RobloxAccountManager.Core.Tests.csproj -c Release`, then `dotnet run --project tests/RobloxAccountManager.Platform.MacOS.Tests/RobloxAccountManager.Platform.MacOS.Tests.csproj -c Release`.
- [ ] Push the amendment with `git push`.
- [ ] Watch PR checks with `gh pr checks 67 --repo CodySimonds65/Roblox-Account-Manager --watch`.
- [ ] Require the unsigned x64 job to pass isolated root/component tests, skip only host-root smoke, and reach artifact upload.
- [ ] Do not merge if any required check remains red.

---

### Task 5: Manually validate the rebuilt x64 package before merge

**Files:** Rebuilt `RobloxAccountManager-<version>-osx-x64-unsigned.pkg` or signed `.pkg`; reference `build/macos/verify-installed-pkg.sh`.

**Interfaces:** Consume the package from the green workflow; produce direct Intel VM evidence for clean install and receipt-only repair.

- [ ] On the Intel VM, confirm `pkgutil --payload-files <pkg>` contains `Applications/Roblox Account Manager.app` and expanded `PackageInfo` contains `install-location="/"`.
- [ ] Install the package and verify `test -d '/Applications/Roblox Account Manager.app'`, the executable is executable, `pkgutil --pkg-info io.github.codysimonds65.roblox-account-manager` reports the expected version, and `open '/Applications/Roblox Account Manager.app'` launches it.
- [ ] In the disposable VM, remove only the exact app bundle with `sudo rm -rf -- '/Applications/Roblox Account Manager.app'`, retain the receipt, install the newer package, and verify the bundle and executable return.
- [ ] For signed packages, run `spctl --assess --type install --verbose=2 <pkg>`; for unsigned packages, explicitly approve only inside the test VM.
- [ ] Merge only after CI is green and both Intel VM scenarios pass. If the root package fails on the VM, investigate the signed/unsigned trust boundary rather than reverting to component packages.

---

## Self-review checklist

- [x] Separates the valid root-package change from the invalid unsigned Intel host-runner assertion.
- [x] Retains component compatibility tests on isolated volumes.
- [x] Retains both-architecture host-root coverage for signed releases.
- [x] Makes the unsigned Intel skip explicit and observable.
- [x] Includes red/green checks, sequential local tests, PR checks, and manual x64 VM acceptance.
- [x] Excludes unrelated untracked artifacts.
