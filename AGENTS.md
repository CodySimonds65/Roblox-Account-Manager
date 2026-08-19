# Repository agent roles

The input-debugging work uses the following focused reviewers. These roles are
deliberate: runtime behavior and lifecycle safety must be checked independently
before a native-input change is considered complete.

- **Luna xhigh — native-input investigator:** own HWND hierarchy, top-level
  docking, activation/raw-input behavior, viewport positioning, and physical
  click diagnostics. Keep investigation read-only unless the primary agent
  explicitly assigns an implementation or test change.
- **Luna xhigh — runtime diagnostics investigator:** own empirical Roblox
  validation, DPI/integrity matrices, foreground/focus/capture traces, and
  reproducible child-versus-top-level A/B evidence. Add focused tests or
  diagnostics only in files assigned by the primary agent.
- **Sol medium — adversarial reviewer:** independently challenge the proposed
  fix for foreground theft, synthetic human input, stale PID/HWND reuse,
  incorrect owner restoration, raw-input loss, UAC/integrity mismatches, DPI
  coordinate errors, cursor/capture leaks, unsafe process termination, and
  exactly-one-visible-client regressions. Report blockers before merge.

All agents must preserve the repository branch naming policy: never create or
use a branch whose prefix is `agent/` or `codex/`; use a purpose-based prefix
such as `fix/`, `test/`, or `refactor/`.
