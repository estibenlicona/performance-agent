using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PerfAgent.Core.Contracts;
using PerfAgent.Core.Execution;
using PerfAgent.Core.Security;
using PerfAgent.Core.Upstream;

namespace PerfAgent.Adapters.K6Mcp;

/// <param name="K6Executable">k6 fijado por versión (eng/tools.json), usado por mcp-k6 y por el fallback CLI.</param>
/// <param name="McpRunScriptMaxSeconds">Tope duro de run_script en mcp-k6 (5 min) menos margen.</param>
public sealed record K6Settings(string K6Executable, string ExpectedK6Version, int McpRunScriptMaxSeconds = 270);

public sealed record K6ValidationResult(bool Valid, string Detail, IReadOnlyList<string> Issues, ProviderInvocation Invocation);

public sealed record K6ExecutionResult(
    K6Summary? Summary,
    string? SummaryJson,
    int? ExitCode,
    bool TimedOut,
    string OutputExcerpt,
    IReadOnlyList<TimelineBucket> Timeline,
    ProviderInvocation Invocation,
    string? FailureDetail);

/// <summary>
/// Ejecuta escenarios ya validados: delega en <c>grafana/mcp-k6</c> (<c>validate_script</c>, <c>run_script</c>) y,
/// si el proveedor no está disponible o la prueba supera su tope de 5 minutos, usa el k6 CLI fijado (fallback
/// controlado y registrado como tal).
/// </summary>
public sealed class K6Executor(UpstreamMcpProvider mcpK6, ICommandRunner runner, K6Settings settings, ILogger<K6Executor> logger)
{
    public const string ProviderName = "grafana/mcp-k6";
    public const string CliProviderName = "k6-cli";

    public UpstreamMcpProvider Provider => mcpK6;

    public async Task<string?> GetK6VersionAsync(CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(AllowedCommands.K6Version(settings.K6Executable), TimeSpan.FromSeconds(20), cancellationToken);
        return result.Succeeded ? result.StandardOutput.Trim().Split('\n')[0].Trim() : null;
    }

    /// <summary>
    /// Validación upstream. <c>validate_script</c> ejecuta 1 VU / 1 iteración contra el destino del script: solo se llama
    /// cuando la fachada ya verificó que el destino es la instancia de laboratorio. Fallback sin tráfico: <c>k6 inspect</c>.
    /// </summary>
    public async Task<K6ValidationResult> ValidateAsync(K6Scenario scenario, string scriptPath, CancellationToken cancellationToken)
    {
        var status = await mcpK6.EnsureConnectedAsync(cancellationToken);
        if (status.Available)
        {
            try
            {
                var call = await mcpK6.CallAsync(
                    "validate_script",
                    new Dictionary<string, object?> { ["script"] = scenario.Script },
                    TimeSpan.FromSeconds(60),
                    cancellationToken,
                    new Dictionary<string, string> { ["script"] = UpstreamMcpProvider.ContentFingerprint(scenario.Script) });
                var root = call.Structured;
                if (root is not { ValueKind: JsonValueKind.Object } r)
                {
                    return new K6ValidationResult(false, "Respuesta de validate_script no reconocida.", [], call.Invocation);
                }

                var exitCode = r.TryGetProperty("exit_code", out var ec) ? ec.GetInt32() : -1;
                var stdout = r.TryGetProperty("stdout", out var so) ? so.GetString() ?? string.Empty : string.Empty;
                var issues = r.TryGetProperty("issues", out var iss) && iss.ValueKind == JsonValueKind.Array
                    ? iss.EnumerateArray().Select(i => $"{Str(i, "severity")}: {Str(i, "message")}").ToList()
                    : [];
                // exit 99 = thresholds cruzados en 1 iteración (no es un error del script); se exige el resumen completo.
                var ran = exitCode is 0 or 99 && K6SummaryParser.ExtractSummaryJson(stdout) is not null;
                var detail = ran
                    ? $"validate_script ejecutó 1 VU / 1 iteración contra la instancia de laboratorio (exit {exitCode})."
                    : $"validate_script falló (exit {exitCode}): {SecretRedactor.Redact(Str(r, "error") ?? Tail(Str(r, "stderr") ?? stdout, 400))}";
                return new K6ValidationResult(ran, detail, issues, call.Invocation);
            }
            catch (ProviderCallException ex)
            {
                logger.LogWarning(ex, "validate_script failed, falling back to k6 inspect");
            }
        }

        var watch = Stopwatch.StartNew();
        var started = DateTimeOffset.UtcNow;
        var inspect = await runner.RunAsync(AllowedCommands.K6Inspect(settings.K6Executable, scriptPath), TimeSpan.FromSeconds(60), cancellationToken);
        var invocation = new ProviderInvocation
        {
            Provider = CliProviderName,
            ProviderVersion = settings.ExpectedK6Version,
            Tool = "k6 inspect",
            StartedAt = started,
            DurationMs = Math.Round(watch.Elapsed.TotalMilliseconds, 1),
            Success = inspect.Succeeded,
            IsFallback = true,
            Arguments = new Dictionary<string, string> { ["script"] = UpstreamMcpProvider.ContentFingerprint(scenario.Script) },
            Detail = $"Fallback: {status.Error ?? "mcp-k6 no disponible"}",
        };
        return new K6ValidationResult(
            inspect.Succeeded,
            inspect.Succeeded ? "k6 inspect validó el script sin generar tráfico (fallback)." : $"k6 inspect falló: {Tail(inspect.StandardError, 400)}",
            [],
            invocation);
    }

