using System.Globalization;
using Microsoft.Extensions.Logging;
using PerfAgent.Adapters.DotNetDiagnosticsMcp;
using PerfAgent.Adapters.K6Mcp;
using PerfAgent.Adapters.Process;
using PerfAgent.Core;
using PerfAgent.Core.Contracts;
using PerfAgent.Core.Discovery;
using PerfAgent.Core.Manifest;
using PerfAgent.Core.Policy;
using PerfAgent.Core.Runs;
using PerfAgent.Core.Triage;
using PerfAgent.Core.Upstream;

namespace PerfAgent.Mcp.Lab;

public sealed partial class LabService
{
    public async Task<ToolResponse<ScenarioData>> GenerateScenarioAsync(string runId, LoadTestKind kind, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var (run, manifest, error) = await ReadyRunAsync<ScenarioData>(runId, cancellationToken);
            if (error is not null)
            {
                return error;
            }

            var (_, record) = Generate(run!, manifest!, kind);
            await _store.SaveAsync(run!, cancellationToken);
            return ToolResponse.Ok($"Escenario {Key(kind)} generado ({record.Endpoints.Count} endpoints, {Describe(record.Load)}). Aún no está validado.", runId,
                ToScenarioData(run!, record, manifest!), $"Ejecuta validate_k6_scenario runId={runId} kind={Key(kind)}.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ToolResponse<ScenarioData>> ValidateScenarioAsync(string runId, LoadTestKind kind, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var (run, manifest, error) = await ReadyRunAsync<ScenarioData>(runId, cancellationToken);
            if (error is not null)
            {
                return error;
            }

            if (!run!.Scenarios.ContainsKey(Key(kind)))
            {
                Generate(run, manifest!, kind);
            }

            var (record, decision) = await ValidateCoreAsync(run, manifest!, kind, cancellationToken);
            await _store.SaveAsync(run, cancellationToken);
            var data = ToScenarioData(run, record, manifest!);
            if (!decision.Allowed)
            {
                return ToolResponse.Blocked($"Escenario rechazado por la validación estática (no se envió al proveedor): {decision.Explain()}", runId, decision.Violations, data);
            }

            return record.Validated
                ? ToolResponse.Ok($"Escenario {Key(kind)} validado. {record.ValidationDetail}", runId, data, $"Ejecuta run_k6_test runId={runId} kind={Key(kind)}.")
                : ToolResponse.Failed($"La validación upstream falló: {record.ValidationDetail}", runId, data);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Ejecuta el smoke o el baseline. Todas las comprobaciones (fase, smoke previo, escenario íntegro y validado,
    /// destino loopback de la instancia, límites y confirmación) se aplican aquí antes de delegar en mcp-k6.
    /// Durante el baseline se capturan en la misma ventana las señales del runtime y el muestreo del SO.
    /// </summary>
    public async Task<ToolResponse<LoadTestData>> RunLoadTestAsync(string runId, LoadTestKind kind, bool confirm, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var (run, manifest, error) = await ReadyRunAsync<LoadTestData>(runId, cancellationToken);
            if (error is not null)
            {
                return error;
            }

            if (kind == LoadTestKind.Baseline)
            {
                var smoke = run!.LatestLoadTest(LoadTestKind.Smoke);
                if (smoke is null || smoke.Status is not (LoadTestStatus.Passed or LoadTestStatus.ThresholdsFailed))
                {
                    return ToolResponse.Blocked<LoadTestData>("El baseline requiere un smoke test funcionalmente correcto (progresión de menor a mayor coste).", runId,
                        [new PolicyViolation("load.smoke-required", "kind", "Ejecuta y aprueba el smoke test primero.")]);
                }
            }

            if (RequiresConfirmation(manifest!, Key(kind)) && !confirm)
            {
                var record0 = run!.Scenarios.TryGetValue(Key(kind), out var existing) ? existing : Generate(run, manifest!, kind).Record;
                await _store.SaveAsync(run, cancellationToken);
                return ToolResponse.ConfirmationRequired(
                    $"El perfil exige confirmación para '{Key(kind)}': {Describe(record0.Load)} contra {run.BaseUrl} ({record0.Endpoints.Count} endpoints: {string.Join(", ", record0.Endpoints)}).",
                    runId, new LoadTestData(Placeholder(kind, record0.Load), null, null, ToScenarioData(run, record0, manifest!)));
            }

            var (scenario, record, decision) = await PrepareScenarioAsync(run!, manifest!, kind, cancellationToken);
            if (!decision.Allowed || scenario is null)
            {
                await _store.SaveAsync(run!, cancellationToken);
                return ToolResponse.Blocked<LoadTestData>($"Ejecución bloqueada antes de generar tráfico: {decision.Explain()}", runId, decision.Violations);
            }

            if (!record.Validated)
            {
                await _store.SaveAsync(run!, cancellationToken);
                return ToolResponse.Failed<LoadTestData>($"El escenario no superó la validación upstream: {record.ValidationDetail}", runId);
            }

            progress?.Report($"Ejecutando {Key(kind)} ({Describe(record.Load)})…");
            var (load, runtime, correlation) = await ExecuteLoadAsync(run!, manifest!, scenario, record, isEvidenceLoad: false, captureRuntime: kind == LoadTestKind.Baseline, cancellationToken);
            run!.Phase = (kind, load.Status) switch
            {
                (LoadTestKind.Smoke, LoadTestStatus.Passed or LoadTestStatus.ThresholdsFailed) => RunPhase.SmokePassed,
                (LoadTestKind.Smoke, _) => RunPhase.SmokeFailed,
                _ => RunPhase.BaselineCompleted,
            };
            await _store.SaveAsync(run, cancellationToken);
            var data = new LoadTestData(load, runtime, correlation, ToScenarioData(run, record, manifest!));
            var headline = $"{Key(kind)}: {load.Status.ToString().ToLowerInvariant()} — {load.TotalRequests} peticiones, {Fmt(load.RequestsPerSecond)} req/s, p95 {Fmt(load.Latency?.P95)} ms, errores {Fmt(load.ErrorRate * 100)}%, checks {load.ChecksPassed}/{load.ChecksPassed + load.ChecksFailed}. Proveedor: {load.Provider?.Provider}/{load.Provider?.Tool}{(load.Provider?.IsFallback == true ? " (fallback)" : string.Empty)}.";
            if (runtime?.Summary is { } rt)
            {
                headline += $" Runtime: CPU media {Fmt(rt.CpuPercentAvg)}%, cola ThreadPool máx. {Fmt(rt.ThreadPoolQueueLengthMax)}, hilos {Fmt(rt.ThreadPoolThreadsFirst)}→{Fmt(rt.ThreadPoolThreadsMax)}, gen2 {rt.Gen2Collections?.ToString(CultureInfo.InvariantCulture) ?? "n/d"}, asignación {Fmt(rt.AllocationRateMbPerSec)} MB/s.";
            }

            return load.Status switch
            {
                LoadTestStatus.GeneratorError or LoadTestStatus.Aborted => ToolResponse.Failed(headline + " " + load.StatusDetail, runId, data),
                LoadTestStatus.NetworkError => ToolResponse.Failed(headline + " La API no respondió (fallo de ambiente, no de rendimiento).", runId, data),
                LoadTestStatus.FunctionalFailure => ToolResponse.Ok(headline + " El smoke detectó respuestas funcionales inválidas: no ejecutes carga hasta corregirlo.", runId, data, "Ejecuta analyze_run."),
                _ => ToolResponse.Ok(headline, runId, data, kind == LoadTestKind.Smoke ? $"Ejecuta run_k6_test runId={runId} kind=baseline." : $"Ejecuta analyze_run runId={runId}."),
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Ejecuta k6 y, si procede, la captura del runtime en la misma ventana. Persiste artefactos y resultados.</summary>
    private async Task<(LoadTestRun Load, RuntimeSnapshot? Runtime, TimeCorrelation? Correlation)> ExecuteLoadAsync(
        LabRun run, ValidatedManifest manifest, K6Scenario scenario, ScenarioRecord record, bool isEvidenceLoad, bool captureRuntime, CancellationToken cancellationToken)
    {
        var active = _active!;
        var loadId = $"{(isEvidenceLoad ? "evidence" : Key(scenario.Kind))}-{run.LoadTests.Count(l => l.Kind == scenario.Kind && l.IsEvidenceLoad == isEvidenceLoad) + 1}";
        var k6Dir = RunPath(run.RunId, "k6");
        var before = await MockStatsAsync(active, cancellationToken);
        var pid = active.Api.ProcessId;
        Task<RuntimeSnapshot?>? capture = null;
        var snapshotId = $"rt-{run.RuntimeSnapshots.Count + 1}-counters";
        if (captureRuntime && manifest.Manifest.Diagnostics.RuntimeCounters && _providers.DotNetDiagnosticsMcp.Enabled)
        {
            await WarmUpDiagnosticsAsync(run, pid, cancellationToken);
            snapshotId = $"rt-{run.RuntimeSnapshots.Count + 1}-counters";
            var window = Math.Max(3, scenario.Load.DurationSeconds + scenario.Load.RampUpSeconds);
            capture = CaptureWindowAsync(snapshotId, pid, window, loadId, run.RunId, cancellationToken);
        }

        ProcessResourceSampler? sampler = captureRuntime ? ProcessResourceSampler.Start(pid, TimeSpan.FromSeconds(1)) : null;

        if (capture is not null)
        {
            // La sesión EventPipe tarda ~0,5–1 s en arrancar: la captura empieza antes que la carga.
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        var started = DateTimeOffset.UtcNow;
        K6ExecutionResult result;
        IReadOnlyList<ResourceSample> samples;
        try
        {
            result = await _k6.RunAsync(scenario, ResolveWorkspacePath(record.ScriptPath), k6Dir, cancellationToken);
        }
        finally
        {
            samples = sampler is null ? [] : await sampler.StopAsync();
            if (sampler is not null)
            {
                await sampler.DisposeAsync();
            }
        }

        var ended = DateTimeOffset.UtcNow;

        RuntimeSnapshot? runtime = capture is null ? null : await capture;
        var after = await MockStatsAsync(active, cancellationToken);
        var artifacts = new List<string>();
        if (result.SummaryJson is not null)
        {
            var summaryPath = Path.Combine(k6Dir, $"{loadId}.summary.json");
            await File.WriteAllTextAsync(summaryPath, result.SummaryJson, CancellationToken.None);
            artifacts.Add(Rel(summaryPath));
        }

        var outputPath = Path.Combine(k6Dir, $"{loadId}.output.txt");
        await File.WriteAllTextAsync(outputPath, result.OutputExcerpt, CancellationToken.None);
        artifacts.Add(Rel(outputPath));
        artifacts.Add(record.ScriptPath);

        var s = result.Summary;
        var testDurationMs = s?.TestRunDurationMs;
        var loadStart = testDurationMs is { } ms ? ended.AddMilliseconds(-ms - 300) : started;
        var (status, detail) = Classify(scenario.Kind, result, s);
        var calls = before.SelectMany(b => after.Where(a => a.Name == b.Name).SelectMany(a => DependencyLauncher.Delta(b.Name, b.Stats, a.Stats))).ToList();
        var load = new LoadTestRun
        {
            Id = loadId,
            Kind = scenario.Kind,
            IsEvidenceLoad = isEvidenceLoad,
            Load = scenario.Load,
            StartedAt = loadStart,
            EndedAt = loadStart.AddMilliseconds(testDurationMs ?? (ended - started).TotalMilliseconds),
            Status = status,
            StatusDetail = detail,
            ScriptPath = record.ScriptPath,
            ScriptSha256 = record.ScriptSha256,
            ScenarioHash = record.ScenarioHash,
            Latency = s?.Latency,
            TotalRequests = s?.TotalRequests ?? 0,
            RequestsPerSecond = s?.RequestsPerSecond,
            FailedRequests = s?.FailedRequests ?? 0,
            ErrorRate = s?.ErrorRate,
            ChecksPassed = s?.ChecksPassed ?? 0,
            ChecksFailed = s?.ChecksFailed ?? 0,
            Iterations = s?.Iterations ?? 0,
            DroppedIterations = s?.DroppedIterations ?? 0,
            StatusClasses = s?.StatusClasses ?? new Dictionary<string, long>(),
            Endpoints = s?.Endpoints ?? [],
            Thresholds = s?.Thresholds ?? [],
            Timeline = result.Timeline,
            DependencyCalls = calls,
            K6Version = run.Versions.K6,
            ExitCode = result.ExitCode,
            Provider = result.Invocation,
            Artifacts = artifacts,
            Warnings = result.Timeline.Count == 0 && !isEvidenceLoad
                ? ["Sin línea temporal por segundo: run_script de mcp-k6 no admite salidas adicionales (solo agregados de la ventana)."]
                : [],
        };

        if (captureRuntime)
        {
            runtime = MergeProcessSamples(runtime, snapshotId, pid, loadId, samples, load);
            await File.WriteAllTextAsync(RunPath(run.RunId, "runtime", $"{snapshotId}.json"), PerfJson.Serialize(runtime), CancellationToken.None);
            runtime = runtime with
            {
                Artifacts = [Rel(RunPath(run.RunId, "runtime", $"{snapshotId}.json")), .. runtime.Artifacts.Select(a => Path.IsPathRooted(a) ? Rel(a) : a)],
                RuntimeVersion = runtime.RuntimeVersion ?? run.Versions.TargetRuntime,
            };
            run.RuntimeSnapshots.Add(runtime);
            load = load with { RuntimeSnapshotId = runtime.Id };
            run.ProviderInvocations.AddRange(runtime.Providers);
            if (runtime.RuntimeVersion is not null)
            {
                run.Versions = run.Versions with { TargetRuntime = runtime.RuntimeVersion };
            }
        }

        run.LoadTests.Add(load);
        run.ProviderInvocations.Add(result.Invocation);
        run.Log($"load:{loadId}", status.ToString(), detail ?? $"{load.TotalRequests} peticiones");
        var correlation = runtime is null ? null : TriageEngine.Correlate(load, runtime, TriageThresholds.Default.MinOverlapRatio);
        return (load, runtime, correlation);
    }

    private async Task<RuntimeSnapshot?> CaptureWindowAsync(string snapshotId, int pid, int window, string loadId, string runId, CancellationToken cancellationToken)
    {
        try
        {
            return await _diagnostics.CaptureLoadWindowAsync(snapshotId, pid, window, loadId, RunPath(runId, "runtime"), cancellationToken);
        }
        catch (Exception ex) when (ex is ProviderCallException or ProviderUnavailableException or ProviderSafetyBlockedException or InvalidOperationException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            _logger.LogWarning(ex, "Runtime capture failed");
            return new RuntimeSnapshot
            {
                Id = snapshotId,
                ProcessId = pid,
                Kind = "counters",
                LoadTestId = loadId,
                StartedAt = DateTimeOffset.UtcNow,
                UnavailableSignals = ["counters, GC y ThreadPool del proveedor de diagnóstico: " + Sanitize(ex)],
                Providers = ex is ProviderCallException pce ? [pce.Invocation] : [],
            };
        }
    }

    /// <summary>
    /// Conecta el proveedor de diagnóstico y ejecuta una inspección barata (view=info) del PID antes de generar carga:
    /// la conexión y la sonda de capacidades del proveedor no deben retrasar la captura de la ventana de carga.
    /// </summary>
    private async Task WarmUpDiagnosticsAsync(LabRun run, int pid, CancellationToken cancellationToken)
    {
        if (run.RuntimeSnapshots.Any(s => s.Kind == "info" && s.ProcessId == pid))
        {
            return;
        }

        try
        {
            var info = await _diagnostics.InspectAsync($"rt-{run.RuntimeSnapshots.Count + 1}-info", pid, "info", 5, RunPath(run.RunId, "runtime"), cancellationToken);
            info = info with { Artifacts = info.Artifacts.Select(Rel).ToList() };
            run.RuntimeSnapshots.Add(info);
            run.ProviderInvocations.AddRange(info.Providers);
            if (info.RuntimeVersion is not null)
            {
                run.Versions = run.Versions with { TargetRuntime = info.RuntimeVersion };
            }
        }
        catch (Exception ex) when (ex is ProviderCallException or ProviderUnavailableException or ProviderSafetyBlockedException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            _logger.LogWarning(ex, "Diagnostics warm-up failed");
        }
    }

    /// <summary>Añade la serie de CPU/memoria del SO y calcula la CPU media de la ventana de carga.</summary>
    private static RuntimeSnapshot MergeProcessSamples(RuntimeSnapshot? snapshot, string snapshotId, int pid, string loadId, IReadOnlyList<ResourceSample> samples, LoadTestRun load)
    {
        snapshot ??= new RuntimeSnapshot
        {
            Id = snapshotId,
            ProcessId = pid,
            Kind = "counters",
            LoadTestId = loadId,
            StartedAt = load.StartedAt,
            EndedAt = load.EndedAt,
            UnavailableSignals = ["counters del runtime (proveedor de diagnóstico deshabilitado)"],
        };
        var start = load.StartedAt;
        var end = load.EndedAt ?? load.StartedAt;
        var inWindow = samples.Where(x => x.Timestamp >= start && x.Timestamp <= end.AddSeconds(1)).ToList();
        if (inWindow.Count == 0)
        {
            inWindow = samples.ToList();
        }

        var cpuPoints = samples.Select(x => new MetricPoint(x.Timestamp, x.CpuPercent)).ToList();
        var wsPoints = samples.Select(x => new MetricPoint(x.Timestamp, x.WorkingSetMb)).ToList();
        var series = snapshot.Series.ToList();
        if (cpuPoints.Count > 0)
        {
            series.Insert(0, new MetricSeries("process.cpu.percent", "% máquina", "os-process-sampler", cpuPoints, MetricAggregate.From(cpuPoints)));
            series.Insert(1, new MetricSeries("process.working_set", "MB", "os-process-sampler", wsPoints, MetricAggregate.From(wsPoints)));
        }

        var summary = snapshot.Summary ?? new RuntimeSummary();
        if (inWindow.Count > 0)
        {
            summary = summary with
            {
                CpuPercentAvg = Math.Round(inWindow.Average(x => x.CpuPercent), 2),
                CpuPercentMax = Math.Round(Math.Max(summary.CpuPercentMax ?? 0, inWindow.Max(x => x.CpuPercent)), 2),
                WorkingSetMbMax = Math.Round(Math.Max(summary.WorkingSetMbMax ?? 0, inWindow.Max(x => x.WorkingSetMb)), 2),
                ProcessorCount = summary.ProcessorCount ?? Environment.ProcessorCount,
            };
        }

        return snapshot with
        {
            Summary = summary,
            Series = series,
            Sources = [.. snapshot.Sources, "cpu media y working set: os-process-sampler (cada 1 s, proceso de la API iniciado por la fachada)"],
        };
    }

    private static (LoadTestStatus Status, string? Detail) Classify(LoadTestKind kind, K6ExecutionResult result, K6Summary? s)
    {
        if (s is null)
        {
            return (result.TimedOut ? LoadTestStatus.Aborted : LoadTestStatus.GeneratorError, result.FailureDetail ?? "k6 no produjo un resumen.");
        }

        var network = s.StatusClasses.GetValueOrDefault("0");
        if (s.TotalRequests > 0 && network == s.TotalRequests)
        {
            return (LoadTestStatus.NetworkError, $"Las {network} peticiones fallaron sin respuesta HTTP (conexión rechazada o timeout).");
        }

        if (kind == LoadTestKind.Smoke && s.ChecksFailed > 0)
        {
            // Fallo sistemático (≥50% de comprobaciones de un endpoint) = error funcional: no se genera carga.
            // Fallos intermitentes (p. ej. errores ocasionales de una dependencia) se cuantifican en el baseline.
            var failing = s.Endpoints.Where(e => e.ChecksFailed > 0).ToList();
            var systematic = failing.Where(e => (double)e.ChecksFailed / Math.Max(1, e.ChecksPassed + e.ChecksFailed) >= 0.5).ToList();
            var list = string.Join(", ", failing.Select(e => $"{e.Name} ({e.ChecksFailed}/{e.ChecksPassed + e.ChecksFailed})"));
            if (systematic.Count > 0)
            {
                return (LoadTestStatus.FunctionalFailure, $"Comprobaciones funcionales fallidas de forma sistemática: {list}.");
            }

            return (result.ExitCode == 99 ? LoadTestStatus.ThresholdsFailed : LoadTestStatus.Passed,
                $"Fallos funcionales intermitentes en el smoke ({list}): se cuantificarán en el baseline.");
        }

        return result.ExitCode switch
        {
            0 => (LoadTestStatus.Passed, null),
            99 => (LoadTestStatus.ThresholdsFailed, "k6 informó thresholds incumplidos (exit 99)."),
            _ => (LoadTestStatus.GeneratorError, $"k6 terminó con código {result.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "?"}."),
        };
    }

    /// <summary>Escenario íntegro, validado (estático + upstream) y vuelto a comprobar justo antes de ejecutar.</summary>
    private async Task<(K6Scenario? Scenario, ScenarioRecord Record, PolicyDecision Decision)> PrepareScenarioAsync(LabRun run, ValidatedManifest manifest, LoadTestKind kind, CancellationToken cancellationToken)
    {
        if (!run.Scenarios.ContainsKey(Key(kind)))
        {
            Generate(run, manifest, kind);
        }

        var record = run.Scenarios[Key(kind)];
        if (!record.Validated)
        {
            var (validated, decision) = await ValidateCoreAsync(run, manifest, kind, cancellationToken);
            if (!decision.Allowed)
            {
                return (null, validated, decision);
            }

            record = validated;
        }

        var script = await File.ReadAllTextAsync(ResolveWorkspacePath(record.ScriptPath), cancellationToken);
        var final = K6ScriptValidator.Validate(script, record, _policy, manifest.Manifest, new Uri(run.BaseUrl!));
        return final.Allowed ? (ToK6Scenario(run, manifest, record, script), record, final) : (null, record, final);
    }

    private async Task<(ScenarioRecord Record, PolicyDecision Decision)> ValidateCoreAsync(LabRun run, ValidatedManifest manifest, LoadTestKind kind, CancellationToken cancellationToken)
    {
        var record = run.Scenarios[Key(kind)];
        var script = await File.ReadAllTextAsync(ResolveWorkspacePath(record.ScriptPath), cancellationToken);
        var decision = K6ScriptValidator.Validate(script, record, _policy, manifest.Manifest, new Uri(run.BaseUrl!));
        if (!decision.Allowed)
        {
            run.Log($"validate:{Key(kind)}", "blocked", decision.Explain());
            return (record with { Validated = false, ValidationDetail = decision.Explain() }, decision);
        }

        // Solo tras verificar que el destino es la instancia de laboratorio se permite la validación upstream (genera 1 petición).
        var upstream = await _k6.ValidateAsync(ToK6Scenario(run, manifest, record, script), ResolveWorkspacePath(record.ScriptPath), cancellationToken);
        run.ProviderInvocations.Add(upstream.Invocation);
        record = record with
        {
            Validated = upstream.Valid,
            ValidatedAt = DateTimeOffset.UtcNow,
            ValidationDetail = upstream.Detail,
            ValidationProvider = upstream.Invocation,
        };
        run.Scenarios[Key(kind)] = record;
        run.Log($"validate:{Key(kind)}", upstream.Valid ? "ok" : "failed", upstream.Detail);
        return (record, decision);
    }

    private (K6Scenario Scenario, ScenarioRecord Record) Generate(LabRun run, ValidatedManifest manifest, LoadTestKind kind)
    {
        var load = kind == LoadTestKind.Smoke ? manifest.Smoke : manifest.Baseline;
        // El smoke valida función, no rendimiento: los thresholds del manifiesto se aplican solo al baseline.
        var thresholds = kind == LoadTestKind.Smoke ? new Dictionary<string, List<string>>() : manifest.Manifest.Load.Thresholds;
        var scenario = K6ScriptGenerator.Generate(kind, run.BaseUrl!, run.Endpoints!.Endpoints, load, thresholds);
        var path = RunPath(run.RunId, "k6", $"{Key(kind)}.js");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, scenario.Script);
        var record = new ScenarioRecord(kind, Rel(path), scenario.ScriptSha256, scenario.ScenarioHash, load, scenario.Endpoints.Select(e => e.Name).ToList(), DateTimeOffset.UtcNow);
        run.Scenarios[Key(kind)] = record;
        run.Log($"generate:{Key(kind)}", "ok", $"{record.Endpoints.Count} endpoints, sha256 {record.ScriptSha256[..12]}");
        return (scenario, record);
    }

    private static K6Scenario ToK6Scenario(LabRun run, ValidatedManifest manifest, ScenarioRecord record, string script)
    {
        var endpoints = run.Endpoints!.Endpoints.Where(e => record.Endpoints.Contains(e.Name, StringComparer.Ordinal)).ToList();
        IReadOnlyDictionary<string, IReadOnlyList<string>> thresholds = record.Kind == LoadTestKind.Smoke
            ? new Dictionary<string, IReadOnlyList<string>>()
            : manifest.Manifest.Load.Thresholds.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value);
        return new K6Scenario(record.Kind, script, record.ScriptSha256, record.ScenarioHash, run.BaseUrl!, record.Load, endpoints, thresholds);
    }

    private ScenarioData ToScenarioData(LabRun run, ScenarioRecord record, ValidatedManifest manifest) =>
        new(record.Kind, record.ScriptPath, record.ScriptSha256, record.ScenarioHash, record.Load, record.Endpoints, run.Endpoints?.Excluded ?? [],
            record.Kind == LoadTestKind.Smoke
                ? new Dictionary<string, IReadOnlyList<string>>()
                : manifest.Manifest.Load.Thresholds.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value),
            record.Validated, record.ValidationDetail, [], record.ValidationProvider);

    /// <summary>Ejecución lista: existe, es la activa, la API sigue viva y el manifiesto no cambió.</summary>
    private async Task<(LabRun? Run, ValidatedManifest? Manifest, ToolResponse<T>? Error)> ReadyRunAsync<T>(string runId, CancellationToken cancellationToken)
    {
        var run = await LoadRunAsync(runId, cancellationToken);
        if (run is null)
        {
            return (null, null, ToolResponse.NotFound<T>($"No existe la ejecución '{runId}'."));
        }

        if (_active?.RunId != runId)
        {
            return (run, null, ToolResponse.Blocked<T>($"La ejecución {runId} no tiene el laboratorio en marcha (estado {run.Phase}). Ejecuta start_lab.", runId,
                [new PolicyViolation("lab.not-running", "runId", "El laboratorio de esta ejecución no está activo.")]));
        }

        if (_active.Api.HasExited)
        {
            run.Fail(FailureKind.ProcessExited, $"La API terminó inesperadamente (código {_active.Api.ExitCode}). Últimas líneas: {string.Join(" | ", _active.Api.StderrTail(5).Concat(_active.Api.StdoutTail(5)))}");
            await _store.SaveAsync(run, cancellationToken);
            await StopLabCoreAsync(runId, "la API terminó", cancellationToken);
            return (run, null, ToolResponse.Failed<T>($"La API terminó inesperadamente: {run.FailureDetail}", runId));
        }

        var loaded = ManifestLoader.Load(ResolveWorkspacePath(run.ManifestPath), run.ManifestPath);
        if (loaded.Sha256 != run.ManifestSha256)
        {
            return (run, null, ToolResponse.Blocked<T>("El manifiesto cambió desde el preflight: detén el laboratorio y repite el preflight.", runId,
                [new PolicyViolation("manifest.changed", "manifestPath", "SHA-256 distinto al registrado.")]));
        }

        var validation = _policy.Validate(loaded);
        if (!validation.Decision.Allowed || validation.Manifest is null)
        {
            return (run, null, ToolResponse.Blocked<T>(validation.Decision.Explain(), runId, validation.Decision.Violations));
        }

        if (run.Endpoints is null || run.Endpoints.IsEmpty)
        {
            return (run, null, ToolResponse.Blocked<T>("No hay endpoints seguros que probar en esta ejecución.", runId,
                [new PolicyViolation("endpoints.empty", "endpoints", "Declara endpoints en el manifiesto.")]));
        }

        return (run, validation.Manifest, null);
    }

    private async Task<List<(string Name, IReadOnlyList<MockRouteStats> Stats)>> MockStatsAsync(ActiveLab active, CancellationToken cancellationToken)
    {
        var list = new List<(string, IReadOnlyList<MockRouteStats>)>();
        foreach (var (name, url) in active.MockUrls)
        {
            try
            {
                list.Add((name, await DependencyLauncher.GetMockStatsAsync(url, cancellationToken)));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                _logger.LogWarning(ex, "Could not read mock stats for {Mock}", name);
            }
        }

        return list;
    }

    private static bool RequiresConfirmation(ValidatedManifest manifest, string step) =>
        manifest.Manifest.Safety.RequireConfirmation.Contains(step, StringComparer.OrdinalIgnoreCase);

    private static LoadTestRun Placeholder(LoadTestKind kind, LoadProfile load) =>
        new() { Id = "pending", Kind = kind, Load = load, StartedAt = DateTimeOffset.UtcNow, Status = LoadTestStatus.Blocked, StatusDetail = "Pendiente de confirmación." };

    internal static string Key(LoadTestKind kind) => kind == LoadTestKind.Smoke ? "smoke" : "baseline";

    private static string Describe(LoadProfile load) => load.Executor == "shared-iterations"
        ? $"{load.Vus} VU × {load.Iterations} iteraciones (máx. {load.DurationSeconds}s)"
        : $"{load.Vus} VUs × {load.DurationSeconds}s ({load.Executor}{(load.RampUpSeconds > 0 ? $", rampa {load.RampUpSeconds}s" : string.Empty)})";

    private static string Fmt(double? value) => value is { } v ? v.ToString("0.##", CultureInfo.InvariantCulture) : "n/d";
}
