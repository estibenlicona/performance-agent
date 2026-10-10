using System.Text.Json;
using System.Text.RegularExpressions;
using PerfAgent.Core.Manifest;
using PerfAgent.Core.Policy;

namespace PerfAgent.Core.Discovery;

/// <summary>Operación descrita por OpenAPI (sin ejecutar nada).</summary>
public sealed record EndpointCandidate(
    string Name,
    string Method,
    string PathTemplate,
    IReadOnlyList<ParameterInfo> PathParameters,
    IReadOnlyList<string> RequiredQueryParameters,
    bool RequiresBody,
    bool RequiresAuthentication,
    int ExpectedStatus);

public sealed record ParameterInfo(string Name, string? SchemaType, string? Example);

/// <summary>Endpoint que entrará en el escenario k6, con ruta concreta y datos sintéticos.</summary>
public sealed record PlannedEndpoint(
    string Name,
    string Method,
    string Path,
    string PathTemplate,
    int ExpectedStatus,
    int Weight,
    string? BodyContains,
    string? Body,
    string Source,
    IReadOnlyList<string> Notes);

public sealed record ExcludedEndpoint(string Method, string Path, string Reason);

public sealed record EndpointPlan(
    string Source,
    IReadOnlyList<PlannedEndpoint> Endpoints,
    IReadOnlyList<ExcludedEndpoint> Excluded,
    IReadOnlyList<string> Notes)
{
    public bool IsEmpty => Endpoints.Count == 0;
}

/// <summary>
/// Lee OpenAPI (JSON 3.x) y decide qué endpoints se pueden probar con seguridad. Nunca infiere
/// operaciones de escritura ni envía peticiones arbitrarias: lo que no se puede probar se excluye con su motivo.
/// </summary>
public static partial class EndpointPlanner
{
    private static readonly string[] HttpMethods = ["get", "head", "post", "put", "patch", "delete"];

    public static IReadOnlyList<EndpointCandidate> ReadOpenApi(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var globalAuth = root.TryGetProperty("security", out var gs) && gs.ValueKind == JsonValueKind.Array && gs.GetArrayLength() > 0;
        var candidates = new List<EndpointCandidate>();
        foreach (var path in paths.EnumerateObject())
        {
            var sharedParameters = path.Value.TryGetProperty("parameters", out var sp) ? sp : default;
            foreach (var method in HttpMethods)
            {
                if (!path.Value.TryGetProperty(method, out var operation))
                {
                    continue;
                }

                var pathParams = new List<ParameterInfo>();
                var requiredQuery = new List<string>();
                foreach (var parameter in EnumerateParameters(sharedParameters).Concat(EnumerateParameters(operation.TryGetProperty("parameters", out var op) ? op : default)))
                {
                    var name = parameter.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                    var location = parameter.TryGetProperty("in", out var i) ? i.GetString() : null;
                    var required = parameter.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.True;
                    if (location == "path" && pathParams.All(p => p.Name != name))
                    {
                        pathParams.Add(new ParameterInfo(name, SchemaType(parameter), Example(parameter)));
                    }
                    else if (location == "query" && required)
                    {
                        requiredQuery.Add(name);
                    }
                }

                var requiresBody = operation.TryGetProperty("requestBody", out var body)
                    && body.TryGetProperty("required", out var br) && br.ValueKind == JsonValueKind.True;
                var auth = operation.TryGetProperty("security", out var os) && os.ValueKind == JsonValueKind.Array
                    ? os.GetArrayLength() > 0
                    : globalAuth;
                var operationId = operation.TryGetProperty("operationId", out var oid) ? oid.GetString() : null;
                candidates.Add(new EndpointCandidate(
                    SafeName(operationId) ?? DefaultName(method, path.Name),
                    method.ToUpperInvariant(),
                    path.Name,
                    pathParams,
                    requiredQuery,
                    requiresBody,
                    auth,
                    ExpectedStatus(operation)));
            }
        }

        return candidates;
    }

