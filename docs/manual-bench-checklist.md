# Manual bench verification checklist — issue #7 (UI, export, packaging)

**These items are NOT covered by CI and must never be claimed by CI.** They
require a physical Windows 10/11 bench machine (and, for the elevation items,
an administrator account). Check each box only after performing it on the
bench; record date + machine OS build next to the box when filing evidence.

## Environment

- [ ] Windows 10 22H2 or Windows 11, unelevated standard user account
- [ ] `autostart-audit-app.exe` (portable, **unsigned preview build** from CI
      tag artifacts) copied to `%USERPROFILE%\`, run without installation
      - [ ] Windows SmartScreen note recorded (unsigned exe may prompt; this
        is expected for preview artifacts and disclosed in README)

## Keyboard-only scan → filter → quarantine → restore

- [ ] F5 starts a scan without touching the mouse; completeness footer updates
- [ ] Tab reaches: toolbar → filter box → filter combos → inventory grid →
      Quarantine button → tabs → journal grid → Restore button
- [ ] Arrow keys move inventory selection; filters narrow rows via keyboard only
- [ ] Narrator (or Accessibility Insights) reads column headers, row text
      statuses ("signed", "unverified", "denied"), and the completeness
      statement — status is never conveyed by color alone
- [ ] Scan-completeness footer stays visible on Inventory, Snapshot diff, and
      Change journal tabs

## Real quarantine round-trips (user scope, unelevated)

- [ ] Create a throwaway HKCU Run value (`wscript.exe` target is fine); scan,
      select it, Quarantine — value renamed to `<name>.aa-quarantined`,
      journal row shows executed/verified
- [ ] Restore from the journal tab — original value byte-identical afterwards
- [ ] Startup-folder `.lnk` quarantine: file moves into
      `%LOCALAPPDATA%\autostart-audit\quarantine`, restore returns it to the
      original path with attributes intact

## Elevation (machine scope) — manual evidence ONLY

- [ ] Select a machine-scope entry (HKLM Run value on a test VM, or an
      Automatic test service); Quarantine triggers a **single-action UAC
      prompt** naming this executable
- [ ] Accepting completes the mutation via the elevated `run-record` child;
      journal shows the verified outcome
- [ ] **Declining** the UAC prompt leaves the entry untouched and the journal
      record honestly `Pending` — the UI must show it as unresolved, never as
      "quarantined" and never as "clean"
- [ ] Machine-scope Restore prompts once and behaves the same way

## Export & redaction

- [ ] Ctrl+E → JSON export to a shared folder; open it and confirm every
      `C:\Users\<you>` path became `[redacted-user-profile]` (fields like
      RawValueSnapshot and Source-detail lines included)
- [ ] Toggle redaction OFF in the dialog flow and confirm raw paths appear —
      then confirm the status bar labels the file "REDACTION OFF"
- [ ] Markdown export renders readable tables + the local-only signature
      policy disclosure in a text editor

## Packaging

- [ ] Portable single-file exe from a `v*` tag artifact runs on a machine with
      **no .NET runtime installed** (self-contained claim)
- [ ] `%LOCALAPPDATA%\autostart-audit` holds journal.db + snapshots; deleting
      the folder removes all app data (local-first claim)

Anything that fails here is a defect for a follow-up issue — do not paper over
it by weakening a CI test. CI's fixture-backed smoke proves the wiring only;
this document is the sole evidence channel for the behaviors above.
