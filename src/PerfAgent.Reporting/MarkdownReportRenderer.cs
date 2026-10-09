using System.Globalization;
using System.Text;
using System.Text.Json;
using PerfAgent.Core.Contracts;

namespace PerfAgent.Reporting;

/// <summary>
/// Renderiza el informe en Markdown. Separa explícitamente observaciones, hipótesis (con evidencia a favor,
/// en contra y evidencia que falta), limitaciones y preguntas abiertas: un síntoma nunca se presenta como causa.
/// </summary>
public static class MarkdownReportRenderer
{
    public static string Render(DiagnosticReport r)
    {
        var md = new StringBuilder();
        md.AppendLine($"# Informe de diagnóstico de rendimiento — `{r.RunId}`");
        md.AppendLine();
        md.AppendLine($"- **Generado:** {r.GeneratedAt:yyyy-MM-dd HH:mm:ss} UTC");
        md.AppendLine($"- **Estado de la ejecución:** `{Kebab(r.Phase)}`");
        md.AppendLine($"- **Veredicto:** **{VerdictText(r.Verdict)}** (`{Kebab(r.Verdict)}`)");
        if (r.Failure != FailureKind.None)
        {
            md.AppendLine($"- **Fallo de ambiente:** `{Kebab(r.Failure)}` — {Esc(r.FailureDetail)}");
        }

        md.AppendLine($"- **Manifiesto:** `{r.ManifestPath}` (sha256 `{Short(r.ManifestSha256)}`)");
        md.AppendLine();
        RenderSummary(md, r);
        RenderTarget(md, r);
        RenderEnvironment(md, r);
        RenderLoad(md, r);
        RenderRuntime(md, r);
        RenderObservations(md, r);
        RenderHypotheses(md, r);
        RenderList(md, "Limitaciones", r.Limitations);
        RenderList(md, "Preguntas abiertas", r.OpenQuestions);
        RenderNext(md, r);
        RenderList(md, "Recomendaciones (no se aplican cambios automáticamente)", r.Recommendations);
        RenderProviders(md, r);
        RenderCleanup(md, r);
        RenderList(md, "Artefactos", r.Artifacts.Select(a => $"`{a}`").ToList());
        md.AppendLine("---");
        md.AppendLine("*Informe generado por perf-lab. Las conclusiones se apoyan solo en los artefactos listados; reprodúcelas con el mismo manifiesto y versiones.*");
        return md.ToString();
    }

    private static void RenderSummary(StringBuilder md, DiagnosticReport r)
    {
        md.AppendLine("## Resumen");
        md.AppendLine();
        var baseline = r.LoadTests.LastOrDefault(l => l.Kind == LoadTestKind.Baseline && !l.IsEvidenceLoad);
        if (baseline is not null)
        {
            md.AppendLine($"- Carga: {baseline.Load.Vus} VUs, {baseline.Load.DurationSeconds}s → {F(baseline.RequestsPerSecond)} req/s, p95 {Ms(baseline.Latency?.P95)}, errores {P(baseline.ErrorRate)}.");
        }

        foreach (var h in r.Hypotheses.Where(h => h.Status is HypothesisStatus.Confirmed or HypothesisStatus.Supported))
        {
            md.AppendLine($"- {StatusText(h.Status)} ({Kebab(h.Confidence)}): {Esc(h.Statement)}");
        }

        if (!r.Hypotheses.Any(h => h.Status is HypothesisStatus.Confirmed or HypothesisStatus.Supported))
        {
            md.AppendLine($"- {VerdictExplanation(r.Verdict)}");
        }

        md.AppendLine();
    }