    public static EndpointPlan Plan(RunManifest manifest, PolicyEngine policy, IReadOnlyList<EndpointCandidate>? openApi, string? openApiUnavailableReason)
    {
        var source = manifest.Load.EndpointSource switch
        {
            "declared" => "declared",
            "openapi" => "openapi",
            _ => manifest.Endpoints.Count > 0 ? "declared" : "openapi",
        };

        var notes = new List<string>();
        if (source == "declared")
        {
            var declared = manifest.Endpoints.Select(e => new PlannedEndpoint(
                    e.Name!,
                    e.Method.ToUpperInvariant(),
                    SubstitutePath(e.Path!, e.PathParameters),
                    e.Path!,
                    e.ExpectedStatus,
                    e.Weight,
                    e.BodyContains,
                    e.Body,
                    "declared",
                    []))
                .ToList();
            return Filter(manifest, policy, declared, [], "declared", notes);
        }

        if (openApi is null)
        {
            notes.Add($"OpenAPI no disponible: {openApiUnavailableReason ?? "sin documento"}. Declara endpoints en el manifiesto para continuar.");
            return new EndpointPlan("openapi", [], [], notes);
        }

        var planned = new List<PlannedEndpoint>();
        var excluded = new List<ExcludedEndpoint>();
        foreach (var c in openApi)
        {
            if (!policy.IsMethodAllowed(manifest, c.Method))
            {
                excluded.Add(new ExcludedEndpoint(c.Method, c.PathTemplate, "Operación de escritura no autorizada por la política."));
                continue;
            }

            if (c.RequiresAuthentication)
            {
                excluded.Add(new ExcludedEndpoint(c.Method, c.PathTemplate, "Requiere autenticación: el MVP no obtiene tokens para k6."));
                continue;
            }

            if (c.RequiredQueryParameters.Count > 0)
            {
                excluded.Add(new ExcludedEndpoint(c.Method, c.PathTemplate, $"Parámetros de query obligatorios sin datos sintéticos ({string.Join(", ", c.RequiredQueryParameters)})."));
                continue;
            }

            if (c.RequiresBody)
            {
                excluded.Add(new ExcludedEndpoint(c.Method, c.PathTemplate, "Requiere cuerpo: declara el endpoint con datos sintéticos."));
                continue;
            }

            var endpointNotes = new List<string>();
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            string? missing = null;
            foreach (var p in c.PathParameters)
            {
                if (p.Example is { } example && PathSampleRegex().IsMatch(example))
                {
                    values[p.Name] = example;
                    endpointNotes.Add($"{{{p.Name}}}={example} (ejemplo OpenAPI)");
                }
                else if (p.SchemaType is "integer" or "number")
                {
                    values[p.Name] = "1";
                    endpointNotes.Add($"{{{p.Name}}}=1 (valor sintético inferido del tipo {p.SchemaType})");
                }
                else
                {
                    missing = p.Name;
                    break;
                }
            }

            if (missing is not null)
            {
                excluded.Add(new ExcludedEndpoint(c.Method, c.PathTemplate, $"Sin valor sintético para el parámetro de ruta '{{{missing}}}': decláralo en el manifiesto."));
                continue;
            }

            planned.Add(new PlannedEndpoint(c.Name, c.Method, SubstitutePath(c.PathTemplate, values), c.PathTemplate, c.ExpectedStatus, 1, null, null, "openapi", endpointNotes));
        }

        return Filter(manifest, policy, planned, excluded, "openapi", notes);
    }

