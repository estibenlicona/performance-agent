using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PerfAgent.Adapters.DotNetDiagnosticsMcp;
using PerfAgent.Adapters.K6Mcp;
using PerfAgent.Adapters.Process;
using PerfAgent.Core;
using PerfAgent.Core.Contracts;
using PerfAgent.Core.Discovery;
using PerfAgent.Core.Execution;
using PerfAgent.Core.Manifest;
using PerfAgent.Core.Policy;
using PerfAgent.Core.Runs;
using PerfAgent.Core.Security;
using PerfAgent.Core.Upstream;
using PerfAgent.Mcp.Configuration;

namespace PerfAgent.Mcp.Lab;

/// <summary>Recursos vivos de la ejecución activa (solo una a la vez en el MVP).</summary>
internal sealed class ActiveLab
{
    public required string RunId { get; init; }

    public required ManagedProcess Api { get; init; }

    public List<ManagedProcess> Mocks { get; } = [];

    public Dictionary<string, string> MockUrls { get; } = new(StringComparer.Ordinal);

    public List<(string ComposeFile, string Project)> Containers { get; } = [];
}

/// <summary>
/// Orquestador del laboratorio y frontera de seguridad de la fachada perf-lab. Cada operación valida la política
/// y el estado de la ejecución antes de compilar, iniciar procesos, generar tráfico o capturar evidencia; el
/// agente solo aporta identificadores y parámetros acotados, nunca comandos, URLs de destino ni PIDs.
/// </summary>
public sealed partial class LabService : IAsyncDisposable
{
    private readonly Workspace _workspace;
    private readonly PolicyEngine _policy;
    private readonly RunStore _store;
    private readonly ICommandRunner _runner;
    private readonly ProcessRegistry _registry;
    private readonly WindowsJobObjectHolder _job;
    private readonly ApiLauncher _launcher;
    private readonly DependencyLauncher _dependencies;
    private readonly DependencyPreflight _preflight;
    private readonly ProjectDiscovery _discovery;
    private readonly K6Executor _k6;
    private readonly DotNetDiagnosticsAdapter _diagnostics;
    private readonly PerfLabOptions _options;
    private readonly CommunityProvidersOptions _providers;
    private readonly ILogger<LabService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<string> _startupNotes = [];
    private ActiveLab? _active;
    private string? _mockAssemblyPath;
    private bool _disposed;

    internal LabService(
        Workspace workspace,
        ICommandRunner runner,
        ExecutableResolver resolver,
        UpstreamMcpProvider k6Provider,
        UpstreamMcpProvider diagnosticsProvider,
        PerfLabOptions options,
        CommunityProvidersOptions providers,
        ILoggerFactory loggerFactory)
    {
        _workspace = workspace;
        _policy = new PolicyEngine(workspace);
        _store = new RunStore(workspace);
        _runner = runner;
        _options = options;
        _providers = providers;
        _logger = loggerFactory.CreateLogger<LabService>();
        _registry = new ProcessRegistry(Path.Combine(workspace.ArtifactsRoot, ".state"));
        _job = WindowsJobObjectHolder.Create(options.AssignServerToJobObject);
        if (_job.Note is not null)
        {
            _startupNotes.Add(_job.Note);
        }

        _launcher = new ApiLauncher(runner, resolver, _registry, _job, loggerFactory.CreateLogger<ApiLauncher>());
        _dependencies = new DependencyLauncher(_launcher, runner);
        _preflight = new DependencyPreflight(runner, workspace);
        _discovery = new ProjectDiscovery(runner, workspace);
        _k6 = new K6Executor(k6Provider, runner, new K6Settings(ResolveWorkspacePath(providers.K6.Executable), providers.K6.ExpectedVersion), loggerFactory.CreateLogger<K6Executor>());
        _diagnostics = new DotNetDiagnosticsAdapter(diagnosticsProvider, loggerFactory.CreateLogger<DotNetDiagnosticsAdapter>());

        // Procesos que quedaron vivos de una sesión anterior (p. ej. si el servidor terminó abruptamente fuera de Windows).
        var (stopped, failed) = _registry.KillOrphans();
        _startupNotes.AddRange(stopped.Select(s => $"Proceso huérfano de una sesión anterior detenido: {s}"));
        _startupNotes.AddRange(failed.Select(f => $"No se pudo detener un proceso huérfano: {f}"));
    }

    public Workspace Workspace => _workspace;

    public string? ActiveRunId => _active?.RunId;

    internal ProcessRegistry Registry => _registry;