    private static void RenderTarget(StringBuilder md, DiagnosticReport r)
    {
        md.AppendLine("## Objetivo y versiones");
        md.AppendLine();
        var t = r.Target;
        md.AppendLine("| Elemento | Valor |");
        md.AppendLine("|---|---|");
        md.AppendLine($"| Proyecto | `{Esc(t?.ProjectPath)}` |");
        md.AppendLine($"| Framework | {Esc(t?.SelectedFramework)} ({Esc(t?.Runtime?.Compatibility)}) |");
        md.AppendLine($"| Revisión | {Esc(t?.Revision?.Commit ?? t?.Revision?.Note)}{(t?.Revision?.HasUncommittedChanges == true ? " (con cambios sin confirmar)" : string.Empty)} |");
        md.AppendLine($"| Runtime del proceso | {Esc(r.Versions.TargetRuntime)} |");
        md.AppendLine($"| SDK .NET | {Esc(r.Versions.DotNetSdk)} |");
        md.AppendLine($"| k6 | {Esc(r.Versions.K6)} |");
        md.AppendLine($"| Proveedor k6 | {Esc(r.Versions.K6McpProvider)} |");
        md.AppendLine($"| Proveedor de diagnóstico | {Esc(r.Versions.DiagnosticsProvider)} |");
        md.AppendLine($"| perf-lab | {Esc(r.Versions.PerfAgent)} |");
        md.AppendLine($"| Sistema | {Esc(r.Versions.OperatingSystem)}, {r.Versions.ProcessorCount?.ToString(CultureInfo.InvariantCulture) ?? "?"} núcleos lógicos |");
        md.AppendLine();
    }

    private static void RenderEnvironment(StringBuilder md, DiagnosticReport r)
    {
        md.AppendLine("## Ambiente y fidelidad");
        md.AppendLine();
        if (r.Environment is not { } env)
        {
            md.AppendLine("_Sin preflight._");
            md.AppendLine();
            return;
        }

        md.AppendLine($"Perfil `{Kebab(env.Kind)}`, ambiente `{Esc(env.EnvironmentLabel)}`, decisión del preflight: **{Kebab(env.Decision)}**.");
        md.AppendLine();
        if (env.Dependencies.Count == 0)
        {
            md.AppendLine("_Sin dependencias declaradas._");
        }
        else
        {
            md.AppendLine("| Dependencia | Modo | Requerida | Fidelidad | Checks | Detalle |");
            md.AppendLine("|---|---|---|---|---|---|");
            foreach (var d in env.Dependencies)
            {
                var checks = string.Join(", ", d.Checks.Select(c => $"{c.Name}:{Kebab(c.Outcome)}"));
                md.AppendLine($"| {Esc(d.Name)} | {Kebab(d.Mode)} | {(d.Required ? "sí" : "no")} | **{FidelityText(d.Fidelity)}** | {Esc(checks)} | {Esc(d.Detail)} |");
            }
        }

        foreach (var reason in env.BlockingReasons)
        {
            md.AppendLine($"- ⛔ {Esc(reason)}");
        }

        md.AppendLine();
    }

