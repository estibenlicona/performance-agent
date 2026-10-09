using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using PerfAgent.Core.Contracts;
using PerfAgent.Core.Runs;
using PerfAgent.Mcp.Lab;
using PerfAgent.Reporting;

namespace PerfAgent.Mcp.Tools;

/// <summary>
/// Catálogo perf-lab expuesto al custom agent de Copilot. Es la ÚNICA superficie que ve el agente: las herramientas
/// genéricas de los proveedores comunitarios (run_script, collect_*, etc.) solo se invocan desde aquí, después de
/// aplicar la política del laboratorio. Ninguna herramienta acepta comandos, URLs de destino ni PIDs.
/// </summary>
[McpServerToolType]
public sealed class PerfLabTools(LabService lab)
{
    [McpServerTool(Name = "get_lab_status", Title = "Estado del laboratorio", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Estado del laboratorio perf-lab: política de seguridad vigente (límites de VUs/duración, destinos y métodos permitidos), disponibilidad y versión de los proveedores (grafana/mcp-k6, dotnet-diagnostics, k6) y ejecución activa. Úsala primero.")]
    public Task<ToolResponse<LabStatusData>> GetLabStatus(CancellationToken cancellationToken) =>
        lab.GetStatusAsync(cancellationToken);

    [McpServerTool(Name = "discover_api", Title = "Descubrir API .NET", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Inspecciona un .csproj/.sln/.slnx (sin compilar ni ejecutar): frameworks, runtime disponible y compatibilidad probada, OpenAPI, pistas de dependencias (solo nombres, nunca valores de secretos) e información que falta.")]
    public Task<ToolResponse<ApiTarget>> DiscoverApi(
        [Description("Ruta relativa al workspace del .csproj, .sln o .slnx.")] string projectPath,
        [Description("TargetFramework a evaluar si el proyecto declara varios (p. ej. net10.0).")] string? framework = null,
        CancellationToken cancellationToken = default) =>
        lab.DiscoverApiAsync(projectPath, framework, cancellationToken);

    [McpServerTool(Name = "validate_manifest", Title = "Validar manifiesto", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Valida un manifiesto de ejecución (scenarios/*.yaml) contra la política: alcance, límites de carga, destinos, operaciones de escritura, secretos y dependencias. Explica cada rechazo ANTES de iniciar nada.")]
    public ToolResponse<ManifestSummary> ValidateManifest(
        [Description("Ruta del manifiesto bajo scenarios/ (p. ej. scenarios/lab-cpu.yaml).")] string manifestPath) =>
        lab.ValidateManifest(manifestPath);

    [McpServerTool(Name = "preflight_environment", Title = "Preflight del ambiente", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true, UseStructuredContent = true)]
    [Description("Crea una ejecución (runId) y comprueba el ambiente sin generar carga: proyecto y runtime, y cada dependencia (DNS, TCP, TLS, salud) clasificada como real, simulada, no disponible o no verificada. Una dependencia requerida inalcanzable bloquea el diagnóstico.")]
    public Task<ToolResponse<PreflightData>> PreflightEnvironment(
        [Description("Ruta del manifiesto bajo scenarios/.")] string manifestPath,
        CancellationToken cancellationToken) =>
        lab.PreflightAsync(manifestPath, cancellationToken);

    [McpServerTool(Name = "start_lab", Title = "Iniciar laboratorio", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Compila la API en Release, levanta sus dependencias declaradas (mocks/contenedores), inicia la API en un puerto libre de 127.0.0.1, espera readiness y descubre endpoints seguros (OpenAPI o declarados). Distingue fallos de compilación, puerto, configuración, autenticación y dependencias.")]
    public Task<ToolResponse<StartLabData>> StartLab(
        [Description("runId devuelto por preflight_environment.")] string runId,
        IProgress<ProgressNotificationValue> progress,
        CancellationToken cancellationToken) =>
        lab.StartLabAsync(runId, Adapt(progress), cancellationToken);

    [McpServerTool(Name = "generate_k6_scenario", Title = "Generar escenario k6", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Genera el script k6 (smoke o baseline) a partir de los endpoints seguros y los límites validados. El script solo puede apuntar a la instancia de laboratorio (k6 bloquea cualquier otra IP o hostname) y queda fijado por SHA-256.")]
    public Task<ToolResponse<ScenarioData>> GenerateK6Scenario(
        [Description("runId con el laboratorio en marcha.")] string runId,
        [Description("smoke o baseline.")] string kind,
        CancellationToken cancellationToken) =>
        ParseKind(kind) is { } k ? lab.GenerateScenarioAsync(runId, k, cancellationToken) : Task.FromResult(BadKind<ScenarioData>(runId));

    [McpServerTool(Name = "validate_k6_scenario", Title = "Validar escenario k6", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Valida el escenario: primero estáticamente (integridad, destino, métodos, límites, protecciones de red) y solo después con validate_script de grafana/mcp-k6, que ejecuta 1 VU / 1 iteración contra la instancia de laboratorio.")]
    public Task<ToolResponse<ScenarioData>> ValidateK6Scenario(
        [Description("runId con el laboratorio en marcha.")] string runId,
        [Description("smoke o baseline.")] string kind,
        CancellationToken cancellationToken) =>
        ParseKind(kind) is { } k ? lab.ValidateScenarioAsync(runId, k, cancellationToken) : Task.FromResult(BadKind<ScenarioData>(runId));

    [McpServerTool(Name = "run_k6_test", Title = "Ejecutar prueba k6", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Ejecuta el smoke test o la carga baseline mediante grafana/mcp-k6 (run_script) o el k6 CLI fijado como fallback. Exige smoke correcto antes del baseline, escenario íntegro y validado, destino loopback y límites de la política. El baseline captura en la misma ventana counters, GC y ThreadPool del proceso (dotnet-diagnostics) y CPU/memoria del SO.")]
    public Task<ToolResponse<LoadTestData>> RunK6Test(
        [Description("runId con el laboratorio en marcha.")] string runId,
        [Description("smoke o baseline.")] string kind,
        IProgress<ProgressNotificationValue> progress,
        [Description("true solo después de que el usuario confirme destino, carga y duración (cuando el perfil lo exige).")] bool confirm = false,
        CancellationToken cancellationToken = default) =>
        ParseKind(kind) is { } k ? lab.RunLoadTestAsync(runId, k, confirm, Adapt(progress), cancellationToken) : Task.FromResult(BadKind<LoadTestData>(runId));

    [McpServerTool(Name = "inspect_runtime", Title = "Inspeccionar runtime", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Inspecciona el proceso .NET de la API de esta ejecución con dotnet-diagnostics (inspect_process): view=preflight (¿se puede diagnosticar?), triage (señales e hipótesis del proveedor en ~5 s) o info. El PID lo asigna el laboratorio.")]
    public Task<ToolResponse<RuntimeSnapshot>> InspectRuntime(
        [Description("runId con el laboratorio en marcha.")] string runId,
        [Description("preflight, triage, info o capabilities.")] string view,
        [Description("Segundos de observación para triage (2-30, por defecto 5).")] int? durationSeconds = null,
        CancellationToken cancellationToken = default) =>
        lab.InspectRuntimeAsync(runId, view, durationSeconds ?? 5, cancellationToken);

    [McpServerTool(Name = "collect_runtime_evidence", Title = "Capturar evidencia del runtime", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Investigación de seguimiento para discriminar hipótesis: cpu-sample (métodos calientes y esperas bloqueantes), gc-events (GC + sitios de asignación), threadpool-events, contention-events o exception-events. Con withLoad=true repite la carga baseline validada durante la captura. Sin dumps ni inspección de heap.")]
    public Task<ToolResponse<RuntimeSnapshot>> CollectRuntimeEvidence(
        [Description("runId con el laboratorio en marcha.")] string runId,
        [Description("cpu-sample, gc-events, threadpool-events, contention-events o exception-events.")] string kind,
        IProgress<ProgressNotificationValue> progress,
        [Description("Segundos de captura (1-60, por defecto 10).")] int? durationSeconds = null,
        [Description("Repetir la carga baseline durante la captura (por defecto true).")] bool? withLoad = null,
        [Description("true solo si el usuario confirmó (cuando el perfil exige confirmación de runtime-capture).")] bool confirm = false,
        CancellationToken cancellationToken = default) =>
        lab.CollectEvidenceAsync(runId, kind, durationSeconds ?? 10, withLoad ?? true, confirm, Adapt(progress), cancellationToken);

    [McpServerTool(Name = "analyze_run", Title = "Analizar ejecución", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Aplica las reglas de triage a la evidencia registrada: observaciones (síntomas), hipótesis con estado (new, supported, contradicted, confirmed, unresolved), evidencia a favor/en contra/faltante, siguiente experimento y veredicto. Un síntoma nunca se marca como causa sin evidencia discriminante.")]
    public Task<ToolResponse<AnalysisData>> AnalyzeRun(
        [Description("runId a analizar.")] string runId,
        CancellationToken cancellationToken) =>
        lab.AnalyzeAsync(runId, cancellationToken);

    [McpServerTool(Name = "generate_report", Title = "Generar informe", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Genera el informe reproducible (Markdown + JSON) de la ejecución en artifacts/runs/<runId>/: versiones, fidelidad del ambiente, carga, runtime, correlación temporal, observaciones, hipótesis, limitaciones, próximos experimentos, proveedores invocados y limpieza.")]
    public Task<ToolResponse<ReportData>> GenerateReport(
        [Description("runId del informe.")] string runId,
        CancellationToken cancellationToken) =>
        lab.GenerateReportAsync(runId, cancellationToken);

    [McpServerTool(Name = "compare_runs", Title = "Comparar ejecuciones", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Compara dos ejecuciones (base → candidata). Rechaza la comparación si difieren materialmente en escenario, carga, perfil, fidelidad de dependencias, runtime o máquina; si son comparables, informa deltas de latencia, throughput, errores y señales del runtime.")]
    public Task<ToolResponse<RunComparison>> CompareRuns(
        [Description("runId de referencia (antes).")] string baseRunId,
        [Description("runId a evaluar (después).")] string candidateRunId,
        CancellationToken cancellationToken) =>
        lab.CompareRunsAsync(baseRunId, candidateRunId, cancellationToken);

    [McpServerTool(Name = "stop_lab", Title = "Detener laboratorio", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Detiene la API, los mocks y los contenedores de la ejecución y verifica que no quedan procesos huérfanos. Úsala siempre al terminar, también tras errores.")]
    public Task<ToolResponse<CleanupSummary>> StopLab(
        [Description("runId a detener (por defecto, la ejecución activa).")] string? runId = null,
        CancellationToken cancellationToken = default) =>
        lab.StopLabAsync(runId, cancellationToken);

    [McpServerTool(Name = "list_runs", Title = "Listar ejecuciones", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Lista las ejecuciones recientes con su veredicto y métricas principales (para elegir qué comparar).")]
    public Task<ToolResponse<IReadOnlyList<RunSummary>>> ListRuns(
        [Description("Número máximo de ejecuciones (1-50, por defecto 10).")] int? limit = null,
        CancellationToken cancellationToken = default) =>
        lab.ListRunsAsync(limit ?? 10, cancellationToken);

    [McpServerTool(Name = "search_k6_docs", Title = "Documentación de k6", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true, UseStructuredContent = true)]
    [Description("Consulta la documentación oficial de k6 a través de grafana/mcp-k6 (list_sections / get_documentation). Solo lectura.")]
    public Task<ToolResponse<DocsData>> SearchK6Docs(
        [Description("Slug de una sección (p. ej. using-k6/thresholds). Si se omite, lista secciones.")] string? slug = null,
        [Description("Categoría de primer nivel para listar (p. ej. using-k6).")] string? category = null,
        CancellationToken cancellationToken = default) =>
        lab.SearchK6DocsAsync(slug, category, cancellationToken);

    [McpServerTool(Name = "run_diagnosis", Title = "Diagnóstico completo", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true, UseStructuredContent = true)]
    [Description("Una sola orden para el flujo completo: preflight → compilar y arrancar → smoke → baseline con runtime → triage → hasta 2 capturas de seguimiento bajo carga → triage → informe → limpieza (siempre, también ante errores).")]
    public Task<ToolResponse<DiagnosisData>> RunDiagnosis(
        [Description("Ruta del manifiesto bajo scenarios/.")] string manifestPath,
        IProgress<ProgressNotificationValue> progress,
        [Description("true solo si el usuario confirmó la carga (cuando el perfil lo exige).")] bool confirm = false,
        [Description("Capturas de seguimiento máximas (0-3, por defecto 2).")] int? maxFollowUps = null,
        CancellationToken cancellationToken = default) =>
        lab.RunDiagnosisAsync(manifestPath, confirm, maxFollowUps ?? 2, Adapt(progress), cancellationToken);

    private static LoadTestKind? ParseKind(string kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "smoke" => LoadTestKind.Smoke,
        "baseline" => LoadTestKind.Baseline,
        _ => null,
    };

    private static ToolResponse<T> BadKind<T>(string runId) =>
        ToolResponse.Blocked<T>("kind admite 'smoke' o 'baseline'.", runId, [new Core.Policy.PolicyViolation("load.kind", "kind", "Valor no admitido.")]);

    private static IProgress<string> Adapt(IProgress<ProgressNotificationValue> progress)
    {
        var step = 0;
        return new Progress<string>(message => progress.Report(new ProgressNotificationValue { Progress = Interlocked.Increment(ref step), Message = message }));
    }
}
