using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace PerfAgent.Core.Manifest;

/// <summary>
/// Manifiesto declarativo de una ejecución (esquema v1, ver <c>scenarios/run-manifest.v1.schema.json</c>).
/// Se escribe en YAML con claves camelCase; los campos desconocidos se rechazan.
/// </summary>
public sealed class RunManifest
{
    public const int CurrentSchemaVersion = 1;

    [Description("Versión del esquema del manifiesto. Solo se admite 1.")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [Description("Nombre corto del escenario.")]
    public string? Name { get; set; }

    public string? Description { get; set; }

    [Description("Perfil de ambiente: local-isolated o local-hybrid.")]
    [AllowedValues("local-isolated", "local-hybrid")]
    public string Profile { get; set; } = "local-isolated";

    [Description("Etiqueta del ambiente objetivo (lab, dev, test...). Producción está prohibida.")]
    public string Environment { get; set; } = "lab";

    public ApiSection Api { get; set; } = new();

    [Description("Endpoints declarados. Obligatorios si la API no expone OpenAPI.")]
    public List<EndpointSection> Endpoints { get; set; } = [];

    public LoadSection Load { get; set; } = new();

    [Description("Dependencias por nombre.")]
    public Dictionary<string, DependencySection> Dependencies { get; set; } = [];

    public DiagnosticsSection Diagnostics { get; set; } = new();

    public SafetySection Safety { get; set; } = new();
}

public sealed class ApiSection
{
    [Description("Ruta relativa al workspace del .csproj de la API.")]
    public string? Project { get; set; }

    [Description("Solo Release en el MVP.")]
    [AllowedValues("Release")]
    public string Configuration { get; set; } = "Release";

    [Description("TargetFramework a usar si el proyecto declara varios.")]
    public string? Framework { get; set; }

    [Description("Valor de ASPNETCORE_ENVIRONMENT para la instancia de laboratorio.")]
    public string AspNetCoreEnvironment { get; set; } = "Development";

    [Description("Ruta de salud usada para readiness.")]
    public string HealthPath { get; set; } = "/health";

    public int ReadinessTimeoutSeconds { get; set; } = 60;

    [Description("Ruta del documento OpenAPI (JSON). Si se omite, se infiere por los paquetes del proyecto.")]
    public string? OpenApiPath { get; set; }

    [Description("Configuración NO secreta para la instancia (variables de entorno, p. ej. Lab__Faults__Cpu).")]
    public Dictionary<string, string> Settings { get; set; } = [];
}

public sealed class EndpointSection
{
    public string? Name { get; set; }

    [AllowedValues("GET", "HEAD", "POST", "PUT", "PATCH", "DELETE")]
    public string Method { get; set; } = "GET";

    [Description("Ruta relativa (empieza por /). Nunca una URL absoluta.")]
    public string? Path { get; set; }

    public int ExpectedStatus { get; set; } = 200;

    [Description("Texto que debe contener la respuesta (validación funcional mínima).")]
    public string? BodyContains { get; set; }

    public int Weight { get; set; } = 1;

    [Description("Valores sintéticos para parámetros de ruta {nombre}.")]
    public Dictionary<string, string> PathParameters { get; set; } = [];

    [Description("Cuerpo JSON sintético (solo escrituras autorizadas).")]
    public string? Body { get; set; }
}

public sealed class LoadSection
{
    [AllowedValues("k6")]
    public string Engine { get; set; } = "k6";

    [Description("Origen de endpoints: auto (OpenAPI si existe, si no declarados), openapi o declared.")]
    [AllowedValues("auto", "openapi", "declared")]
    public string EndpointSource { get; set; } = "auto";

    [Description("Si se indica, solo se prueban estos endpoints (por nombre o ruta).")]
    public List<string> Include { get; set; } = [];

    public List<string> Exclude { get; set; } = [];

    public SmokeSection Smoke { get; set; } = new();

    public BaselineSection Baseline { get; set; } = new();

    [Description("Thresholds de k6 por métrica, p. ej. http_req_duration: ['p(95)<500'].")]
    public Dictionary<string, List<string>> Thresholds { get; set; } = [];
}

public sealed class SmokeSection
{
    public int Vus { get; set; } = 1;

    [Description("Iteraciones totales del smoke test.")]
    public int Iterations { get; set; } = 10;

    [Description("Duración máxima (formato k6: 30s, 1m).")]
    public string MaxDuration { get; set; } = "30s";
}

public sealed class BaselineSection
{
    [AllowedValues("constant-vus", "ramping-vus")]
    public string Executor { get; set; } = "constant-vus";

    public int Vus { get; set; } = 10;

    [Description("Duración de la carga sostenida (formato k6: 30s, 2m).")]
    public string Duration { get; set; } = "30s";

    [Description("Rampa de subida (solo ramping-vus).")]
    public string RampUp { get; set; } = "0s";
}

public sealed class DependencySection
{
    [AllowedValues("container", "mock", "remote", "unavailable")]
    public string? Mode { get; set; }

    public bool Required { get; set; } = true;

    [Description("Clave de configuración de la API que recibe la URL de la dependencia (p. ej. Dependencies__Pricing__BaseUrl).")]
    public string? InjectAs { get; set; }

    [Description("mock: ruta del contrato JSON del simulador.")]
    public string? Contract { get; set; }

    [Description("remote: URL base autorizada (sin credenciales).")]
    public string? Url { get; set; }

    [Description("remote/container: ruta de salud a comprobar.")]
    public string? HealthPath { get; set; }

    [AllowedValues("none", "developer-credential")]
    public string Authentication { get; set; } = "none";

    [Description("container: archivo docker compose relativo al workspace.")]
    public string? ComposeFile { get; set; }

    [Description("container: servicio del compose.")]
    public string? Service { get; set; }

    [Description("container: puerto publicado en loopback.")]
    public int? Port { get; set; }

    [Description("Datos de la dependencia: synthetic (único valor admitido en el MVP).")]
    [AllowedValues("synthetic")]
    public string Data { get; set; } = "synthetic";
}

public sealed class DiagnosticsSection
{
    public bool RuntimeCounters { get; set; } = true;

    [Description("Intervalo de muestreo de counters en segundos.")]
    public int CounterIntervalSeconds { get; set; } = 1;

    [AllowedValues("on-demand", "disabled")]
    public string CpuTrace { get; set; } = "on-demand";

    [Description("Dumps de memoria: deshabilitados en el MVP.")]
    [AllowedValues("disabled")]
    public string MemoryDump { get; set; } = "disabled";
}

public sealed class SafetySection
{
    [AllowedValues("deny")]
    public string ProductionTargets { get; set; } = "deny";

    [AllowedValues("deny", "allow-declared")]
    public string WriteOperations { get; set; } = "deny";

    [AllowedValues("deny")]
    public string SecretsInModelContext { get; set; } = "deny";

    [Description("Pasos que requieren confirmación explícita (confirm=true) antes de ejecutarse, p. ej. [baseline].")]
    public List<string> RequireConfirmation { get; set; } = [];
}
