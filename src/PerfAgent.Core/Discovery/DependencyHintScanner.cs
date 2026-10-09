using System.Text.Json;
using System.Text.RegularExpressions;
using PerfAgent.Core.Contracts;

namespace PerfAgent.Core.Discovery;

/// <summary>
/// Detecta dependencias conocidas por paquetes y por NOMBRES de claves de configuración.
/// Nunca devuelve valores: como mucho, el host saneado de una URL o cadena de conexión.
/// </summary>
public static partial class DependencyHintScanner
{
    private static readonly (string Prefix, string Kind)[] PackageKinds =
    [
        ("Microsoft.EntityFrameworkCore.SqlServer", "sql"),
        ("Microsoft.Data.SqlClient", "sql"),
        ("System.Data.SqlClient", "sql"),
        ("Npgsql", "sql"),
        ("MySqlConnector", "sql"),
        ("Pomelo.EntityFrameworkCore.MySql", "sql"),
        ("Microsoft.EntityFrameworkCore.Sqlite", "sql"),
        ("StackExchange.Redis", "redis"),
        ("Microsoft.Extensions.Caching.StackExchangeRedis", "redis"),
        ("MongoDB.Driver", "mongodb"),
        ("Microsoft.Azure.Cosmos", "cosmos"),
        ("Azure.Messaging.ServiceBus", "messaging"),
        ("Azure.Messaging.EventHubs", "messaging"),
        ("RabbitMQ.Client", "messaging"),
        ("Confluent.Kafka", "messaging"),
        ("MassTransit", "messaging"),
        ("Azure.Storage.", "storage"),
        ("Azure.Security.KeyVault.", "key-vault"),
        ("Azure.Extensions.AspNetCore.Configuration.Secrets", "key-vault"),
        ("Azure.Identity", "identity"),
        ("Microsoft.Identity.Web", "identity"),
        ("Microsoft.AspNetCore.Authentication.JwtBearer", "identity"),
        ("Refit", "http"),
        ("Grpc.Net.Client", "grpc"),
        ("Dapr.", "other"),
    ];

    private static readonly string[] UrlKeySuffixes = ["url", "uri", "baseurl", "baseaddress", "endpoint", "host", "authority", "instance"];

    public static IReadOnlyList<DependencyHint> Scan(string projectDir, IReadOnlyCollection<string> packages, bool usesUserSecrets)
    {
        var hints = new List<DependencyHint>();
        foreach (var package in packages)
        {
            var kind = PackageKinds.FirstOrDefault(k => package.StartsWith(k.Prefix, StringComparison.OrdinalIgnoreCase)).Kind;
            if (kind is not null)
            {
                hints.Add(new DependencyHint(package, kind, "package", null, null));
            }
        }

        foreach (var file in Directory.EnumerateFiles(projectDir, "appsettings*.json", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            ScanSettingsFile(file, hints);
        }

        if (usesUserSecrets)
        {
            hints.Add(new DependencyHint("user-secrets", "configuration", "user-secrets", null,
                "Puede haber configuración de dependencias en user-secrets (no se lee)."));
        }

        return hints
            .GroupBy(h => (h.Name, h.Kind, h.Source, h.Host))
            .Select(g => g.First())
            .ToList();
    }

    private static void ScanSettingsFile(string file, List<DependencyHint> hints)
    {
        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException)
        {
            return;
        }

        using (json)
        {
            var source = Path.GetFileName(file);
            if (json.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (json.RootElement.TryGetProperty("ConnectionStrings", out var connectionStrings) && connectionStrings.ValueKind == JsonValueKind.Object)
            {
                foreach (var cs in connectionStrings.EnumerateObject())
                {
                    var value = cs.Value.ValueKind == JsonValueKind.String ? cs.Value.GetString() : null;
                    var (kind, host) = ClassifyConnectionString(value);
                    hints.Add(new DependencyHint($"ConnectionStrings:{cs.Name}", kind, $"connection-string ({source})", host,
                        string.IsNullOrWhiteSpace(value) ? "Valor vacío en el archivo (probablemente se inyecta por entorno o secretos)." : null));
                }
            }

            Walk(json.RootElement, string.Empty, source, hints, depth: 0);
        }
    }

    private static void Walk(JsonElement element, string prefix, string source, List<DependencyHint> hints, int depth)
    {
        if (depth > 6 || element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in element.EnumerateObject())
        {
            var key = prefix.Length == 0 ? property.Name : $"{prefix}:{property.Name}";
            if (key.StartsWith("ConnectionStrings", StringComparison.OrdinalIgnoreCase) || key.StartsWith("Logging", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                if (property.Name is "AzureAd" or "AzureAdB2C" or "Authentication" or "ManagedIdentity")
                {
                    hints.Add(new DependencyHint(key, "identity", $"configuration-key ({source})", null, "Configuración de identidad detectada (no se leen valores)."));
                }

                Walk(property.Value, key, source, hints, depth + 1);
                continue;
            }

            var lower = property.Name.ToLowerInvariant();
            if (property.Value.ValueKind != JsonValueKind.String || !UrlKeySuffixes.Any(s => lower.EndsWith(s, StringComparison.Ordinal)))
            {
                continue;
            }

            var value = property.Value.GetString();
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "sb" or "amqp" or "amqps" or "redis" or "rediss")
            {
                hints.Add(new DependencyHint(key, ClassifyHost(uri.Host, uri.Scheme), $"configuration-key ({source})", SanitizeHost(uri), null));
            }
            else if (string.IsNullOrWhiteSpace(value))
            {
                hints.Add(new DependencyHint(key, "http", $"configuration-key ({source})", null, "Valor vacío en el archivo (probablemente se inyecta por entorno o secretos)."));
            }
        }
    }

    internal static (string Kind, string? Host) ClassifyConnectionString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ("unknown", null);
        }

