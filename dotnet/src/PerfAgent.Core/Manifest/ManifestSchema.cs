using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace PerfAgent.Core.Manifest;

/// <summary>
/// Genera el JSON Schema del manifiesto a partir de los tipos C# (fuente de verdad), con descripciones y valores
/// permitidos. El archivo versionado <c>scenarios/run-manifest.v1.schema.json</c> se comprueba en las pruebas.
/// </summary>
public static class ManifestSchema
{
    public const string RelativePath = "scenarios/run-manifest.v1.schema.json";

    public static string Generate()
    {
        // Sin los valores por defecto "Web" (que admiten números como texto): el YAML usa números reales.
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = null,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        var exporter = new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = (context, node) =>
            {
                if (node is not JsonObject schema)
                {
                    return node;
                }

                var attributes = (context.PropertyInfo?.AttributeProvider ?? (System.Reflection.ICustomAttributeProvider)context.TypeInfo.Type)
                    .GetCustomAttributes(inherit: true);
                if (attributes.OfType<DescriptionAttribute>().FirstOrDefault() is { } description)
                {
                    schema.Insert(0, "description", description.Description);
                }

                if (attributes.OfType<AllowedValuesAttribute>().FirstOrDefault() is { } allowed)
                {
                    schema["enum"] = new JsonArray(allowed.Values.Select(v => (JsonNode?)JsonValue.Create(v?.ToString())).ToArray());
                }

                return schema;
            },
        };
        var root = (JsonObject)options.GetJsonSchemaAsNode(typeof(RunManifest), exporter);
        root.Insert(0, "$schema", "https://json-schema.org/draft/2020-12/schema");
        root.Insert(1, "$id", "https://perf-lab.local/schemas/run-manifest.v1.schema.json");
        root.Insert(2, "title", "Manifiesto de ejecución perf-lab (v1)");
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
            .Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }
}
