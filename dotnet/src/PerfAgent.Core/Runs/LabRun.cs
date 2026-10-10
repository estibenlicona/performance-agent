using PerfAgent.Core.Contracts;
using PerfAgent.Core.Discovery;

namespace PerfAgent.Core.Runs;

/// <summary>
/// Estado persistido de una ejecución de diagnóstico (<c>artifacts/runs/&lt;runId&gt;/run.json</c>).
/// Es la fuente de verdad del laboratorio: el agente consulta este estado en lugar de recordar la conversación.
/// </summary>
public sealed class LabRun
{
    public string SchemaVersion { get; set; } = "1.0";

    public required string RunId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public RunPhase Phase { get; set; } = RunPhase.Created;

    public DiagnosisVerdict Verdict { get; set; } = DiagnosisVerdict.NotEvaluated;

    public FailureKind Failure { get; set; } = FailureKind.None;

    public string? FailureDetail { get; set; }

    public required string ManifestPath { get; set; }

    public string? ManifestSha256 { get; set; }

    public string? ScenarioName { get; set; }

    public EnvironmentProfileKind Profile { get; set; }

    public ApiTarget? Target { get; set; }

    public EnvironmentProfile? Environment { get; set; }

    public LabProcessInfo? Api { get; set; }

    public List<LabProcessInfo> DependencyProcesses { get; set; } = [];

    public string? BaseUrl { get; set; }

    public EndpointPlan? Endpoints { get; set; }

    public Dictionary<string, ScenarioRecord> Scenarios { get; set; } = [];

    public List<LoadTestRun> LoadTests { get; set; } = [];

    public List<RuntimeSnapshot> RuntimeSnapshots { get; set; } = [];

    public List<Observation> Observations { get; set; } = [];

    public List<Hypothesis> Hypotheses { get; set; } = [];

    public List<NextAction> NextExperiments { get; set; } = [];

    public List<string> OpenQuestions { get; set; } = [];

    public List<string> Recommendations { get; set; } = [];

    public List<string> Limitations { get; set; } = [];

    public List<ProviderInvocation> ProviderInvocations { get; set; } = [];

    public List<RunEvent> Events { get; set; } = [];

    public CleanupSummary? Cleanup { get; set; }

    public ToolVersions Versions { get; set; } = new();

    public void Log(string step, string outcome, string? detail = null) =>
        Events.Add(new RunEvent(DateTimeOffset.UtcNow, step, outcome, detail));

    /// <summary>Última medición de ese tipo (excluye las cargas usadas solo para capturar evidencia).</summary>
    public LoadTestRun? LatestLoadTest(LoadTestKind kind) => LoadTests.LastOrDefault(t => t.Kind == kind && !t.IsEvidenceLoad);

    public void Fail(FailureKind kind, string detail)
    {
        Failure = kind;
        FailureDetail = detail;
        Phase = RunPhase.Failed;
        Verdict = DiagnosisVerdict.EnvironmentFailure;
    }
}

/// <param name="Role"><c>api</c>, <c>mock:&lt;nombre&gt;</c> o <c>container:&lt;nombre&gt;</c>.</param>
public sealed record LabProcessInfo(
    string Role,
    int? ProcessId,
    DateTimeOffset StartedAt,
    int Port,
    string BaseUrl,
    string Command,
    string? StdoutLog,
    string? StderrLog)
{
    public bool Exited { get; init; }

    public int? ExitCode { get; init; }
}

/// <summary>Escenario k6 generado por la fachada. Solo se ejecuta si su hash coincide y fue validado.</summary>
public sealed record ScenarioRecord(
    LoadTestKind Kind,
    string ScriptPath,
    string ScriptSha256,
    string ScenarioHash,
    LoadProfile Load,
    IReadOnlyList<string> Endpoints,
    DateTimeOffset GeneratedAt)
{
    public bool Validated { get; init; }

    public DateTimeOffset? ValidatedAt { get; init; }

    public string? ValidationDetail { get; init; }

    public ProviderInvocation? ValidationProvider { get; init; }
}
