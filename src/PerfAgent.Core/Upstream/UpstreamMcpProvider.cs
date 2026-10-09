using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PerfAgent.Core.Contracts;
using PerfAgent.Core.Security;

namespace PerfAgent.Core.Upstream;

/// <summary>Configuración de un proveedor MCP comunitario lanzado como proceso secundario de la fachada.</summary>
public sealed record UpstreamProviderOptions
{
    /// <summary>Identificador del proveedor (p. ej. <c>grafana/mcp-k6</c>).</summary>
    public required string Name { get; init; }

    public required string Command { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];

    public string? WorkingDirectory { get; init; }

    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();

    /// <summary>Herramientas que deben existir en tools/list; si falta alguna, el proveedor no está disponible.</summary>
    public required IReadOnlyList<string> RequiredTools { get; init; }

    /// <summary>
    /// Allowlist de herramientas que la fachada puede invocar. Cualquier otra herramienta del proveedor
    /// (dumps, heap, lectura de bytes, ejecución genérica) queda inaccesible aunque exista.
    /// </summary>
    public required IReadOnlyList<string> AllowedTools { get; init; }

    /// <summary>Versión fijada esperada (serverInfo.version).</summary>
    public string? ExpectedVersion { get; init; }

    public TimeSpan InitializationTimeout { get; init; } = TimeSpan.FromSeconds(60);
}

public sealed record ProviderStatus(
    string Provider,
    bool Available,
    string? ServerName,
    string? ServerVersion,
    string? ExpectedVersion,
    string? ProtocolVersion,
    IReadOnlyList<string> Tools,
    IReadOnlyList<string> MissingTools,
    string? Error)
{
    public bool VersionMatches => ExpectedVersion is null || string.Equals(ExpectedVersion, ServerVersion, StringComparison.Ordinal);
}

/// <param name="Structured">structuredContent del proveedor, o el texto parseado como JSON si el proveedor solo devuelve texto.</param>
public sealed record UpstreamCallResult(bool IsError, JsonElement? Structured, string Text, ProviderInvocation Invocation);

