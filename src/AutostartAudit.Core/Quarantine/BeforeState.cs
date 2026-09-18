using System.Text.Json;

namespace AutostartAudit.Core.Quarantine;

/// <summary>
/// Canonical codec for journaled before-states. The before-state is the exact
/// inverse description a strategy commits before mutating; it is written and
/// read with the shared wire options so journal contents stay inspectable and
/// stable across builds.
/// </summary>
internal static class BeforeState
{
    public static string Serialize<T>(T state) => JsonSerializer.Serialize(state, JsonOptions.Default);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions.Default);
}
