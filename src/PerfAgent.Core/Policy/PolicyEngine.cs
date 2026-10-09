using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using PerfAgent.Core.Contracts;
using PerfAgent.Core.Manifest;
using PerfAgent.Core.Security;

namespace PerfAgent.Core.Policy;

/// <summary>Manifiesto que superó la política, con los valores ya normalizados.</summary>
public sealed record ValidatedManifest(
    LoadedManifest Source,
    EnvironmentProfileKind Profile,
    string ProjectFullPath,
    string ProjectRelativePath,
    LoadProfile Smoke,
    int SmokeMaxDurationSeconds,
    LoadProfile Baseline,
    IReadOnlyDictionary<string, string> ContractFullPaths,
    IReadOnlyDictionary<string, string> ComposeFullPaths)
{
    public RunManifest Manifest => Source.Manifest;
}

public sealed record ManifestValidation(PolicyDecision Decision, ValidatedManifest? Manifest);

/// <summary>
/// Aplica la política de seguridad a manifiestos y peticiones. Todas las comprobaciones se hacen aquí,
/// en el servidor, antes de compilar, iniciar procesos o generar tráfico.
/// </summary>
public sealed partial class PolicyEngine(Workspace workspace)
{
    private static readonly string[] ReservedSettingPrefixes =
    [
        "DOTNET_", "COMPLUS_", "CORECLR_", "COREHOST_", "ASPNETCORE_", "KESTREL__", "KESTREL:",
    ];

    private static readonly string[] ReservedSettingNames =
    [
        "URLS", "HTTP_PORTS", "HTTPS_PORTS", "PATH", "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "ALL_PROXY",
        "SSL_CERT_FILE", "SSL_CERT_DIR", "LD_PRELOAD", "LD_LIBRARY_PATH", "DYLD_INSERT_LIBRARIES",
    ];

    private static readonly HashSet<string> WriteMethods = new(StringComparer.OrdinalIgnoreCase) { "POST", "PUT", "PATCH", "DELETE" };

    private static readonly HashSet<string> ConfirmableSteps = new(StringComparer.OrdinalIgnoreCase) { "smoke", "baseline", "runtime-capture" };

    public Workspace Workspace => workspace;

    public SafetyPolicy Policy => workspace.Policy;

    /// <summary>Resuelve la ruta del manifiesto dentro de las raíces permitidas.</summary>
    public string? ResolveManifestPath(string path, out PolicyViolation? violation) =>
        workspace.TryResolve(path, Policy.AllowedManifestRoots, "manifestPath", out violation);

    public ManifestValidation Validate(LoadedManifest loaded)
    {
        var m = loaded.Manifest;
        var b = new PolicyDecisionBuilder();

        if (m.SchemaVersion != RunManifest.CurrentSchemaVersion)
        {
            b.Deny("manifest.schema-version", "schemaVersion", $"Solo se admite schemaVersion {RunManifest.CurrentSchemaVersion}.");
        }

        var profile = m.Profile switch
        {
            "local-isolated" => EnvironmentProfileKind.LocalIsolated,
            "local-hybrid" => EnvironmentProfileKind.LocalHybrid,
            _ => (EnvironmentProfileKind?)null,
        };
        if (profile is null)
        {
            b.Deny("manifest.profile", "profile", $"Perfil '{m.Profile}' no soportado en el MVP (local-isolated, local-hybrid).");
        }

        if (Policy.DeniedEnvironmentNames.Any(n => string.Equals(n, m.Environment?.Trim(), StringComparison.OrdinalIgnoreCase))
            || HasDeniedToken(m.Environment))
        {
            b.Deny("target.production", "environment", $"El ambiente '{m.Environment}' parece producción. Las pruebas contra producción están prohibidas.");
        }

        ValidateSafetySection(m.Safety, b);
        var projectFull = ValidateApi(m.Api, b);
        ValidateEndpoints(m, b);
        var (smoke, smokeMax, baseline) = ValidateLoad(m.Load, b);
        var (contracts, composeFiles) = ValidateDependencies(m, profile, b);
        ValidateDiagnostics(m.Diagnostics, b);

        var decision = b.Build();
        if (!decision.Allowed || projectFull is null || profile is null || smoke is null || baseline is null)
        {
            return new ManifestValidation(decision, null);
        }

        return new ManifestValidation(
            decision,
            new ValidatedManifest(
                loaded,
                profile.Value,
                projectFull,
                workspace.ToRelative(projectFull),
                smoke,
                smokeMax,
                baseline,
                contracts,
                composeFiles));
    }

