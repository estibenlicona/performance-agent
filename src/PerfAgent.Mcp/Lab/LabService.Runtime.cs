using System.Globalization;
using PerfAgent.Adapters.DotNetDiagnosticsMcp;
using PerfAgent.Core;
using PerfAgent.Core.Contracts;
using PerfAgent.Core.Policy;
using PerfAgent.Core.Upstream;

namespace PerfAgent.Mcp.Lab;

public sealed partial class LabService
{
    /// <summary>inspect_process (preflight/triage/info) sobre el PID de la API de esta ejecución; el modelo nunca aporta el PID.</summary>
    public async Task<ToolResponse<RuntimeSnapshot>> InspectRuntimeAsync(string runId, string view, int durationSeconds, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!DiagnosticsCatalog.InspectViews.Contains(view))
            {
                return ToolResponse.Blocked<RuntimeSnapshot>($"Vista '{view}' no permitida (info, capabilities, preflight, triage).", runId,
                    [new PolicyViolation("diagnostics.view", "view", "Solo vistas de bajo riesgo sin attach ni suspensión del proceso.")]);
            }

            var decision = _policy.EvaluateCapture("counters", Math.Clamp(durationSeconds, 1, 60));
            if (!decision.Allowed)
            {
                return ToolResponse.Blocked<RuntimeSnapshot>(decision.Explain(), runId, decision.Violations);
            }

            var (run, _, error) = await ReadyRunAsync<RuntimeSnapshot>(runId, cancellationToken);
            if (error is not null)
            {
                return error;
            }