    public async Task<ToolResponse<LabStatusData>> GetStatusAsync(CancellationToken cancellationToken)
    {
        var k6Version = await _k6.GetK6VersionAsync(cancellationToken);
        var statuses = new List<ProviderStatus>
        {
            _providers.K6Mcp.Enabled ? await _k6.Provider.EnsureConnectedAsync(cancellationToken) : Disabled(K6Executor.ProviderName),
            _providers.DotNetDiagnosticsMcp.Enabled ? await _diagnostics.StatusAsync(cancellationToken) : Disabled(DiagnosticsCatalog.ProviderName),
        };
        var p = _workspace.Policy;
        var policy = new PolicySummary(p.Load.MaxVus, p.Load.MaxDurationSeconds, p.Load.MaxSmokeVus, p.Load.MaxSmokeIterations, p.AllowedLoadTargetHosts,
            p.AllowedHttpMethods, p.WriteOperations, p.AllowedRemoteDependencyHosts, p.SupportedTargetFrameworks, p.Diagnostics.AllowDumps, p.Diagnostics.MaxCaptureSeconds);
        var notes = new List<string>(_startupNotes);
        if (k6Version is null)
        {
            notes.Add("k6 no está instalado en la ruta fijada: ejecuta eng/bootstrap.ps1.");
        }

        var unavailable = statuses.Where(s => !s.Available).Select(s => $"{s.Provider}: {s.Error}").ToList();
        notes.AddRange(unavailable.Select(u => $"Proveedor no disponible → {u}"));
        var data = new LabStatusData(_workspace.Root, policy, statuses, k6Version, _active?.RunId, _active is null ? null : (await _store.LoadAsync(_active.RunId, cancellationToken))?.BaseUrl,
            _job.SelfAssigned ? "job-object (Windows, KILL_ON_JOB_CLOSE)" : "registro de procesos + limpieza explícita", notes);
        var summary = unavailable.Count == 0 && k6Version is not null
            ? $"Laboratorio listo. k6 {k6Version}; proveedores: {string.Join(", ", statuses.Select(s => $"{s.Provider} {s.ServerVersion}"))}."
            : "Laboratorio con limitaciones: " + string.Join(" ", notes);
        return ToolResponse.Ok(summary, _active?.RunId, data, "Para empezar: discover_api o validate_manifest con un manifiesto bajo scenarios/.");
    }

    public async Task<ToolResponse<ApiTarget>> DiscoverApiAsync(string projectPath, string? framework, CancellationToken cancellationToken)
    {
        var full = _workspace.TryResolve(projectPath, _workspace.Policy.AllowedProjectRoots, "projectPath", out var violation);
        if (violation is not null)
        {
            return ToolResponse.Blocked<ApiTarget>(violation.Message, null, [violation]);
        }

        if (!File.Exists(full) || !(full!.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || full.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || full.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)))
        {
            return ToolResponse.NotFound<ApiTarget>($"No existe un .csproj/.sln/.slnx en '{projectPath}'.");
        }

