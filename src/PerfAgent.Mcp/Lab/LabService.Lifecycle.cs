using System.Text.Json;
using Microsoft.Extensions.Logging;
using PerfAgent.Adapters.Process;
using PerfAgent.Core.Contracts;
using PerfAgent.Core.Discovery;
using PerfAgent.Core.Execution;
using PerfAgent.Core.Policy;
using PerfAgent.Core.Runs;

namespace PerfAgent.Mcp.Lab;

public sealed partial class LabService
{
    private static readonly HttpClient LoopbackHttp = ReadinessProbe.CreateLoopbackClient();

    /// <summary>Compila en Release, levanta dependencias declaradas y la API, espera readiness y descubre endpoints.</summary>
    public async Task<ToolResponse<StartLabData>> StartLabAsync(string runId, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var run = await LoadRunAsync(runId, cancellationToken);
            if (run is null)
            {
                return ToolResponse.NotFound<StartLabData>($"No existe la ejecución '{runId}'.");
            }

            if (_active is not null)
            {
                return _active.RunId == runId
                    ? ToolResponse.Ok("El laboratorio de esta ejecución ya está en marcha.", runId, StartData(run, 0, "ya en marcha"))
                    : ToolResponse.Blocked<StartLabData>($"Hay otra ejecución activa ({_active.RunId}). Detenla con stop_lab antes de iniciar otra.", runId,
                        [new PolicyViolation("lab.busy", "runId", "Solo una ejecución activa a la vez en el MVP.")]);
            }

            if (run.Phase != RunPhase.PreflightPassed && run.Phase != RunPhase.Stopped)
            {
                return ToolResponse.Blocked<StartLabData>($"La ejecución está en fase '{run.Phase}': solo se inicia tras un preflight superado.", runId,
                    [new PolicyViolation("lab.phase", "runId", "Ejecuta preflight_environment primero.")]);
            }

            var loaded = Core.Manifest.ManifestLoader.Load(ResolveWorkspacePath(run.ManifestPath), run.ManifestPath);
            if (loaded.Sha256 != run.ManifestSha256)
            {
                return ToolResponse.Blocked<StartLabData>("El manifiesto cambió desde el preflight: repite preflight_environment.", runId,
                    [new PolicyViolation("manifest.changed", "manifestPath", "SHA-256 distinto al registrado en el preflight.")]);
            }

            var validation = _policy.Validate(loaded);
            if (!validation.Decision.Allowed || validation.Manifest is null)
            {
                return ToolResponse.Blocked<StartLabData>(validation.Decision.Explain(), runId, validation.Decision.Violations);
            }

            var manifest = validation.Manifest;
            var logs = RunPath(runId, "logs");
            progress?.Report("Compilando la API en Release…");
            var build = await _launcher.BuildAsync(manifest.ProjectFullPath, run.Target?.SelectedFramework, logs, TimeSpan.FromSeconds(_workspace.Policy.Timeouts.BuildSeconds), cancellationToken);
            run.Log("build", build.Succeeded ? "ok" : "failed", build.Detail);
            if (!build.Succeeded)
            {
                run.Fail(build.Failure == FailureKind.None ? FailureKind.BuildFailed : build.Failure, $"{build.Detail} {string.Join(" | ", build.Errors)}");
                await _store.SaveAsync(run, cancellationToken);
                return ToolResponse.Failed($"Fallo de compilación (no es un problema de rendimiento): {build.Detail} {string.Join(" | ", build.Errors.Take(3))}", runId,
                    StartData(run, build.Duration.TotalSeconds, build.Detail), $"Revisa {Rel(build.LogPath)}.");
            }

            var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ASPNETCORE_ENVIRONMENT"] = manifest.Manifest.Api.AspNetCoreEnvironment,
                ["DOTNET_ENVIRONMENT"] = manifest.Manifest.Api.AspNetCoreEnvironment,
            };
            foreach (var (key, value) in manifest.Manifest.Api.Settings)
            {
                settings[key.Replace(":", "__", StringComparison.Ordinal)] = value;
            }

