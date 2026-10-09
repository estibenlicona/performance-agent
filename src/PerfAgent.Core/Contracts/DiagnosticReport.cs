namespace PerfAgent.Core.Contracts;

/// <summary>Informe reproducible de una ejecución. Se serializa a JSON y se renderiza a Markdown.</summary>
public sealed record DiagnosticReport
{
    public const string CurrentSchemaVersion = "1.0";

    public string SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required string RunId { get; init; }

    public required DateTimeOffset GeneratedAt { get; init; }

    public required RunPhase Phase { get; init; }

    public required DiagnosisVerdict Verdict { get; init; }

    public FailureKind Failure { get; init; }

    public string? FailureDetail { get; init; }

    public required string ManifestPath { get; init; }

    public string? ManifestSha256 { get; init; }

    public ApiTarget? Target { get; init; }

    public EnvironmentProfile? Environment { get; init; }

    public required ToolVersions Versions { get; init; }

    public IReadOnlyList<LoadTestRun> LoadTests { get; init; } = [];

    public IReadOnlyList<RuntimeSnapshot> RuntimeSnapshots { get; init; } = [];

    public IReadOnlyList<TimeCorrelation> Correlations { get; init; } = [];

    public IReadOnlyList<Observation> Observations { get; init; } = [];

    public IReadOnlyList<Hypothesis> Hypotheses { get; init; } = [];

    public IReadOnlyList<string> Limitations { get; init; } = [];

    public IReadOnlyList<string> OpenQuestions { get; init; } = [];

    public IReadOnlyList<NextAction> NextExperiments { get; init; } = [];

    public IReadOnlyList<string> Recommendations { get; init; } = [];

    public IReadOnlyList<ProviderInvocation> ProviderInvocations { get; init; } = [];

    public CleanupSummary? Cleanup { get; init; }

    public IReadOnlyList<RunEvent> Events { get; init; } = [];

    public IReadOnlyList<string> Artifacts { get; init; } = [];
}

public sealed record ToolVersions
{
    public string? PerfAgent { get; init; }

    public string? DotNetSdk { get; init; }

    public string? TargetRuntime { get; init; }

    public string? K6 { get; init; }

    public string? K6McpProvider { get; init; }

    public string? DiagnosticsProvider { get; init; }

    public string? OperatingSystem { get; init; }

    public int? ProcessorCount { get; init; }
}

/// <summary>Ventana de k6 frente a la ventana de captura del runtime.</summary>
public sealed record TimeCorrelation(
    string LoadTestId,
    string RuntimeSnapshotId,
    DateTimeOffset LoadStart,
    DateTimeOffset LoadEnd,
    DateTimeOffset RuntimeStart,
    DateTimeOffset RuntimeEnd,
    double OverlapSeconds,
    double OverlapRatio,
    string Assessment);

public sealed record CleanupSummary(
    DateTimeOffset At,
    IReadOnlyList<string> Stopped,
    IReadOnlyList<string> FailedToStop,
    string Trigger);

/// <summary>Bitácora de pasos de la ejecución (auditoría).</summary>
public sealed record RunEvent(DateTimeOffset At, string Step, string Outcome, string? Detail = null);
