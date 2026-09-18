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

### Issue #3 implementation note

M2 uses dependency-free Task Scheduler COM traversal and direct, read-only SCM
queries. Fixture tests cover nested/hidden enumeration inputs, multiple and
unsupported actions, malformed XML/binary evidence, automatic-delayed services,
elevation transitions, and partial access denial. The Windows CI smoke test
executes the native probes but cannot prove all protected scopes were readable;
only the emitted per-scope observations make that claim. No Windows-native local
test result is claimed by the Linux .NET SDK container run.

### Issue #4 implementation note

M3 uses an injectable `ISignatureVerifier` and a separate injectable
`IWinVerifyTrust` boundary. Native verification holds stable root-to-parent
directory handles that deny write/delete sharing plus one read-only-shared local
file handle through metadata, SHA-256 hashing, trust evaluation, and the cache
decision. It caches by normalized path + native file identity + size +
last-write time + content hash, closes WinTrust state in `finally`, and retains
signer subjects only for successful signed results. Only
`TRUST_E_NOSIGNATURE` is affirmative unsigned evidence; malformed or unfamiliar
trust results, including `TRUST_E_FAIL`, fail closed to `unverified`.

Network/device paths, mapped remote drives, relative paths, and paths traversing
reparse points are refused before trust evaluation. `WTD_REVOKE_NONE`,
`WTD_REVOCATION_CHECK_NONE`, and `WTD_CACHE_ONLY_URL_RETRIEVAL` enforce the
documented local-only boundary. Per-target results prevent multi-action entries
from being represented by a misleading publisher or definitive mixed status.
Windows CI verifies an embedded signature on `kernel32.dll`; non-Windows hosts
explicitly skip that integration and cannot establish native success.
The JSON envelope extensions are additive: existing readers must ignore unknown
properties. Writers emit canonical `invalidSignature`, while readers continue
to accept the legacy `invalid` signing value.

### Issue #6 implementation note

M4 quarantine uses an `IQuarantineStrategy` per source kind behind a
journal-first coordinator. The strategy `Plan` performs live re-reads and
returns read-only (never throws) when an exact restore cannot be described;
the coordinator commits the record via `IJournal.Begin` before calling
`Execute`, so a journal failure performs zero mutation. Mutations verify their
post-state by re-reading and fail closed otherwise (with registry rollback of
the copied value where a half-state occurred). The `ServiceStartType`
enum is journaled verbatim; only `Automatic`/`AutomaticDelayed` are accepted,
and the delayed-auto flag round-trips through
`ChangeServiceConfig2(SERVICE_CONFIG_DELAYED_AUTO_START_INFO)`.

Machine-scope work uses single-action elevation: the parent journals, then
re-launches the same executable elevated with `run-record <id>
--journal <path>`; the child performs and durably records the whole mutation,
so decline/crash cannot yield a partial write (the record stays `Pending`,
which `journal` surfaces as unresolved state — never clean). Elevated child
outcomes are decided from the durable journal, not the process exit signal.

Round-trip contract tests per strategy run on all OSes against injected
probes, temp-dir, and SQLite fixtures (quarantine → restore → byte/state-equal
original), plus declined-elevation, journal-failure, drift, and external-
takeover paths with injected faults. Windows CI additionally executes the
native registry write boundary and a full HKCU Run strategy round-trip on a
unique sandbox value it removes afterwards; the native task/service write
probes compile but their round-trips require elevated machine scopes and are
manual-bench items — CI never claims them. No Windows-native local test result
is claimed by the Linux .NET SDK container run.

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
