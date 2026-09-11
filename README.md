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

**Status:** documentation & backlog only. No code, build, installer, or test results exist yet — see the issue backlog for the execution order.

1. M1 — project skeleton, CI, scan engine for Run keys + startup folders (read-only).
2. M2 — scheduled tasks + services scanning, elevation-aware capability states.
3. M3 — Authenticode verification and publisher presentation.
4. M4 — snapshot diff and quarantine/restore journal.
5. M5 — export, packaging (portable + MSIX), accessibility pass.

## Development quickstart (planned)

- Stack: .NET 8 + WPF (Windows-only desktop), built with `dotnet build` and `dotnet test`.
- Scaffold step will add a solution with `src/AutostartAudit` and `tests/`, plus a CI workflow running build + unit tests on `windows-latest`.
- Elevation-dependent code paths are verified by integration tests with explicit capability fixtures; unit tests run unelevated.

## License

MIT — see [LICENSE](LICENSE).
