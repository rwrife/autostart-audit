using AutostartAudit.Core.Model;
using AutostartAudit.Core.Scan;

namespace AutostartAudit.Core.Snapshot;

/// <summary>
/// Compares two scan snapshots and classifies every entry as Added, Removed,
/// Changed, or Unchanged, in two passes:
/// 1. Exact identity match on the full normalized identity
///    (source kind + scope + stable key + normalized target paths). Matches
///    differ only by display name, signing status, or enabled state; a
///    renamed display with the same target is one Changed entry with
///    <see cref="ReasonDisplay"/>, never added+removed.
/// 2. Residual pairing by native source key (registry key+value name, task
///    path, service name, file path). Entries whose target paths moved pair
///    up as one Changed entry with <see cref="ReasonPath"/> rather than
///    appearing as an unrelated add plus remove.
/// Anything left unpaired is Added (after-side) or Removed (before-side).
/// Display names are never identity inputs.
/// </summary>
public static class DiffEngine
{
    public const string ReasonPath = "target-path";
    public const string ReasonSigning = "signing-status";
    public const string ReasonEnabled = "enabled-state";
    public const string ReasonDisplay = "display-renamed";

    /// <summary>
    /// Diffs <paramref name="before"/> against <paramref name="after"/>.
    /// Both sides may be partial scans; the engine compares only what was
    /// actually observed and never invents entries for sources that failed
    /// to read. Scan completeness is disclosed by the snapshot pair itself,
    /// not fabricated here.
    /// </summary>
    public static ScanDiff Compare(ScanDocument before, ScanDocument after)
    {
        var beforeMap = BuildIdentityMap(before);
        var afterMap = BuildIdentityMap(after);

        var added = new List<DiffRecord>();
        var removed = new List<DiffRecord>();
        var changed = new List<DiffRecord>();
        var unchanged = new List<DiffRecord>();

        // Pass 1: exact full-identity match.
        var residualBefore = new List<KeyValuePair<string, AutoStartEntry>>();
        var residualAfter = new List<KeyValuePair<string, AutoStartEntry>>();
        foreach (var pair in beforeMap)
        {
            if (afterMap.TryGetValue(pair.Key, out var current))
            {
                var record = MatchedRecord(pair.Key, pair.Value, current);
                if (record.Reasons.Count > 0)
                    changed.Add(record);
                else
                    unchanged.Add(record);
            }
            else
            {
                residualBefore.Add(pair);
            }
        }

        foreach (var pair in afterMap)
        {
            if (!beforeMap.ContainsKey(pair.Key))
                residualAfter.Add(pair);
        }

        // Pass 2: pair residuals that share a native source key — the same
        // registry value / task / service / file with different target paths.
        // Deterministic pairing: stable-key order on both sides, index-wise.
        var nativeBefore = GroupByNativeKey(residualBefore);
        var nativeAfter = GroupByNativeKey(residualAfter);
        var consumedBefore = new HashSet<string>(StringComparer.Ordinal);
        var consumedAfter = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (nativeKey, befores) in nativeBefore)
        {
            if (!nativeAfter.TryGetValue(nativeKey, out var afters))
                continue;

            var pairs = Math.Min(befores.Count, afters.Count);
            for (var i = 0; i < pairs; i++)
            {
                consumedBefore.Add(befores[i].Key);
                consumedAfter.Add(afters[i].Key);
                changed.Add(MatchedRecord(
                    StableKey.Normalize($"native|{nativeKey}|{i}"), befores[i].Value, afters[i].Value));
            }
        }

        foreach (var (identity, previous) in residualBefore)
        {
            if (consumedBefore.Contains(identity))
                continue;
            removed.Add(new DiffRecord
            {
                IdentityKey = identity,
                SourceKind = previous.SourceKind,
                Scope = previous.Scope,
                StableKeyBefore = previous.StableKey,
                DisplayNameBefore = previous.DisplayName,
                TargetPathsBefore = NormalizedTargets(previous),
                SigningBefore = previous.Signing,
                EnabledBefore = previous.Enabled,
            });
        }