            var pid = _active!.Api.ProcessId;
            var id = $"rt-{run!.RuntimeSnapshots.Count + 1}-{view}";
            var snapshot = await _diagnostics.InspectAsync(id, pid, view, Math.Clamp(durationSeconds, 2, 30), RunPath(runId, "runtime"), cancellationToken);
            snapshot = snapshot with { Artifacts = snapshot.Artifacts.Select(Rel).ToList() };
            run.RuntimeSnapshots.Add(snapshot);
            run.ProviderInvocations.AddRange(snapshot.Providers);
            run.Log($"inspect:{view}", "ok", $"PID {pid}");
            await _store.SaveAsync(run, cancellationToken);
            return ToolResponse.Ok($"{view} del proceso {pid}: " + string.Join(" ", snapshot.ProviderFindings.Take(4)), runId, snapshot);
        }
        catch (ProviderUnavailableException ex)
        {
            return new ToolResponse<RuntimeSnapshot>("unavailable", $"Proveedor de diagnóstico no disponible: {Sanitize(ex)}", runId, null, [], ["Ejecuta get_lab_status y eng/bootstrap.ps1."]);
        }
        catch (Exception ex) when (ex is ProviderCallException or ProviderSafetyBlockedException)
        {
            return ToolResponse.Failed<RuntimeSnapshot>($"La inspección falló: {Sanitize(ex)}", runId);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Investigación de seguimiento: captura una evidencia concreta (muestra de CPU, GC/asignaciones, ThreadPool,
    /// contención o excepciones) sobre el PID de laboratorio, opcionalmente bajo la misma carga del baseline.
    /// </summary>
    public async Task<ToolResponse<RuntimeSnapshot>> CollectEvidenceAsync(string runId, string kind, int durationSeconds, bool withLoad, bool confirm, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var policyKind = kind == "cpu-sample" ? "cpu-sample" : kind;
            if (!DiagnosticsCatalog.EvidenceKinds.Contains(kind))
            {
                return ToolResponse.Blocked<RuntimeSnapshot>($"Captura '{kind}' no permitida. Opciones: {string.Join(", ", DiagnosticsCatalog.EvidenceKinds)}.", runId,
                    [new PolicyViolation("diagnostics.kind", "kind", "Dumps, heap en vivo y captura de parámetros no están habilitados en el MVP.")]);
            }

            var decision = _policy.EvaluateCapture(policyKind, durationSeconds);
            if (!decision.Allowed)
            {
                return ToolResponse.Blocked<RuntimeSnapshot>(decision.Explain(), runId, decision.Violations);
            }

            var (run, manifest, error) = await ReadyRunAsync<RuntimeSnapshot>(runId, cancellationToken);
            if (error is not null)
            {
                return error;
            }

            if (kind == "cpu-sample" && manifest!.Manifest.Diagnostics.CpuTrace == "disabled")
            {
                return ToolResponse.Blocked<RuntimeSnapshot>("El manifiesto deshabilita las trazas de CPU (diagnostics.cpuTrace: disabled).", runId,
                    [new PolicyViolation("diagnostics.cpu-trace-disabled", "kind", "cpuTrace=disabled en el manifiesto.")]);
            }

            var pid = _active!.Api.ProcessId;
            var id = $"rt-{run!.RuntimeSnapshots.Count + 1}-{kind}";
            var notes = new List<string>();
            RuntimeSnapshot snapshot;
            if (withLoad)
            {
                var baseline = run.LatestLoadTest(LoadTestKind.Baseline);
                if (baseline is null)
                {
                    return ToolResponse.Blocked<RuntimeSnapshot>("La captura con carga reutiliza el escenario baseline: ejecuta primero run_k6_test kind=baseline.", runId,
                        [new PolicyViolation("evidence.baseline-required", "withLoad", "Sin baseline previo.")]);
                }

                if (RequiresConfirmation(manifest!, "runtime-capture") && !confirm)
                {
                    return ToolResponse.ConfirmationRequired($"La captura '{kind}' repetirá la carga baseline ({baseline.Load.Vus} VUs × {baseline.Load.DurationSeconds}s) contra {run.BaseUrl}.", runId,
                        new RuntimeSnapshot { Id = "pending", ProcessId = pid, Kind = kind, StartedAt = DateTimeOffset.UtcNow });
                }

                var maxCapture = Math.Max(3, baseline.Load.DurationSeconds + baseline.Load.RampUpSeconds - 4);
                if (durationSeconds > maxCapture)
                {
                    notes.Add($"Duración de captura ajustada de {durationSeconds}s a {maxCapture}s para quedar dentro de la ventana de carga.");
                    durationSeconds = maxCapture;
                }

                var (scenario, record, scenarioDecision) = await PrepareScenarioAsync(run, manifest!, LoadTestKind.Baseline, cancellationToken);
                if (!scenarioDecision.Allowed || scenario is null || !record.Validated)
                {
                    return ToolResponse.Blocked<RuntimeSnapshot>($"No se puede generar la carga de evidencia: {scenarioDecision.Explain()} {record.ValidationDetail}", runId, scenarioDecision.Violations);
                }

                progress?.Report($"Repitiendo la carga baseline para capturar '{kind}' ({durationSeconds}s)…");
                var loadTask = ExecuteLoadAsync(run, manifest!, scenario, record, isEvidenceLoad: true, captureRuntime: false, cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                try
                {
                    snapshot = await _diagnostics.CollectAsync(id, pid, kind, durationSeconds, RunPath(runId, "runtime"), cancellationToken);
                }
                finally
                {
                    var (evidenceLoad, _, _) = await loadTask;
                    notes.Add($"Carga de evidencia {evidenceLoad.Id}: {evidenceLoad.TotalRequests} peticiones, p95 {evidenceLoad.Latency?.P95?.ToString("0.#", CultureInfo.InvariantCulture) ?? "n/d"} ms.");
                }

                snapshot = snapshot with { LoadTestId = run.LoadTests.LastOrDefault(l => l.IsEvidenceLoad)?.Id };
            }
            else
            {
                progress?.Report($"Capturando '{kind}' sin carga adicional ({durationSeconds}s)…");
                snapshot = await _diagnostics.CollectAsync(id, pid, kind, durationSeconds, RunPath(runId, "runtime"), cancellationToken);
                notes.Add("Captura sin carga: refleja el estado del proceso en reposo.");
            }

            snapshot = snapshot with { Artifacts = snapshot.Artifacts.Select(Rel).ToList(), Warnings = [.. snapshot.Warnings, .. notes] };
            run.RuntimeSnapshots.Add(snapshot);
            run.ProviderInvocations.AddRange(snapshot.Providers);
            run.Log($"evidence:{kind}", "ok", $"PID {pid}, {durationSeconds}s{(withLoad ? " bajo carga" : string.Empty)}");
            await _store.SaveAsync(run, cancellationToken);
            await File.WriteAllTextAsync(RunPath(runId, "runtime", $"{id}.json"), PerfJson.Serialize(snapshot), CancellationToken.None);
            var top = snapshot.HotPath.LastOrDefault() is { } leaf ? $" Hoja del camino caliente: {leaf.Frame} ({leaf.InclusivePercent?.ToString("0.#", CultureInfo.InvariantCulture)}%)." : string.Empty;
            var spots = snapshot.HotSpots.Where(h => h.IsUserCode && !Core.Triage.TriageEngine.IsGlueFrame(h.Frame)).Take(3).Select(h => $"{h.Frame} ({(h.ExclusivePercent ?? h.InclusivePercent)?.ToString("0.#", CultureInfo.InvariantCulture)}%)");
            return ToolResponse.Ok($"Evidencia '{kind}' capturada sobre PID {pid}.{top} Código de usuario destacado: {string.Join(", ", spots).NullIfEmpty() ?? "ninguno"}. {string.Join(" ", notes)}",
                runId, snapshot, $"Ejecuta analyze_run runId={runId} para actualizar las hipótesis.");
        }
        catch (ProviderUnavailableException ex)
        {
            return new ToolResponse<RuntimeSnapshot>("unavailable", $"Proveedor de diagnóstico no disponible: {Sanitize(ex)}", runId, null, [], ["Ejecuta get_lab_status y eng/bootstrap.ps1."]);
        }
        catch (ProviderSafetyBlockedException ex)
        {
            return ToolResponse.Blocked<RuntimeSnapshot>($"Captura detenida por la política de riesgo del proveedor: {Sanitize(ex)}", runId,
                [new PolicyViolation("diagnostics.high-risk", "kind", "La fachada no concede aprobaciones de riesgo alto/crítico.")]);
        }
        catch (ProviderCallException ex)
        {
            return ToolResponse.Failed<RuntimeSnapshot>($"La captura falló: {Sanitize(ex)}", runId);
        }
        finally
        {
            _gate.Release();
        }
    }
}

internal static class StringExtensions
{
    public static string? NullIfEmpty(this string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
