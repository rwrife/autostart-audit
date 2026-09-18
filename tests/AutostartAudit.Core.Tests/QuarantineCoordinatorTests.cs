using AutostartAudit.Core.Quarantine;
using AutostartAudit.Core.Scan;

namespace AutostartAudit.Core.Tests;

public class QuarantineCoordinatorTests
{
    [Fact]
    public void JournalCommitHappensBeforeMutation()
    {
        var log = new List<string>();
        var journal = new FakeJournal(log);
        var strategy = new RecordingStrategy(log);
        var coordinator = new QuarantineCoordinator(journal, new[] { strategy });

        var outcome = coordinator.Quarantine(QuarantineTestEntries.RunKey("user", "MyApp", @"C:\t.exe"));

        Assert.Equal(QuarantineStatus.Quarantined, outcome.Status);
        // The journal record is committed before the mutation runs, and the
        // executed transition is durable afterwards.
        Assert.Equal(new[] { "Plan", "Begin", "Execute", "MarkExecuted" }, log);
    }

    [Fact]
    public void JournalFailure_AbortsBeforeAnyMutation()
    {
        var log = new List<string>();
        var journal = new FakeJournal { FailBegin = true };
        var strategy = new RecordingStrategy(log);
        var coordinator = new QuarantineCoordinator(journal, new[] { strategy });

        var outcome = coordinator.Quarantine(QuarantineTestEntries.RunKey("user", "MyApp", @"C:\t.exe"));

        Assert.Equal(QuarantineStatus.Failed, outcome.Status);
        Assert.Null(outcome.RecordId);
        Assert.Contains("journal commit failed", outcome.Detail);
        // Refuse-to-act: Execute never ran.
        Assert.DoesNotContain("Execute", log);
    }

    [Fact]
    public void ReadOnlyPlan_PerformsNoJournalAndNoMutation()
    {
        var log = new List<string>();
        var journal = new FakeJournal();
        var strategy = new RecordingStrategy(log)
        {
            PlanOverride = _ => QuarantineDecision.ReadOnly("cannot describe exact restore"),
        };
        var coordinator = new QuarantineCoordinator(journal, new[] { strategy });

        var outcome = coordinator.Quarantine(QuarantineTestEntries.RunKey("user", "MyApp", @"C:\t.exe"));

        Assert.Equal(QuarantineStatus.ReadOnly, outcome.Status);
        Assert.Contains("exact restore", outcome.Detail);
        Assert.Empty(journal.CallLog);
        Assert.DoesNotContain("Execute", log);
    }

    [Fact]
    public void UnregisteredSource_IsReadOnly()
    {
        var coordinator = new QuarantineCoordinator(new FakeJournal(), Array.Empty<IQuarantineStrategy>());
        var outcome = coordinator.Quarantine(QuarantineTestEntries.RunKey("user", "MyApp", @"C:\t.exe"));
        Assert.Equal(QuarantineStatus.ReadOnly, outcome.Status);
        Assert.Contains("run-key", outcome.Detail);
    }

    [Fact]
    public void StrategyThrows_RecordIsFailedNotPending()
    {
        var log = new List<string>();
        var journal = new FakeJournal();
        var strategy = new RecordingStrategy(log)
        {
            ExecuteOverride = _ => throw new InvalidOperationException("boom"),
        };
        var coordinator = new QuarantineCoordinator(journal, new[] { strategy });

        var outcome = coordinator.Quarantine(QuarantineTestEntries.RunKey("user", "MyApp", @"C:\t.exe"));

        Assert.Equal(QuarantineStatus.Failed, outcome.Status);
        var record = journal.Get(outcome.RecordId!.Value)!;
        Assert.Equal(QuarantineResult.Failed, record.Result);
        Assert.Contains("boom", record.ErrorDetail);
    }

