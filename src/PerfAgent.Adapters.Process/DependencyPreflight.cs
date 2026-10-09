using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;
using PerfAgent.Core.Contracts;
using PerfAgent.Core.Execution;
using PerfAgent.Core.Manifest;
using PerfAgent.Core.Policy;
using PerfAgent.Core.Security;

namespace PerfAgent.Adapters.Process;

/// <summary>
/// Preflight de dependencias: clasifica cada una como real, simulada, no disponible o no verificada, con checks
/// de DNS, TCP, TLS y salud cuando aplican. Una dependencia requerida inalcanzable bloquea el diagnóstico:
/// nunca se sustituye en silencio.
/// </summary>
public sealed class DependencyPreflight(ICommandRunner runner, Workspace workspace)
{
    public async Task<EnvironmentProfile> RunAsync(ValidatedManifest validated, CancellationToken cancellationToken)
    {
        var manifest = validated.Manifest;
        var checks = new List<DependencyCheck>();
        foreach (var (name, dependency) in manifest.Dependencies.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            checks.Add(dependency.Mode switch
            {
                "mock" => CheckMock(name, dependency, validated.ContractFullPaths.GetValueOrDefault(name)),
                "remote" => await CheckRemoteAsync(name, dependency, cancellationToken),
                "container" => await CheckContainerAsync(name, dependency, validated.ComposeFullPaths.GetValueOrDefault(name), cancellationToken),
                _ => new DependencyCheck
                {
                    Name = name,
                    Mode = DependencyMode.Unavailable,
                    Required = dependency.Required,
                    Fidelity = FidelityStatus.Unavailable,
                    Failure = FailureKind.DependencyUnavailable,
                    Detail = "Declarada como no disponible y sin reemplazo válido.",
                },
            });
        }

        var blocking = checks
            .Where(c => c.Required && c.Fidelity is FidelityStatus.Unavailable || (c.Required && c.Failure is FailureKind.AuthenticationError))
            .Select(c => $"La dependencia requerida '{c.Name}' ({Kebab(c.Mode)}) no está disponible: {c.Detail}")
            .ToList();
        var limitations = new List<string>();
        foreach (var c in checks)
        {
            if (c.Fidelity == FidelityStatus.Simulated)
            {
                limitations.Add($"'{c.Name}' es un mock declarado: las conclusiones se limitan a la API con ese comportamiento simulado.");
            }
            else if (!c.Required && c.Fidelity is FidelityStatus.Unavailable or FidelityStatus.NotVerified)
            {
                limitations.Add($"La dependencia opcional '{c.Name}' no está disponible o no se verificó: las rutas que la usan pueden fallar.");
            }
        }

        var decision = blocking.Count > 0
            ? PreflightDecision.Blocked
            : checks.Any(c => c.Fidelity is FidelityStatus.Unavailable or FidelityStatus.NotVerified) ? PreflightDecision.ContinueDegraded : PreflightDecision.Continue;
        return new EnvironmentProfile
        {
            Kind = validated.Profile,
            EnvironmentLabel = manifest.Environment,
            Dependencies = checks,
            Decision = decision,
            BlockingReasons = blocking,
            Limitations = limitations,
            CheckedAt = DateTimeOffset.UtcNow,
        };
    }