    public async Task<K6ExecutionResult> RunAsync(K6Scenario scenario, string scriptPath, string outputDirectory, CancellationToken cancellationToken)
    {
        var totalSeconds = scenario.Load.DurationSeconds + scenario.Load.RampUpSeconds + K6ScriptGenerator.GracefulStopSeconds;
        var status = await mcpK6.EnsureConnectedAsync(cancellationToken);
        string? fallbackReason = null;
        if (!status.Available)
        {
            fallbackReason = $"mcp-k6 no disponible: {status.Error}";
        }
        else if (totalSeconds > settings.McpRunScriptMaxSeconds)
        {
            fallbackReason = $"La prueba ({totalSeconds}s) supera el tope de run_script de mcp-k6 ({settings.McpRunScriptMaxSeconds}s).";
        }
        else
        {
            try
            {
                return await RunViaMcpAsync(scenario, totalSeconds, cancellationToken);
            }
            catch (ProviderCallException ex)
            {
                // Un fallo del proveedor no se reintenta en silencio: se informa como error del generador.
                return new K6ExecutionResult(null, null, null, ex.Message.Contains("Timeout", StringComparison.Ordinal), string.Empty, [], ex.Invocation, ex.Message);
            }
        }

        logger.LogWarning("Using k6 CLI fallback: {Reason}", fallbackReason);
        return await RunViaCliAsync(scenario, scriptPath, outputDirectory, totalSeconds, fallbackReason, cancellationToken);
    }

