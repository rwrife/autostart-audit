# Autostart Audit

**Pitch:** A local-first Windows utility that takes inventory of everything that starts at boot or login, shows signed publisher and evidence for each entry, and lets you quarantine and restore changes safely — no accounts, no cloud.

## Overview

Windows starts a lot of software you never consciously approved: updaters, launcher helpers, sync agents, "experience optimizers", and the occasional thing you did not install at all. Autostart Audit gives you one honest, readable inventory of everything that runs at boot or login, explains where each entry comes from, tells you who signed the executable (or that nothing signed it), and lets you disable/re-enable entries with full undo — without silently "optimizing" anything for you.

Autostart Audit is an **audit and quarantine tool**, not a cleanup product. It never decides what is safe; it shows evidence and lets you decide, with every change reversible.

## Motivation

- Built-in Task Manager startup review only covers the two classic Run-key locations — not scheduled tasks, services, startup-folders, or the other RunOnce/StartupApp registry paths.
- Existing sysinternals-style tools list entries but are read-only or offer no undo and no evidence-oriented presentation.
- People who inherit or maintain shared/family PCs need a way to see "what starts on this machine and why" and to *safely* remove the ones they do not want, with an audit trail of what was changed and how to put it back.

## Target users

- Windows 10/11 power users and home technicians who maintain their own or family machines.
- IT-support helpers who need explainable startup evidence for remote guidance.
- Security-hygiene-minded users who periodically want a signed-publisher review of auto-start code.

## Concrete use cases

1. **New-machine checkup.** After buying or rebuilding a PC, run a scan, review every auto-start entry grouped by publisher, and quarantine the vendor updaters you do not want.
2. **Slow-boot investigation.** Sort entries by startup impact and disabled-scope, quarantine suspects one at a time, and compare boot behavior with reversible toggles.
3. **Family PC maintenance.** Snapshot the machine's auto-start inventory, quarantine unknown-publisher entries, and keep a local change journal you can review or undo later.
4. **Periodic hygiene review.** Re-scan monthly; the app highlights entries that appeared since the last snapshot so new auto-starters stand out.

## How to use (intended end-to-end workflow)

1. Launch Autostart Audit (portable exe, no install required).
2. It performs a read-only scan of all supported auto-start sources and presents a table: entry name, enabled state, scope (machine/user), source (Run key, startup folder, scheduled task, service, etc.), target path, signing status, and publisher.
3. Filter or sort by signing status, publisher, or recently-changed-since-last-snapshot.
4. Select entries and choose **Quarantine** (disables the entry via a mechanism appropriate to its source) — every quarantine is recorded in a local change journal with the original value needed for exact restore.
5. **Restore** any entry from the journal with one action.
6. Export a redacted JSON/Markdown report for your own records or to share with someone helping you.

## MVP feature list

- Read-only scan of: HKLM/HKCU `Run` and `RunOnce` keys, user and common Startup folders, scheduled tasks that run at logon/boot, and services set to Automatic (with clear separation between the two).
- Per-entry evidence: target path existence, Authenticode signature status and signer name (or `unsigned` / `signature invalid`), hive/scope, and source category.
- Snapshot diff: what appeared/disappeared/changed since the last saved snapshot.
- Quarantine & restore with a durable local change journal; app refuses to quarantine anything it cannot precisely restore.
- Report export (JSON + Markdown) with optional path redaction.
- Local SQLite storage; no network traffic in MVP (publisher info comes from local Authenticode verification only).

## Non-goals

- No "startup score", one-click optimize, or automated recommendations.
- No deletion of files or registry values — quarantine disables and is always reversible; nothing is uninstalled.
- No real-time monitoring, resident guard, or blocking of new entries.
- No driver, shell-extension, context-menu, or browser-extension auditing in MVP.
- No scheduled-task *editing* beyond quarantine/restore of whole tasks; no service reconfiguration beyond start-type quarantine/restore.
- No macOS version in MVP (Windows-only; macOS noted as a possible later track).
- No cloud sync, accounts, telemetry, or "in the cloud" anything.

## Privacy, permissions, and data storage

- **Local-first:** all scanning and signing verification happens on-device via Win32 APIs. The app performs no outbound network requests in MVP.
- **Storage:** one SQLite database under `%LOCALAPPDATA%\autostart-audit` holding snapshots, the change journal, and app settings. Deleting the folder removes all app data.
- **Permissions:** scanning Run keys and startup folders requires no elevation. Reading scheduled tasks, services, and HKLM entries works best elevated; without elevation those sources are shown with an explicit `not visible without elevation` state — never silently omitted or reported as absent. Quarantining machine-scope entries prompts for elevation (single-operation UAC), user-scope entries do not.
- **Sensitive data:** target paths are shown in-UI but exports redact user-profile path prefixes by default. No serials, MACs, or account identifiers are collected.
- **No telemetry.** Crashes produce a local log file only.

## Current status and milestones

**Status:** the headless .NET 8 scan engine and CLI implement the M1/M2/M3
sources plus local Authenticode evidence and the M4 snapshot store + diff
engine. The desktop UI, quarantine/restore journal, and packaging remain
future milestones.

1. M1 — project skeleton, CI, scan engine for Run keys + startup folders (read-only).
2. M2 — scheduled tasks + services scanning, elevation-aware capability states.
3. M3 — Authenticode verification and publisher presentation. **Implemented.**
4. M4 — snapshot diff (store, migration stub, new/removed/changed engine) **implemented**; quarantine/restore journal pending.
5. M5 — export, packaging (portable + MSIX), accessibility pass.

## Scan and signature coverage and limits

