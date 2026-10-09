namespace PerfAgent.Core.Contracts;

/// <summary>Ejecución de k6 normalizada (independiente del proveedor upstream).</summary>
public sealed record LoadTestRun
{
    public required string Id { get; init; }

    public required LoadTestKind Kind { get; init; }

    /// <summary>true si la carga solo sirvió para capturar evidencia del runtime (no es una medición del baseline).</summary>
    public bool IsEvidenceLoad { get; init; }

    public required LoadProfile Load { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? EndedAt { get; init; }

    public required LoadTestStatus Status { get; init; }

    public string? StatusDetail { get; init; }

    public string? ScriptPath { get; init; }

    public string? ScriptSha256 { get; init; }

    /// <summary>Hash del escenario sin la URL base: igual entre ejecuciones comparables.</summary>
    public string? ScenarioHash { get; init; }

    public LatencyStats? Latency { get; init; }

    public long TotalRequests { get; init; }

    public double? RequestsPerSecond { get; init; }

    public long FailedRequests { get; init; }

    public double? ErrorRate { get; init; }

    public long ChecksPassed { get; init; }

    public long ChecksFailed { get; init; }

    public long Iterations { get; init; }

    public long DroppedIterations { get; init; }

    /// <summary>Distribución por clase de estado: 2xx, 3xx, 4xx, 5xx y 0 (error de red/timeout).</summary>
    public IReadOnlyDictionary<string, long> StatusClasses { get; init; } = new Dictionary<string, long>();

    public IReadOnlyList<EndpointStats> Endpoints { get; init; } = [];

    public IReadOnlyList<ThresholdResult> Thresholds { get; init; } = [];

    public IReadOnlyList<TimelineBucket> Timeline { get; init; } = [];

    /// <summary>Llamadas a dependencias simuladas medidas por el propio mock durante la ventana de carga.</summary>
    public IReadOnlyList<DependencyCallStats> DependencyCalls { get; init; } = [];

    public string? K6Version { get; init; }

    public int? ExitCode { get; init; }

    public ProviderInvocation? Provider { get; init; }

    /// <summary>Snapshot del runtime capturado en la misma ventana temporal.</summary>
    public string? RuntimeSnapshotId { get; init; }

    public IReadOnlyList<string> Artifacts { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <param name="Executor">shared-iterations, constant-vus o ramping-vus.</param>
public sealed record LoadProfile(string Executor, int Vus, int? Iterations, int DurationSeconds, int RampUpSeconds);

/// <summary>Latencias en milisegundos.</summary>
public sealed record LatencyStats(double? Avg, double? Min, double? Med, double? P90, double? P95, double? P99, double? Max);

public sealed record EndpointStats(
    string Name,
    string Method,
    string Path,
    long Requests,
    double? ErrorRate,
    LatencyStats? Latency,
    long ChecksPassed,
    long ChecksFailed);

public sealed record ThresholdResult(string Metric, string Expression, bool Passed);

/// <param name="Source">Origen del dato: <c>mock-stats</c> (contadores del simulador declarado).</param>
public sealed record DependencyCallStats(
    string Dependency,
    string Route,
    long Requests,
    long Failures,
    double? AvgLatencyMs,
    string Source);

/// <summary>Cubeta de la línea temporal de k6 (para correlación con el runtime).</summary>
public sealed record TimelineBucket(
    DateTimeOffset Start,
    double DurationSeconds,
    long Requests,
    long Failed,
    double? AvgMs,
    double? P95Ms,
    int? Vus);
