namespace AutostartAudit.Core.Model;

/// <summary>How an entry-level signature status relates to its target results.</summary>
public enum SigningAggregation
{
    NoTarget,
    Single,
    Uniform,
    Mixed,
}