- Scheduled tasks are read through Task Scheduler 2.0 COM. The scan walks the
  root and every nested folder and requests hidden folders/tasks. Tasks with a
  `LogonTrigger` or `BootTrigger` are included, including disabled tasks. Every
  `Exec` action contributes its command as target evidence; other action kinds
  remain visible as unsupported evidence instead of being discarded. The raw
  task XML, scheduler state, enabled state, and trigger summary are retained.
- Services are read through the Service Control Manager with enumerate and
  query-config access only. `Automatic` and delayed-automatic services retain
  service name, display name, current state, exact binary command line, and
  normalized start type. If delayed-start configuration is unreadable, the known
  automatic entry stays visible as `automatic-delay-unknown`. Ambiguous unquoted
  paths and malformed commands retain raw evidence with an unknown target rather
  than guessing an executable. Manual, disabled, boot, and system-start drivers are
  outside this source's inventory.
- Both native probes preserve entries from readable scopes while reporting each
  denied or unknown folder/service configuration. An unelevated token makes the
  source explicitly incomplete because protected scopes may be invisible.
  Elevation removes only that uncertainty: individual failures still keep the
  source partial or denied.
- Entry identity is normalized source + native key/path + target executable
  path(s), never a friendly display name. Signature enrichment does not alter
  identity, observation health, or source capability.
- These two sources invoke only read operations. Their public scan interfaces
  expose enumeration, not task/service mutation.

### Local Authenticode evidence

- Every observed target is evaluated through `WinVerifyTrust` on Windows. The
  four statuses are `signed`, `unsigned`, `invalidSignature`, and `unverified`.
  A signer subject is emitted as the publisher only for `signed`; missing,
  locked, unreadable, malformed, non-local, and unknown native results stay
  `unverified`. In particular, `TRUST_E_SUBJECT_FORM_UNKNOWN` is not treated as
  proof that a file is unsigned.
- Verification is strictly local. Revocation checking is disabled and
  `WTD_CACHE_ONLY_URL_RETRIEVAL` is set, so the trust provider does not perform
  outbound CRL, OCSP, AIA, or other certificate retrieval. This means a result
  describes the currently available local Windows trust state, not fresh
  online revocation status or a security verdict. JSON and text output repeat
  this limitation on every scan.
- UNC/device paths and mapped remote drives are rejected before file reads.
  Paths traversing any reparse point are conservatively left `unverified`, even
  when the reparse point may resolve locally. Relative targets are also
  `unverified`; the scanner does not guess a working directory or search path.
- Results are cached by normalized path, file identity, file size, last-write
  timestamp, and a SHA-256 content hash read from the protected open handle.
  Stable root-to-parent directory handles deny write and delete sharing so path
  components cannot be mutated as reparse points, renamed, or deleted while the
  final file is opened. Those handles and the read-only-shared file handle
  remain held through hashing, WinTrust, and the cache decision. Any changed
  content or metadata invalidates the cached result.
- Each JSON entry includes authoritative `targetSignatures`. Entry-level
  `signing` is `single`-target or uniform evidence only. Different statuses, or
  different publishers across multiple signed targets, are explicitly marked
  with `signingAggregation: "mixed"`; the entry status becomes `unverified`
  and its publisher is omitted rather than inventing an aggregate publisher.
- The JSON envelope additions are backward-compatible extensions: consumers
  must continue to ignore unknown properties. New output canonically writes
  `invalidSignature`; deserialization also accepts the legacy `invalid` value.
  `signatureVerificationPolicy`, `signingAggregation`, and `targetSignatures`
  are additive fields rather than a schema-version replacement.

### Snapshot store and diff

- `scan --save` persists the scan as a snapshot in a SQLite database
  (`Microsoft.Data.Sqlite`) under `%LOCALAPPDATA%\autostart-audit` and prints
  the diff against the previous snapshot; `--json` wraps the result in a
  capture envelope, and `--store <path>` relocates the database. A plain
  `scan` never touches the store.
- Diffs classify entries as Added / Removed / Changed / Unchanged with
  before/after evidence. Change reasons are `target-path`, `signing-status`,
  `enabled-state`, and `display-renamed`. Matching uses normalized identity
  (source kind + scope + stable key + target paths), then pairs residual
  entries by native source key, so a display rename or a moved target path
  reads as one `changed` entry — never as an unrelated add plus remove.
  Display names are never identity inputs.
- The first capture against an empty store is reported as establishing a
  baseline; it deliberately yields no diff, because "everything is new"
  would misrepresent a first run as a mass change event.
- The database carries a schema version. A store written by a newer build is
  refused outright, and unknown older versions fail closed rather than being
  reinterpreted under the current shape.

## Development quickstart

- Stack: .NET 8 + WPF (Windows-only desktop), built with `dotnet build` and `dotnet test`.
- Build with `dotnet build AutostartAudit.sln -c Release` and test with
  `dotnet test AutostartAudit.sln -c Release`.
- Unit tests use synthetic source snapshots and injected file/trust boundaries
  for deterministic elevation, denial, malformed-input, trust-code, cache, and
  multi-target behavior. Windows CI separately verifies the embedded signature
  on `%SystemRoot%\System32\kernel32.dll` and emits a `BENCH-VERIFIED` line only
  after the live signed/publisher assertions pass. That test explicitly skips
  on Linux; a Linux skip is not native verification evidence.
- A passing hosted Windows integration test establishes that the native API and
  local policy worked for that OS file on that runner. It does not claim manual
  bench testing, current online revocation, full security verification,
  interactive WPF coverage, or visibility into every protected startup scope.

## License

MIT — see [LICENSE](LICENSE).