        foreach (var (identity, current) in residualAfter)
        {
            if (consumedAfter.Contains(identity))
                continue;
            added.Add(new DiffRecord
            {
                IdentityKey = identity,
                SourceKind = current.SourceKind,
                Scope = current.Scope,
                StableKeyAfter = current.StableKey,
                DisplayNameAfter = current.DisplayName,
                TargetPathsAfter = NormalizedTargets(current),
                SigningAfter = current.Signing,
                EnabledAfter = current.Enabled,
            });
        }

        return new ScanDiff
        {
            Added = added.OrderBy(r => r.IdentityKey, StringComparer.Ordinal).ToList(),
            Removed = removed.OrderBy(r => r.IdentityKey, StringComparer.Ordinal).ToList(),
            Changed = changed.OrderBy(r => r.IdentityKey, StringComparer.Ordinal).ToList(),
            Unchanged = unchanged.OrderBy(r => r.IdentityKey, StringComparer.Ordinal).ToList(),
        };
    }

    private static DiffRecord MatchedRecord(string identity, AutoStartEntry previous, AutoStartEntry current)
    {
        var reasons = new List<string>();
        var pathsBefore = NormalizedTargets(previous);
        var pathsAfter = NormalizedTargets(current);
        if (!pathsBefore.SequenceEqual(pathsAfter, StringComparer.Ordinal))
            reasons.Add(ReasonPath);
        if (previous.Signing != current.Signing)
            reasons.Add(ReasonSigning);
        if (previous.Enabled != current.Enabled)
            reasons.Add(ReasonEnabled);
        if (!string.Equals(previous.DisplayName, current.DisplayName, StringComparison.Ordinal))
            reasons.Add(ReasonDisplay);

        return new DiffRecord
        {
            IdentityKey = identity,
            SourceKind = current.SourceKind,
            Scope = current.Scope,
            StableKeyBefore = previous.StableKey,
            StableKeyAfter = current.StableKey,
            DisplayNameBefore = previous.DisplayName,
            DisplayNameAfter = current.DisplayName,
            TargetPathsBefore = pathsBefore,
            TargetPathsAfter = pathsAfter,
            SigningBefore = previous.Signing,
            SigningAfter = current.Signing,
            EnabledBefore = previous.Enabled,
            EnabledAfter = current.Enabled,
            Reasons = reasons,
        };
    }

    private static Dictionary<string, List<KeyValuePair<string, AutoStartEntry>>> GroupByNativeKey(
        IEnumerable<KeyValuePair<string, AutoStartEntry>> residuals) =>
        residuals
            .GroupBy(kv => NativeKeyOf(kv.Value), StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);

    /// <summary>
    /// Native pairing key for pass 2. Falls back to the full stable key for
    /// entries produced before native-key separation existed, so they only
    /// pair with themselves (which pass 1 already handled).
    /// </summary>
    private static string NativeKeyOf(AutoStartEntry entry) =>
        StableKey.Normalize($"{entry.SourceKind}|{entry.Scope}|{entry.NativeKey ?? entry.StableKey}");

    private static Dictionary<string, AutoStartEntry> BuildIdentityMap(ScanDocument document)
    {
        var map = new Dictionary<string, AutoStartEntry>(StringComparer.Ordinal);
        // Deterministic fold: if a malformed scan ever yields duplicate
        // identities, the first entry in stable-key order wins, both sides
        // applying the same rule.
        foreach (var entry in document.Entries.OrderBy(e => e.StableKey, StringComparer.Ordinal))
            map.TryAdd(entry.StableKey, entry);
        return map;
    }

    private static IReadOnlyList<string> NormalizedTargets(AutoStartEntry entry) =>
        entry.TargetPaths
            .Select(StableKey.Normalize)
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
}
