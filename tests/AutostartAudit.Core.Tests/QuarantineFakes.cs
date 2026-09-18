using AutostartAudit.Core.Model;
using AutostartAudit.Core.Quarantine;
using AutostartAudit.Core.Scan;

namespace AutostartAudit.Core.Tests;

/// <summary>In-memory writable registry view for quarantine strategy tests.</summary>
internal sealed class FakeRegistryWriteProbe : IRegistryWriteProbe
{
    /// <summary>Key path (e.g. "HKCU\Software\...\Run") -> value name -> value.</summary>
    public Dictionary<string, Dictionary<string, RegistryValue>> Keys { get; } = new(StringComparer.Ordinal);

    /// <summary>Paths whose write-open throws (simulating unelevated HKLM).</summary>
    public HashSet<string> WriteDenied { get; } = new(StringComparer.Ordinal);

    /// <summary>Paths whose write-open throws "unknown".</summary>
    public HashSet<string> WriteUnknown { get; } = new(StringComparer.Ordinal);

    /// <summary>Paths whose keys do not exist at all.</summary>
    public HashSet<string> MissingKeys { get; } = new(StringComparer.Ordinal);

    /// <summary>Monotonic counter of mutating calls, for ordering assertions.</summary>
    public int MutationCount { get; private set; }

    public IRegistryKeyWriter OpenWritable(string hiveName, string subPath)
    {
        var path = $@"{hiveName}\{subPath}";
        if (WriteDenied.Contains(path))
            throw new RegistryAccessDeniedException(path, "requires elevation (test)");
        if (WriteUnknown.Contains(path))
            throw new RegistryUnknownException(path, "simulated unknown failure");
        if (MissingKeys.Contains(path) || !Keys.ContainsKey(path))
            throw new RegistryUnknownException(path, "key does not exist");
        return new Writer(this, path);
    }

    public void Seed(string keyPath, string name, ValueKind kind, string data)
    {
        if (!Keys.TryGetValue(keyPath, out var values))
            Keys[keyPath] = values = new Dictionary<string, RegistryValue>(StringComparer.Ordinal);
        values[name] = new RegistryValue(name, kind, data);
    }

    public RegistryValue? Peek(string keyPath, string name) =>
        Keys.TryGetValue(keyPath, out var values) && values.TryGetValue(name, out var v) ? v : null;

    private sealed class Writer : IRegistryKeyWriter
    {
        private readonly FakeRegistryWriteProbe _owner;
        private readonly string _path;

        public Writer(FakeRegistryWriteProbe owner, string path)
        {
            _owner = owner;
            _path = path;
        }

        public RegistryValue? TryGetValue(string name) => _owner.Peek(_path, name);

        public bool ValueMissing(string name) => _owner.Peek(_path, name) is null;

        public void SetValue(string name, ValueKind kind, string data)
        {
            _owner.MutationCount++;
            _owner.Keys[_path][name] = new RegistryValue(name, kind, data);
        }

        public void DeleteValue(string name)
        {
            _owner.MutationCount++;
            if (!_owner.Keys[_path].Remove(name))
                throw new RegistryUnknownException(_path, $"value {name} missing");
        }

        public void Dispose() { }
    }
}

/// <summary>In-memory task view for scheduled-task quarantine tests.</summary>
internal sealed class FakeTaskWriteProbe : ITaskWriteProbe
{
    public Dictionary<string, ScheduledTaskSnapshot> Tasks { get; } = new(StringComparer.Ordinal);
    public bool DenyWrite { get; set; }
    public bool UnknownWrite { get; set; }
    public bool DenyRead { get; set; }
    public int MutationCount { get; private set; }

    public ScheduledTaskSnapshot GetTask(string path)
    {
        if (DenyRead)
            throw new QuarantineDeniedException(path, "read denied (test)");
        if (!Tasks.TryGetValue(path, out var task))
            throw new QuarantineUnknownException(path, "task not registered");
        return task;
    }

