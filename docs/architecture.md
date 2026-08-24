# Application architecture

Roblox Account Manager has two deliberately separate desktop frontends:

| Platform | Supported frontend | Source |
| --- | --- | --- |
| Windows | WPF | `client/` |
| macOS | Avalonia | `src/RobloxAccountManager.Desktop/` |

The Avalonia application is macOS-only. It must reject normal startup on Windows rather than
advertising platform services that it does not compose. The Windows frontend may reuse libraries
from `src/RobloxAccountManager.Core`, but that does not make Avalonia the Windows UI.

`src/RobloxAccountManager.Platform.Windows` contains reusable Windows platform adapters. It is not
currently wired into the Avalonia desktop. `src/RobloxAccountManager.Platform.MacOS` provides the
native implementation used by the macOS frontend.

Shared business rules, persisted-data models, plugin contracts, and serialization formats belong in
platform-neutral libraries. UI controls, native transports, and operating-system integration remain
inside their frontend or platform projects.

## Pull-request release candidates

Every non-draft pull request produces a test-only GitHub draft containing an unsigned Windows x64
executable and unsigned `osx-arm64` and `osx-x64` packages from the same immutable head commit. The
build workflow has read-only repository permission. A separate `workflow_run` workflow may attach
the completed artifacts to a draft, but it never executes pull-request code or binaries.

Candidate drafts are never promoted. Production artifacts are rebuilt from the merged commit and
follow the normal signing and release gates. Superseded and closed-pull-request drafts are removed
automatically.

Before merging a ready pull request, wait for the `PR candidate build / bundle` job and the
`PR candidate draft ready` workflow to succeed. Use the resulting `PR #... candidate` draft to test
the Windows executable and both unsigned macOS packages as applicable, and confirm the downloaded
files against `SHA256SUMS.txt`. A new head commit invalidates that test result and creates a new
commit-specific candidate draft.