    /// <summary>Comprueba que k6 solo apunte a la instancia de laboratorio iniciada por la fachada.</summary>
    public PolicyDecision EvaluateLoadTarget(Uri target, int expectedPort)
    {
        var b = new PolicyDecisionBuilder();
        if (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps)
        {
            b.Deny("target.scheme", "baseUrl", $"Esquema '{target.Scheme}' no permitido.");
        }

        if (!Policy.AllowedLoadTargetHosts.Contains(target.Host, StringComparer.OrdinalIgnoreCase))
        {
            b.Deny("target.host-not-allowed", "baseUrl", $"El host '{target.Host}' no está en allowedLoadTargetHosts ({string.Join(", ", Policy.AllowedLoadTargetHosts)}).");
        }
        else if (!IPAddress.TryParse(target.Host, out var ip) || !IPAddress.IsLoopback(ip))
        {
            b.Deny("target.not-loopback", "baseUrl", "En el MVP la carga solo puede dirigirse a una dirección IP de loopback.");
        }

        if (target.Port != expectedPort)
        {
            b.Deny("target.port-mismatch", "baseUrl", $"El puerto {target.Port} no corresponde a la API iniciada por el laboratorio ({expectedPort}).");
        }

        return b.Build();
    }

    /// <summary>Límites de carga en tiempo de ejecución (defensa en profundidad frente al manifiesto).</summary>
    public PolicyDecision EvaluateLoad(LoadTestKind kind, LoadProfile load)
    {
        var b = new PolicyDecisionBuilder();
        var limits = Policy.Load;
        if (kind == LoadTestKind.Smoke)
        {
            if (load.Vus < 1 || load.Vus > limits.MaxSmokeVus)
            {
                b.Deny("load.smoke-vus", "smoke.vus", $"El smoke test admite entre 1 y {limits.MaxSmokeVus} VUs.");
            }

            if (load.Iterations is null or < 1 || load.Iterations > limits.MaxSmokeIterations)
            {
                b.Deny("load.smoke-iterations", "smoke.iterations", $"El smoke test admite entre 1 y {limits.MaxSmokeIterations} iteraciones.");
            }
        }
        else
        {
            if (load.Vus < 1 || load.Vus > limits.MaxVus)
            {
                b.Deny("load.vus-exceeded", "baseline.vus", $"VUs {load.Vus} fuera del límite autorizado (1..{limits.MaxVus}).");
            }
        }

        var total = load.DurationSeconds + load.RampUpSeconds;
        if (load.DurationSeconds < 1 || total > limits.MaxDurationSeconds)
        {
            b.Deny("load.duration-exceeded", $"{kind.ToString().ToLowerInvariant()}.duration", $"Duración total {total}s fuera del límite autorizado (1..{limits.MaxDurationSeconds}s).");
        }

        return b.Build();
    }

    /// <summary>Capturas del runtime permitidas por la política (sin dumps ni inspección de heap).</summary>
    public PolicyDecision EvaluateCapture(string kind, int durationSeconds)
    {
        var d = Policy.Diagnostics;
        var b = new PolicyDecisionBuilder();
        var allowed = kind switch
        {
            "counters" => d.AllowCounters,
            "cpu-sample" => d.AllowCpuSampling,
            "gc-events" or "threadpool-events" or "contention-events" or "exception-events" => d.AllowEventCollection,
            "dump" => d.AllowDumps,
            "heap" => d.AllowHeapInspection,
            _ => false,
        };
        if (!allowed)
        {
            b.Deny("diagnostics.capture-not-allowed", "kind", $"La captura '{kind}' no está permitida por la política de diagnóstico.");
        }

        if (durationSeconds < 1 || durationSeconds > d.MaxCaptureSeconds)
        {
            b.Deny("diagnostics.duration", "durationSeconds", $"La captura admite entre 1 y {d.MaxCaptureSeconds} segundos.");
        }

        return b.Build();
    }