    public void SetEnabled(string path, bool enabled)
    {
        MutationCount++;
        if (DenyWrite)
            throw new QuarantineDeniedException(path, "SetEnabled denied: requires elevation (test)");
        if (UnknownWrite)
            throw new QuarantineUnknownException(path, "SetEnabled unresolved (test)");
        if (!Tasks.TryGetValue(path, out var task))
            throw new QuarantineUnknownException(path, "task not registered");
        Tasks[path] = task with { Enabled = enabled, State = enabled ? "Ready" : "Disabled" };
    }
}

/// <summary>In-memory service view for service quarantine tests.</summary>
internal sealed class FakeServiceWriteProbe : IServiceWriteProbe
{
    public Dictionary<string, ServiceSnapshot> Services { get; } = new(StringComparer.Ordinal);
    public bool DenyWrite { get; set; }
    public bool UnknownWrite { get; set; }
    public bool DenyRead { get; set; }
    public int MutationCount { get; private set; }

    public ServiceSnapshot GetService(string name)
    {
        if (DenyRead)
            throw new QuarantineDeniedException(name, "read denied (test)");
        if (!Services.TryGetValue(name, out var service))
            throw new QuarantineUnknownException(name, "service not found");
        return service;
    }

    public void SetStartType(string name, ServiceStartType startType)
    {
        MutationCount++;
        if (DenyWrite)
            throw new QuarantineDeniedException(name, "ChangeServiceConfig denied: requires elevation (test)");
        if (UnknownWrite)
            throw new QuarantineUnknownException(name, "ChangeServiceConfig unresolved (test)");
        if (!Services.TryGetValue(name, out var service))
            throw new QuarantineUnknownException(name, "service not found");
        Services[name] = service with { StartType = startType };
    }
}

/// <summary>In-memory journal for ordering and fault-injection tests.</summary>
internal sealed class FakeJournal : IJournal
{
    private readonly Dictionary<long, JournalRecord> _records = new();
    private long _nextId = 1;
    public bool FailBegin { get; set; }
    public bool FailMarkExecuted { get; set; }
    public List<string> CallLog { get; }

    public FakeJournal(List<string>? sharedLog = null)
    {
        CallLog = sharedLog ?? new List<string>();
    }

    public long Begin(JournalDraft draft)
    {
        CallLog.Add("Begin");
        if (FailBegin)
            throw new InvalidOperationException("journal disk full (test fault)");
        var id = _nextId++;
        _records[id] = new JournalRecord
        {
            Id = id,
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
            EntryIdentity = draft.EntryIdentity,
            SourceKind = draft.SourceKind,
            Scope = draft.Scope,
            NativeKey = draft.NativeKey,
            DisplayName = draft.DisplayName,
            Strategy = draft.Strategy,
            BeforeStateJson = draft.BeforeStateJson,
            Result = QuarantineResult.Pending,
            RestoreStatus = JournalRestoreStatus.None,
        };
        return id;
    }

    public void MarkExecuted(long id)
    {
        CallLog.Add("MarkExecuted");
        if (FailMarkExecuted)
            throw new InvalidOperationException("journal lost mid-transition (test fault)");
        _records[id] = _records[id] with { Result = QuarantineResult.Executed };
    }

    public void MarkFailed(long id, string detail)
    {
        CallLog.Add("MarkFailed");
        _records[id] = _records[id] with { Result = QuarantineResult.Failed, ErrorDetail = detail };
    }

    public void MarkRestored(long id)
    {
        CallLog.Add("MarkRestored");
        _records[id] = _records[id] with { RestoreStatus = JournalRestoreStatus.Restored };
    }

    public void MarkRestoreFailed(long id, string detail)
    {
        CallLog.Add("MarkRestoreFailed");
        _records[id] = _records[id] with { RestoreStatus = JournalRestoreStatus.Failed, RestoreErrorDetail = detail };
    }

    public JournalRecord? Get(long id) => _records.TryGetValue(id, out var r) ? r : null;

