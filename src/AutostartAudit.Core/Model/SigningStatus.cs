namespace AutostartAudit.Core.Model;

/// <summary>
/// Local Authenticode chain status for an entry's target binary (issue #4 fills
/// this in). <see cref="Unverified"/> is kept deliberately distinct from
/// <see cref="Unsigned"/>: "we did not check (or could not check)" is a
/// different observation than "the file carries no signature".
/// </summary>
public enum SigningStatus
{
    /// <summary>Signature present and local chain evaluation succeeded.</summary>
    Signed,

    /// <summary>File readable and carries no Authenticode signature.</summary>
    Unsigned,

    /// <summary>Signature present but invalid according to local chain evaluation.</summary>
    InvalidSignature,

    /// <summary>
    /// Not checked or not checkable (missing file, locked, remote path,
    /// not-elevated). Never collapses into <see cref="Unsigned"/>.
    /// </summary>
    Unverified,
}