    public bool IsWriteMethod(string method) => WriteMethods.Contains(method);

    public bool WritesAllowed(RunManifest manifest) =>
        string.Equals(Policy.WriteOperations, "allow-declared", StringComparison.OrdinalIgnoreCase)
        && string.Equals(manifest.Safety.WriteOperations, "allow-declared", StringComparison.OrdinalIgnoreCase);

    public bool IsMethodAllowed(RunManifest manifest, string method) =>
        Policy.AllowedHttpMethods.Contains(method, StringComparer.OrdinalIgnoreCase)
        || (IsWriteMethod(method) && WritesAllowed(manifest));

    public bool HasDeniedToken(string? hostOrLabel)
    {
        if (string.IsNullOrWhiteSpace(hostOrLabel))
        {
            return false;
        }

        var tokens = hostOrLabel.ToLowerInvariant().Split(['.', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        return tokens.Any(t => Policy.DeniedHostTokens.Contains(t, StringComparer.OrdinalIgnoreCase));
    }

    public bool IsRemoteHostAllowed(string host) =>
        Policy.AllowedRemoteDependencyHosts.Any(pattern =>
            pattern.StartsWith("*.", StringComparison.Ordinal)
                ? host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)
                : string.Equals(pattern, host, StringComparison.OrdinalIgnoreCase));

    /// <summary>Valida una expresión de threshold de k6 (se embebe en el script: debe ser estricta).</summary>
    public static bool IsValidThreshold(string metric, string expression) =>
        ThresholdMetricRegex().IsMatch(metric) && ThresholdExpressionRegex().IsMatch(expression.Trim());

    private void ValidateSafetySection(SafetySection s, PolicyDecisionBuilder b)
    {
        if (!string.Equals(s.ProductionTargets, "deny", StringComparison.OrdinalIgnoreCase))
        {
            b.Deny("safety.production-targets", "safety.productionTargets", "productionTargets debe ser 'deny'.");
        }

        if (!string.Equals(s.SecretsInModelContext, "deny", StringComparison.OrdinalIgnoreCase))
        {
            b.Deny("safety.secrets", "safety.secretsInModelContext", "secretsInModelContext debe ser 'deny'.");
        }

        if (s.WriteOperations is not ("deny" or "allow-declared"))
        {
            b.Deny("safety.write-operations", "safety.writeOperations", "writeOperations admite 'deny' o 'allow-declared'.");
        }
        else if (s.WriteOperations == "allow-declared" && !string.Equals(Policy.WriteOperations, "allow-declared", StringComparison.OrdinalIgnoreCase))
        {
            b.Deny("safety.write-operations-policy", "safety.writeOperations", "La política del laboratorio no permite operaciones de escritura (perf-lab.policy.json: writeOperations=deny).");
        }

        foreach (var step in s.RequireConfirmation.Where(step => !ConfirmableSteps.Contains(step)))
        {
            b.Deny("safety.require-confirmation", "safety.requireConfirmation", $"Paso '{step}' desconocido (smoke, baseline, runtime-capture).");
        }
    }

    private string? ValidateApi(ApiSection api, PolicyDecisionBuilder b)
    {
        string? projectFull = null;
        if (string.IsNullOrWhiteSpace(api.Project))
        {
            b.Deny("api.project-missing", "api.project", "Indica la ruta explícita del .csproj de la API.");
        }
        else if (!api.Project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            b.Deny("api.project-not-csproj", "api.project", "El MVP solo inicia un .csproj explícito (no .sln).");
        }
        else
        {
            projectFull = workspace.TryResolve(api.Project, Policy.AllowedProjectRoots, "api.project", out var violation);
            if (violation is not null)
            {
                b.Deny(violation.Code, violation.Field, violation.Message);
            }
            else if (!File.Exists(projectFull))
            {
                b.Deny("api.project-not-found", "api.project", $"No existe el proyecto '{api.Project}'.");
                projectFull = null;
            }
        }

        if (!string.Equals(api.Configuration, "Release", StringComparison.Ordinal))
        {
            b.Deny("api.configuration", "api.configuration", "El MVP solo compila en configuración Release.");
        }

        if (!SimpleNameRegex().IsMatch(api.AspNetCoreEnvironment ?? string.Empty)
            || Policy.DeniedEnvironmentNames.Contains(api.AspNetCoreEnvironment!, StringComparer.OrdinalIgnoreCase))
        {
            b.Deny("api.environment-name", "api.aspNetCoreEnvironment", "ASPNETCORE_ENVIRONMENT no válido o de producción.");
        }

        if (!IsSafeRelativePath(api.HealthPath))
        {
            b.Deny("api.health-path", "api.healthPath", "healthPath debe ser una ruta relativa que empiece por '/'.");
        }

        if (api.OpenApiPath is not null && !IsSafeRelativePath(api.OpenApiPath))
        {
            b.Deny("api.openapi-path", "api.openApiPath", "openApiPath debe ser una ruta relativa que empiece por '/'.");
        }

        if (api.ReadinessTimeoutSeconds is < 5 || api.ReadinessTimeoutSeconds > Policy.Timeouts.StartupSeconds)
        {
            b.Deny("api.readiness-timeout", "api.readinessTimeoutSeconds", $"readinessTimeoutSeconds admite 5..{Policy.Timeouts.StartupSeconds}.");
        }

        if (api.Framework is not null && !FrameworkRegex().IsMatch(api.Framework))
        {
            b.Deny("api.framework", "api.framework", "framework debe tener la forma netX.Y.");
        }

        foreach (var (key, value) in api.Settings)
        {
            ValidateSetting(key, value, $"api.settings.{key}", b);
        }

        return projectFull;
    }

    private static void ValidateSetting(string key, string? value, string field, PolicyDecisionBuilder b)
    {
        if (!SettingKeyRegex().IsMatch(key))
        {
            b.Deny("settings.key-invalid", field, "Clave de configuración no válida.");
            return;
        }

        var upper = key.ToUpperInvariant();
        if (ReservedSettingPrefixes.Any(p => upper.StartsWith(p, StringComparison.Ordinal)) || ReservedSettingNames.Contains(upper))
        {
            b.Deny("settings.reserved", field, "Clave reservada: controla el host, el runtime, la red o el diagnóstico y la fija el laboratorio.");
            return;
        }

        if (SecretRedactor.IsSensitiveKey(key))
        {
            b.Deny("settings.secret-key", field, "Los secretos no se declaran en el manifiesto: configúralos fuera del repositorio (user-secrets o variables del entorno local).");
            return;
        }

        if (value is null || value.Length > 512 || value.Contains('\n') || value.Contains('\r') || value.Contains('\0'))
        {
            b.Deny("settings.value-invalid", field, "Valor no válido (máximo 512 caracteres, sin saltos de línea).");
            return;
        }

        if (SecretRedactor.LooksLikeSecret(value))
        {
            b.Deny("settings.secret-value", field, "El valor parece contener un secreto; no se admite en el manifiesto.");
        }
    }

    private void ValidateEndpoints(RunManifest m, PolicyDecisionBuilder b)
    {
        if (m.Endpoints.Count > Policy.Load.MaxEndpoints)
        {
            b.Deny("endpoints.too-many", "endpoints", $"Máximo {Policy.Load.MaxEndpoints} endpoints por ejecución.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < m.Endpoints.Count; i++)
        {
            var e = m.Endpoints[i];
            var field = $"endpoints[{i}]";
            if (string.IsNullOrWhiteSpace(e.Name) || !EndpointNameRegex().IsMatch(e.Name))
            {
                b.Deny("endpoints.name", $"{field}.name", "Nombre obligatorio: letras, números, '.', '_' o '-' (máx. 64).");
            }
            else if (!names.Add(e.Name))
            {
                b.Deny("endpoints.duplicate", $"{field}.name", $"Endpoint '{e.Name}' duplicado.");
            }

            if (!IsSafeRelativePath(e.Path))
            {
                b.Deny("endpoints.path", $"{field}.path", "La ruta debe ser relativa, empezar por '/' y no contener esquema, host ni '..'.");
            }
            else
            {
                foreach (Match match in PathParameterRegex().Matches(e.Path!))
                {
                    var name = match.Groups[1].Value;
                    if (!e.PathParameters.TryGetValue(name, out var sample) || !PathSampleRegex().IsMatch(sample))
                    {
                        b.Deny("endpoints.path-parameter", $"{field}.pathParameters.{name}", $"Falta un valor sintético válido para el parámetro de ruta '{{{name}}}'.");
                    }
                }
            }

            var method = e.Method.ToUpperInvariant();
            if (method is not ("GET" or "HEAD" or "POST" or "PUT" or "PATCH" or "DELETE"))
            {
                b.Deny("endpoints.method", $"{field}.method", $"Método '{e.Method}' no soportado.");
            }
            else if (!IsMethodAllowed(m, method))
            {
                b.Deny("endpoints.write-not-authorized", $"{field}.method", $"{method} es una operación de escritura y no está autorizada (política y manifiesto deben declarar writeOperations: allow-declared).");
            }

            if (e.Body is not null && !IsWriteMethod(method))
            {
                b.Deny("endpoints.body", $"{field}.body", "Solo las escrituras autorizadas pueden declarar cuerpo.");
            }

            if (e.Body is not null && (e.Body.Length > 4096 || SecretRedactor.LooksLikeSecret(e.Body)))
            {
                b.Deny("endpoints.body-invalid", $"{field}.body", "Cuerpo demasiado grande o con apariencia de secreto.");
            }

            if (e.ExpectedStatus is < 100 or > 599)
            {
                b.Deny("endpoints.expected-status", $"{field}.expectedStatus", "expectedStatus debe estar entre 100 y 599.");
            }

            if (e.Weight is < 1 or > 100)
            {
                b.Deny("endpoints.weight", $"{field}.weight", "weight admite 1..100.");
            }

            if (e.BodyContains is { Length: > 256 })
            {
                b.Deny("endpoints.body-contains", $"{field}.bodyContains", "bodyContains admite como máximo 256 caracteres.");
            }
        }

        if (m.Load.EndpointSource == "declared" && m.Endpoints.Count == 0)
        {
            b.Deny("endpoints.missing", "endpoints", "endpointSource=declared requiere al menos un endpoint declarado.");
        }
    }

    private (LoadProfile? Smoke, int SmokeMax, LoadProfile? Baseline) ValidateLoad(LoadSection load, PolicyDecisionBuilder b)
    {
        if (!string.Equals(load.Engine, "k6", StringComparison.Ordinal))
        {
            b.Deny("load.engine", "load.engine", "Solo se admite k6.");
        }

        if (load.EndpointSource is not ("auto" or "openapi" or "declared"))
        {
            b.Deny("load.endpoint-source", "load.endpointSource", "endpointSource admite auto, openapi o declared.");
        }

        foreach (var (metric, expressions) in load.Thresholds)
        {
            foreach (var expression in expressions)
            {
                if (!IsValidThreshold(metric, expression))
                {
                    b.Deny("load.threshold", $"load.thresholds.{metric}", $"Threshold no válido: '{expression}'.");
                }
            }
        }

        LoadProfile? smoke = null;
        var smokeMax = 0;
        if (!K6Duration.TryParseSeconds(load.Smoke.MaxDuration, out smokeMax))
        {
            b.Deny("load.smoke-duration", "load.smoke.maxDuration", "maxDuration no tiene formato k6 válido (p. ej. 30s).");
        }
        else
        {
            smoke = new LoadProfile("shared-iterations", load.Smoke.Vus, load.Smoke.Iterations, smokeMax, 0);
            b.Merge(EvaluateLoad(LoadTestKind.Smoke, smoke));
        }

        LoadProfile? baseline = null;
        var b0 = load.Baseline;
        if (b0.Executor is not ("constant-vus" or "ramping-vus"))
        {
            b.Deny("load.executor", "load.baseline.executor", "executor admite constant-vus o ramping-vus.");
        }
        else if (!K6Duration.TryParseSeconds(b0.Duration, out var duration) || !K6Duration.TryParseSeconds(b0.RampUp, out var rampUp))
        {
            b.Deny("load.baseline-duration", "load.baseline.duration", "duration/rampUp no tienen formato k6 válido (p. ej. 30s, 2m).");
        }
        else if (b0.Executor == "constant-vus" && rampUp > 0)
        {
            b.Deny("load.ramp-up", "load.baseline.rampUp", "rampUp solo aplica al executor ramping-vus.");
        }
        else
        {
            baseline = new LoadProfile(b0.Executor, b0.Vus, null, duration, rampUp);
            b.Merge(EvaluateLoad(LoadTestKind.Baseline, baseline));
        }

        return (smoke, smokeMax, baseline);
    }

    private (Dictionary<string, string> Contracts, Dictionary<string, string> ComposeFiles) ValidateDependencies(
        RunManifest m, EnvironmentProfileKind? profile, PolicyDecisionBuilder b)
    {
        var contracts = new Dictionary<string, string>(StringComparer.Ordinal);
        var composeFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, d) in m.Dependencies)
        {
            var field = $"dependencies.{name}";
            if (!EndpointNameRegex().IsMatch(name))
            {
                b.Deny("dependencies.name", field, "Nombre de dependencia no válido.");
            }

            if (d.InjectAs is not null)
            {
                ValidateSetting(d.InjectAs, "http://127.0.0.1:1", $"{field}.injectAs", b);
            }

            if (!string.Equals(d.Data, "synthetic", StringComparison.Ordinal))
            {
                b.Deny("dependencies.data", $"{field}.data", "El MVP solo admite datos sintéticos.");
            }

            switch (d.Mode)
            {
                case "mock":
                    var contract = workspace.TryResolve(d.Contract, Policy.AllowedContractRoots, $"{field}.contract", out var cv);
                    if (cv is not null)
                    {
                        b.Deny(cv.Code, cv.Field, cv.Message);
                    }
                    else if (!File.Exists(contract))
                    {
                        b.Deny("dependencies.contract-missing", $"{field}.contract", $"No existe el contrato '{d.Contract}'.");
                    }
                    else
                    {
                        contracts[name] = contract!;
                    }

                    if (d.InjectAs is null)
                    {
                        b.Deny("dependencies.inject-missing", $"{field}.injectAs", "Un mock necesita injectAs para que la API use su URL.");
                    }

                    break;

                case "remote":
                    if (profile == EnvironmentProfileKind.LocalIsolated)
                    {
                        b.Deny("dependencies.remote-in-isolated", $"{field}.mode", "Una dependencia remota requiere el perfil local-hybrid.");
                    }

                    ValidateRemoteUrl(d, field, b);
                    break;

                case "container":
                    var compose = workspace.TryResolve(d.ComposeFile, Policy.AllowedProjectRoots, $"{field}.composeFile", out var compv);
                    if (compv is not null)
                    {
                        b.Deny(compv.Code, compv.Field, compv.Message);
                    }
                    else if (!File.Exists(compose))
                    {
                        b.Deny("dependencies.compose-missing", $"{field}.composeFile", $"No existe '{d.ComposeFile}'.");
                    }
                    else
                    {
                        composeFiles[name] = compose!;
                    }

                    if (string.IsNullOrWhiteSpace(d.Service) || !EndpointNameRegex().IsMatch(d.Service))
                    {
                        b.Deny("dependencies.service", $"{field}.service", "service obligatorio y con nombre válido.");
                    }

                    if (d.Port is null or < 1 or > 65535)
                    {
                        b.Deny("dependencies.port", $"{field}.port", "port obligatorio (1..65535) para comprobar la disponibilidad.");
                    }

                    break;

                case "unavailable":
                    break;

                default:
                    b.Deny("dependencies.mode", $"{field}.mode", "mode admite container, mock, remote o unavailable.");
                    break;
            }

            if (d.Authentication is not ("none" or "developer-credential"))
            {
                b.Deny("dependencies.authentication", $"{field}.authentication", "authentication admite none o developer-credential. La identidad administrada/federada no se simula en el MVP.");
            }
        }

        return (contracts, composeFiles);
    }

    private void ValidateRemoteUrl(DependencySection d, string field, PolicyDecisionBuilder b)
    {
        if (!Uri.TryCreate(d.Url, UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
        {
            b.Deny("dependencies.url", $"{field}.url", "url debe ser absoluta http(s).");
            return;
        }

        if (!string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Query) || SecretRedactor.LooksLikeSecret(d.Url))
        {
            b.Deny("dependencies.url-credentials", $"{field}.url", "La URL no puede incluir credenciales ni query string.");
        }

        if (HasDeniedToken(url.Host))
        {
            b.Deny("target.production", $"{field}.url", $"El host '{url.Host}' parece de producción.");
        }

        if (!IsRemoteHostAllowed(url.Host))
        {
            b.Deny("dependencies.remote-host-not-allowed", $"{field}.url", $"El host '{url.Host}' no está en allowedRemoteDependencyHosts de perf-lab.policy.json.");
        }

        if (d.HealthPath is not null && !IsSafeRelativePath(d.HealthPath))
        {
            b.Deny("dependencies.health-path", $"{field}.healthPath", "healthPath debe ser relativa.");
        }
    }

    private void ValidateDiagnostics(DiagnosticsSection d, PolicyDecisionBuilder b)
    {
        if (d.MemoryDump != "disabled")
        {
            b.Deny("diagnostics.dumps", "diagnostics.memoryDump", "Los dumps de memoria están deshabilitados en el MVP (pueden contener secretos o datos personales).");
        }

        if (d.CpuTrace is not ("on-demand" or "disabled"))
        {
            b.Deny("diagnostics.cpu-trace", "diagnostics.cpuTrace", "cpuTrace admite on-demand o disabled.");
        }

        if (d.CounterIntervalSeconds is < 1 or > 10)
        {
            b.Deny("diagnostics.interval", "diagnostics.counterIntervalSeconds", "counterIntervalSeconds admite 1..10.");
        }

        if (d.RuntimeCounters && !Policy.Diagnostics.AllowCounters)
        {
            b.Deny("diagnostics.counters", "diagnostics.runtimeCounters", "La política no permite capturar counters.");
        }
    }

    public static bool IsSafeRelativePath(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && path.Length <= 512
        && path.StartsWith('/')
        && !path.StartsWith("//", StringComparison.Ordinal)
        && !path.Contains("..", StringComparison.Ordinal)
        && !path.Contains('\\')
        && !path.Contains("://", StringComparison.Ordinal)
        && SafePathRegex().IsMatch(path);

    [GeneratedRegex(@"^/[A-Za-z0-9_\-./{}~%:@!$&'()*+,;=?]*$")]
    private static partial Regex SafePathRegex();

    [GeneratedRegex(@"\{([A-Za-z_][A-Za-z0-9_]*)(?::[a-z]+)?\}")]
    private static partial Regex PathParameterRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_\-.~]{1,64}$")]
    private static partial Regex PathSampleRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]{1,64}$")]
    private static partial Regex EndpointNameRegex();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_.:\-]{0,127}$")]
    private static partial Regex SettingKeyRegex();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_\-]{0,31}$")]
    private static partial Regex SimpleNameRegex();

    [GeneratedRegex(@"^net\d{1,2}\.\d$")]
    private static partial Regex FrameworkRegex();

    [GeneratedRegex(@"^[a-z_]{1,64}(\{[a-z_]{1,32}:[A-Za-z0-9_.\-/]{1,64}\})?$")]
    private static partial Regex ThresholdMetricRegex();

    [GeneratedRegex(@"^(p\((\d{1,2}(\.\d{1,3})?|100)\)|avg|min|max|med|rate|count)\s*(<|<=|>|>=|==|!=)\s*\d{1,9}(\.\d{1,6})?$")]
    private static partial Regex ThresholdExpressionRegex();

    internal static string FormatInvariant(double value) => value.ToString(CultureInfo.InvariantCulture);
}