    private static void RenderLoad(StringBuilder md, DiagnosticReport r)
    {
        md.AppendLine("## Pruebas de carga (k6)");
        md.AppendLine();
        if (r.LoadTests.Count == 0)
        {
            md.AppendLine("_No se ejecutaron pruebas de carga._");
            md.AppendLine();
            return;
        }

        md.AppendLine("| Prueba | Estado | Carga | Peticiones | req/s | Error | p50 | p95 | p99 | Máx | Checks | Proveedor |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var l in r.LoadTests)
        {
            var load = l.Kind == LoadTestKind.Smoke
                ? $"{l.Load.Vus} VU × {l.Load.Iterations} it."
                : $"{l.Load.Vus} VUs × {l.Load.DurationSeconds}s ({l.Load.Executor})";
            var provider = l.Provider is null ? "?" : $"{l.Provider.Provider}/{l.Provider.Tool}{(l.Provider.IsFallback ? " (fallback)" : string.Empty)}";
            md.AppendLine($"| {Kebab(l.Kind)}{(l.IsEvidenceLoad ? " (carga de evidencia)" : string.Empty)} | {Kebab(l.Status)} | {load} | {l.TotalRequests} | {F(l.RequestsPerSecond)} | {P(l.ErrorRate)} | {Ms(l.Latency?.Med)} | {Ms(l.Latency?.P95)} | {Ms(l.Latency?.P99)} | {Ms(l.Latency?.Max)} | {l.ChecksPassed}/{l.ChecksPassed + l.ChecksFailed} | {Esc(provider)} |");
        }

        md.AppendLine();
        var baseline = r.LoadTests.LastOrDefault(l => l.Kind == LoadTestKind.Baseline && !l.IsEvidenceLoad);
        if (baseline is not null)
        {
            md.AppendLine("### Endpoints (baseline)");
            md.AppendLine();
            md.AppendLine("| Endpoint | Método | Ruta | Peticiones | Error | p95 | Checks fallidos |");
            md.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var e in baseline.Endpoints)
            {
                md.AppendLine($"| {Esc(e.Name)} | {e.Method} | `{Esc(e.Path)}` | {e.Requests} | {P(e.ErrorRate)} | {Ms(e.Latency?.P95)} | {e.ChecksFailed} |");
            }

            md.AppendLine();
            if (baseline.Thresholds.Count > 0)
            {
                md.AppendLine("Thresholds: " + string.Join("; ", baseline.Thresholds.Select(t => $"`{t.Metric} {t.Expression}` {(t.Passed ? "✅" : "❌")}")));
                md.AppendLine();
            }

            if (baseline.StatusClasses.Count > 0)
            {
                md.AppendLine("Clases de estado HTTP: " + string.Join(", ", baseline.StatusClasses.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key}={kv.Value}")));
                md.AppendLine();
            }

            if (baseline.DependencyCalls.Count > 0)
            {
                md.AppendLine("Llamadas a dependencias simuladas (medidas por el mock): " + string.Join("; ", baseline.DependencyCalls.Select(d => $"{d.Dependency} `{d.Route}` {d.Requests} llamadas, {d.Failures} fallos, media {Ms(d.AvgLatencyMs)}")));
                md.AppendLine();
            }

            if (baseline.Timeline.Count > 0)
            {
                md.AppendLine("<details><summary>Línea temporal de k6 (por segundo)</summary>");
                md.AppendLine();
                md.AppendLine("| Segundo (UTC) | Peticiones | Fallidas | Media | p95 | VUs |");
                md.AppendLine("|---|---|---|---|---|---|");
                foreach (var b in baseline.Timeline)
                {
                    md.AppendLine($"| {b.Start:HH:mm:ss} | {b.Requests} | {b.Failed} | {Ms(b.AvgMs)} | {Ms(b.P95Ms)} | {b.Vus?.ToString(CultureInfo.InvariantCulture) ?? "-"} |");
                }

                md.AppendLine();
                md.AppendLine("</details>");
                md.AppendLine();
            }
        }
    }

    private static void RenderRuntime(StringBuilder md, DiagnosticReport r)
    {
        md.AppendLine("## Evidencia del runtime .NET");
        md.AppendLine();
        if (r.RuntimeSnapshots.Count == 0)
        {
            md.AppendLine("_No se recopilaron señales del runtime._");
            md.AppendLine();
            return;
        }

        foreach (var c in r.Correlations)
        {
            md.AppendLine($"- Correlación temporal `{c.LoadTestId}` ↔ `{c.RuntimeSnapshotId}`: carga {c.LoadStart:HH:mm:ss}–{c.LoadEnd:HH:mm:ss}, runtime {c.RuntimeStart:HH:mm:ss}–{c.RuntimeEnd:HH:mm:ss}, solapamiento {c.OverlapSeconds:0.#}s ({c.OverlapRatio:P0}). {Esc(c.Assessment)}");
        }

        if (r.Correlations.Count > 0)
        {
            md.AppendLine();
        }

        foreach (var s in r.RuntimeSnapshots)
        {
            md.AppendLine($"### `{s.Id}` — {s.Kind} (PID {s.ProcessId}, {s.StartedAt:HH:mm:ss}–{s.EndedAt:HH:mm:ss}, runtime {Esc(s.RuntimeVersion)})");
            md.AppendLine();
            if (s.Summary is { } m)
            {
                md.AppendLine("| Señal | Valor |");
                md.AppendLine("|---|---|");
                Row(md, "CPU media / máx. (% máquina)", $"{F(m.CpuPercentAvg)} / {F(m.CpuPercentMax)}");
                Row(md, "Working set máx. (MB)", F(m.WorkingSetMbMax));
                Row(md, "Heap GC inicio → máx. → fin (MB)", $"{F(m.GcHeapMbFirst)} → {F(m.GcHeapMbMax)} → {F(m.GcHeapMbLast)}");
                Row(md, "Asignación (MB/s, último intervalo / máx.)", $"{F(m.AllocationRateMbPerSec)} / {F(m.AllocationRateMbPerSecMax)}");
                Row(md, "Tiempo en GC máx. (%)", F(m.TimeInGcPercentMax));
                Row(md, "GCs gen0 / gen1 / gen2 en la ventana", $"{N(m.Gen0Collections)} / {N(m.Gen1Collections)} / {N(m.Gen2Collections)}");
                Row(md, "Pausas GC total / máx. (ms)", $"{F(m.GcPauseMsTotal)} / {F(m.GcPauseMsMax)}");
                Row(md, "Hilos ThreadPool inicio → máx.", $"{F(m.ThreadPoolThreadsFirst)} → {F(m.ThreadPoolThreadsMax)}");
                Row(md, "Cola ThreadPool (último / máx.)", $"{F(m.ThreadPoolQueueLengthLast)} / {F(m.ThreadPoolQueueLengthMax)}");
                Row(md, "Ajustes por starvation", N(m.ThreadPoolStarvationAdjustments));
                Row(md, "Contención de locks (/s)", F(m.LockContentionsPerSec));
                Row(md, "Excepciones (/s)", F(m.ExceptionsPerSec));
                Row(md, "Peticiones/s vistas por el servidor", F(m.ServerRequestsPerSec));
                md.AppendLine();
            }

            if (s.HotPath.Count > 0)
            {
                md.AppendLine("Camino caliente (raíz → hoja): " + string.Join(" → ", s.HotPath.Skip(Math.Max(0, s.HotPath.Count - 5)).Select(h => $"`{Esc(h.Frame)}` ({F(h.InclusivePercent)}%)")));
                md.AppendLine();
            }

            if (s.HotSpots.Count > 0)
            {
                md.AppendLine("| Método | Módulo | Inclusivo % | Propio % | Código de usuario | Espera |");
                md.AppendLine("|---|---|---|---|---|---|");
                foreach (var h in s.HotSpots.Take(10))
                {
                    md.AppendLine($"| `{Esc(h.Frame)}` | {Esc(h.Module)} | {F(h.InclusivePercent)} | {F(h.ExclusivePercent)} | {(h.IsUserCode ? "sí" : "no")} | {(h.IsBlocking ? "sí" : "no")} |");
                }

                md.AppendLine();
            }

            foreach (var series in s.Series)
            {
                md.AppendLine($"- Serie `{series.Name}` ({series.Unit}, {series.Source}): {series.Aggregate.Samples} puntos, mín {F(series.Aggregate.Min)}, media {F(series.Aggregate.Avg)}, máx {F(series.Aggregate.Max)}.");
            }

            if (s.ProviderFindings.Count > 0)
            {
                md.AppendLine();
                md.AppendLine("<details><summary>Hallazgos del proveedor (citados como datos, no como conclusiones)</summary>");
                md.AppendLine();
                foreach (var f in s.ProviderFindings)
                {
                    md.AppendLine($"- {Esc(f)}");
                }

                md.AppendLine();
                md.AppendLine("</details>");
            }

            foreach (var u in s.UnavailableSignals)
            {
                md.AppendLine($"- ⚠️ No disponible: {Esc(u)}");
            }

            foreach (var source in s.Sources)
            {
                md.AppendLine($"- Fuente: {Esc(source)}");
            }

            md.AppendLine();
        }
    }

    private static void RenderObservations(StringBuilder md, DiagnosticReport r)
    {
        md.AppendLine("## Observaciones (síntomas medidos)");
        md.AppendLine();
        if (r.Observations.Count == 0)
        {
            md.AppendLine("_Sin observaciones._");
        }

        foreach (var o in r.Observations)
        {
            md.AppendLine($"- **{o.Id}** [{o.Category}] {Esc(o.Description)}");
            foreach (var e in o.Evidence.Where(e => e.ArtifactRef is not null).Take(1))
            {
                md.AppendLine($"  - Artefacto: `{e.ArtifactRef}`");
            }
        }

        md.AppendLine();
    }

    private static void RenderHypotheses(StringBuilder md, DiagnosticReport r)
    {
        md.AppendLine("## Hipótesis");
        md.AppendLine();
        if (r.Hypotheses.Count == 0)
        {
            md.AppendLine("_No se formularon hipótesis (no hubo síntomas que explicar o faltan datos)._");
            md.AppendLine();
            return;
        }

        foreach (var h in r.Hypotheses)
        {
            md.AppendLine($"### {h.Id} — {Esc(h.Title)} · {StatusText(h.Status)} · confianza {Kebab(h.Confidence)} (regla {h.RuleId})");
            md.AppendLine();
            md.AppendLine(Esc(h.Statement));
            md.AppendLine();
            Evidence(md, "Evidencia a favor", h.SupportingEvidence);
            Evidence(md, "Evidencia en contra", h.ContradictingEvidence);
            if (h.MissingEvidence.Count > 0)
            {
                md.AppendLine("**Evidencia que falta:**");
                foreach (var m in h.MissingEvidence)
                {
                    md.AppendLine($"- {Esc(m)}");
                }
            }

            if (h.NextAction is { } next)
            {
                md.AppendLine($"**Siguiente acción:** `perf-lab/{next.Tool}` ({string.Join(", ", next.Arguments.Select(kv => $"{kv.Key}={kv.Value}"))}) — {Esc(next.Rationale)}");
            }

            foreach (var l in h.Limitations)
            {
                md.AppendLine($"- Limitación: {Esc(l)}");
            }

            md.AppendLine();
        }
    }

    private static void RenderNext(StringBuilder md, DiagnosticReport r)
    {
        md.AppendLine("## Próximos experimentos");
        md.AppendLine();
        if (r.NextExperiments.Count == 0)
        {
            md.AppendLine("_Ninguno pendiente dentro del alcance del MVP._");
        }

        foreach (var n in r.NextExperiments)
        {
            md.AppendLine($"- `perf-lab/{n.Tool}` ({string.Join(", ", n.Arguments.Select(kv => $"{kv.Key}={kv.Value}"))}): {Esc(n.Rationale)}");
        }

        md.AppendLine();
    }

    private static void RenderProviders(StringBuilder md, DiagnosticReport r)
    {
        md.AppendLine("## Proveedores upstream invocados");
        md.AppendLine();
        if (r.ProviderInvocations.Count == 0)
        {
            md.AppendLine("_Ninguno._");
            md.AppendLine();
            return;
        }

        md.AppendLine("| Proveedor | Versión | Herramienta | Llamadas | Fallback | Éxitos |");
        md.AppendLine("|---|---|---|---|---|---|");
        foreach (var g in r.ProviderInvocations.GroupBy(p => (p.Provider, p.ProviderVersion, p.Tool, p.IsFallback)))
        {
            md.AppendLine($"| {Esc(g.Key.Provider)} | {Esc(g.Key.ProviderVersion)} | {Esc(g.Key.Tool)} | {g.Count()} | {(g.Key.IsFallback ? "sí" : "no")} | {g.Count(p => p.Success)} |");
        }

        md.AppendLine();
    }

    private static void RenderCleanup(StringBuilder md, DiagnosticReport r)
    {
        md.AppendLine("## Limpieza");
        md.AppendLine();
        if (r.Cleanup is not { } c)
        {
            md.AppendLine("_La limpieza aún no se ha ejecutado (la API puede seguir en marcha: usa `perf-lab/stop_lab`)._");
            md.AppendLine();
            return;
        }

        md.AppendLine($"- Ejecutada {c.At:HH:mm:ss} UTC (motivo: {Esc(c.Trigger)}).");
        foreach (var s in c.Stopped)
        {
            md.AppendLine($"- Detenido: {Esc(s)}");
        }

        foreach (var f in c.FailedToStop)
        {
            md.AppendLine($"- ⚠️ No se pudo detener: {Esc(f)}");
        }

        md.AppendLine();
    }

    private static void RenderList(StringBuilder md, string title, IReadOnlyList<string> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        md.AppendLine($"## {title}");
        md.AppendLine();
        foreach (var item in items)
        {
            md.AppendLine($"- {(item.StartsWith('`') ? item : Esc(item))}");
        }

        md.AppendLine();
    }

    private static void Evidence(StringBuilder md, string title, IReadOnlyList<EvidenceItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        md.AppendLine($"**{title}:**");
        foreach (var e in items)
        {
            var value = e.Value is { } v ? $" = {v.ToString("0.##", CultureInfo.InvariantCulture)}{(e.Unit is null ? string.Empty : " " + e.Unit)}" : string.Empty;
            var artifact = e.ArtifactRef is null ? string.Empty : $" (`{e.ArtifactRef}`)";
            md.AppendLine($"- [{e.Source}] `{Esc(e.Signal)}`{value}: {Esc(e.Description)}{artifact}");
        }
    }

    private static void Row(StringBuilder md, string name, string value) => md.AppendLine($"| {name} | {value} |");

    public static string VerdictText(DiagnosisVerdict verdict) => verdict switch
    {
        DiagnosisVerdict.CauseConfirmed => "Causa confirmada con evidencia discriminante",
        DiagnosisVerdict.SymptomsObserved => "Síntomas observados; hipótesis sustentadas pero no confirmadas",
        DiagnosisVerdict.Inconclusive => "Inconcluso: faltan datos para explicar los síntomas",
        DiagnosisVerdict.NoDegradationDetected => "Sin degradación detectada con la carga probada",
        DiagnosisVerdict.EnvironmentFailure => "Fallo de ambiente (no es una conclusión de rendimiento)",
        DiagnosisVerdict.FunctionalFailure => "Fallo funcional detectado antes de la carga",
        _ => "No evaluado",
    };

    private static string VerdictExplanation(DiagnosisVerdict verdict) => verdict switch
    {
        DiagnosisVerdict.NoDegradationDetected => "No se detectó degradación con la carga probada (no implica ausencia de problemas con más carga u otro ambiente).",
        DiagnosisVerdict.Inconclusive => "Hay síntomas, pero ninguna hipótesis tiene evidencia suficiente: ver evidencia que falta.",
        DiagnosisVerdict.EnvironmentFailure => "La ejecución falló por el ambiente; no se extraen conclusiones de rendimiento.",
        DiagnosisVerdict.FunctionalFailure => "El smoke test falló funcionalmente; corrige el escenario o la API antes de medir rendimiento.",
        _ => "Diagnóstico aún no evaluado.",
    };

    private static string StatusText(HypothesisStatus status) => status switch
    {
        HypothesisStatus.Confirmed => "✅ confirmada",
        HypothesisStatus.Supported => "🟡 sustentada",
        HypothesisStatus.Contradicted => "❌ contradicha",
        HypothesisStatus.Unresolved => "❔ sin resolver",
        _ => "🆕 nueva",
    };

    private static string FidelityText(FidelityStatus fidelity) => fidelity switch
    {
        FidelityStatus.Real => "real",
        FidelityStatus.Simulated => "simulada",
        FidelityStatus.Unavailable => "no disponible",
        _ => "no verificada",
    };

    private static string Kebab<TEnum>(TEnum value) where TEnum : struct, Enum =>
        JsonNamingPolicy.KebabCaseLower.ConvertName(value.ToString());

    private static string F(double? v) => v is { } x ? x.ToString("0.##", CultureInfo.InvariantCulture) : "n/d";

    private static string N(long? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "n/d";

    private static string Ms(double? v) => v is { } x ? x.ToString("0.#", CultureInfo.InvariantCulture) + " ms" : "n/d";

    private static string P(double? fraction) => fraction is { } x ? (x * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%" : "n/d";

    private static string Short(string? sha) => sha is { Length: > 12 } ? sha[..12] : sha ?? "n/d";

    private static string Esc(string? text) =>
        string.IsNullOrEmpty(text) ? "n/d" : text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Replace("\r", string.Empty, StringComparison.Ordinal);
}
