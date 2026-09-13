namespace AutostartAudit.Core.Model;

/// <summary>
/// One named sub-source observation inside a scan source (e.g. one registry
/// key, one startup folder). Every attempt produces exactly one observation,
/// including failed ones — that is what keeps a partial scan honest.
/// </summary>
public sealed record SourceObservation
{
    /// <summary>Stable identifier, e.g. "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run".</summary>
    public required string Id { get; init; }

    public required ObservationHealth Health { get; init; }

    /// <summary>Human-readable failure reason or note. Null when health is Ok and nothing to note.</summary>
    public string? Detail { get; init; }
}
