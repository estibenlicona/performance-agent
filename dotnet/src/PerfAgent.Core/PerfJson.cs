using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace PerfAgent.Core;

/// <summary>Opciones JSON comunes: camelCase, enums en kebab-case y nulos omitidos.</summary>
public static class PerfJson
{
    public static readonly JsonSerializerOptions Indented = Create(writeIndented: true);

    public static readonly JsonSerializerOptions Compact = Create(writeIndented: false);

    public static JsonSerializerOptions Create(bool writeIndented) => new(JsonSerializerDefaults.Web)
    {
        WriteIndented = writeIndented,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    public static string Serialize<T>(T value, bool indented = true) =>
        JsonSerializer.Serialize(value, indented ? Indented : Compact);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Indented);
}
