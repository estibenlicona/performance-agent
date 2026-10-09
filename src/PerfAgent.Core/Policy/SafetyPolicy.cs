using System.Text.Json;
using System.Text.Json.Serialization;

namespace PerfAgent.Core.Policy;

/// <summary>
/// Política de seguridad del laboratorio (archivo versionado <c>perf-lab.policy.json</c>).
/// Define límites duros que ningún manifiesto puede superar. Los valores por defecto son
/// restrictivos: solo loopback, solo GET/HEAD, sin dumps y sin destinos remotos.
/// La aplica el servidor <c>perf-lab</c>; no depende de las instrucciones del agente.
/// </summary>
public sealed class SafetyPolicy
{
    public const string FileName = "perf-lab.policy.json";

    public int SchemaVersion { get; set; } = 1;

    /// <summary>Raíces (relativas al workspace) donde pueden estar los proyectos a ejecutar.</summary>
    public List<string> AllowedProjectRoots { get; set; } = ["."];

    public List<string> AllowedManifestRoots { get; set; } = ["scenarios"];

    public List<string> AllowedContractRoots { get; set; } = ["lab/contracts", "contracts"];

    public LoadLimits Load { get; set; } = new();

    /// <summary>Hosts a los que k6 puede enviar tráfico. En el MVP solo loopback.</summary>
    public List<string> AllowedLoadTargetHosts { get; set; } = ["127.0.0.1"];

    /// <summary>Hosts remotos autorizados para dependencias (admite <c>*.dominio</c>). Vacío por defecto.</summary>
    public List<string> AllowedRemoteDependencyHosts { get; set; } = [];

    public List<string> DeniedEnvironmentNames { get; set; } = ["prod", "production", "prd", "live", "produccion", "producción"];

    /// <summary>Etiquetas de host (separadas por . o -) que delatan producción.</summary>
    public List<string> DeniedHostTokens { get; set; } = ["prod", "production", "prd", "live"];

    public List<string> AllowedHttpMethods { get; set; } = ["GET", "HEAD"];

    /// <summary><c>deny</c> o <c>allow-declared</c> (escrituras declaradas con datos sintéticos).</summary>
    public string WriteOperations { get; set; } = "deny";

    /// <summary>Frameworks validados en la matriz de pruebas (docs/tested-matrix.md).</summary>
    public List<string> SupportedTargetFrameworks { get; set; } = ["net10.0"];

    public bool AllowUntestedFrameworks { get; set; }

    public DiagnosticsPolicy Diagnostics { get; set; } = new();

    public TimeoutPolicy Timeouts { get; set; } = new();

    public string ArtifactsRoot { get; set; } = "artifacts";

    private static readonly JsonSerializerOptions StrictOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static SafetyPolicy LoadOrDefault(string workspaceRoot)
    {
        var path = Path.Combine(workspaceRoot, FileName);
        if (!File.Exists(path))
        {
            return new SafetyPolicy();
        }

        var policy = JsonSerializer.Deserialize<SafetyPolicy>(File.ReadAllText(path), StrictOptions)
            ?? throw new InvalidDataException($"{FileName} está vacío.");
        if (policy.SchemaVersion != 1)
        {
            throw new InvalidDataException($"{FileName}: schemaVersion {policy.SchemaVersion} no soportado.");
        }

        return policy;
    }
}

public sealed class LoadLimits
{
    public int MaxVus { get; set; } = 50;

    public int MaxDurationSeconds { get; set; } = 300;

    public int MaxSmokeVus { get; set; } = 2;

    public int MaxSmokeIterations { get; set; } = 50;

    public int MaxEndpoints { get; set; } = 20;
}

public sealed class DiagnosticsPolicy
{
    public bool AllowCounters { get; set; } = true;

    public bool AllowCpuSampling { get; set; } = true;

    public bool AllowEventCollection { get; set; } = true;

    /// <summary>Los dumps pueden contener secretos y datos personales: prohibidos en el MVP.</summary>
    public bool AllowDumps { get; set; }

    public bool AllowHeapInspection { get; set; }

    public bool AllowMethodParameterCapture { get; set; }

    public int MaxCaptureSeconds { get; set; } = 60;
}

public sealed class TimeoutPolicy
{
    public int BuildSeconds { get; set; } = 300;

    public int StartupSeconds { get; set; } = 90;

    public int K6GraceSeconds { get; set; } = 30;

    public int PreflightCheckSeconds { get; set; } = 5;

    public int ProviderCallSeconds { get; set; } = 120;
}
