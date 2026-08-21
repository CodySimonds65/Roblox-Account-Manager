# macOS PKG Root Layout and CI Validation Spec

## Goal

Publish macOS packages with an explicit root-volume `Applications/` payload so
component-package receipt/relocation behavior cannot leave the app absent, while
keeping unsigned Intel CI checks aligned with the known GitHub-hosted runner
limitation.

## Requirements

1. Signed and unsigned release artifacts must use the default root-package form
   of `build/macos/package-pkg.sh`; component packages remain compatibility
   fixtures only.
2. Isolated target-volume installation tests must continue to run for both
   `osx-arm64` and `osx-x64`, covering the root package and the legacy component
   package's receipt-only repair behavior.
3. Unsigned host-root installation smoke tests must run on Apple Silicon but be
   skipped on the Intel hosted runner, because that runner can report successful
   unsigned installation without materializing the application in
   `/Applications`.
4. The signed release workflow must retain host-root smoke coverage for both
   architectures; it tests the package form users receive in normal releases.
5. PR checks must be green before merge. A rebuilt package must also be manually
   installed and checked on the x64 macOS VM, including a receipt-only repair
   scenario.

## Non-goals

- Do not remove component-package compatibility coverage.
- Do not treat the unsigned Intel hosted runner as proof that a signed package
  installs correctly on a real Intel Mac.
- Do not publish or replace release assets as part of the PR implementation.
