namespace PerfAgent.Adapters.DotNetDiagnosticsMcp;

/// <summary>
/// Superficie aprobada de <c>pedrosakuma/dotnet-diagnostics</c>. Todo lo demás (dumps, heap en vivo, lectura de
/// bytes, snapshots de hilos con suspensión, captura de parámetros, orquestadores cloud) queda fuera de la allowlist.
/// </summary>
public static class DiagnosticsCatalog
{
    public const string ProviderName = "pedrosakuma/dotnet-diagnostics";

    public static readonly IReadOnlyList<string> RequiredTools = ["inspect_process", "collect_events", "collect_sample", "collect_batch", "query_snapshot"];

    public static readonly IReadOnlyList<string> AllowedTools = RequiredTools;

    /// <summary>Vistas de bajo riesgo (sin ptrace ni suspensión del proceso).</summary>
    public static readonly IReadOnlySet<string> InspectViews = new HashSet<string>(StringComparer.Ordinal) { "info", "capabilities", "preflight", "triage" };

    public static readonly IReadOnlySet<string> EventKinds = new HashSet<string>(StringComparer.Ordinal) { "counters", "gc", "threadpool", "contention", "exceptions" };

    public static readonly IReadOnlySet<string> SampleKinds = new HashSet<string>(StringComparer.Ordinal) { "cpu", "allocation" };

    public static readonly IReadOnlySet<string> QueryViews = new HashSet<string>(StringComparer.Ordinal)
    {
        "top-methods", "hot-path", "timeline", "summary", "byGeneration", "byCallSite", "byOwner", "byType",
    };

    /// <summary>Capturas que el agente puede pedir con collect_runtime_evidence.</summary>
    public static readonly IReadOnlySet<string> EvidenceKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "cpu-sample", "gc-events", "threadpool-events", "contention-events", "exception-events",
    };
}
