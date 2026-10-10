namespace PerfAgent.Core.Contracts;

/// <param name="Source">k6, runtime, preflight, process, provider, mock.</param>
/// <param name="Signal">Nombre de la señal (p. ej. <c>http_req_duration.p95</c>, <c>cpu.avg</c>).</param>
/// <param name="ArtifactRef">Ruta relativa del artefacto que respalda el dato.</param>
public sealed record EvidenceItem(
    string Source,
    string Signal,
    string Description,
    double? Value = null,
    string? Unit = null,
    string? ArtifactRef = null);

/// <summary>Síntoma observado: un hecho medido, no una causa.</summary>
/// <param name="Category">latency, errors, throughput, cpu, memory, gc, threadpool, contention, dependency, environment, functional, generator.</param>
public sealed record Observation(string Id, string Category, string Description, IReadOnlyList<EvidenceItem> Evidence);

/// <summary>Siguiente acción recomendada; siempre una herramienta del catálogo <c>perf-lab</c>.</summary>
public sealed record NextAction(string Tool, IReadOnlyDictionary<string, string> Arguments, string Rationale);

public sealed record Hypothesis
{
    public required string Id { get; init; }

    public required string RuleId { get; init; }

    public required string Title { get; init; }

    public required string Statement { get; init; }

    public required HypothesisStatus Status { get; init; }

    public required Confidence Confidence { get; init; }

    public IReadOnlyList<EvidenceItem> SupportingEvidence { get; init; } = [];

    public IReadOnlyList<EvidenceItem> ContradictingEvidence { get; init; } = [];

    /// <summary>Evidencia que falta para confirmar o descartar la hipótesis.</summary>
    public IReadOnlyList<string> MissingEvidence { get; init; } = [];

    public NextAction? NextAction { get; init; }

    public IReadOnlyList<string> Limitations { get; init; } = [];
}
