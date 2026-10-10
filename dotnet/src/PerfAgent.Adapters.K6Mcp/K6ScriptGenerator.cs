using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using PerfAgent.Core.Contracts;
using PerfAgent.Core.Discovery;

namespace PerfAgent.Adapters.K6Mcp;

/// <summary>Escenario k6 generado por la fachada (el único tipo de script que se ejecuta en el MVP).</summary>
public sealed record K6Scenario(
    LoadTestKind Kind,
    string Script,
    string ScriptSha256,
    string ScenarioHash,
    string BaseUrl,
    LoadProfile Load,
    IReadOnlyList<PlannedEndpoint> Endpoints,
    IReadOnlyDictionary<string, IReadOnlyList<string>> UserThresholds);

/// <summary>
/// Genera scripts k6 deterministas a partir de un plan de endpoints validado. Los datos variables se embeben como
/// literales JSON (sin concatenar código), la URL base es siempre la instancia de laboratorio y el script lleva
/// <c>blacklistIPs</c> + <c>blockHostnames</c> para que k6 rechace cualquier destino que no sea loopback.
/// </summary>
public static class K6ScriptGenerator
{
    public const string HeaderMarker = "// perf-lab:generated v1";
    public const string SummaryBegin = "@@PERFLAB-SUMMARY-BEGIN@@";
    public const string SummaryEnd = "@@PERFLAB-SUMMARY-END@@";
    public const string BaseUrlPlaceholder = "__PERFLAB_BASE_URL__";
    public const int GracefulStopSeconds = 5;

    /// <summary>Complemento de 127.0.0.0/8 en IPv4 más todo IPv6: k6 solo puede conectar con loopback IPv4.</summary>
    public static readonly IReadOnlyList<string> BlockedIpRanges =
    [
        "0.0.0.0/2", "64.0.0.0/3", "96.0.0.0/4", "112.0.0.0/5", "120.0.0.0/6", "124.0.0.0/7", "126.0.0.0/8", "128.0.0.0/1", "::/0",
    ];

    public static readonly IReadOnlyList<string> StatusClasses = ["2xx", "3xx", "4xx", "5xx", "0"];

    // Literales JSON legibles dentro del script: las comillas y barras se escapan (no se puede salir del literal);
    // los separadores de línea U+2028/U+2029 se escapan aparte en ToJs.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Serializa un valor como literal JavaScript seguro (JSON escapado).</summary>
    public static string ToJs(object? value) =>
        JsonSerializer.Serialize(value, Json).Replace("\u2028", "\\u2028", StringComparison.Ordinal)
            .Replace("\u2029", "\\u2029", StringComparison.Ordinal);

    public static K6Scenario Generate(
        LoadTestKind kind,
        string baseUrl,
        IReadOnlyList<PlannedEndpoint> endpoints,
        LoadProfile load,
        IReadOnlyDictionary<string, List<string>> userThresholds)
    {
        if (endpoints.Count == 0)
        {
            throw new ArgumentException("No hay endpoints que probar.", nameof(endpoints));
        }

        var thresholds = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var e in endpoints)
        {
            // Umbrales siempre verdaderos: hacen que k6 exporte submétricas por endpoint en el resumen.
            Add(thresholds, $"http_req_duration{{name:{e.Name}}}", "max>=0");
            Add(thresholds, $"http_req_failed{{name:{e.Name}}}", "rate>=0");
            Add(thresholds, $"checks{{name:{e.Name}}}", "rate>=0");
        }