        if (value.StartsWith("mongodb", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(value, UriKind.Absolute, out var mongo))
        {
            return ("mongodb", SanitizeHost(mongo));
        }

        var server = ConnectionStringValue(value, "Server") ?? ConnectionStringValue(value, "Data Source") ?? ConnectionStringValue(value, "Host");
        if (ConnectionStringValue(value, "Endpoint") is { } endpoint && endpoint.StartsWith("sb://", StringComparison.OrdinalIgnoreCase))
        {
            return ("messaging", Uri.TryCreate(endpoint, UriKind.Absolute, out var sb) ? sb.Host : null);
        }

        if (ConnectionStringValue(value, "AccountName") is { } account)
        {
            return ("storage", $"{account}.(storage)");
        }

        if (value.Contains("AccountEndpoint=", StringComparison.OrdinalIgnoreCase))
        {
            return ("cosmos", null);
        }

        if (server is not null)
        {
            var host = server.Replace("tcp:", string.Empty, StringComparison.OrdinalIgnoreCase).Split(',', ':')[0].Trim();
            return (value.Contains("Host=", StringComparison.OrdinalIgnoreCase) ? "sql (postgres)" : "sql", host.Length == 0 ? null : host);
        }

        if (RedisRegex().IsMatch(value))
        {
            return ("redis", value.Split(',', ':')[0].Trim());
        }

        return ("unknown", null);
    }

    private static string? ConnectionStringValue(string connectionString, string key)
    {
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var index = part.IndexOf('=');
            if (index > 0 && string.Equals(part[..index].Trim(), key, StringComparison.OrdinalIgnoreCase))
            {
                return part[(index + 1)..].Trim();
            }
        }

        return null;
    }

    private static string ClassifyHost(string host, string scheme) => host switch
    {
        _ when host.EndsWith(".vault.azure.net", StringComparison.OrdinalIgnoreCase) => "key-vault",
        _ when host.EndsWith(".servicebus.windows.net", StringComparison.OrdinalIgnoreCase) || scheme is "sb" or "amqp" or "amqps" => "messaging",
        _ when host.Contains(".blob.core.", StringComparison.OrdinalIgnoreCase) || host.Contains(".queue.core.", StringComparison.OrdinalIgnoreCase) => "storage",
        _ when host.EndsWith(".documents.azure.com", StringComparison.OrdinalIgnoreCase) => "cosmos",
        _ when host.Contains("redis", StringComparison.OrdinalIgnoreCase) || scheme is "redis" or "rediss" => "redis",
        _ when host.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase) => "identity",
        _ => "http",
    };

    private static string SanitizeHost(Uri uri) => uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";

    [GeneratedRegex(@"^[A-Za-z0-9.\-]+:\d{2,5}(,|$)")]
    private static partial Regex RedisRegex();
}
