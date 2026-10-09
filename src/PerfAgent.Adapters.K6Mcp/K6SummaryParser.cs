using System.Text.Json;
using PerfAgent.Core.Contracts;

namespace PerfAgent.Adapters.K6Mcp;

/// <summary>Métricas normalizadas a partir del resumen de k6 (datos de <c>handleSummary</c>).</summary>
public sealed record K6Summary(
    LatencyStats? Latency,
    long TotalRequests,
    double? RequestsPerSecond,
    long FailedRequests,
    double? ErrorRate,
    long ChecksPassed,
    long ChecksFailed,
    long Iterations,
    long DroppedIterations,
    int? VusMax,
    double? TestRunDurationMs,
    IReadOnlyDictionary<string, long> StatusClasses,
    IReadOnlyList<EndpointStats> Endpoints,
    IReadOnlyList<ThresholdResult> Thresholds);

public static class K6SummaryParser
{
    private static readonly HashSet<string> InternalExpressions = ["max>=0", "rate>=0"];

    /// <summary>Extrae el JSON delimitado por los marcadores que escribe el <c>handleSummary</c> generado.</summary>
    public static string? ExtractSummaryJson(string stdout)
    {
        var begin = stdout.IndexOf(K6ScriptGenerator.SummaryBegin, StringComparison.Ordinal);
        if (begin < 0)
        {
            return null;
        }

        begin += K6ScriptGenerator.SummaryBegin.Length;
        var end = stdout.IndexOf(K6ScriptGenerator.SummaryEnd, begin, StringComparison.Ordinal);
        return end < 0 ? null : stdout[begin..end];
    }

    /// <summary>Elimina el bloque del resumen para conservar solo la salida legible (banner, progreso, avisos).</summary>
    public static string StripSummary(string stdout)
    {
        var begin = stdout.IndexOf(K6ScriptGenerator.SummaryBegin, StringComparison.Ordinal);
        var end = stdout.IndexOf(K6ScriptGenerator.SummaryEnd, StringComparison.Ordinal);
        return begin < 0 || end < begin ? stdout : stdout[..begin] + "<resumen JSON extraído>" + stdout[(end + K6ScriptGenerator.SummaryEnd.Length)..];
    }

    public static K6Summary Parse(string summaryJson, IReadOnlyList<Core.Discovery.PlannedEndpoint> plannedEndpoints)
    {
        using var document = JsonDocument.Parse(summaryJson);
        var metrics = document.RootElement.GetProperty("metrics");
        var duration = metrics.TryGetProperty("http_req_duration", out var d) ? Latency(d) : null;
        var (requests, rps) = Counter(metrics, "http_reqs");
        var (failedRate, failed, _) = Rate(metrics, "http_req_failed");
        var (_, checksPassed, checksFailed) = Rate(metrics, "checks");
        var (iterations, _) = Counter(metrics, "iterations");
        var (dropped, _) = Counter(metrics, "dropped_iterations");
        int? vusMax = metrics.TryGetProperty("vus_max", out var vm) && vm.GetProperty("values").TryGetProperty("max", out var vmax) ? (int)vmax.GetDouble() : null;
        double? runMs = document.RootElement.TryGetProperty("state", out var state) && state.TryGetProperty("testRunDurationMs", out var t) ? t.GetDouble() : null;

        var classes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var statusClass in K6ScriptGenerator.StatusClasses)
        {
            var (count, _) = Counter(metrics, $"perflab_status_{statusClass}");
            classes[statusClass] = count;
        }

        var endpoints = plannedEndpoints.Select(e =>
        {
            var latency = metrics.TryGetProperty($"http_req_duration{{name:{e.Name}}}", out var ed) ? Latency(ed) : null;
            var (rate, epFailed, epOk) = Rate(metrics, $"http_req_failed{{name:{e.Name}}}");
            var (_, cPass, cFail) = Rate(metrics, $"checks{{name:{e.Name}}}");
            return new EndpointStats(e.Name, e.Method, e.Path, epFailed + epOk, rate, latency, cPass, cFail);
        }).ToList();

        var thresholds = new List<ThresholdResult>();
        foreach (var metric in metrics.EnumerateObject())
        {
            if (!metric.Value.TryGetProperty("thresholds", out var th))
            {
                continue;
            }

            foreach (var expression in th.EnumerateObject().Where(x => !InternalExpressions.Contains(x.Name)))
            {
                thresholds.Add(new ThresholdResult(metric.Name, expression.Name, expression.Value.TryGetProperty("ok", out var ok) && ok.GetBoolean()));
            }
        }

        return new K6Summary(duration, requests, rps, failed, failedRate, checksPassed, checksFailed, iterations, dropped, vusMax, runMs, classes, endpoints, thresholds);
    }

    private static LatencyStats Latency(JsonElement metric)
    {
        var v = metric.GetProperty("values");
        return new LatencyStats(Get(v, "avg"), Get(v, "min"), Get(v, "med"), Get(v, "p(90)"), Get(v, "p(95)"), Get(v, "p(99)"), Get(v, "max"));
    }

    private static (long Count, double? Rate) Counter(JsonElement metrics, string name)
    {
        if (!metrics.TryGetProperty(name, out var metric))
        {
            return (0, null);
        }

        var v = metric.GetProperty("values");
        return ((long)(Get(v, "count") ?? 0), Get(v, "rate"));
    }

    /// <summary>Métrica Rate de k6: <c>passes</c> cuenta valores verdaderos (p. ej. peticiones fallidas en http_req_failed).</summary>
    private static (double? Rate, long Passes, long Fails) Rate(JsonElement metrics, string name)
    {
        if (!metrics.TryGetProperty(name, out var metric))
        {
            return (null, 0, 0);
        }

        var v = metric.GetProperty("values");
        return (Get(v, "rate"), (long)(Get(v, "passes") ?? 0), (long)(Get(v, "fails") ?? 0));
    }

    private static double? Get(JsonElement values, string name) =>
        values.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? Math.Round(value.GetDouble(), 4) : null;
}
