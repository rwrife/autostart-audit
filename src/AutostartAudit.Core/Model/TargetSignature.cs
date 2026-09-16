namespace AutostartAudit.Core.Model;

/// <summary>Local Authenticode evidence for one exact target path.</summary>
public sealed record TargetSignature
{
    public required string Path { get; init; }
    public required SigningStatus Status { get; init; }

    private readonly string? _publisher;

    /// <summary>Signer subject. Present only when <see cref="Status"/> is signed.</summary>
    public string? Publisher
    {
        get => Status == SigningStatus.Signed ? _publisher : null;
        init => _publisher = value;
    }

    /// <summary>Non-sensitive reason when verification could not produce a definitive status.</summary>
    public string? Detail { get; init; }
}
