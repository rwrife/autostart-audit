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
        Converters =
        {
            new SigningStatusJsonConverter(),
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
        },
    };
}

internal sealed class SigningStatusJsonConverter : JsonConverter<Model.SigningStatus>
{
    public override Model.SigningStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var numeric)
            && Enum.IsDefined(typeof(Model.SigningStatus), numeric))
            return (Model.SigningStatus)numeric;
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Signing status must be a string or defined numeric value.");

        var value = reader.GetString();
        if (string.Equals(value, "signed", StringComparison.OrdinalIgnoreCase))
            return Model.SigningStatus.Signed;
        if (string.Equals(value, "unsigned", StringComparison.OrdinalIgnoreCase))
            return Model.SigningStatus.Unsigned;
        if (string.Equals(value, "invalid", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "invalidSignature", StringComparison.OrdinalIgnoreCase))
            return Model.SigningStatus.InvalidSignature;
        if (string.Equals(value, "unverified", StringComparison.OrdinalIgnoreCase))
            return Model.SigningStatus.Unverified;
        throw new JsonException($"Unknown signing status '{value}'.");
    }

    public override void Write(Utf8JsonWriter writer, Model.SigningStatus value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            Model.SigningStatus.Signed => "signed",
            Model.SigningStatus.Unsigned => "unsigned",
            Model.SigningStatus.InvalidSignature => "invalidSignature",
            Model.SigningStatus.Unverified => "unverified",
            _ => throw new JsonException($"Unknown signing status '{value}'."),
        });
}
