using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PerfAgent.Core;
using PerfAgent.Core.Contracts;
using PerfAgent.Core.Runs;
using PerfAgent.Core.Triage;
using PerfAgent.Core.Upstream;
using PerfAgent.Reporting;

namespace PerfAgent.Mcp.Lab;

public sealed partial class LabService
{
    /// <summary>Triage por reglas sobre la evidencia persistida (no requiere el laboratorio en marcha).</summary>
    public async Task<ToolResponse<AnalysisData>> AnalyzeAsync(string runId, CancellationToken cancellationToken)
    {
        var run = await LoadRunAsync(runId, cancellationToken);
        if (run is null)
        {
            return ToolResponse.NotFound<AnalysisData>($"No existe la ejecución '{runId}'.");
        }

        var triage = Apply(run);
        await _store.SaveAsync(run, cancellationToken);
        var data = new AnalysisData(triage.Verdict, triage.Observations, triage.Hypotheses, triage.NextExperiments, triage.OpenQuestions, triage.Limitations, triage.Recommendations);
        var headline = triage.Hypotheses.Where(h => h.Status is HypothesisStatus.Confirmed or HypothesisStatus.Supported)
            .Select(h => $"[{h.Status.ToString().ToLowerInvariant()}] {h.Statement}");
        var next = triage.NextExperiments.Select(n => $"{n.Tool} {string.Join(" ", n.Arguments.Select(kv => $"{kv.Key}={kv.Value}"))}: {n.Rationale}").ToArray();
        return ToolResponse.Ok($"Veredicto: {MarkdownReportRenderer.VerdictText(triage.Verdict)}. {string.Join(" ", headline)}", runId, data, next.Length > 0 ? next : ["Ejecuta generate_report."]);
    }

    public async Task<ToolResponse<ReportData>> GenerateReportAsync(string runId, CancellationToken cancellationToken)
    {
        var run = await LoadRunAsync(runId, cancellationToken);
        if (run is null)
        {
            return ToolResponse.NotFound<ReportData>($"No existe la ejecución '{runId}'.");
        }

        var data = await WriteReportAsync(run, cancellationToken);
        return ToolResponse.Ok($"Informe generado: {data.MarkdownPath} (veredicto: {MarkdownReportRenderer.VerdictText(data.Verdict)}).", runId, data,
            _active?.RunId == runId ? $"Cuando termines la investigación, ejecuta stop_lab runId={runId}." : "Puedes comparar con otra ejecución usando compare_runs.");
    }

