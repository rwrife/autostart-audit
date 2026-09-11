# Autostart Audit — PLAN

## Scope

Windows-only desktop utility that:

1. Scans all supported auto-start sources read-only and renders an evidence-rich inventory.
2. Verifies Authenticode signatures locally for every entry's target binary.
3. Diffs the current scan against the last saved snapshot (new/removed/changed entries).
4. Quarantines (disables) user-selected entries with exact, journaled restore.
5. Exports redacted JSON/Markdown reports.

Everything except quarantine/restore is read-only. No deletion, no uninstalling, no "optimizer" logic, no network.

## Architecture

```
AutostartAudit.sln
├── src/
│   ├── AutostartAudit.Core        # domain + scan engine (no UI deps)
│   │   ├── Sources/               # RunKeySource, StartupFolderSource,
│   │   │                          #   ScheduledTaskSource, ServiceSource
│   │   ├── SignatureVerifier.cs   # WinTrust wrapper (Authenticode)
│   │   ├── Quarantine/            # per-source quarantine strategies +
│   │   │                          #   UndoJournal (record/restore contract)
│   │   ├── Snapshot/              # scan model, diff engine
│   │   └── Store/                 # SQLite (Microsoft.Data.Sqlite)
│   ├── AutostartAudit.Cli         # scan/export headless mode (testing & scripting)
│   └── AutostartAudit.App         # WPF UI, MVVM (CommunityToolkit.Mvvm)
├── tests/
│   ├── AutostartAudit.Core.Tests  # unit tests w/ registry+task fixtures
│   └── AutostartAudit.Integration.Tests # unelevated capability tests
└── docs/                          # evidence-model spec, quarantine contract
```

- **Scan engine:** each source implements `IAutoStartSource { Scan(ScanContext) -> IReadOnlyList<AutoStartEntry>; Quarantine/Restore }`. `ScanContext` carries an `ElevationState`; sources that cannot read their data unelevated return a **`Capability.Unsupported/Denied/Unknown`** marker rather than an empty list — a source that failed to read is never reported as "nothing found."
- **Evidence model:** `AutoStartEntry` = identity (source kind, scope, stable key), display name, target path(s), raw value snapshot, `SigningStatus {signed, unsigned, invalid, unverified(not-elevated/missing-file)}` + signer subject, and `ObservationHealth`. Identity matching across snapshots uses normalized source-key + target path, not display name (friendly names are not identities).
- **Quarantine strategies:** Run keys → value renamed to `<name>.aa-quarantined` with original stored verbatim; startup folder → file moved into app-managed folder with original path+attributes journaled; scheduled task → `Enabled=false` (previous state journaled); service → start-type captured to `Disabled`, journaled. Refuse-to-act rule: if the journal write or strategy precondition fails, the entry is untouched and an error is surfaced.
- **Signing verification:** `WinVerifyTrust` on target files; results cached by path+size+mtime; explicit `unverified` states for missing/locked/remote paths. No online certificate checks (no CRL/OCSP network access) — status is local chain evaluation only, disclosed in UI.
- **UI:** single-window WPF app — inventory grid (virtualized), source/status filters, snapshot-diff view, change journal view with per-row Restore, export dialog with redaction options. Keyboard accessible, screen-reader labels on all columns, no color-only status.

## Technology choices

| Choice | Rationale |
|---|---|
| .NET 8 + WPF | Windows-native APIs (registry, Task Scheduler COM, SCM, WinTrust) with first-class support; mature accessibility; single-file self-contained publish |
| Microsoft.Data.Sqlite | One local file DB, transactional journaling, zero server |
| CommunityToolkit.Mvvm | Low-boilerplate MVVM, well-tested |
| TaskScheduler NuGet (or COM interop) | Reliable folder-enumerated task scan with logon-trigger detection |
| No elevation by default | Least privilege; capability-aware UI instead of a full admin app |

## Milestones & dependency order

1. **M1 Skeleton + Run-key/startup-folder scan** (issues #1, #2) — solution, CI, Core scan engine for the two unelevated sources, CLI `scan --json`, headless-testable.
2. **M2 Full coverage + capability model** (issue #3) — scheduled tasks, services, elevation-aware `Unknown` states.
3. **M3 Signatures** (issue #4) — WinTrust verifier + cache + publisher presentation.
4. **M4 Snapshot diff + quarantine/restore journal** (issue #5) — the risky/valuable write path; contract tests mandatory.
5. **M5 UI + export + packaging** (issues #6, #7) — WPF surface, redacted exports, portable + MSIX packaging, accessibility pass.

## Testing strategy

- **Unit (unelevated, CI `windows-latest`):** source parsers against recorded fixtures (synthetic registry exports, XML task definitions, service snapshots); signing-status mapping with the WinTrust call behind an interface; quarantine strategies against temp-dir/registry-HKCU-sandbox fixtures with round-trip restore assertions.
- **Diff engine:** golden-file tests for new/removed/changed/renamed-entry classification.
- **Integration:** unelevated run asserting each source reports either data or explicit capability state (never silent-empty); machine-scope behaviors (services/HKLM quarantine, elevation prompt) are marked manual-bench and documented as such — CI never claims them.
- **Observation-truth boundary:** an unsignable, denied, or unreadable target is `unverified`/`unknown`, never "malicious" or "absent." Tests assert the three-state distinction.

## Packaging / distribution

- Portable single-file exe (self-contained, unsigned preview builds in CI artifacts).
- MSIX for managed installs later; code signing is an explicit open question (cost) — unsigned preview is the MVP default, documented honestly in README.

## Risks

| Risk | Mitigation |
|---|---|
| Quarantine leaves machine without needed startup item | Journal-first design, per-entry restore, "quarantine nothing on first run" default, batch size warning |
| Registry/task edge cases break exact restore | Refuse-to-act preconditions; round-trip tests per strategy; unsupported variants listed as read-only |
| Silent scan gaps look like clean machine | Capability model: `Unsupported/Denied/Unknown` rendered explicitly; footer always shows scan completeness |
| Elevation complexity | User-scope quarantines never need elevation; machine-scope prompts are single-action UAC, and failure degrades to clear error, never partial write |
| Antivirus flags behavior-heuristic tool | Deterministic per-op explanation, journal transparency, no persistence of the tool itself (no run-at-start entry by default) |

## Explicit non-goals

Auto-optimization/scoring, deletion/uninstall, real-time guarding, driver/shell/browser-extension auditing, macOS build in MVP, cloud sync, telemetry, online certificate revocation checks, and any claim of security "verification" beyond local signature chain status.
