namespace PerfAgent.Core.Contracts;

/// <summary>Resultado del preflight: perfil, fidelidad por dependencia y decisión explícita.</summary>
public sealed record EnvironmentProfile
{
    public required EnvironmentProfileKind Kind { get; init; }

    /// <summary>Etiqueta declarada del ambiente (p. ej. <c>lab</c>, <c>dev</c>). Producción se rechaza.</summary>
    public required string EnvironmentLabel { get; init; }

    public required IReadOnlyList<DependencyCheck> Dependencies { get; init; }

    public required PreflightDecision Decision { get; init; }

    public IReadOnlyList<string> BlockingReasons { get; init; } = [];

    public IReadOnlyList<string> Limitations { get; init; } = [];

    public DateTimeOffset CheckedAt { get; init; }
}

public sealed record DependencyCheck
{
    public required string Name { get; init; }

    public required DependencyMode Mode { get; init; }

    public bool Required { get; init; }

    public required FidelityStatus Fidelity { get; init; }

    /// <summary>Endpoint saneado (sin credenciales, sin query string).</summary>
    public string? Endpoint { get; init; }

    public IReadOnlyList<CheckResult> Checks { get; init; } = [];

    public FailureKind Failure { get; init; }

    public string? Detail { get; init; }
}

/// <param name="Name">dns, tcp, tls, health, auth, contract, docker, compose-file.</param>
public sealed record CheckResult(string Name, CheckOutcome Outcome, string Detail, double? DurationMs = null);