    public IReadOnlyList<JournalRecord> ListAll() => _records.Values.OrderByDescending(r => r.Id).ToList();
}

/// <summary>Configurable elevation channel that can simulate a child run by
/// invoking the coordinator's record methods directly (same journal).</summary>
internal sealed class FakeElevatedRunner : IElevatedRecordRunner
{
    public ElevatedRunStatus RunQuarantineResult { get; set; } = ElevatedRunStatus.Completed;
    public ElevatedRunStatus RunRestoreResult { get; set; } = ElevatedRunStatus.Completed;
    public List<string> Calls { get; } = new();
    public QuarantineCoordinator? ChildCoordinator { get; set; }

    public ElevatedRunStatus RunQuarantine(long recordId)
    {
        Calls.Add($"quarantine:{recordId}");
        if (RunQuarantineResult == ElevatedRunStatus.Completed && ChildCoordinator is not null)
            return ChildCoordinator.RunQuarantineRecord(recordId).Status == QuarantineStatus.Quarantined
                ? ElevatedRunStatus.Completed
                : ElevatedRunStatus.Uncertain;
        return RunQuarantineResult;
    }

    public ElevatedRunStatus RunRestore(long recordId)
    {
        Calls.Add($"restore:{recordId}");
        if (RunRestoreResult == ElevatedRunStatus.Completed && ChildCoordinator is not null)
            return ChildCoordinator.RunRestoreRecord(recordId).Status == RestoreStatus.Restored
                ? ElevatedRunStatus.Completed
                : ElevatedRunStatus.Uncertain;
        return RunRestoreResult;
    }
}

/// <summary>Strategy that records call order against a probe/fake journal.</summary>
internal sealed class RecordingStrategy : IQuarantineStrategy
{
    private readonly List<string> _log;
    public Func<AutoStartEntry, QuarantineDecision>? PlanOverride { get; set; }
    public Func<string, MutationResult>? ExecuteOverride { get; set; }
    public Func<string, MutationResult>? RestoreOverride { get; set; }
    public string BeforeState { get; set; } = "{\"fixture\":true}";

    public RecordingStrategy(List<string> log, string sourceKind = "run-key")
    {
        _log = log;
        SourceKind = sourceKind;
    }

    public string SourceKind { get; }

    public QuarantineDecision Plan(AutoStartEntry entry)
    {
        _log.Add("Plan");
        return PlanOverride?.Invoke(entry) ?? QuarantineDecision.Ready(BeforeState);
    }

    public MutationResult Execute(string beforeStateJson)
    {
        _log.Add("Execute");
        return ExecuteOverride?.Invoke(beforeStateJson) ?? new MutationResult(MutationStatus.Ok);
    }

    public MutationResult Restore(string beforeStateJson)
    {
        _log.Add("Restore");
        return RestoreOverride?.Invoke(beforeStateJson) ?? new MutationResult(MutationStatus.Ok);
    }
}

internal static class QuarantineTestEntries
{
    private const string UserRunKeyPath = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";
    private const string MachineRunKeyPath = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    public static string RunKeyNativeKey(string scope, string name)
    {
        var runKeyPath = scope == "machine" ? MachineRunKeyPath : UserRunKeyPath;
        return $@"{runKeyPath}\{name}".ToLowerInvariant();
    }

    public static AutoStartEntry RunKey(string scope, string name, string data)
    {
        var native = RunKeyNativeKey(scope, name);
        return new AutoStartEntry
        {
            SourceKind = RunKeySource.Kind,
            Scope = scope,
            StableKey = StableKey.BuildWithTargets(RunKeySource.Kind, scope, native, TargetPathParser.Parse(data)),
            NativeKey = native,
            DisplayName = name,
            TargetPaths = TargetPathParser.Parse(data),
            RawValueSnapshot = data,
            Signing = SigningStatus.Unverified,
            Observation = ObservationHealth.Ok,
        };
    }
}
