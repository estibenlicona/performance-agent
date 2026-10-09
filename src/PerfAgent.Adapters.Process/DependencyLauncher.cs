using System.Text.Json;
using PerfAgent.Core.Contracts;
using PerfAgent.Core.Execution;

namespace PerfAgent.Adapters.Process;

public sealed record MockRouteStats(string Route, long Requests, long Failures, double AvgLatencyMs);

/// <summary>Arranca y detiene dependencias del laboratorio: mocks declarados y contenedores (docker compose).</summary>
public sealed class DependencyLauncher(ApiLauncher launcher, ICommandRunner runner)
{
    private static readonly HttpClient Http = ReadinessProbe.CreateLoopbackClient();

    public Task<StartOutcome> StartMockAsync(string runId, string name, string mockAssemblyPath, string contractPath, string logDirectory, CancellationToken cancellationToken) =>
        launcher.StartAsync(
            new LaunchRequest(
                runId,
                $"mock:{name}",
                mockAssemblyPath,
                $"Iniciar mock declarado '{name}'",
                ["--contract", contractPath],
                new Dictionary<string, string>(),
                "/__mock/health",
                TimeSpan.FromSeconds(30),
                logDirectory,
                SetAspNetCoreUrls: false),
            cancellationToken);

    public async Task<(bool Ok, string Detail)> StartContainerAsync(string composePath, string projectName, string service, int port, CancellationToken cancellationToken)
    {
        var up = await runner.RunAsync(AllowedCommands.DockerComposeUp(composePath, projectName, service), TimeSpan.FromMinutes(3), cancellationToken);
        if (!up.Succeeded)
        {
            return (false, $"docker compose up falló (código {up.ExitCode}): {Last(up.StandardError)}");
        }

        for (var i = 0; i < 30; i++)
        {
            if (await PortAllocator.IsTcpOpenAsync("127.0.0.1", port, TimeSpan.FromSeconds(1), cancellationToken))
            {
                return (true, $"Contenedor '{service}' escuchando en 127.0.0.1:{port}.");
            }

            await Task.Delay(500, cancellationToken);
        }

        return (false, $"El contenedor '{service}' no abrió el puerto {port}.");
    }

    public async Task<(bool Ok, string Detail)> StopContainersAsync(string composePath, string projectName, CancellationToken cancellationToken)
    {
        var down = await runner.RunAsync(AllowedCommands.DockerComposeDown(composePath, projectName), TimeSpan.FromMinutes(2), cancellationToken);
        return down.Succeeded ? (true, $"docker compose down ({projectName}).") : (false, $"docker compose down falló: {Last(down.StandardError)}");
    }

    /// <summary>Contadores del propio mock (peticiones, fallos y latencia servida por ruta).</summary>
    public static async Task<IReadOnlyList<MockRouteStats>> GetMockStatsAsync(string baseUrl, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(baseUrl.TrimEnd('/') + "/__mock/stats", cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return json.RootElement.GetProperty("routes").EnumerateArray()
            .Select(r => new MockRouteStats(
                r.GetProperty("route").GetString() ?? "?",
                r.GetProperty("requests").GetInt64(),
                r.GetProperty("failures").GetInt64(),
                r.GetProperty("avgLatencyMs").GetDouble()))
            .ToList();
    }

    /// <summary>Diferencia entre dos lecturas de estadísticas del mock (ventana de una prueba de carga).</summary>
    public static IReadOnlyList<DependencyCallStats> Delta(string dependency, IReadOnlyList<MockRouteStats> before, IReadOnlyList<MockRouteStats> after) =>
        after.Select(a =>
            {
                var b = before.FirstOrDefault(x => x.Route == a.Route) ?? new MockRouteStats(a.Route, 0, 0, 0);
                var requests = a.Requests - b.Requests;
                var totalAfter = a.AvgLatencyMs * a.Requests;
                var totalBefore = b.AvgLatencyMs * b.Requests;
                return new DependencyCallStats(
                    dependency,
                    a.Route,
                    requests,
                    a.Failures - b.Failures,
                    requests > 0 ? Math.Round((totalAfter - totalBefore) / requests, 2) : null,
                    "mock-stats");
            })
            .Where(d => d.Requests > 0)
            .ToList();

    private static string Last(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "(sin salida)" : lines[^1];
    }
}
