using AutostartAudit.Core.Model;
using AutostartAudit.Core.Quarantine;
using AutostartAudit.Core.Snapshot;
using AutostartAudit.Core.Store;

namespace AutostartAudit.App.Model;

/// <summary>
/// Fixture-backed <see cref="IScanService"/> for the UI smoke test and view
/// model tests. It performs ZERO real-machine reads or mutations: the scan
/// document, snapshot capture, and journal are all synthetic and deterministic.
/// </summary>
public sealed class FixtureScanService : IScanService
{
    /// <summary>How many entries the fixture scan returns.</summary>
    public const int EntryCount = 3;

    /// <summary>How many fixture entries match the smoke filter token "updater".</summary>
    public const int MatchingUpdaterCount = 1;

    public int QuarantineCalls { get; private set; }
    public List<long> RestoreCalls { get; } = new();

    public static ScanDocument FixtureDocument() => new()
    {
        Entries = new[]
        {
            new AutoStartEntry
            {
                SourceKind = "run-key",
                Scope = "user",
                StableKey = @"hkcu|user|software\microsoft\windows\currentversion\run\updater",
                NativeKey = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Updater",
                DisplayName = "Vendor Updater",
                TargetPaths = new[] { @"C:\Users\Test\AppData\Local\Vendor\updater.exe" },
                RawValueSnapshot = @"""C:\Users\Test\AppData\Local\Vendor\updater.exe"" --watch",
                Signing = SigningStatus.Signed,
                SignerSubject = "Vendor Inc",
                SigningAggregation = SigningAggregation.Single,
                Observation = ObservationHealth.Ok,
            },
            new AutoStartEntry
            {
                SourceKind = "scheduled-task",
                Scope = "machine",
                StableKey = @"scheduled-task|machine|\vendor\telemetry",
                NativeKey = @"\Vendor\Telemetry",
                DisplayName = "Telemetry task",
                TargetPaths = new[] { @"C:\Windows\temp\telemetry.exe" },
                RawValueSnapshot = "telemetry.exe",
                Signing = SigningStatus.Unverified,
                Observation = ObservationHealth.Ok,
                TriggerSummary = "logon",
            },
            new AutoStartEntry
            {
                SourceKind = "service",
                Scope = "machine",
                StableKey = @"service|machine|vbssvc",
                NativeKey = "VbsSvc",
                DisplayName = "Virtual service",
                TargetPaths = new[] { @"C:\Windows\System32\svchost.exe -k netsvcs" },
                RawValueSnapshot = @"C:\Windows\System32\svchost.exe -k netsvcs",
                Signing = SigningStatus.Signed,
                SignerSubject = "Microsoft Corporation",
                SigningAggregation = SigningAggregation.Single,
                Observation = ObservationHealth.Ok,
                StartType = "automatic",
            },
        },
        Sources = new[]
        {
            new SourceReport { SourceKind = "run-key", Capability = SourceCapability.Scanned },
            new SourceReport { SourceKind = "startup-folder", Capability = SourceCapability.Scanned },
            new SourceReport { SourceKind = "scheduled-task", Capability = SourceCapability.Scanned },
            new SourceReport
            {
                SourceKind = "service",
                Capability = SourceCapability.Denied,
                Detail = "fixture: SCM access denied without elevation",
            },
        },
        ScanComplete = false,
    };

    public Task<ScanDocument> ScanAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(FixtureDocument());

    public Task<CaptureOutcome> CaptureAndDiffAsync(ScanDocument document, CancellationToken cancellationToken = default) =>
        Task.FromResult(new CaptureOutcome
        {
            IsBaseline = true,
            SnapshotId = 1,
            PreviousSnapshotId = null,
            Diff = null,
        });

    public Task<IReadOnlyList<JournalRecord>> ListJournalAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<JournalRecord> records = new JournalRecord[]
        {
            new()
            {
                Id = 2,
                CreatedAtUtc = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
                EntryIdentity = "scheduled-task|machine|\\vendor\\telemetry",
                SourceKind = "scheduled-task",
                Scope = "machine",
                NativeKey = @"\Vendor\Telemetry",
                DisplayName = "Telemetry task",
                Strategy = "scheduled-task",
                BeforeStateJson = "{}",
                Result = QuarantineResult.Pending,
                ErrorDetail = "elevated child never confirmed the mutation",
                RestoreStatus = JournalRestoreStatus.None,
            },
            new()
            {
                Id = 1,
                CreatedAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                EntryIdentity = "run-key|user|software\\microsoft\\windows\\currentversion\\run\\old-helper",
                SourceKind = "run-key",
                Scope = "user",
                NativeKey = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Old-Helper",
                DisplayName = "Old Helper",
                Strategy = "run-key",
                BeforeStateJson = "{}",
                Result = QuarantineResult.Executed,
                RestoreStatus = JournalRestoreStatus.None,
            },
        };
        return Task.FromResult(records);
    }

    public QuarantineDecision AssessQuarantine(AutoStartEntry entry) =>
        // In the fixture, only user-scope run-key entries claim an exact-restore plan.
        entry.SourceKind == "run-key" && entry.Scope == "user"
            ? QuarantineDecision.Ready("{}")
            : QuarantineDecision.ReadOnly("fixture: no strategy can describe an exact restore here");

    public Task<QuarantineOutcome> QuarantineAsync(AutoStartEntry entry, CancellationToken cancellationToken = default)
    {
        QuarantineCalls++;
        return Task.FromResult(new QuarantineOutcome
        {
            Status = QuarantineStatus.Quarantined,
            RecordId = 99,
        });
    }

    public Task<RestoreOutcome> RestoreAsync(long recordId, CancellationToken cancellationToken = default)
    {
        RestoreCalls.Add(recordId);
        return Task.FromResult(new RestoreOutcome
        {
            Status = RestoreStatus.Restored,
            RecordId = recordId,
        });
    }
}
