using PerfAgent.Core.Contracts;

namespace PerfAgent.Adapters.Process;

/// <summary>
/// Distingue fallos de arranque (puerto, configuración, autenticación, dependencia) a partir de la salida
/// saneada del proceso. Un fallo de ambiente nunca se presenta como problema de rendimiento.
/// </summary>
public static class StartupFailureClassifier
{
    private static readonly (FailureKind Kind, string[] Markers)[] Rules =
    [
        (FailureKind.PortInUse, ["address already in use", "Failed to bind to address", "EADDRINUSE", "Only one usage of each socket address"]),
        (FailureKind.AuthenticationError, ["AuthenticationFailedException", "CredentialUnavailableException", "AADSTS", "Unauthorized", "401 (Unauthorized)", "403 (Forbidden)", "invalid_client", "MsalServiceException"]),
        (FailureKind.DependencyUnavailable, ["SqlException", "NpgsqlException", "RedisConnectionException", "No such host is known", "Name or service not known", "Connection refused", "actively refused", "MongoConnectionException", "ServiceBusException", "HttpRequestException"]),
        (FailureKind.ConfigurationError, ["OptionsValidationException", "InvalidOperationException", "Unable to resolve service", "ConfigurationErrorsException", "FormatException", "is not valid", "Missing", "JsonException"]),
    ];

    public static (FailureKind Kind, string Detail) Classify(IReadOnlyList<string> stdoutTail, IReadOnlyList<string> stderrTail, int? exitCode)
    {
        var lines = stderrTail.Concat(stdoutTail).ToList();
        foreach (var (kind, markers) in Rules)
        {
            var hit = lines.FirstOrDefault(l => markers.Any(m => l.Contains(m, StringComparison.OrdinalIgnoreCase)));
            if (hit is not null)
            {
                return (kind, $"{Describe(kind)} Evidencia (log saneado): {Trim(hit)}");
            }
        }

        var last = lines.LastOrDefault(l => !string.IsNullOrWhiteSpace(l));
        return (FailureKind.ProcessExited, $"El proceso terminó durante el arranque (código {exitCode?.ToString() ?? "desconocido"}). Última línea: {Trim(last ?? "(sin salida)")}");
    }

    public static string Describe(FailureKind kind) => kind switch
    {
        FailureKind.PortInUse => "Puerto ocupado.",
        FailureKind.AuthenticationError => "Error de autenticación/autorización (no es un problema de rendimiento).",
        FailureKind.DependencyUnavailable => "Una dependencia no está disponible.",
        FailureKind.ConfigurationError => "Error de configuración de la API.",
        FailureKind.ReadinessTimeout => "La API no respondió al endpoint de salud a tiempo.",
        FailureKind.BuildFailed => "La compilación falló.",
        _ => "El proceso terminó.",
    };

    private static string Trim(string line) => line.Length <= 300 ? line.Trim() : line[..300].Trim() + "…";
}