            var mocks = new List<ManagedProcess>();
            var mockUrls = new Dictionary<string, string>(StringComparer.Ordinal);
            var containers = new List<(string, string)>();
            try
            {
                foreach (var (name, dependency) in manifest.Manifest.Dependencies.OrderBy(d => d.Key, StringComparer.Ordinal))
                {
                    string? url = null;
                    switch (dependency.Mode)
                    {
                        case "mock":
                            progress?.Report($"Iniciando mock declarado '{name}'…");
                            var mockDll = await EnsureMockServerAsync(logs, cancellationToken);
                            var mock = await _dependencies.StartMockAsync(runId, name, mockDll, manifest.ContractFullPaths[name], logs, cancellationToken);
                            if (!mock.Ready || mock.Process is null)
                            {
                                throw new LabStartException(FailureKind.DependencyUnavailable, $"El mock '{name}' no arrancó: {mock.Detail}");
                            }

                            mocks.Add(mock.Process);
                            mockUrls[name] = mock.BaseUrl;
                            url = mock.BaseUrl;
                            break;
                        case "container":
                            progress?.Report($"Levantando contenedor '{dependency.Service}'…");
                            var project = $"perflab-{runId}";
                            var (ok, detail) = await _dependencies.StartContainerAsync(manifest.ComposeFullPaths[name], project, dependency.Service!, dependency.Port!.Value, cancellationToken);
                            containers.Add((manifest.ComposeFullPaths[name], project));
                            if (!ok)
                            {
                                throw new LabStartException(FailureKind.DependencyUnavailable, detail);
                            }

                            url = $"http://127.0.0.1:{dependency.Port}";
                            break;
                        case "remote":
                            url = dependency.Url;
                            break;
                    }

                    if (dependency.InjectAs is not null && url is not null)
                    {
                        settings[dependency.InjectAs.Replace(":", "__", StringComparison.Ordinal)] = url;
                    }
                }

                progress?.Report("Iniciando la API y esperando readiness…");
                var start = await _launcher.StartAsync(
                    new LaunchRequest(runId, "api", build.TargetPath!, "Iniciar la API de laboratorio", [], settings, manifest.Manifest.Api.HealthPath,
                        TimeSpan.FromSeconds(manifest.Manifest.Api.ReadinessTimeoutSeconds), logs, SetAspNetCoreUrls: true),
                    cancellationToken);
                if (!start.Ready || start.Process is null)
                {
                    throw new LabStartException(start.Failure, start.Detail);
                }

                _active = new ActiveLab { RunId = runId, Api = start.Process };
                _active.Mocks.AddRange(mocks);
                foreach (var (k, v) in mockUrls)
                {
                    _active.MockUrls[k] = v;
                }

                _active.Containers.AddRange(containers);
                run.Api = new LabProcessInfo("api", start.Process.ProcessId, start.Process.StartedAt, start.Port, start.BaseUrl, start.Process.Command, Rel(start.Process.StdoutLogPath), Rel(start.Process.StderrLogPath));
                run.DependencyProcesses = mocks.Select(m => new LabProcessInfo(m.Role, m.ProcessId, m.StartedAt, 0, mockUrls.GetValueOrDefault(m.Role["mock:".Length..]) ?? string.Empty, m.Command, Rel(m.StdoutLogPath), Rel(m.StderrLogPath))).ToList();
                run.BaseUrl = start.BaseUrl;
                // Los escenarios generados para una instancia anterior apuntan a otro puerto: se regeneran y revalidan.
                run.Scenarios.Clear();
                run.Log("start", "ready", $"PID {start.Process.ProcessId} en {start.BaseUrl}. {start.Detail}");

                progress?.Report("Descubriendo endpoints…");
                run.Endpoints = await PlanEndpointsAsync(run, manifest, cancellationToken);
                run.Phase = RunPhase.Ready;
                run.Failure = FailureKind.None;
                run.FailureDetail = null;
                await _store.SaveAsync(run, cancellationToken);
                var plan = run.Endpoints;
                var summary = $"API en marcha (PID {start.Process.ProcessId}, {start.BaseUrl}). Endpoints a probar: {plan.Endpoints.Count} ({plan.Source}); excluidos: {plan.Excluded.Count}.";
                return plan.IsEmpty
                    ? ToolResponse.Failed(summary + " No hay endpoints seguros que probar: " + string.Join(" ", plan.Notes), runId, StartData(run, build.Duration.TotalSeconds, start.Detail),
                        "Declara endpoints en el manifiesto (con datos sintéticos) y repite el preflight.")
                    : ToolResponse.Ok(summary, runId, StartData(run, build.Duration.TotalSeconds, start.Detail), $"Ejecuta run_k6_test runId={runId} kind=smoke.");
            }
            catch (LabStartException ex)
            {
                foreach (var mock in mocks)
                {
                    await mock.DisposeAsync();
                }

                foreach (var (compose, project) in containers)
                {
                    await _dependencies.StopContainersAsync(compose, project, CancellationToken.None);
                }

                run.Fail(ex.Kind, ex.Message);
                run.Log("start", "failed", ex.Message);
                await _store.SaveAsync(run, CancellationToken.None);
                return ToolResponse.Failed($"La API no quedó lista: {Describe(ex.Kind)} {ex.Message}", runId, StartData(run, build.Duration.TotalSeconds, ex.Message),
                    "Es un fallo de ambiente: corrige la causa (logs en artifacts/runs/<runId>/logs) y repite preflight + start_lab.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ToolResponse<CleanupSummary>> StopLabAsync(string? runId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var target = runId ?? _active?.RunId;
            if (target is null)
            {
                var (stopped, failed) = _registry.KillOrphans();
                var cleanup = new CleanupSummary(DateTimeOffset.UtcNow, stopped, failed, "stop_lab sin ejecución activa");
                return ToolResponse.Ok(stopped.Count == 0 ? "No había ningún laboratorio activo ni procesos registrados." : $"Detenidos {stopped.Count} proceso(s) registrados.", null, cleanup);
            }

            var summary = await StopLabCoreAsync(target, "stop_lab", cancellationToken);
            return summary.FailedToStop.Count == 0
                ? ToolResponse.Ok($"Laboratorio detenido: {summary.Stopped.Count} recurso(s) cerrados.", target, summary)
                : ToolResponse.Failed($"Limpieza incompleta: {string.Join(", ", summary.FailedToStop)}", target, summary);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Detiene API, mocks y contenedores de la ejecución y verifica con el registro que no quedan procesos.</summary>
    private async Task<CleanupSummary> StopLabCoreAsync(string runId, string trigger, CancellationToken cancellationToken)
    {
        var stopped = new List<string>();
        var failed = new List<string>();
        if (_active is { } active && active.RunId == runId)
        {
            var apiPid = active.Api.ProcessId;
            if (await active.Api.StopAsync(TimeSpan.FromSeconds(15)))
            {
                stopped.Add($"api (PID {apiPid})");
            }
            else
            {
                failed.Add($"api (PID {apiPid})");
            }

            await active.Api.DisposeAsync();
            foreach (var mock in active.Mocks)
            {
                var pid = mock.ProcessId;
                if (await mock.StopAsync(TimeSpan.FromSeconds(10)))
                {
                    stopped.Add($"{mock.Role} (PID {pid})");
                }
                else
                {
                    failed.Add($"{mock.Role} (PID {pid})");
                }

                await mock.DisposeAsync();
            }

            foreach (var (compose, project) in active.Containers)
            {
                var (ok, detail) = await _dependencies.StopContainersAsync(compose, project, cancellationToken);
                (ok ? stopped : failed).Add(detail);
            }

            _active = null;
        }

        var (orphans, orphanFailures) = _registry.KillOrphans(runId);
        stopped.AddRange(orphans);
        failed.AddRange(orphanFailures);
        var cleanup = new CleanupSummary(DateTimeOffset.UtcNow, stopped, failed, trigger);
        var run = await LoadRunAsync(runId, cancellationToken);
        if (run is not null)
        {
            run.Cleanup = cleanup;
            if (run.Phase != RunPhase.Failed && run.Phase != RunPhase.PreflightBlocked)
            {
                run.Phase = run.Phase is RunPhase.Analyzed ? RunPhase.Analyzed : RunPhase.Stopped;
            }

            if (run.Api is { } api)
            {
                run.Api = api with { Exited = true };
            }

            run.Log("cleanup", failed.Count == 0 ? "ok" : "incomplete", $"{trigger}: {stopped.Count} detenidos, {failed.Count} fallidos.");
            await _store.SaveAsync(run, CancellationToken.None);
        }

        return cleanup;
    }

    private async Task<string> EnsureMockServerAsync(string logs, CancellationToken cancellationToken)
    {
        if (_mockAssemblyPath is not null && File.Exists(_mockAssemblyPath))
        {
            return _mockAssemblyPath;
        }

        var project = ResolveWorkspacePath(_options.MockServerProject);
        var build = await _launcher.BuildAsync(project, null, logs, TimeSpan.FromSeconds(_workspace.Policy.Timeouts.BuildSeconds), cancellationToken);
        if (!build.Succeeded)
        {
            throw new LabStartException(FailureKind.BuildFailed, $"No se pudo compilar el servidor de mocks: {build.Detail}");
        }

        _mockAssemblyPath = build.TargetPath;
        return _mockAssemblyPath!;
    }

    private async Task<EndpointPlan> PlanEndpointsAsync(LabRun run, ValidatedManifest manifest, CancellationToken cancellationToken)
    {
        var m = manifest.Manifest;
        var needsOpenApi = m.Load.EndpointSource == "openapi" || (m.Load.EndpointSource == "auto" && m.Endpoints.Count == 0);
        if (!needsOpenApi)
        {
            return EndpointPlanner.Plan(m, _policy, null, null);
        }

        var path = m.Api.OpenApiPath ?? run.Target?.OpenApi?.DocumentPath;
        if (path is null)
        {
            return EndpointPlanner.Plan(m, _policy, null, "no se detectó OpenAPI y el manifiesto no indica api.openApiPath");
        }

        try
        {
            using var response = await LoopbackHttp.GetAsync(run.BaseUrl + path, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return EndpointPlanner.Plan(m, _policy, null, $"{path} respondió {(int)response.StatusCode}");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (json.Length > 5_000_000)
            {
                return EndpointPlanner.Plan(m, _policy, null, "documento OpenAPI demasiado grande (>5 MB)");
            }

            await File.WriteAllTextAsync(RunPath(run.RunId, "openapi.json"), json, cancellationToken);
            return EndpointPlanner.Plan(m, _policy, EndpointPlanner.ReadOpenApi(json), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return EndpointPlanner.Plan(m, _policy, null, $"no se pudo leer {path}: {Sanitize(ex)}");
        }
    }

    private static StartLabData StartData(LabRun run, double buildSeconds, string detail) =>
        new(run.BaseUrl ?? string.Empty, run.Api?.ProcessId ?? 0, detail, Math.Round(buildSeconds, 1),
            new[] { run.Api }.OfType<LabProcessInfo>().Concat(run.DependencyProcesses)
                .Select(p => new StartedProcess(p.Role, p.ProcessId, p.BaseUrl, p.StdoutLog, p.StderrLog)).ToList(),
            run.Endpoints, []);

    private static string Describe(FailureKind kind) => StartupFailureClassifier.Describe(kind);

    private sealed class LabStartException(FailureKind kind, string message) : Exception(message)
    {
        public FailureKind Kind { get; } = kind;
    }
}
