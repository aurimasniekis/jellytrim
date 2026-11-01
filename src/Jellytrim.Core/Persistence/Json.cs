using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellytrim.Core.Persistence;

public static class Json
{
    /// <summary>Stable for persisted documents and for the plan hash. Do not change property ordering semantics.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)!;
}
