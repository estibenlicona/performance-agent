using System.Text.Json;
using System.Text.RegularExpressions;
using PerfAgent.Core.Policy;
using PerfAgent.Core.Runs;

namespace PerfAgent.Adapters.K6Mcp;

/// <summary>
/// Validación estática previa a cualquier delegación (la validación upstream de mcp-k6 ejecuta 1 iteración y genera
/// tráfico, así que solo se permite después de esta). Comprueba integridad, destino, métodos y límites.
/// </summary>
public static partial class K6ScriptValidator
{
    private static readonly string[] ForbiddenTokens =
    [
        "open(", "require(", "import(", "eval(", "Function(", "exec(", "k6/x/", "k6/experimental", "k6/browser", "k6/ws",
        "k6/net/grpc", "http.batch", "__ENV", "http.file", "k6/execution",
    ];

    private static readonly string[] AllowedImports = ["k6", "k6/http", "k6/metrics"];

    public static PolicyDecision Validate(string script, ScenarioRecord record, PolicyEngine policy, Core.Manifest.RunManifest manifest, Uri expectedBaseUrl)
    {
        var b = new PolicyDecisionBuilder();
        if (!script.StartsWith(K6ScriptGenerator.HeaderMarker, StringComparison.Ordinal))
        {
            b.Deny("k6.not-generated", "script", "Solo se ejecutan escenarios generados por perf-lab (falta la cabecera).");
        }

        var sha = K6ScriptGenerator.Sha256(script);
        if (!string.Equals(sha, record.ScriptSha256, StringComparison.Ordinal))
        {
            b.Deny("k6.tampered", "script", "El script cambió desde que se generó (SHA-256 distinto). Regenera el escenario.");
        }

        b.Merge(policy.EvaluateLoadTarget(expectedBaseUrl, expectedBaseUrl.Port));
        b.Merge(policy.EvaluateLoad(record.Kind, record.Load));

        foreach (Match url in UrlLiteralRegex().Matches(script))
        {
            if (!string.Equals(url.Value.TrimEnd('/'), expectedBaseUrl.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal))
            {
                b.Deny("k6.foreign-url", "script", $"El script contiene una URL distinta de la instancia de laboratorio: {url.Value}");
            }
        }

        foreach (var token in ForbiddenTokens.Where(t => script.Contains(t, StringComparison.Ordinal)))
        {
            b.Deny("k6.forbidden-construct", "script", $"Construcción no permitida en el escenario: '{token}'.");
        }

        foreach (Match import in ImportRegex().Matches(script))
        {
            if (!AllowedImports.Contains(import.Groups["module"].Value, StringComparer.Ordinal))
            {
                b.Deny("k6.import", "script", $"Módulo no permitido: {import.Groups["module"].Value}.");
            }
        }

        if (!script.Contains("blacklistIPs: " + K6ScriptGenerator.ToJs(K6ScriptGenerator.BlockedIpRanges), StringComparison.Ordinal)
            || !script.Contains("blockHostnames: ['*']", StringComparison.Ordinal))
        {
            b.Deny("k6.network-guard", "script", "Faltan las protecciones de red (blacklistIPs/blockHostnames).");
        }

        var endpoints = EndpointsRegex().Match(script);
        if (!endpoints.Success)
        {
            b.Deny("k6.endpoints", "script", "No se encontró la lista de endpoints.");
        }
        else
        {
            using var json = JsonDocument.Parse(endpoints.Groups["json"].Value);
            foreach (var e in json.RootElement.EnumerateArray())
            {
                var method = e.GetProperty("method").GetString() ?? string.Empty;
                var path = e.GetProperty("path").GetString() ?? string.Empty;
                if (!policy.IsMethodAllowed(manifest, method))
                {
                    b.Deny("k6.method", "script", $"{method} {path}: método no autorizado por la política.");
                }

                if (!PolicyEngine.IsSafeRelativePath(path))
                {
                    b.Deny("k6.path", "script", $"Ruta no segura: {path}.");
                }
            }
        }

        return b.Build();
    }

    [GeneratedRegex(@"https?://[^\s'""`]+")]
    private static partial Regex UrlLiteralRegex();

    [GeneratedRegex(@"^import\s+.*?\s+from\s+'(?<module>[^']+)';", RegexOptions.Multiline)]
    private static partial Regex ImportRegex();

    [GeneratedRegex(@"^const ENDPOINTS = (?<json>\[.*\]);$", RegexOptions.Multiline)]
    private static partial Regex EndpointsRegex();
}
