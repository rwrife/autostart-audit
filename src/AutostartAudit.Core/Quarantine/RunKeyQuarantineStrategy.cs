using AutostartAudit.Core.Model;
using AutostartAudit.Core.Scan;

namespace AutostartAudit.Core.Quarantine;

/// <summary>
/// Strategy for Run/RunOnce registry values: the value is renamed to
/// <c>&lt;name&gt;.aa-quarantined</c> and the original name+data are journaled
/// verbatim, so restore writes back byte-identical content. Only string and
/// expand-string values qualify; any other value kind cannot be described
/// exactly through the string codec and is reported read-only.
/// </summary>
public sealed class RunKeyQuarantineStrategy : IQuarantineStrategy
{
    public const string QuarantinedSuffix = ".aa-quarantined";
    public const string Kind = AutostartAudit.Core.Scan.RunKeySource.Kind;

    private static readonly (string HivePrefix, string Hive, string SubPath)[] Layouts =
    {
        (@"hklm\software\microsoft\windows\currentversion\runonce\", "HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
        (@"hkcu\software\microsoft\windows\currentversion\runonce\", "HKCU", @"Software\Microsoft\Windows\CurrentVersion\RunOnce"),
        (@"hklm\software\microsoft\windows\currentversion\run\", "HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
        (@"hkcu\software\microsoft\windows\currentversion\run\", "HKCU", @"Software\Microsoft\Windows\CurrentVersion\Run"),
    };

    private readonly IRegistryWriteProbe _probe;

    public RunKeyQuarantineStrategy(IRegistryWriteProbe probe) => _probe = probe;

    public string SourceKind => Kind;

    internal sealed record State(string Hive, string SubPath, string ValueName, Scan.ValueKind Kind, string Data)
    {
        public string QuarantinedName => ValueName + QuarantinedSuffix;
    }

    public QuarantineDecision Plan(AutoStartEntry entry)
    {
        if (!TryLocate(entry, out var hive, out var subPath, out var normalizedSuffix))
            return QuarantineDecision.ReadOnly("run-key entry identity does not match a known Run/RunOnce layout");
        // NativeKey is normalized; DisplayName carries the verbatim value name
        // from the scan. Reconcile them so we never write a mangled name.
        var valueName = entry.DisplayName;
        if (string.IsNullOrWhiteSpace(valueName)
            || !string.Equals(Scan.StableKey.Normalize(valueName), normalizedSuffix, StringComparison.Ordinal))
            return QuarantineDecision.ReadOnly("run-key display name does not reconcile with the normalized native key");
        if (valueName.EndsWith(QuarantinedSuffix, StringComparison.OrdinalIgnoreCase))
            return QuarantineDecision.ReadOnly("value already carries the quarantine suffix");

        Scan.RegistryValue? live;
        string readFailure;
        try
        {
            using var key = _probe.OpenWritable(hive, subPath);
            live = key.TryGetValue(valueName);
            readFailure = "";
        }
        catch (Exception ex)
        {
            live = null;
            readFailure = ex.Message;
        }

        if (live is null && readFailure.Length > 0)
        {
            // Unelevated reads of machine-scope keys may be refused; the
            // elevated mutation re-verifies live state before touching it, so
            // definitive scan evidence is enough to journal a candidate.
            // User-scope refusals are unexplained — refuse to act.
            if (!string.Equals(entry.Scope, "machine", StringComparison.OrdinalIgnoreCase))
                return QuarantineDecision.ReadOnly($"current value state unreadable: {readFailure}");
            if (entry.RawValueSnapshot.Length == 0)
                return QuarantineDecision.ReadOnly("current value state unreadable and scan snapshot is empty");
            // Reconstruct the journaled state from the verbatim scan snapshot.
            return QuarantineDecision.Ready(BeforeState.Serialize(new State(hive, subPath, valueName,
                InferKind(entry.RawValueSnapshot), entry.RawValueSnapshot)));
        }

        if (live is null)
            return QuarantineDecision.ReadOnly("value is no longer present at its journaled key");
        if (live.Kind == Scan.ValueKind.Other)
            return QuarantineDecision.ReadOnly("value kind is not string/expand-string; exact restore cannot be described");
        if (live.Data.Contains('\0'))
            return QuarantineDecision.ReadOnly("multi-string value data cannot be restored verbatim through the string codec");
        if (!string.Equals(live.Data, entry.RawValueSnapshot, StringComparison.Ordinal))
            return QuarantineDecision.ReadOnly("value data changed since the scan; refusing to quarantine drifted state");

        return QuarantineDecision.Ready(BeforeState.Serialize(new State(hive, subPath, live.Name, live.Kind, live.Data)));
    }

    public MutationResult Execute(string beforeStateJson)
    {
        var state = BeforeState.Deserialize<State>(beforeStateJson);
        if (state is null)
            return new MutationResult(MutationStatus.Failed, "journaled before-state failed to deserialize");
        try
        {
            using var key = _probe.OpenWritable(state.Hive, state.SubPath);

            // Drift/permission checks run before any write — a refusal here
            // performs zero mutations.
            var live = key.TryGetValue(state.ValueName);
            if (live is null)
                return new MutationResult(MutationStatus.Failed, "value disappeared since planning; nothing was quarantined");
            if (!string.Equals(live.Data, state.Data, StringComparison.Ordinal) || live.Kind != state.Kind)
                return new MutationResult(MutationStatus.Failed, "value drifted since planning; nothing was quarantined");
            if (!key.ValueMissing(state.QuarantinedName))
                return new MutationResult(MutationStatus.Failed, "quarantine name is already occupied; nothing was quarantined");

            key.SetValue(state.QuarantinedName, state.Kind, state.Data);
            var written = key.TryGetValue(state.QuarantinedName);
            if (written is null || !string.Equals(written.Data, state.Data, StringComparison.Ordinal))
            {
                TryRollback(key, state.QuarantinedName);
                return new MutationResult(MutationStatus.Failed, "quarantined value failed post-write verification; rolled back");
            }

            key.DeleteValue(state.ValueName);
            if (!key.ValueMissing(state.ValueName))
            {
                // Original still present alongside the copy: remove the copy
                // so the entry is left exactly as found.
                TryRollback(key, state.QuarantinedName);
                return new MutationResult(MutationStatus.Failed, "original value could not be removed; rolled back to original state");
            }
            return new MutationResult(MutationStatus.Ok);
        }
        catch (Exception ex) when (IsDenied(ex))
        {
            return new MutationResult(MutationStatus.Denied, ex.Message);
        }
        catch (Exception ex)
        {
            return new MutationResult(MutationStatus.Failed, ex.Message);
        }
    }

    public MutationResult Restore(string beforeStateJson)
    {
        var state = BeforeState.Deserialize<State>(beforeStateJson);
        if (state is null)
            return new MutationResult(MutationStatus.Failed, "journaled before-state failed to deserialize");
        try
        {
            using var key = _probe.OpenWritable(state.Hive, state.SubPath);

            if (!key.ValueMissing(state.ValueName))
                return new MutationResult(MutationStatus.Failed, "original value name is occupied; refusing to clobber current state");
            var copy = key.TryGetValue(state.QuarantinedName);
            if (copy is null)
                return new MutationResult(MutationStatus.Failed, "quarantined copy is missing; exact restore cannot be performed");
            if (!string.Equals(copy.Data, state.Data, StringComparison.Ordinal) || copy.Kind != state.Kind)
                return new MutationResult(MutationStatus.Failed, "quarantined copy no longer matches the journaled data; refusing to restore unknown bytes");

            key.SetValue(state.ValueName, state.Kind, state.Data);
            var written = key.TryGetValue(state.ValueName);
            if (written is null || !string.Equals(written.Data, state.Data, StringComparison.Ordinal))
            {
                TryRollback(key, state.ValueName);
                return new MutationResult(MutationStatus.Failed, "restored value failed post-write verification; rolled back");
            }

            key.DeleteValue(state.QuarantinedName);
            return new MutationResult(MutationStatus.Ok);
        }
        catch (Exception ex) when (IsDenied(ex))
        {
            return new MutationResult(MutationStatus.Denied, ex.Message);
        }
        catch (Exception ex)
        {
            return new MutationResult(MutationStatus.Failed, ex.Message);
        }
    }

    private static void TryRollback(IRegistryKeyWriter key, string valueName)
    {
        try
        {
            key.DeleteValue(valueName);
        }
        catch (Exception)
        {
            // Rollback failure is reported through the mutation result detail
            // that follows; the journal keeps the record Failed so the state
            // is never presented as clean.
        }
    }

    private static Scan.ValueKind InferKind(string snapshot) =>
        snapshot.Contains('%') ? Scan.ValueKind.ExpandString : Scan.ValueKind.String;

    private static bool IsDenied(Exception ex) =>
        ex is RegistryAccessDeniedException or QuarantineDeniedException;

    private static bool TryLocate(AutoStartEntry entry, out string hive, out string subPath, out string valueName)
    {
        hive = string.Empty;
        subPath = string.Empty;
        valueName = string.Empty;
        var native = entry.NativeKey ?? entry.StableKey;
        foreach (var (prefix, hiveName, layout) in Layouts)
        {
            if (native.StartsWith(prefix, StringComparison.Ordinal))
            {
                hive = hiveName;
                subPath = layout;
                valueName = native[prefix.Length..];
                return valueName.Length > 0 && !valueName.Contains('\\');
            }
        }
        return false;
    }
}
