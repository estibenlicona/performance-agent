namespace PerfAgent.Core.Contracts;

/// <summary>
/// Registro de una delegación a un proveedor upstream (p. ej. <c>grafana/mcp-k6</c> → <c>run_script</c>).
/// Permite demostrar en los artefactos qué proveedor, versión y herramienta se usaron.
/// </summary>
public sealed record ProviderInvocation
{
    public required string Provider { get; init; }

    public string? ProviderVersion { get; init; }

    public required string Tool { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public double DurationMs { get; init; }

    public bool Success { get; init; }

    /// <summary>true cuando se usó el fallback controlado (p. ej. k6 CLI) en lugar del proveedor MCP.</summary>
    public bool IsFallback { get; init; }

    /// <summary>Parámetros enviados (saneados) para demostrar qué recibió el proveedor.</summary>
    public IReadOnlyDictionary<string, string> Arguments { get; init; } = new Dictionary<string, string>();

    public string? Detail { get; init; }
}
