using System.Globalization;
using System.Text;
using PerfAgent.Core.Contracts;
using PerfAgent.Core.Runs;

namespace PerfAgent.Reporting;

/// <param name="Direction">better, worse, same o n/a (según si la métrica mejora al bajar o al subir).</param>
public sealed record MetricDelta(string Metric, string Unit, double? Base, double? Candidate, double? AbsoluteChange, double? RelativeChangePercent, string Direction);

public sealed record RunComparison(
    string BaseRunId,
    string CandidateRunId,
    bool Comparable,
    IReadOnlyList<string> MaterialDifferences,
    IReadOnlyList<string> ChangesUnderEvaluation,
    IReadOnlyList<MetricDelta> Deltas,
    IReadOnlyList<string> Notes,
    string Summary);

/// <summary>
/// Compara dos ejecuciones. Rechaza la comparación si difieren materialmente en escenario, carga, perfil de
/// ambiente, fidelidad de dependencias, runtime o máquina; la revisión del código y la configuración declarada
/// se listan como "cambios bajo evaluación" (lo que se quiere medir).
/// </summary>
public static class RunComparer
{
    /// <summary>Variación relativa por debajo de la cual una métrica se considera igual (ruido de medición local).</summary>
    public const double NoiseThresholdPercent = 5;

    public static RunComparison Compare(LabRun baseRun, LabRun candidate)
    {
        var material = new List<string>();
        var changes = new List<string>();
        var notes = new List<string>();
        var b = baseRun.LatestLoadTest(LoadTestKind.Baseline);
        var c = candidate.LatestLoadTest(LoadTestKind.Baseline);
        if (b is null || c is null)
        {
            material.Add("Alguna de las ejecuciones no tiene prueba baseline.");
        }
        else
        {
            if (b.ScenarioHash != c.ScenarioHash)
            {
                material.Add($"Escenario k6 distinto (hash {Short(b.ScenarioHash)} vs {Short(c.ScenarioHash)}): endpoints, thresholds o carga cambiaron.");
            }

            if (b.Load != c.Load)
            {
                material.Add($"Carga distinta: {Describe(b.Load)} vs {Describe(c.Load)}.");
            }

            if (b.Provider?.IsFallback != c.Provider?.IsFallback)
            {
                notes.Add("Una ejecución usó mcp-k6 y la otra el fallback CLI: mismo k6, distinto ejecutor.");
            }
        }

        if (baseRun.Profile != candidate.Profile)
        {
            material.Add($"Perfil de ambiente distinto: {baseRun.Profile} vs {candidate.Profile}.");
        }

        var baseFidelity = Fidelity(baseRun);
        var candidateFidelity = Fidelity(candidate);
        if (baseFidelity != candidateFidelity)
        {
            material.Add($"Fidelidad de dependencias distinta: [{baseFidelity}] vs [{candidateFidelity}].");
        }

        if (baseRun.Target?.ProjectPath != candidate.Target?.ProjectPath)
        {
            material.Add($"Proyecto distinto: {baseRun.Target?.ProjectPath} vs {candidate.Target?.ProjectPath}.");
        }

        if (baseRun.Versions.TargetRuntime != candidate.Versions.TargetRuntime)
        {
            material.Add($"Runtime distinto: {baseRun.Versions.TargetRuntime} vs {candidate.Versions.TargetRuntime}.");
        }

        if (baseRun.Versions.ProcessorCount != candidate.Versions.ProcessorCount || baseRun.Versions.OperatingSystem != candidate.Versions.OperatingSystem)
        {
            material.Add("Máquina distinta (sistema operativo o núcleos).");
        }

        if (baseRun.Versions.K6 != candidate.Versions.K6)
        {
            notes.Add($"Versión de k6 distinta: {baseRun.Versions.K6} vs {candidate.Versions.K6}.");
        }

        if (baseRun.Target?.Revision?.Commit != candidate.Target?.Revision?.Commit)
        {
            changes.Add($"Revisión del código: {Short(baseRun.Target?.Revision?.Commit)} → {Short(candidate.Target?.Revision?.Commit)}.");
        }

        if (baseRun.ManifestSha256 != candidate.ManifestSha256)
        {
            changes.Add($"Manifiesto distinto ({baseRun.ManifestPath} → {candidate.ManifestPath}): revisa api.settings (configuración bajo evaluación).");
        }

        var deltas = new List<MetricDelta>();
        if (b is not null && c is not null)
        {
            deltas.Add(Delta("p50", "ms", b.Latency?.Med, c.Latency?.Med, lowerIsBetter: true));
            deltas.Add(Delta("p95", "ms", b.Latency?.P95, c.Latency?.P95, lowerIsBetter: true));
            deltas.Add(Delta("p99", "ms", b.Latency?.P99, c.Latency?.P99, lowerIsBetter: true));
            deltas.Add(Delta("throughput", "req/s", b.RequestsPerSecond, c.RequestsPerSecond, lowerIsBetter: false));
            deltas.Add(Delta("error-rate", "%", b.ErrorRate * 100, c.ErrorRate * 100, lowerIsBetter: true));
        }

        var rb = Counters(baseRun, b);
        var rc = Counters(candidate, c);
        if (rb is not null && rc is not null)
        {
            deltas.Add(Delta("cpu-avg", "%", rb.CpuPercentAvg, rc.CpuPercentAvg, lowerIsBetter: true));
            deltas.Add(Delta("allocation-rate", "MB/s", rb.AllocationRateMbPerSec, rc.AllocationRateMbPerSec, lowerIsBetter: true));
            deltas.Add(Delta("gen2-collections", "count", rb.Gen2Collections, rc.Gen2Collections, lowerIsBetter: true));
            deltas.Add(Delta("threadpool-threads-max", "threads", rb.ThreadPoolThreadsMax, rc.ThreadPoolThreadsMax, lowerIsBetter: true));
            deltas.Add(Delta("threadpool-starvation-adjustments", "events", rb.ThreadPoolStarvationAdjustments, rc.ThreadPoolStarvationAdjustments, lowerIsBetter: true));
        }
        else
        {
            notes.Add("Sin métricas del runtime en alguna de las ejecuciones: solo se comparan métricas de k6.");
        }

        var comparable = material.Count == 0;
        notes.Add($"Variaciones menores al {NoiseThresholdPercent}% se tratan como iguales (ruido de medición local). Repite cada ejecución para estimar la variabilidad antes de concluir.");
        return new RunComparison(baseRun.RunId, candidate.RunId, comparable, material, changes, deltas, notes, Summarize(comparable, material, deltas));
    }