    private async Task<K6ExecutionResult> RunViaMcpAsync(K6Scenario scenario, int totalSeconds, CancellationToken cancellationToken)
    {
        var arguments = new Dictionary<string, object?> { ["script"] = scenario.Script };
        var shown = new Dictionary<string, string> { ["script"] = UpstreamMcpProvider.ContentFingerprint(scenario.Script) };
        // Parámetros explícitos cuando son exactamente representables; ramping-vus usa las opciones del script.
        if (scenario.Kind == LoadTestKind.Smoke)
        {
            arguments["vus"] = scenario.Load.Vus;
            arguments["iterations"] = scenario.Load.Iterations;
            shown["vus"] = scenario.Load.Vus.ToString(CultureInfo.InvariantCulture);
            shown["iterations"] = (scenario.Load.Iterations ?? 1).ToString(CultureInfo.InvariantCulture);
        }
        else if (scenario.Load.Executor == "constant-vus")
        {
            arguments["vus"] = scenario.Load.Vus;
            arguments["duration"] = $"{scenario.Load.DurationSeconds}s";
            shown["vus"] = scenario.Load.Vus.ToString(CultureInfo.InvariantCulture);
            shown["duration"] = $"{scenario.Load.DurationSeconds}s";
        }
        else
        {
            shown["options"] = "definidas en el script (ramping-vus)";
        }

        var call = await mcpK6.CallAsync("run_script", arguments, TimeSpan.FromSeconds(totalSeconds + 90), cancellationToken, shown);
        if (call.Structured is not { ValueKind: JsonValueKind.Object } r)
        {
            return new K6ExecutionResult(null, null, null, false, Tail(call.Text, 1000), [], call.Invocation, "Respuesta de run_script no reconocida.");
        }

        var stdout = Str(r, "stdout") ?? string.Empty;
        var stderr = Str(r, "stderr") ?? string.Empty;
        int? exitCode = r.TryGetProperty("exit_code", out var ec) && ec.ValueKind == JsonValueKind.Number ? ec.GetInt32() : null;
        var json = K6SummaryParser.ExtractSummaryJson(stdout);
        var excerpt = SecretRedactor.Redact(Tail(K6SummaryParser.StripSummary(stdout) + "\n" + stderr, 3000));
        return new K6ExecutionResult(Parse(json, scenario), json, exitCode, false, excerpt, [], call.Invocation,
            json is null ? $"k6 no produjo resumen (exit {exitCode?.ToString(CultureInfo.InvariantCulture) ?? "?"}): {SecretRedactor.Redact(Tail(Str(r, "error") ?? stderr, 400))}" : null);
    }

    private async Task<K6ExecutionResult> RunViaCliAsync(K6Scenario scenario, string scriptPath, string outputDirectory, int totalSeconds, string? reason, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        var jsonOut = Path.Combine(outputDirectory, $"k6-{scenario.Kind.ToString().ToLowerInvariant()}-points.json");
        var started = DateTimeOffset.UtcNow;
        var watch = Stopwatch.StartNew();
        var result = await runner.RunAsync(AllowedCommands.K6Run(settings.K6Executable, scriptPath, jsonOut), TimeSpan.FromSeconds(totalSeconds + 90), cancellationToken);
        var invocation = new ProviderInvocation
        {
            Provider = CliProviderName,
            ProviderVersion = settings.ExpectedK6Version,
            Tool = "k6 run",
            StartedAt = started,
            DurationMs = Math.Round(watch.Elapsed.TotalMilliseconds, 1),
            Success = result.ExitCode is 0 or 99,
            IsFallback = true,
            Arguments = new Dictionary<string, string> { ["script"] = UpstreamMcpProvider.ContentFingerprint(scenario.Script), ["out"] = "json" },
            Detail = $"Fallback controlado: {reason}",
        };
        var json = K6SummaryParser.ExtractSummaryJson(result.StandardOutput);
        var timeline = K6JsonOutputParser.ParseTimeline(jsonOut);
        return new K6ExecutionResult(Parse(json, scenario), json, result.ExitCode, result.TimedOut,
            SecretRedactor.Redact(Tail(K6SummaryParser.StripSummary(result.StandardOutput) + "\n" + result.StandardError, 3000)),
            timeline, invocation,
            json is null ? $"k6 no produjo resumen (exit {result.ExitCode}): {Tail(result.StandardError, 400)}" : null);
    }

    private static K6Summary? Parse(string? json, K6Scenario scenario)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            return K6SummaryParser.Parse(json, scenario.Endpoints);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Tail(string text, int max) => text.Length <= max ? text : "…" + text[^max..];
}