        var target = await _discovery.DiscoverAsync(full, framework, cancellationToken);
        var summary = target.CandidateProjects.Count > 0
            ? $"Solución con {target.CandidateProjects.Count} proyecto(s) web: {string.Join(", ", target.CandidateProjects)}."
            : $"{target.ProjectName}: {(target.IsWebProject ? "ASP.NET Core" : "no web")}, {string.Join("/", target.TargetFrameworks)}, runtime {(target.Runtime?.RuntimeAvailable == true ? "disponible" : "no disponible")} ({target.Runtime?.Compatibility ?? "?"}), OpenAPI {(target.OpenApi?.Likely == true ? target.OpenApi.DocumentPath : "no detectado")}, {target.DependencyHints.Count} pista(s) de dependencias."
              + (target.MissingInformation.Count > 0 ? " Falta: " + string.Join(" ", target.MissingInformation) : string.Empty);
        return ToolResponse.Ok(summary, null, target, "Declara el proyecto, endpoints y dependencias en un manifiesto bajo scenarios/ y valídalo con validate_manifest.");
    }

    public ToolResponse<ManifestSummary> ValidateManifest(string manifestPath)
    {
        var (validation, loaded, error) = LoadAndValidate(manifestPath);
        if (error is not null)
        {
            return ToolResponse.Blocked<ManifestSummary>(error.Message, null, [error]);
        }

        var summary = Summarize(loaded!, validation!);
        return validation!.Decision.Allowed
            ? ToolResponse.Ok($"Manifiesto válido: {summary.Profile}, smoke {summary.Smoke?.Vus} VU × {summary.Smoke?.Iterations} it., baseline {summary.Baseline?.Vus} VUs × {summary.Baseline?.DurationSeconds}s.", null, summary,
                "Ejecuta preflight_environment con este manifiesto.")
            : ToolResponse.Blocked($"Manifiesto rechazado antes de iniciar nada. {validation.Decision.Explain()}", null, validation.Decision.Violations, summary);
    }

    public async Task<ToolResponse<PreflightData>> PreflightAsync(string manifestPath, CancellationToken cancellationToken)
    {
        var (validation, loaded, error) = LoadAndValidate(manifestPath);
        if (error is not null)
        {
            return ToolResponse.Blocked<PreflightData>(error.Message, null, [error]);
        }

        var manifestSummary = Summarize(loaded!, validation!);
        if (!validation!.Decision.Allowed || validation.Manifest is null)
        {
            return ToolResponse.Blocked($"Manifiesto rechazado: {validation.Decision.Explain()}", null, validation.Decision.Violations,
                new PreflightData(manifestSummary, null, null, RunPhase.PreflightBlocked, validation.Decision.Violations.Select(v => v.Message).ToList()));
        }

        var validated = validation.Manifest;
        var target = await _discovery.DiscoverAsync(validated.ProjectFullPath, validated.Manifest.Api.Framework, cancellationToken);
        var blocking = TargetBlockingReasons(target);
        var environment = await _preflight.RunAsync(validated, cancellationToken);
        blocking.AddRange(environment.BlockingReasons);

        var run = new LabRun
        {
            RunId = RunStore.NewRunId(DateTimeOffset.Now),
            CreatedAt = DateTimeOffset.UtcNow,
            ManifestPath = loaded!.RelativePath,
            ManifestSha256 = loaded.Sha256,
            ScenarioName = validated.Manifest.Name ?? Path.GetFileNameWithoutExtension(loaded.RelativePath),
            Profile = validated.Profile,
            Target = target,
            Environment = environment,
            Phase = blocking.Count > 0 ? RunPhase.PreflightBlocked : RunPhase.PreflightPassed,
            Versions = await CollectVersionsAsync(target, cancellationToken),
        };
        run.Limitations.AddRange(validation.Decision.Warnings);
        if (validated.Manifest.Api.AspNetCoreEnvironment == "Development")
        {
            run.Limitations.Add("La API se ejecuta con ASPNETCORE_ENVIRONMENT=Development: logging y configuración pueden diferir del ambiente objetivo.");
        }

        if (blocking.Count > 0)
        {
            run.Verdict = DiagnosisVerdict.EnvironmentFailure;
        }

        run.Log("preflight", blocking.Count > 0 ? "blocked" : "passed", blocking.Count > 0 ? string.Join(" | ", blocking) : $"Decisión {environment.Decision}.");
        Directory.CreateDirectory(_store.RunDirectory(run.RunId));
        File.Copy(loaded.FullPath, Path.Combine(_store.RunDirectory(run.RunId), "manifest.yaml"), overwrite: true);
        await _store.SaveAsync(run, cancellationToken);

        var data = new PreflightData(manifestSummary, target, environment, run.Phase, blocking);
        if (blocking.Count > 0)
        {
            return ToolResponse.Blocked($"Diagnóstico bloqueado en el preflight (no se generará carga): {string.Join(" ", blocking)}", run.RunId,
                blocking.Select(b => new PolicyViolation("preflight.blocked", "environment", b)).ToList(), data,
                "Resuelve los bloqueos y repite el preflight. Un fallo de ambiente no es una conclusión de rendimiento.");
        }

        var fidelity = string.Join(", ", environment.Dependencies.Select(d => $"{d.Name}={d.Fidelity.ToString().ToLowerInvariant()}"));
        return ToolResponse.Ok(
            $"Preflight superado ({environment.Decision}). Proyecto {target.ProjectPath} ({target.SelectedFramework}). Dependencias: {(fidelity.Length == 0 ? "ninguna" : fidelity)}.",
            run.RunId, data, $"Ejecuta start_lab con runId={run.RunId}.");
    }

    private (ManifestValidation? Validation, LoadedManifest? Loaded, PolicyViolation? Error) LoadAndValidate(string manifestPath)
    {
        var full = _policy.ResolveManifestPath(manifestPath, out var violation);
        if (violation is not null)
        {
            return (null, null, violation);
        }

        if (!File.Exists(full))
        {
            return (null, null, new PolicyViolation("manifest.not-found", "manifestPath", $"No existe el manifiesto '{manifestPath}'."));
        }

        try
        {
            var loaded = ManifestLoader.Load(full!, _workspace.ToRelative(full!));
            return (_policy.Validate(loaded), loaded, null);
        }
        catch (ManifestFormatException ex)
        {
            return (null, null, new PolicyViolation("manifest.format", "manifest", ex.Message));
        }
    }

    private static ManifestSummary Summarize(LoadedManifest loaded, ManifestValidation validation)
    {
        var m = loaded.Manifest;
        return new ManifestSummary(
            loaded.RelativePath,
            m.Name,
            m.Profile,
            m.Environment,
            m.Api.Project ?? "?",
            validation.Manifest?.Smoke,
            validation.Manifest?.Baseline,
            m.Load.EndpointSource,
            m.Endpoints.Count,
            m.Dependencies.ToDictionary(d => d.Key, d => $"{d.Value.Mode}{(d.Value.Required ? " (requerida)" : string.Empty)}"),
            m.Load.Thresholds,
            m.Safety.RequireConfirmation,
            validation.Decision.Warnings);
    }

    private List<string> TargetBlockingReasons(ApiTarget target)
    {
        var reasons = new List<string>();
        if (!target.IsWebProject)
        {
            reasons.Add("El proyecto no es una API ASP.NET Core (Microsoft.NET.Sdk.Web).");
        }

        if (target.SelectedFramework is null)
        {
            reasons.Add("No se pudo determinar un único TargetFramework (indica api.framework).");
        }

        if (target.Runtime is { } runtime)
        {
            if (!runtime.RuntimeAvailable)
            {
                reasons.Add($"No hay runtime ASP.NET Core instalado para {runtime.Framework}.");
            }

            if (runtime.Compatibility == "unsupported" || (runtime.Compatibility == "untested" && !_workspace.Policy.AllowUntestedFrameworks))
            {
                reasons.Add($"{runtime.Detail}");
            }
        }

        return reasons;
    }

    private async Task<ToolVersions> CollectVersionsAsync(ApiTarget target, CancellationToken cancellationToken)
    {
        var sdk = await _runner.RunAsync(AllowedCommands.DotnetVersion(_workspace.Root), TimeSpan.FromSeconds(30), cancellationToken);
        var k6 = await _k6.GetK6VersionAsync(cancellationToken);
        var k6Status = _k6.Provider.LastStatus;
        var diagStatus = _diagnostics.Provider.LastStatus;
        return new ToolVersions
        {
            PerfAgent = typeof(LabService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0],
            DotNetSdk = sdk.Succeeded ? sdk.StandardOutput.Trim() : null,
            TargetRuntime = target.Runtime?.InstalledAspNetCoreRuntimes.LastOrDefault(v => target.Runtime.RequiredAspNetCoreMajor is { } major && v.StartsWith(major + ".", StringComparison.Ordinal)),
            K6 = k6,
            K6McpProvider = $"{K6Executor.ProviderName} {k6Status?.ServerVersion ?? _providers.K6Mcp.ExpectedVersion}",
            DiagnosticsProvider = $"{DiagnosticsCatalog.ProviderName} {diagStatus?.ServerVersion ?? _providers.DotNetDiagnosticsMcp.ExpectedVersion}",
            OperatingSystem = $"{RuntimeInformation.OSDescription.Trim()} {RuntimeInformation.OSArchitecture}",
            ProcessorCount = Environment.ProcessorCount,
        };
    }

    private string ResolveWorkspacePath(string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(_workspace.Root, path));

    private static ProviderStatus Disabled(string name) => new(name, false, null, null, null, null, [], [], "Deshabilitado en la configuración.");

    private async Task<LabRun?> LoadRunAsync(string runId, CancellationToken cancellationToken) =>
        RunStore.IsValidRunId(runId) ? await _store.LoadAsync(runId, cancellationToken) : null;

    private string RunPath(string runId, params string[] parts) => Path.Combine([_store.RunDirectory(runId), .. parts]);

    private string Rel(string fullPath) => _workspace.ToRelative(fullPath);

    private static string Sanitize(Exception ex) => SecretRedactor.Redact(ex.Message);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_active is not null)
        {
            try
            {
                await StopLabCoreAsync(_active.RunId, "apagado del servidor perf-lab", CancellationToken.None);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                _logger.LogWarning(ex, "Cleanup on shutdown failed");
            }
        }

        await _k6.Provider.DisposeAsync();
        await _diagnostics.Provider.DisposeAsync();
        _job.Dispose();
        _gate.Dispose();
    }
}