    private static RuntimeSummary? Counters(LabRun run, LoadTestRun? load) =>
        (run.RuntimeSnapshots.LastOrDefault(s => s.Kind == "counters" && s.LoadTestId == load?.Id)
         ?? run.RuntimeSnapshots.LastOrDefault(s => s.Kind == "counters"))?.Summary;

    private static MetricDelta Delta(string metric, string unit, double? b, double? c, bool lowerIsBetter)
    {
        if (b is null || c is null)
        {
            return new MetricDelta(metric, unit, b, c, null, null, "n/a");
        }

        var abs = Math.Round(c.Value - b.Value, 3);
        double? rel = b.Value == 0 ? null : Math.Round((c.Value - b.Value) / Math.Abs(b.Value) * 100, 1);
        var direction = rel is null
            ? (abs == 0 ? "same" : (abs < 0) == lowerIsBetter ? "better" : "worse")
            : Math.Abs(rel.Value) < NoiseThresholdPercent ? "same" : (rel < 0) == lowerIsBetter ? "better" : "worse";
        return new MetricDelta(metric, unit, Math.Round(b.Value, 3), Math.Round(c.Value, 3), abs, rel, direction);
    }

    private static string Summarize(bool comparable, IReadOnlyList<string> material, IReadOnlyList<MetricDelta> deltas)
    {
        if (!comparable)
        {
            return "Comparación rechazada: las ejecuciones difieren materialmente (" + string.Join(" ", material) + ").";
        }

        var sb = new StringBuilder("Ejecuciones comparables. ");
        foreach (var d in deltas.Where(d => d.Direction is "better" or "worse"))
        {
            sb.Append(CultureInfo.InvariantCulture, $"{d.Metric}: {d.Base} → {d.Candidate} {d.Unit} ({(d.RelativeChangePercent is { } r ? $"{r:+0.#;-0.#}%" : "n/a")}, {(d.Direction == "better" ? "mejor" : "peor")}). ");
        }

        if (!deltas.Any(d => d.Direction is "better" or "worse"))
        {
            sb.Append("Sin cambios por encima del umbral de ruido.");
        }

        return sb.ToString().Trim();
    }

    private static string Fidelity(LabRun run) =>
        string.Join(", ", (run.Environment?.Dependencies ?? []).OrderBy(d => d.Name, StringComparer.Ordinal).Select(d => $"{d.Name}={d.Fidelity}"));

    private static string Describe(LoadProfile load) =>
        $"{load.Executor} {load.Vus} VUs {load.DurationSeconds}s" + (load.Iterations is { } i ? $" {i} it." : string.Empty);

    private static string Short(string? value) => value is { Length: > 12 } ? value[..12] : value ?? "n/d";
}