/// <summary>
/// Cliente MCP hacia un proveedor comunitario. Valida el catálogo real (tools/list) al conectar, aplica una
/// allowlist de herramientas, registra cada delegación (proveedor, versión, herramienta, argumentos saneados)
/// y nunca expone sus herramientas genéricas al agente: solo la fachada perf-lab las usa.
/// </summary>
public sealed class UpstreamMcpProvider(UpstreamProviderOptions options, ILoggerFactory loggerFactory) : IAsyncDisposable
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<UpstreamMcpProvider>();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private McpClient? _client;
    private ProviderStatus? _status;

    public UpstreamProviderOptions Options => options;

    public ProviderStatus? LastStatus => _status;

    /// <summary>Conecta (si no lo estaba) y devuelve el estado del proveedor tras validar su catálogo.</summary>
    public async Task<ProviderStatus> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_client is not null && _status is { Available: true })
            {
                return _status;
            }

            await DisposeClientAsync();
            try
            {
                var transport = new StdioClientTransport(
                    new StdioClientTransportOptions
                    {
                        Name = options.Name,
                        Command = options.Command,
                        Arguments = options.Arguments.ToList(),
                        WorkingDirectory = options.WorkingDirectory,
                        EnvironmentVariables = options.Environment.ToDictionary(kv => kv.Key, kv => kv.Value),
                        ShutdownTimeout = TimeSpan.FromSeconds(2),
                        StandardErrorLines = line => _logger.LogDebug("[{Provider}] {Line}", options.Name, SecretRedactor.Redact(line)),
                    },
                    loggerFactory);
                _client = await McpClient.CreateAsync(
                    transport,
                    new McpClientOptions
                    {
                        ClientInfo = new Implementation { Name = "perf-lab", Version = typeof(UpstreamMcpProvider).Assembly.GetName().Version?.ToString(3) ?? "0.1.0" },
                        InitializationTimeout = options.InitializationTimeout,
                    },
                    loggerFactory,
                    cancellationToken);

                var tools = (await _client.ListToolsAsync(cancellationToken: cancellationToken)).Select(t => t.Name).Order(StringComparer.Ordinal).ToList();
                var missing = options.RequiredTools.Where(t => !tools.Contains(t, StringComparer.Ordinal)).ToList();
                string? serverName = null;
                string? serverVersion = null;
                try
                {
                    serverName = _client.ServerInfo.Name;
                    serverVersion = _client.ServerInfo.Version;
                }
                catch (InvalidOperationException)
                {
                    // El servidor no declaró su identidad.
                }

                var error = missing.Count > 0 ? $"Faltan herramientas requeridas en el catálogo real: {string.Join(", ", missing)}." : null;
                if (error is null && options.ExpectedVersion is not null && !string.Equals(options.ExpectedVersion, serverVersion, StringComparison.Ordinal))
                {
                    error = $"Versión {serverVersion ?? "desconocida"} distinta de la fijada ({options.ExpectedVersion}).";
                }

                _status = new ProviderStatus(options.Name, error is null, serverName, serverVersion, options.ExpectedVersion, _client.NegotiatedProtocolVersion, tools, missing, error);
                if (error is not null)
                {
                    await DisposeClientAsync();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Provider {Provider} unavailable", options.Name);
                await DisposeClientAsync();
                _status = new ProviderStatus(options.Name, false, null, null, options.ExpectedVersion, null, [], options.RequiredTools, SecretRedactor.Redact(ex.Message));
            }

            return _status;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<UpstreamCallResult> CallAsync(
        string tool,
        IReadOnlyDictionary<string, object?> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? displayArguments = null)
    {
        if (!options.AllowedTools.Contains(tool, StringComparer.Ordinal))
        {
            throw new UnauthorizedAccessException($"La herramienta '{tool}' de {options.Name} no está en la allowlist de la fachada.");
        }

        var status = await EnsureConnectedAsync(cancellationToken);
        if (!status.Available || _client is null)
        {
            throw new ProviderUnavailableException(options.Name, status.Error ?? "Proveedor no disponible.");
        }

        var started = DateTimeOffset.UtcNow;
        var watch = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var shown = displayArguments ?? DescribeArguments(arguments);
        try
        {
            var result = await _client.CallToolAsync(tool, arguments, cancellationToken: cts.Token);
            var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(t => t.Text));
            var structured = result.StructuredContent ?? TryParseJson(text);
            var isError = result.IsError == true;
            return new UpstreamCallResult(isError, structured, text, Invocation(tool, started, watch.Elapsed, !isError, shown, isError ? SecretRedactor.Redact(Truncate(text, 500)) : null));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // El proveedor puede dejar trabajo en curso (p. ej. k6 lanzado por mcp-k6): se reinicia su árbol de procesos.
            await ResetAsync();
            throw new ProviderCallException(options.Name, tool, $"Timeout tras {timeout.TotalSeconds:0} s.", Invocation(tool, started, watch.Elapsed, false, shown, "timeout"));
        }
        catch (OperationCanceledException)
        {
            // Cancelación del llamante: no se confía en que el proveedor detenga sus procesos hijos al recibir
            // notifications/cancelled; al cerrar el cliente se termina el árbol completo (proveedor y, p. ej., k6).
            await ResetAsync();
            throw;
        }
        catch (McpProtocolException ex)
        {
            throw new ProviderCallException(options.Name, tool, SecretRedactor.Redact(ex.Message), Invocation(tool, started, watch.Elapsed, false, shown, SecretRedactor.Redact(ex.Message)));
        }
        catch (ClientTransportClosedException ex)
        {
            await ResetAsync();
            throw new ProviderCallException(options.Name, tool, "El proceso del proveedor terminó: " + SecretRedactor.Redact(ex.Message), Invocation(tool, started, watch.Elapsed, false, shown, "transport-closed"));
        }
    }

    public async Task ResetAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await DisposeClientAsync();
            _status = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeClientAsync();
        _gate.Dispose();
    }

    /// <summary>Hash corto para registrar contenido grande (p. ej. scripts) sin copiarlo en la bitácora.</summary>
    public static string ContentFingerprint(string content) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..16]} ({content.Length} chars)";

    private ProviderInvocation Invocation(string tool, DateTimeOffset started, TimeSpan elapsed, bool success, IReadOnlyDictionary<string, string> arguments, string? detail) =>
        new()
        {
            Provider = options.Name,
            ProviderVersion = _status?.ServerVersion,
            Tool = tool,
            StartedAt = started,
            DurationMs = Math.Round(elapsed.TotalMilliseconds, 1),
            Success = success,
            Arguments = arguments,
            Detail = detail,
        };

    private static IReadOnlyDictionary<string, string> DescribeArguments(IReadOnlyDictionary<string, object?> arguments) =>
        arguments.ToDictionary(
            kv => kv.Key,
            kv => kv.Value switch
            {
                null => "null",
                string s when s.Length > 200 => ContentFingerprint(s),
                string s => SecretRedactor.Redact(s),
                JsonElement e => Truncate(SecretRedactor.Redact(e.GetRawText()), 200),
                _ => Truncate(SecretRedactor.Redact(JsonSerializer.Serialize(kv.Value)), 200),
            });

    private static JsonElement? TryParseJson(string text)
    {
        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private async Task DisposeClientAsync()
    {
        if (_client is not null)
        {
            try
            {
                await _client.DisposeAsync();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                _logger.LogDebug(ex, "Error disposing provider client {Provider}", options.Name);
            }

            _client = null;
        }
    }
}

public sealed class ProviderUnavailableException(string provider, string message)
    : Exception($"{provider}: {message}")
{
    public string Provider { get; } = provider;
}

public sealed class ProviderCallException(string provider, string tool, string message, ProviderInvocation invocation)
    : Exception($"{provider}/{tool}: {message}")
{
    public ProviderInvocation Invocation { get; } = invocation;
}