    [Fact]
    public void StrategyDenied_RecordFailedAndNoExecutedState()
    {
        var log = new List<string>();
        var journal = new FakeJournal();
        var strategy = new RecordingStrategy(log)
        {
            ExecuteOverride = _ => new MutationResult(MutationStatus.Denied, "requires elevation"),
        };
        var coordinator = new QuarantineCoordinator(journal, new[] { strategy });

        var outcome = coordinator.Quarantine(QuarantineTestEntries.RunKey("user", "MyApp", @"C:\t.exe"));

        Assert.Equal(QuarantineStatus.Failed, outcome.Status);
        Assert.Contains("elevation", outcome.Detail);
        var record = journal.Get(outcome.RecordId!.Value)!;
        Assert.Equal(QuarantineResult.Failed, record.Result);
    }

    [Fact]
    public void RestoreOfExecutedRecord_InvokesInverseAndMarksRestored()
    {
        var log = new List<string>();
        var journal = new FakeJournal();
        var strategy = new RecordingStrategy(log);
        var coordinator = new QuarantineCoordinator(journal, new[] { strategy });
        var outcome = coordinator.Quarantine(QuarantineTestEntries.RunKey("user", "MyApp", @"C:\t.exe"));

        var restore = coordinator.Restore(outcome.RecordId!.Value);

        Assert.Equal(RestoreStatus.Restored, restore.Status);
        Assert.Contains("Restore", log);
        Assert.Equal(JournalRestoreStatus.Restored, journal.Get(outcome.RecordId.Value)!.RestoreStatus);
    }

    [Fact]
    public void RestoreOfNeverExecutedRecord_IsRefused()
    {
        var log = new List<string>();
        var journal = new FakeJournal();
        var strategy = new RecordingStrategy(log)
        {
            ExecuteOverride = _ => new MutationResult(MutationStatus.Failed, "drifted"),
        };
        var coordinator = new QuarantineCoordinator(journal, new[] { strategy });
        var outcome = coordinator.Quarantine(QuarantineTestEntries.RunKey("user", "MyApp", @"C:\t.exe"));

        var restore = coordinator.Restore(outcome.RecordId!.Value);

        Assert.Equal(RestoreStatus.ReadOnly, restore.Status);
        Assert.DoesNotContain("Restore", log); // never invoked the inverse
    }

    [Fact]
    public void RestoreTwice_IsIdempotent()
    {
        var log = new List<string>();
        var journal = new FakeJournal();
        var strategy = new RecordingStrategy(log);
        var coordinator = new QuarantineCoordinator(journal, new[] { strategy });
        var outcome = coordinator.Quarantine(QuarantineTestEntries.RunKey("user", "MyApp", @"C:\t.exe"));
        coordinator.Restore(outcome.RecordId!.Value);

        var second = coordinator.Restore(outcome.RecordId!.Value);

        Assert.Equal(RestoreStatus.Restored, second.Status);
        Assert.Single(log, c => c == "Restore");
    }

    [Fact]
    public void MachineScopeElevationDeclined_EntryUntouched_RecordPending()
    {
        var log = new List<string>();
        var journal = new FakeJournal();
        var strategy = new RecordingStrategy(log, sourceKind: "service");
        var elevated = new FakeElevatedRunner { RunQuarantineResult = ElevatedRunStatus.Declined };
        var coordinator = new QuarantineCoordinator(journal, new[] { strategy }, elevated);

        var entry = new AutostartAudit.Core.Model.AutoStartEntry
        {
            SourceKind = "service",
            Scope = "machine",
            StableKey = "service|machine|mysvc",
            NativeKey = "MySvc",
            DisplayName = "MySvc",
            TargetPaths = new[] { @"C:\svc\s.exe" },
            RawValueSnapshot = @"C:\svc\s.exe",
            Signing = AutostartAudit.Core.Model.SigningStatus.Unverified,
            Observation = AutostartAudit.Core.Model.ObservationHealth.Ok,
        };

        var outcome = coordinator.Quarantine(entry);

        Assert.Equal(QuarantineStatus.ElevationDeclined, outcome.Status);
        Assert.Single(elevated.Calls); // exactly one single-action prompt
        Assert.DoesNotContain("Execute", log); // the in-process mutation never ran
        var record = journal.Get(outcome.RecordId!.Value)!;
        Assert.Equal(QuarantineResult.Pending, record.Result); // honestly pending, never "Quarantined"
    }

