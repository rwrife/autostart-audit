namespace AutostartAudit.Core.Model;

/// <summary>
/// One observed auto-start entry. Identity is the normalized
/// (SourceKind, Scope, StableKey) triple plus target paths — never the display
/// name, which is not stable and not unique.
/// </summary>
public sealed record AutoStartEntry
{
    /// <summary>e.g. "run-key", "startup-folder", "scheduled-task", "service".</summary>
    public required string SourceKind { get; init; }

    /// <summary>"machine" or "user".</summary>
    public required string Scope { get; init; }

    /// <summary>Normalized stable key, e.g. "HKCU\Software\Microsoft\Windows\CurrentVersion\Run\MyApp".</summary>
    public required string StableKey { get; init; }

    /// <summary>Human-facing label. Presentation only; not an identity.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Target executable/script paths extracted from the raw value.</summary>
    public required IReadOnlyList<string> TargetPaths { get; init; }

    /// <summary>Verbatim value as read from the source, for exact restore.</summary>
    public required string RawValueSnapshot { get; init; }

    public required SigningStatus Signing { get; init; }

    /// <summary>Signer subject when <see cref="Signing"/> is <see cref="SigningStatus.Signed"/>; otherwise null.</summary>
    public string? SignerSubject { get; init; }

    /// <summary>Health of this single observation (read ok, denied, partial...).</summary>
    public required Capability Observation { get; init; }
}