        var user = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (metric, expressions) in userThresholds)
        {
            foreach (var expression in expressions)
            {
                Add(thresholds, metric, expression.Trim());
            }

            user[metric] = expressions.Select(x => x.Trim()).ToList();
        }

        var endpointData = endpoints.Select(e => new
        {
            name = e.Name,
            method = e.Method,
            path = e.Path,
            expectedStatus = e.ExpectedStatus,
            weight = e.Weight,
            bodyContains = e.BodyContains,
            body = e.Body,
        }).ToList();

        var sb = new StringBuilder();
        sb.AppendLine(HeaderMarker + " — no editar: se regenera con perf-lab/generate_k6_scenario y se ejecuta solo si su SHA-256 coincide.");
        sb.AppendLine(CultureInfo.InvariantCulture, $"// kind={kind.ToString().ToLowerInvariant()} executor={load.Executor} vus={load.Vus} duration={load.DurationSeconds}s rampUp={load.RampUpSeconds}s iterations={load.Iterations?.ToString(CultureInfo.InvariantCulture) ?? "-"}");
        sb.AppendLine("import http from 'k6/http';");
        sb.AppendLine("import { check } from 'k6';");
        sb.AppendLine("import { Counter } from 'k6/metrics';");
        sb.AppendLine();
        sb.AppendLine($"const BASE_URL = {ToJs(baseUrl)};");
        sb.AppendLine($"const ENDPOINTS = {ToJs(endpointData)};");
        sb.AppendLine("const TOTAL_WEIGHT = ENDPOINTS.reduce((sum, e) => sum + e.weight, 0);");
        sb.AppendLine("const STATUS = {");
        foreach (var statusClass in StatusClasses)
        {
            sb.AppendLine($"  '{statusClass}': new Counter('perflab_status_{statusClass}'),");
        }

        sb.AppendLine("};");
        sb.AppendLine();
        sb.AppendLine("export const options = {");
        sb.AppendLine($"  scenarios: {{ {ScenarioName(kind)}: {Executor(kind, load)} }},");
        sb.AppendLine("  thresholds: {");
        foreach (var (metric, expressions) in thresholds)
        {
            sb.AppendLine($"    {ToJs(metric)}: {ToJs(expressions)},");
        }

        sb.AppendLine("  },");
        sb.AppendLine("  summaryTrendStats: ['avg', 'min', 'med', 'max', 'p(90)', 'p(95)', 'p(99)'],");
        sb.AppendLine($"  blacklistIPs: {ToJs(BlockedIpRanges)},");
        sb.AppendLine("  blockHostnames: ['*'],");
        sb.AppendLine("  userAgent: 'perf-lab-k6/1',");
        sb.AppendLine("};");
        sb.AppendLine();
        sb.AppendLine("function pick() {");
        sb.AppendLine("  let slot = ((__VU * 7919) + __ITER) % TOTAL_WEIGHT;");
        sb.AppendLine("  for (const e of ENDPOINTS) {");
        sb.AppendLine("    if (slot < e.weight) { return e; }");
        sb.AppendLine("    slot -= e.weight;");
        sb.AppendLine("  }");
        sb.AppendLine("  return ENDPOINTS[0];");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("function statusClass(status) {");
        sb.AppendLine("  if (status >= 200 && status < 300) { return '2xx'; }");
        sb.AppendLine("  if (status >= 300 && status < 400) { return '3xx'; }");
        sb.AppendLine("  if (status >= 400 && status < 500) { return '4xx'; }");
        sb.AppendLine("  if (status >= 500) { return '5xx'; }");
        sb.AppendLine("  return '0';");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("export default function () {");
        sb.AppendLine("  const e = pick();");
        sb.AppendLine("  const params = { tags: { name: e.name }, timeout: '30s', headers: e.body === null ? {} : { 'Content-Type': 'application/json' } };");
        sb.AppendLine("  const res = http.request(e.method, BASE_URL + e.path, e.body, params);");
        sb.AppendLine("  STATUS[statusClass(res.status)].add(1, { name: e.name });");
        sb.AppendLine("  const checks = {};");
        sb.AppendLine("  checks[e.name + ' status ' + e.expectedStatus] = (r) => r.status === e.expectedStatus;");
        sb.AppendLine("  if (e.bodyContains !== null) {");
        sb.AppendLine("    checks[e.name + ' body contains expected text'] = (r) => typeof r.body === 'string' && r.body.includes(e.bodyContains);");
        sb.AppendLine("  }");
        sb.AppendLine("  check(res, checks, { name: e.name });");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("export function handleSummary(data) {");
        sb.AppendLine($"  return {{ stdout: '\\n{SummaryBegin}' + JSON.stringify(data) + '{SummaryEnd}\\n' }};");
        sb.AppendLine("}");

        var script = sb.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
        var normalized = script.Replace(ToJs(baseUrl), ToJs(BaseUrlPlaceholder), StringComparison.Ordinal);
        return new K6Scenario(kind, script, Sha256(script), Sha256(normalized), baseUrl, load, endpoints, user);
    }

    public static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string ScenarioName(LoadTestKind kind) => kind == LoadTestKind.Smoke ? "smoke" : "baseline";

    private static string Executor(LoadTestKind kind, LoadProfile load)
    {
        var inv = CultureInfo.InvariantCulture;
        if (kind == LoadTestKind.Smoke)
        {
            return string.Create(inv, $"{{ executor: 'shared-iterations', vus: {load.Vus}, iterations: {load.Iterations ?? 1}, maxDuration: '{load.DurationSeconds}s' }}");
        }

        return load.Executor == "ramping-vus"
            ? string.Create(inv, $"{{ executor: 'ramping-vus', startVUs: 0, stages: [{{ duration: '{load.RampUpSeconds}s', target: {load.Vus} }}, {{ duration: '{load.DurationSeconds}s', target: {load.Vus} }}], gracefulRampDown: '{GracefulStopSeconds}s', gracefulStop: '{GracefulStopSeconds}s' }}")
            : string.Create(inv, $"{{ executor: 'constant-vus', vus: {load.Vus}, duration: '{load.DurationSeconds}s', gracefulStop: '{GracefulStopSeconds}s' }}");
    }

    private static void Add(SortedDictionary<string, List<string>> thresholds, string metric, string expression)
    {
        if (!thresholds.TryGetValue(metric, out var list))
        {
            thresholds[metric] = list = [];
        }

        if (!list.Contains(expression, StringComparer.Ordinal))
        {
            list.Add(expression);
        }
    }
}