    [Fact]
    public void MachineScope_ElevatedChildViaSameJournal_CompletesRoundTrip()
    {
        var log = new List<string>();
        var journal = new FakeJournal();
        var strategy = new RecordingStrategy(log, sourceKind: "service");
        var elevated = new FakeElevatedRunner();
        var coordinator = new QuarantineCoordinator(journal, new[] { strategy }, elevated);
        elevated.ChildCoordinator = coordinator;

        var entry = new AutostartAudit.Core.Model.AutoStartEntry
        {
            SourceKind = "service",
            Scope = "machine",
            StableKey = "service|machine|mysvc",
            NativeKey = "MySvc",
            DisplayName = "MySvc",
            TargetPaths = new[] { @"C:\svc\s.exe" },
            RawValueSnapshot = @"C:\svc\s.exe",
            Signing = AutostartAudit.Core.Model.SigningStatus.Unverified,
            Observation = AutostartAudit.Core.Model.ObservationHealth.Ok,
        };

        var outcome = coordinator.Quarantine(entry);
        Assert.Equal(QuarantineStatus.Quarantined, outcome.Status);
        Assert.Equal(QuarantineResult.Executed, journal.Get(outcome.RecordId!.Value)!.Result);

        var restore = coordinator.Restore(outcome.RecordId!.Value);
        Assert.Equal(RestoreStatus.Restored, restore.Status);
    }

    [Fact]
    public void ElevatedChildCrash_UncertainResult_MarksFailedNotExecuted()
    {
        var log = new List<string>();
        var journal = new FakeJournal();
        var strategy = new RecordingStrategy(log, sourceKind: "service");
        var elevated = new FakeElevatedRunner { RunQuarantineResult = ElevatedRunStatus.Uncertain };
        var coordinator = new QuarantineCoordinator(journal, new[] { strategy }, elevated);

        var outcome = coordinator.Quarantine(new AutostartAudit.Core.Model.AutoStartEntry
        {
            SourceKind = "service",
            Scope = "machine",
            StableKey = "service|machine|mysvc",
            NativeKey = "MySvc",
            DisplayName = "MySvc",
            TargetPaths = new[] { @"C:\svc\s.exe" },
            RawValueSnapshot = @"C:\svc\s.exe",
            Signing = AutostartAudit.Core.Model.SigningStatus.Unverified,
            Observation = AutostartAudit.Core.Model.ObservationHealth.Ok,
        });

        Assert.Equal(QuarantineStatus.Failed, outcome.Status);
        Assert.Contains("no verified result", outcome.Detail);
        Assert.Equal(QuarantineResult.Failed, journal.Get(outcome.RecordId!.Value)!.Result);
    }

    [Fact]
    public void JournalIdentityUsesNormalizedSourceKeyNotDisplayName()
    {
        var journal = new FakeJournal();
        var strategy = new RecordingStrategy(new List<string>());
        var coordinator = new QuarantineCoordinator(journal, new[] { strategy });

        var first = coordinator.Quarantine(QuarantineTestEntries.RunKey("user", "Pretty Name", @"C:\t.exe"));
        // Same native key, different display name -> same identity.
        var duplicate = QuarantineTestEntries.RunKey("user", "Pretty Name", @"C:\t.exe") with
        {
            DisplayName = "Relabelled",
        };
        var second = coordinator.Quarantine(duplicate);

        Assert.Equal(
            journal.Get(first.RecordId!.Value)!.EntryIdentity,
            journal.Get(second.RecordId!.Value)!.EntryIdentity);
        Assert.Equal(StableKey.Build("run-key", "user", QuarantineTestEntries.RunKeyNativeKey("user", "Pretty Name")),
            journal.Get(first.RecordId!.Value)!.EntryIdentity);
    }
}