    public async Task<ToolResponse<RunComparison>> CompareRunsAsync(string baseRunId, string candidateRunId, CancellationToken cancellationToken)
    {
        var baseRun = await LoadRunAsync(baseRunId, cancellationToken);
        var candidate = await LoadRunAsync(candidateRunId, cancellationToken);
        if (baseRun is null || candidate is null)
        {
            return ToolResponse.NotFound<RunComparison>($"No existe la ejecución '{(baseRun is null ? baseRunId : candidateRunId)}'.");
        }

        var comparison = RunComparer.Compare(baseRun, candidate);
        var dir = RunPath(candidateRunId, "comparisons");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, $"{baseRunId}-vs-{candidateRunId}.json"), PerfJson.Serialize(comparison), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(dir, $"{baseRunId}-vs-{candidateRunId}.md"), RenderComparison(comparison), cancellationToken);
        return comparison.Comparable
            ? ToolResponse.Ok(comparison.Summary, candidateRunId, comparison)
            : ToolResponse.Blocked(comparison.Summary, candidateRunId,
                comparison.MaterialDifferences.Select(d => new Core.Policy.PolicyViolation("compare.not-comparable", "runs", d)).ToList(), comparison);
    }

    public async Task<ToolResponse<IReadOnlyList<RunSummary>>> ListRunsAsync(int limit, CancellationToken cancellationToken)
    {
        var runs = await _store.ListAsync(Math.Clamp(limit, 1, 50), cancellationToken);
        return ToolResponse.Ok($"{runs.Count} ejecución(es) recientes.", _active?.RunId, runs);
    }

    /// <summary>Documentación de k6 vía mcp-k6 (solo lectura: list_sections / get_documentation).</summary>
    public async Task<ToolResponse<DocsData>> SearchK6DocsAsync(string? slug, string? category, CancellationToken cancellationToken)
    {
        try
        {
            var arguments = new Dictionary<string, object?>();
            if (slug is not null)
            {
                arguments["slug"] = slug;
            }
            else
            {
                arguments["depth"] = 2;
                if (category is not null)
                {
                    arguments["category"] = category;
                }
            }

            var call = await _k6.Provider.CallAsync(slug is null ? "list_sections" : "get_documentation", arguments, TimeSpan.FromSeconds(90), cancellationToken);
            var text = call.Text.Length > 20_000 ? call.Text[..20_000] + "\n…(truncado)" : call.Text;
            return call.IsError
                ? ToolResponse.Failed($"mcp-k6 devolvió un error: {text[..Math.Min(300, text.Length)]}", null, new DocsData(slug is null ? "sections" : "documentation", text, call.Invocation))
                : ToolResponse.Ok(slug is null ? "Secciones de la documentación de k6." : $"Documentación de k6: {slug}.", null, new DocsData(slug is null ? "sections" : "documentation", text, call.Invocation));
        }
        catch (Exception ex) when (ex is ProviderUnavailableException or ProviderCallException)
        {
            return new ToolResponse<DocsData>("unavailable", $"Documentación de k6 no disponible: {Sanitize(ex)}", null, null, [], []);
        }
    }

    /// <summary>
    /// Flujo completo con una sola orden: preflight → arranque → smoke → baseline (con runtime) → triage →
    /// hasta <paramref name="maxFollowUps"/> capturas de seguimiento → triage → informe → limpieza (siempre).
    /// </summary>
    public async Task<ToolResponse<DiagnosisData>> RunDiagnosisAsync(string manifestPath, bool confirm, int maxFollowUps, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var steps = new List<DiagnosisStep>();
        var preflight = await PreflightAsync(manifestPath, cancellationToken);
        steps.Add(new DiagnosisStep("preflight", preflight.Status, preflight.Summary));
        if (preflight.Status != "ok" || preflight.RunId is null)
        {
            ReportData? blockedReport = preflight.RunId is null ? null : await WriteReportAsync((await LoadRunAsync(preflight.RunId, cancellationToken))!, cancellationToken);
            return new ToolResponse<DiagnosisData>(preflight.Status, $"Diagnóstico detenido en el preflight: {preflight.Summary}", preflight.RunId,
                new DiagnosisData(blockedReport?.Verdict ?? DiagnosisVerdict.EnvironmentFailure, steps, blockedReport, null), preflight.Violations, preflight.NextSteps);
        }

        var runId = preflight.RunId;
        var run = await LoadRunAsync(runId, cancellationToken);
        var loaded = Core.Manifest.ManifestLoader.Load(ResolveWorkspacePath(run!.ManifestPath), run.ManifestPath);
        var pending = loaded.Manifest.Safety.RequireConfirmation;
        if (pending.Count > 0 && !confirm)
        {
            return new ToolResponse<DiagnosisData>("confirmation-required",
                $"El perfil exige confirmación para: {string.Join(", ", pending)}. Plan: smoke {preflight.Data!.Manifest.Smoke?.Vus} VU × {preflight.Data.Manifest.Smoke?.Iterations} it., baseline {preflight.Data.Manifest.Baseline?.Vus} VUs × {preflight.Data.Manifest.Baseline?.DurationSeconds}s contra una instancia local en 127.0.0.1.",
                runId, new DiagnosisData(DiagnosisVerdict.NotEvaluated, steps, null, null), [], ["Confirma con el usuario y repite run_diagnosis con confirm=true."]);
        }

        CleanupSummary? cleanup = null;
        string? stoppedAt = null;
        try
        {
            stoppedAt = await RunPipelineAsync();
        }
        catch (OperationCanceledException)
        {
            stoppedAt = "cancelación";
            steps.Add(new DiagnosisStep("cancel", "cancelled", "Diagnóstico cancelado: se limpia el laboratorio y se genera un informe parcial."));
        }
        finally
        {
            if (_active?.RunId == runId)
            {
                var stop = await StopLabAsync(runId, CancellationToken.None);
                cleanup = stop.Data;
                steps.Add(new DiagnosisStep("stop_lab", stop.Status, stop.Summary));
            }
        }

        // El informe se escribe tras la limpieza para que incluya los recursos cerrados.
        var current = await LoadRunAsync(runId, CancellationToken.None);
        var report = await WriteReportAsync(current!, CancellationToken.None);
        steps.Add(new DiagnosisStep("generate_report", "ok", report.MarkdownPath));
        var summary = stoppedAt is null
            ? $"Diagnóstico completo. Veredicto: {MarkdownReportRenderer.VerdictText(report.Verdict)}. {string.Join(" ", report.Headlines)} Informe: {report.MarkdownPath}."
            : $"Diagnóstico detenido en '{stoppedAt}'. Veredicto: {MarkdownReportRenderer.VerdictText(report.Verdict)}. Informe: {report.MarkdownPath}.";
        return new ToolResponse<DiagnosisData>(stoppedAt is null ? "ok" : "failed", summary, runId, new DiagnosisData(report.Verdict, steps, report, cleanup), [], []);

        async Task<string?> RunPipelineAsync()
        {
            progress?.Report("Iniciando el laboratorio…");
            var start = await StartLabAsync(runId, progress, cancellationToken);
            steps.Add(new DiagnosisStep("start_lab", start.Status, start.Summary));
            if (start.Status != "ok")
            {
                return "start_lab";
            }

            var smoke = await RunLoadTestAsync(runId, LoadTestKind.Smoke, confirm, progress, cancellationToken);
            steps.Add(new DiagnosisStep("smoke", smoke.Status, smoke.Summary));
            if (smoke.Status != "ok" || smoke.Data?.LoadTest.Status is not (LoadTestStatus.Passed or LoadTestStatus.ThresholdsFailed))
            {
                return "smoke";
            }

            var baseline = await RunLoadTestAsync(runId, LoadTestKind.Baseline, confirm, progress, cancellationToken);
            steps.Add(new DiagnosisStep("baseline", baseline.Status, baseline.Summary));
            if (baseline.Status != "ok")
            {
                return "baseline";
            }

            var analysis = await AnalyzeAsync(runId, cancellationToken);
            steps.Add(new DiagnosisStep("analyze", analysis.Status, analysis.Summary));
            var followUps = analysis.Data?.NextExperiments
                .Where(n => n.Tool == "collect_runtime_evidence")
                .Select(n => n.Arguments["kind"])
                .Distinct()
                .Take(Math.Clamp(maxFollowUps, 0, 3))
                .ToList() ?? [];
            foreach (var kind in followUps)
            {
                var evidence = await CollectEvidenceAsync(runId, kind, 10, withLoad: true, confirm, progress, cancellationToken);
                steps.Add(new DiagnosisStep($"collect_runtime_evidence:{kind}", evidence.Status, evidence.Summary));
            }

            if (followUps.Count > 0)
            {
                var reanalysis = await AnalyzeAsync(runId, cancellationToken);
                steps.Add(new DiagnosisStep("analyze (tras seguimiento)", reanalysis.Status, reanalysis.Summary));
            }

            return null;
        }
    }

    private TriageResult Apply(LabRun run)
    {
        var triage = TriageEngine.Analyze(run);
        run.Verdict = triage.Verdict;
        run.Observations = triage.Observations.ToList();
        run.Hypotheses = triage.Hypotheses.ToList();
        run.NextExperiments = triage.NextExperiments.ToList();
        run.OpenQuestions = triage.OpenQuestions.ToList();
        run.Recommendations = triage.Recommendations.ToList();
        if (run.Phase is RunPhase.BaselineCompleted)
        {
            run.Phase = RunPhase.Analyzed;
        }

        run.Log("analyze", run.Verdict.ToString(), $"{triage.Observations.Count} observaciones, {triage.Hypotheses.Count} hipótesis.");
        return triage;
    }

    private async Task<ReportData> WriteReportAsync(LabRun run, CancellationToken cancellationToken)
    {
        var triage = Apply(run);
        await _store.SaveAsync(run, cancellationToken);
        var report = ReportBuilder.Build(run, triage);
        var json = RunPath(run.RunId, "report.json");
        var md = RunPath(run.RunId, "report.md");
        await File.WriteAllTextAsync(json, PerfJson.Serialize(report), cancellationToken);
        await File.WriteAllTextAsync(md, MarkdownReportRenderer.Render(report), Encoding.UTF8, cancellationToken);
        var headlines = triage.Hypotheses.Where(h => h.Status is HypothesisStatus.Confirmed or HypothesisStatus.Supported)
            .Select(h => $"[{h.Status.ToString().ToLowerInvariant()}] {h.Statement}").ToList();
        _logger.LogInformation("Report written for {RunId}: {Verdict}", run.RunId, triage.Verdict);
        return new ReportData(triage.Verdict, Rel(md), Rel(json), headlines, triage.Observations.Count, triage.Hypotheses.Count);
    }

    private static string RenderComparison(RunComparison c)
    {
        var md = new StringBuilder();
        md.AppendLine($"# Comparación `{c.BaseRunId}` → `{c.CandidateRunId}`");
        md.AppendLine();
        md.AppendLine($"**{(c.Comparable ? "Comparables" : "No comparables")}.** {c.Summary}");
        md.AppendLine();
        foreach (var d in c.MaterialDifferences)
        {
            md.AppendLine($"- ⛔ {d}");
        }

        foreach (var d in c.ChangesUnderEvaluation)
        {
            md.AppendLine($"- 🔁 Cambio bajo evaluación: {d}");
        }

        md.AppendLine();
        md.AppendLine("| Métrica | Base | Candidata | Δ | Δ % | Resultado |");
        md.AppendLine("|---|---|---|---|---|---|");
        foreach (var d in c.Deltas)
        {
            md.AppendLine(string.Create(CultureInfo.InvariantCulture, $"| {d.Metric} ({d.Unit}) | {d.Base} | {d.Candidate} | {d.AbsoluteChange} | {d.RelativeChangePercent} | {d.Direction} |"));
        }

        md.AppendLine();
        foreach (var n in c.Notes)
        {
            md.AppendLine($"- {n}");
        }

        return md.ToString();
    }
}
