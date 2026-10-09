namespace PerfAgent.Core.Contracts;

/// <summary>API objetivo descubierta: qué se va a iniciar, con qué runtime y qué falta para continuar.</summary>
public sealed record ApiTarget
{
    /// <summary>Ruta relativa al workspace del proyecto (.csproj) o solución inspeccionada.</summary>
    public required string ProjectPath { get; init; }

    public required string ProjectName { get; init; }

    public string? AssemblyName { get; init; }

    public bool IsWebProject { get; init; }

    public IReadOnlyList<string> TargetFrameworks { get; init; } = [];

    public string? SelectedFramework { get; init; }

    public RuntimeCompatibility? Runtime { get; init; }

    public OpenApiDetection? OpenApi { get; init; }

    /// <summary>Proyectos candidatos cuando se indicó una solución (.sln/.slnx).</summary>
    public IReadOnlyList<string> CandidateProjects { get; init; } = [];

    /// <summary>Dependencias detectadas por nombre de clave/paquete. Nunca incluye valores de secretos.</summary>
    public IReadOnlyList<DependencyHint> DependencyHints { get; init; } = [];

    public IReadOnlyList<string> MissingInformation { get; init; } = [];

    public IReadOnlyList<string> Notes { get; init; } = [];

    public SourceRevision? Revision { get; init; }
}

/// <param name="Compatibility"><c>tested</c>, <c>untested</c> o <c>unsupported</c> según la matriz probada.</param>
public sealed record RuntimeCompatibility(
    string Framework,
    string? RequiredAspNetCoreMajor,
    IReadOnlyList<string> InstalledAspNetCoreRuntimes,
    bool RuntimeAvailable,
    string Compatibility,
    string? Detail);

public sealed record OpenApiDetection(bool Likely, string? Package, string? DocumentPath, string? Note);

/// <param name="Kind">sql, redis, http, messaging, storage, key-vault, identity, mongodb, other.</param>
/// <param name="Source">Origen de la pista: package, connection-string, configuration-key, user-secrets.</param>
/// <param name="Host">Host saneado (sin credenciales ni query) cuando se pudo extraer.</param>
public sealed record DependencyHint(string Name, string Kind, string Source, string? Host, string? Note);

public sealed record SourceRevision(string? Commit, string? Branch, bool? HasUncommittedChanges, string? Note);
