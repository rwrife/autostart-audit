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

    /// <summary>
    /// Normalized native source identifier without target paths baked in
    /// (registry key+value name, task path, service name, file path). The
    /// snapshot diff matches on this so a target-path edit reads as one
    /// <c>changed</c> entry with before/after evidence, never as
    /// added+removed. Falls back to <see cref="StableKey"/> when absent.
    /// </summary>
    public string? NativeKey { get; init; }

    /// <summary>Human-facing label. Presentation only; not an identity.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Native source name/path when it differs from the friendly display label.</summary>
    public string? SourceName { get; init; }

    /// <summary>Target executable/script paths extracted from the raw value.</summary>
    public required IReadOnlyList<string> TargetPaths { get; init; }

    /// <summary>Verbatim value as read from the source, for exact restore.</summary>
    public required string RawValueSnapshot { get; init; }

    /// <summary>Native runtime/configuration state, when exposed by the source.</summary>
    public string? State { get; init; }

    /// <summary>Native enabled state when the source exposes it separately.</summary>
    public bool? Enabled { get; init; }

    /// <summary>Human-readable trigger types for scheduled tasks.</summary>
    public string? TriggerSummary { get; init; }

    /// <summary>Normalized service start type.</summary>
    public string? StartType { get; init; }

    /// <summary>Additional source evidence, including unsupported-but-observed constructs.</summary>
    public IReadOnlyList<string> Evidence { get; init; } = Array.Empty<string>();

    public required SigningStatus Signing { get; init; }

    /// <summary>Whether the entry status is a single, uniform, mixed, or absent-target result.</summary>
    public SigningAggregation SigningAggregation { get; init; } = SigningAggregation.NoTarget;

    /// <summary>Authoritative per-target signature evidence; avoids lossy multi-target aggregation.</summary>
    public IReadOnlyList<TargetSignature> TargetSignatures { get; init; } = Array.Empty<TargetSignature>();

    private readonly string? _signerSubject;

    /// <summary>Signer subject when <see cref="Signing"/> is <see cref="SigningStatus.Signed"/>; otherwise null.</summary>
    public string? SignerSubject
    {
        get => Signing == SigningStatus.Signed ? _signerSubject : null;
        init => _signerSubject = value;
    }

    /// <summary>Health of this single observation (read ok, denied, partial...).</summary>
    public required ObservationHealth Observation { get; init; }
}