    private static DependencyCheck CheckMock(string name, DependencySection dependency, string? contractPath)
    {
        var result = new List<CheckResult>();
        string? detail;
        var ok = false;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(contractPath!));
            var routes = json.RootElement.GetProperty("routes").GetArrayLength();
            var contractName = json.RootElement.GetProperty("name").GetString();
            ok = routes > 0;
            detail = ok ? $"Contrato '{contractName}' con {routes} rutas." : "El contrato no declara rutas.";
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IOException or InvalidOperationException or ArgumentNullException)
        {
            detail = $"Contrato inválido: {ex.Message}";
        }

        result.Add(new CheckResult("contract", ok ? CheckOutcome.Passed : CheckOutcome.Failed, detail));
        return new DependencyCheck
        {
            Name = name,
            Mode = DependencyMode.Mock,
            Required = dependency.Required,
            Fidelity = ok ? FidelityStatus.Simulated : FidelityStatus.Unavailable,
            Checks = result,
            Failure = ok ? FailureKind.None : FailureKind.ConfigurationError,
            Detail = ok ? $"Simulador declarado ({detail})" : detail,
        };
    }

    private async Task<DependencyCheck> CheckRemoteAsync(string name, DependencySection dependency, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(workspace.Policy.Timeouts.PreflightCheckSeconds);
        var url = new Uri(dependency.Url!);
        var endpoint = $"{url.Scheme}://{url.Host}{(url.IsDefaultPort ? string.Empty : ":" + url.Port)}";
        var results = new List<CheckResult>();
        IPAddress[] addresses;
        var watch = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            addresses = await Dns.GetHostAddressesAsync(url.Host, cts.Token);
            results.Add(new CheckResult("dns", CheckOutcome.Passed, $"{addresses.Length} dirección(es).", watch.Elapsed.TotalMilliseconds));
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            results.Add(new CheckResult("dns", CheckOutcome.Failed, $"No se pudo resolver {url.Host}: {ex.Message}", watch.Elapsed.TotalMilliseconds));
            return Remote(name, dependency, endpoint, results, FidelityStatus.Unavailable, FailureKind.DependencyUnavailable, "Fallo de DNS antes de generar carga: error de ambiente, no de rendimiento.");
        }

        watch.Restart();
        if (!await PortAllocator.IsTcpOpenAsync(url.Host, url.Port, timeout, cancellationToken))
        {
            results.Add(new CheckResult("tcp", CheckOutcome.Failed, $"Sin conexión TCP a {url.Host}:{url.Port}.", watch.Elapsed.TotalMilliseconds));
            return Remote(name, dependency, endpoint, results, FidelityStatus.Unavailable, FailureKind.DependencyUnavailable, "Conectividad TCP fallida (red privada, firewall o servicio caído).");
        }

        results.Add(new CheckResult("tcp", CheckOutcome.Passed, $"Conexión TCP a {url.Host}:{url.Port}.", watch.Elapsed.TotalMilliseconds));
        if (url.Scheme == Uri.UriSchemeHttps)
        {
            watch.Restart();
            var tls = await CheckTlsAsync(url, timeout, cancellationToken);
            results.Add(tls with { DurationMs = watch.Elapsed.TotalMilliseconds });
            if (tls.Outcome == CheckOutcome.Failed)
            {
                return Remote(name, dependency, endpoint, results, FidelityStatus.Unavailable, FailureKind.DependencyUnavailable, "Fallo de TLS antes de generar carga.");
            }
        }

        if (dependency.HealthPath is null)
        {
            results.Add(new CheckResult("health", CheckOutcome.Skipped, "Sin healthPath declarado: solo se verificó conectividad."));
            return Remote(name, dependency, endpoint, results, FidelityStatus.NotVerified, FailureKind.None, "Conectividad verificada; el servicio no se comprobó a nivel de aplicación.");
        }

        watch.Restart();
        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = timeout, AllowAutoRedirect = false }) { Timeout = timeout };
        try
        {
            using var response = await http.GetAsync(new Uri(url, dependency.HealthPath), cancellationToken);
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                results.Add(new CheckResult("health", CheckOutcome.Passed, $"{dependency.HealthPath} → {status}.", watch.Elapsed.TotalMilliseconds));
                var authNote = dependency.Authentication == "developer-credential"
                    ? " La API usará su credencial de desarrollo; el laboratorio no obtiene ni maneja tokens."
                    : string.Empty;
                return Remote(name, dependency, endpoint, results, FidelityStatus.Real, FailureKind.None, "Dependencia remota real, accesible y autorizada." + authNote);
            }

            if (status is 401 or 403)
            {
                results.Add(new CheckResult("auth", CheckOutcome.Failed, $"{dependency.HealthPath} → {status}: requiere credenciales que el laboratorio no maneja.", watch.Elapsed.TotalMilliseconds));
                return Remote(name, dependency, endpoint, results, FidelityStatus.NotVerified, FailureKind.AuthenticationError,
                    $"Error de autenticación/autorización ({status}): no es un problema de rendimiento. Configura un endpoint de salud anónimo o verifica la identidad de la API.");
            }

            results.Add(new CheckResult("health", CheckOutcome.Failed, $"{dependency.HealthPath} → {status}.", watch.Elapsed.TotalMilliseconds));
            return Remote(name, dependency, endpoint, results, FidelityStatus.Unavailable, FailureKind.DependencyUnavailable, $"El endpoint de salud respondió {status}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            results.Add(new CheckResult("health", CheckOutcome.Failed, SecretRedactor.Redact(ex.Message), watch.Elapsed.TotalMilliseconds));
            return Remote(name, dependency, endpoint, results, FidelityStatus.Unavailable, FailureKind.DependencyUnavailable, "El endpoint de salud no respondió.");
        }
    }

    private static async Task<CheckResult> CheckTlsAsync(Uri url, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(url.Host, url.Port, cts.Token);
            await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = url.Host }, cts.Token);
            var expires = ssl.RemoteCertificate is System.Security.Cryptography.X509Certificates.X509Certificate2 cert ? cert.NotAfter.ToString("yyyy-MM-dd") : "?";
            return new CheckResult("tls", CheckOutcome.Passed, $"{ssl.SslProtocol}, certificado válido hasta {expires}.");
        }
        catch (Exception ex) when (ex is System.Security.Authentication.AuthenticationException or IOException or SocketException or OperationCanceledException)
        {
            return new CheckResult("tls", CheckOutcome.Failed, $"Handshake TLS fallido: {SecretRedactor.Redact(ex.Message)}");
        }
    }

    private async Task<DependencyCheck> CheckContainerAsync(string name, DependencySection dependency, string? composePath, CancellationToken cancellationToken)
    {
        var docker = await runner.RunAsync(AllowedCommands.DockerVersion(), TimeSpan.FromSeconds(15), cancellationToken);
        if (!docker.Succeeded)
        {
            return new DependencyCheck
            {
                Name = name,
                Mode = DependencyMode.Container,
                Required = dependency.Required,
                Fidelity = FidelityStatus.Unavailable,
                Checks = [new CheckResult("docker", CheckOutcome.Failed, "Docker no está instalado o el daemon no responde.")],
                Failure = FailureKind.ToolUnavailable,
                Detail = "Docker no disponible: no se puede levantar el contenedor de la dependencia.",
            };
        }

        return new DependencyCheck
        {
            Name = name,
            Mode = DependencyMode.Container,
            Required = dependency.Required,
            Fidelity = FidelityStatus.Real,
            Endpoint = $"127.0.0.1:{dependency.Port}",
            Checks =
            [
                new CheckResult("docker", CheckOutcome.Passed, $"Docker {docker.StandardOutput.Trim()}."),
                new CheckResult("compose-file", CheckOutcome.Passed, workspace.ToRelative(composePath!)),
            ],
            Detail = $"Instancia local del servicio '{dependency.Service}' con datos sintéticos (se levanta en start_lab).",
        };
    }

    private static DependencyCheck Remote(string name, DependencySection d, string endpoint, List<CheckResult> checks, FidelityStatus fidelity, FailureKind failure, string detail) =>
        new()
        {
            Name = name,
            Mode = DependencyMode.Remote,
            Required = d.Required,
            Fidelity = fidelity,
            Endpoint = endpoint,
            Checks = checks,
            Failure = failure,
            Detail = detail,
        };

    private static string Kebab<TEnum>(TEnum value) where TEnum : struct, Enum =>
        JsonNamingPolicy.KebabCaseLower.ConvertName(value.ToString());
}
