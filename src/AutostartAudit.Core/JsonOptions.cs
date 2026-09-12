using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutostartAudit.Core;

/// <summary>
/// Single source of truth for JSON serialization of scan documents and
/// snapshots (shared by CLI export and the snapshot store in issue #5).
/// Enums are written as lower-case strings so exports stay human-readable
/// and stable across enum reordering.
/// </summary>
public static class JsonOptions
{
    /// <summary>Compact camelCase, nulls omitted, string enums. Canonical wire format.</summary>
    public static JsonSerializerOptions Default { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