    private static EndpointPlan Filter(RunManifest manifest, PolicyEngine policy, List<PlannedEndpoint> planned, List<ExcludedEndpoint> excluded, string source, List<string> notes)
    {
        var selected = new List<PlannedEndpoint>();
        foreach (var e in planned)
        {
            if (manifest.Load.Include.Count > 0 && !manifest.Load.Include.Any(i => Matches(e, i)))
            {
                excluded.Add(new ExcludedEndpoint(e.Method, e.PathTemplate, "No está en load.include."));
            }
            else if (manifest.Load.Exclude.Any(x => Matches(e, x)))
            {
                excluded.Add(new ExcludedEndpoint(e.Method, e.PathTemplate, "Excluido por load.exclude."));
            }
            else if (!PolicyEngine.IsSafeRelativePath(e.Path))
            {
                excluded.Add(new ExcludedEndpoint(e.Method, e.PathTemplate, "Ruta no segura."));
            }
            else
            {
                selected.Add(e);
            }
        }

        var max = policy.Policy.Load.MaxEndpoints;
        if (selected.Count > max)
        {
            foreach (var e in selected.Skip(max))
            {
                excluded.Add(new ExcludedEndpoint(e.Method, e.PathTemplate, $"Supera el máximo de {max} endpoints."));
            }

            selected = selected.Take(max).ToList();
        }

        return new EndpointPlan(source, selected, excluded, notes);
    }

    private static bool Matches(PlannedEndpoint e, string filter) =>
        string.Equals(e.Name, filter, StringComparison.OrdinalIgnoreCase)
        || string.Equals(e.PathTemplate, filter, StringComparison.OrdinalIgnoreCase)
        || string.Equals(e.Path, filter, StringComparison.OrdinalIgnoreCase);

    private static string SubstitutePath(string template, IReadOnlyDictionary<string, string> values) =>
        PathParameterRegex().Replace(template, m => values.TryGetValue(m.Groups[1].Value, out var v) ? Uri.EscapeDataString(v) : m.Value);

    private static IEnumerable<JsonElement> EnumerateParameters(JsonElement parameters) =>
        parameters.ValueKind == JsonValueKind.Array ? parameters.EnumerateArray() : [];

    private static string? SchemaType(JsonElement parameter)
    {
        if (!parameter.TryGetProperty("schema", out var schema))
        {
            return null;
        }

        if (schema.TryGetProperty("type", out var type))
        {
            // OpenAPI 3.1 admite arrays de tipos (["integer", "null"]); en .NET 10 también "integer" con "pattern".
            return type.ValueKind == JsonValueKind.Array
                ? type.EnumerateArray().Select(t => t.GetString()).FirstOrDefault(t => t is not "null")
                : type.GetString();
        }

        return null;
    }

    private static string? Example(JsonElement parameter)
    {
        if (parameter.TryGetProperty("example", out var example) || (parameter.TryGetProperty("schema", out var s) && s.TryGetProperty("example", out example)))
        {
            return example.ValueKind switch
            {
                JsonValueKind.String => example.GetString(),
                JsonValueKind.Number => example.GetRawText(),
                _ => null,
            };
        }

        return null;
    }

    private static int ExpectedStatus(JsonElement operation)
    {
        if (!operation.TryGetProperty("responses", out var responses) || responses.ValueKind != JsonValueKind.Object)
        {
            return 200;
        }

        var codes = responses.EnumerateObject()
            .Select(r => int.TryParse(r.Name, out var code) ? code : 0)
            .Where(code => code is >= 200 and < 300)
            .Order()
            .ToList();
        return codes.Count > 0 ? codes[0] : 200;
    }

    private static string? SafeName(string? operationId) =>
        operationId is not null && EndpointNameRegex().IsMatch(operationId) ? operationId : null;

    private static string DefaultName(string method, string path)
    {
        var slug = NonSlugRegex().Replace(path.Trim('/'), "-").Trim('-');
        var name = $"{method}-{slug}";
        return name.Length > 64 ? name[..64] : name;
    }

    [GeneratedRegex(@"\{([A-Za-z_][A-Za-z0-9_]*)(?::[a-z]+)?\}")]
    private static partial Regex PathParameterRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_\-.~]{1,64}$")]
    private static partial Regex PathSampleRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]{1,64}$")]
    private static partial Regex EndpointNameRegex();

    [GeneratedRegex(@"[^A-Za-z0-9]+")]
    private static partial Regex NonSlugRegex();
}
