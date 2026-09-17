using AutostartAudit.Core.Model;

namespace AutostartAudit.Core.Snapshot;

/// <summary>
/// One entry in a snapshot diff, carrying before/after evidence for every
/// state the entry existed in. Matching is by normalized identity
/// (source kind + scope + stable native key) — never by display name, which
/// is presentation-only and mutable. A display rename therefore shows up as
/// a <see cref="DiffEngine.ReasonDisplayName"/> change on a matched entry,
/// never as added+removed.
/// </summary>
public sealed record DiffRecord
{
    /// <summary>Normalized identity used for matching (never the display name).</summary>
    public required string IdentityKey { get; init; }

    public required string SourceKind { get; init; }

    public required string Scope { get; init; }

    public string? StableKeyBefore { get; init; }
    public string? StableKeyAfter { get; init; }

    public string? DisplayNameBefore { get; init; }
    public string? DisplayNameAfter { get; init; }

    /// <summary>Normalized target paths, before/after. Null side means the entry did not exist then/now.</summary>
    public IReadOnlyList<string>? TargetPathsBefore { get; init; }
    public IReadOnlyList<string>? TargetPathsAfter { get; init; }

    /// <summary>Signing status before/after. Kept per side; Unverified is never folded into Unsigned.</summary>
    public SigningStatus? SigningBefore { get; init; }
    public SigningStatus? SigningAfter { get; init; }

    /// <summary>Native enabled state before/after, when the source exposes one.</summary>
    public bool? EnabledBefore { get; init; }
    public bool? EnabledAfter { get; init; }

    /// <summary>Why the entry is in the Changed bucket. Empty for Added/Removed/Unchanged.</summary>
    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();
}
