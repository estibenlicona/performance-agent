using PerfAgent.Core.Contracts;
using PerfAgent.Core.Discovery;
using PerfAgent.Core.Policy;
using PerfAgent.Core.Upstream;

namespace PerfAgent.Mcp.Lab;

/// <summary>
/// Respuesta común de las herramientas perf-lab: estado inequívoco, resumen legible y datos tipados.
/// </summary>
/// <param name="Status">ok, blocked, failed, confirmation-required, not-found o unavailable.</param>
public sealed record ToolResponse<T>(
    string Status,
    string Summary,
    string? RunId,
    T? Data,
    IReadOnlyList<PolicyViolation> Violations,
    IReadOnlyList<string> NextSteps);

public static class ToolResponse
{
    public static ToolResponse<T> Ok<T>(string summary, string? runId, T data, params string[] next) => new("ok", summary, runId, data, [], next);

    public static ToolResponse<T> Blocked<T>(string summary, string? runId, IReadOnlyList<PolicyViolation> violations, T? data = default, params string[] next) =>
        new("blocked", summary, runId, data, violations, next);

    public static ToolResponse<T> Failed<T>(string summary, string? runId, T? data = default, params string[] next) => new("failed", summary, runId, data, [], next);

    public static ToolResponse<T> NotFound<T>(string summary) => new("not-found", summary, null, default, [], []);

    public static ToolResponse<T> ConfirmationRequired<T>(string summary, string? runId, T data) =>
        new("confirmation-required", summary, runId, data, [], ["Confirma con el usuario el destino, la carga y la duración; después repite la llamada con confirm=true."]);
}

public sealed record PolicySummary(
    int MaxVus,
    int MaxDurationSeconds,
    int MaxSmokeVus,
    int MaxSmokeIterations,
    IReadOnlyList<string> AllowedLoadTargetHosts,
    IReadOnlyList<string> AllowedHttpMethods,
    string WriteOperations,
    IReadOnlyList<string> AllowedRemoteDependencyHosts,
    IReadOnlyList<string> SupportedTargetFrameworks,
    bool DumpsAllowed,
    int MaxCaptureSeconds);

public sealed record LabStatusData(
    string Workspace,
    PolicySummary Policy,
    IReadOnlyList<ProviderStatus> Providers,
    string? K6Version,
    string? ActiveRunId,
    string? ActiveBaseUrl,
    string OrphanProtection,
    IReadOnlyList<string> Notes);

public sealed record ManifestSummary(
    string ManifestPath,
    string? Name,
    string Profile,
    string Environment,
    string Project,
    LoadProfile? Smoke,
    LoadProfile? Baseline,
    string EndpointSource,
    int DeclaredEndpoints,
    IReadOnlyDictionary<string, string> Dependencies,
    IReadOnlyDictionary<string, List<string>> Thresholds,
    IReadOnlyList<string> RequireConfirmation,
    IReadOnlyList<string> Warnings);

public sealed record PreflightData(ManifestSummary Manifest, ApiTarget? Target, EnvironmentProfile? Environment, RunPhase Phase, IReadOnlyList<string> BlockingReasons);

public sealed record StartedProcess(string Role, int? ProcessId, string BaseUrl, string? StdoutLog, string? StderrLog);

public sealed record StartLabData(
    string BaseUrl,
    int ProcessId,
    string ReadinessDetail,
    double BuildSeconds,
    IReadOnlyList<StartedProcess> Processes,
    EndpointPlan? Endpoints,
    IReadOnlyList<string> Notes);

public sealed record ScenarioData(
    LoadTestKind Kind,
    string ScriptPath,
    string ScriptSha256,
    string ScenarioHash,
    LoadProfile Load,
    IReadOnlyList<string> Endpoints,
    IReadOnlyList<ExcludedEndpoint> ExcludedEndpoints,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Thresholds,
    bool Validated,
    string? ValidationDetail,
    IReadOnlyList<string> ValidationIssues,
    ProviderInvocation? ValidationProvider);

public sealed record LoadTestData(LoadTestRun LoadTest, RuntimeSnapshot? Runtime, TimeCorrelation? Correlation, ScenarioData? Scenario);

public sealed record AnalysisData(
    DiagnosisVerdict Verdict,
    IReadOnlyList<Observation> Observations,
    IReadOnlyList<Hypothesis> Hypotheses,
    IReadOnlyList<NextAction> NextExperiments,
    IReadOnlyList<string> OpenQuestions,
    IReadOnlyList<string> Limitations,
    IReadOnlyList<string> Recommendations);

public sealed record ReportData(DiagnosisVerdict Verdict, string MarkdownPath, string JsonPath, IReadOnlyList<string> Headlines, int Observations, int Hypotheses);

public sealed record DiagnosisStep(string Step, string Outcome, string Detail);

public sealed record DiagnosisData(DiagnosisVerdict Verdict, IReadOnlyList<DiagnosisStep> Steps, ReportData? Report, CleanupSummary? Cleanup);

public sealed record DocsData(string Kind, string Content, ProviderInvocation? Provider);
