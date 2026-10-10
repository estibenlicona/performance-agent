using System.Text.Json;
using Microsoft.Extensions.Logging;
using PerfAgent.Core.Contracts;
using PerfAgent.Core.Execution;

namespace PerfAgent.Adapters.Process;

public sealed record BuildOutcome(
    bool Succeeded,
    string? TargetPath,
    string? Framework,
    TimeSpan Duration,
    IReadOnlyList<string> Errors,
    string LogPath,
    FailureKind Failure,
    string Detail);

public sealed record StartOutcome(
    bool Ready,
    ManagedProcess? Process,
    int Port,
    string BaseUrl,
    FailureKind Failure,
    string Detail,
    int Attempts,
    TimeSpan Elapsed);

/// <param name="Settings">Variables de entorno no secretas ya validadas por la política.</param>
public sealed record LaunchRequest(
    string RunId,
    string Role,
    string AssemblyPath,
    string Purpose,
    IReadOnlyList<string> ExtraArguments,
    IReadOnlyDictionary<string, string> Settings,
    string HealthPath,
    TimeSpan ReadinessTimeout,
    string LogDirectory,
    bool SetAspNetCoreUrls);

/// <summary>Compila en Release y arranca procesos del laboratorio con argumentos generados a partir de configuración validada.</summary>
public sealed class ApiLauncher(
    ICommandRunner runner,
    ExecutableResolver resolver,
    ProcessRegistry registry,
    WindowsJobObjectHolder job,
    ILogger<ApiLauncher> logger)
{
    private static readonly HttpClient Http = ReadinessProbe.CreateLoopbackClient();

    public async Task<BuildOutcome> BuildAsync(string projectFullPath, string? framework, string logDirectory, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(logDirectory);
        var logPath = Path.Combine(logDirectory, $"build-{Path.GetFileNameWithoutExtension(projectFullPath)}.log");
        var build = await runner.RunAsync(AllowedCommands.DotnetBuildRelease(projectFullPath, framework), timeout, cancellationToken);
        await File.WriteAllTextAsync(logPath, build.StandardOutput + Environment.NewLine + build.StandardError, CancellationToken.None);
        if (!build.Succeeded)
        {
            var errors = (build.StandardOutput + "\n" + build.StandardError)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => l.Contains(": error ", StringComparison.OrdinalIgnoreCase) || l.StartsWith("error ", StringComparison.OrdinalIgnoreCase))
                .Distinct()
                .Take(10)
                .ToList();
            var failure = build.Cancelled ? FailureKind.Cancelled : FailureKind.BuildFailed;
            var detail = build.TimedOut
                ? $"La compilación superó el tiempo máximo ({timeout.TotalSeconds:0} s)."
                : errors.Count > 0 ? $"La compilación falló con {errors.Count} error(es)." : $"La compilación falló (código {build.ExitCode}).";
            return new BuildOutcome(false, null, framework, build.Duration, errors, logPath, failure, detail);
        }

        var props = await runner.RunAsync(AllowedCommands.DotnetGetProperties(projectFullPath, framework, "TargetPath", "TargetFramework"), TimeSpan.FromSeconds(60), cancellationToken);
        string? targetPath = null;
        string? tfm = framework;
        if (props.Succeeded)
        {
            try
            {
                using var json = JsonDocument.Parse(props.StandardOutput);
                var p = json.RootElement.GetProperty("Properties");
                targetPath = p.GetProperty("TargetPath").GetString();
                tfm = p.GetProperty("TargetFramework").GetString() is { Length: > 0 } t ? t : framework;
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                logger.LogWarning(ex, "Could not parse MSBuild properties output");
            }
        }

        if (string.IsNullOrWhiteSpace(targetPath) || !File.Exists(targetPath))
        {
            return new BuildOutcome(false, null, tfm, build.Duration, [], logPath, FailureKind.ConfigurationError,
                "No se pudo determinar el ensamblado compilado (TargetPath). Si el proyecto multi-targetea, indica api.framework.");
        }

        return new BuildOutcome(true, targetPath, tfm, build.Duration, [], logPath, FailureKind.None, $"Compilado en Release ({build.Duration.TotalSeconds:0.0} s).");
    }

    /// <summary>Inicia el proceso en un puerto libre de loopback y espera readiness. Reintenta si el puerto estaba ocupado.</summary>
    public async Task<StartOutcome> StartAsync(LaunchRequest request, CancellationToken cancellationToken)
    {
        StartOutcome? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var port = PortAllocator.GetFreeLoopbackPort();
            var baseUrl = $"http://127.0.0.1:{port}";
            var environment = new Dictionary<string, string>(request.Settings, StringComparer.OrdinalIgnoreCase);
            if (request.SetAspNetCoreUrls)
            {
                environment["ASPNETCORE_URLS"] = baseUrl;
            }

            var arguments = request.SetAspNetCoreUrls ? request.ExtraArguments : [.. request.ExtraArguments, "--urls", baseUrl];
            var spec = AllowedCommands.DotnetRunAssembly(request.AssemblyPath, request.Purpose, [.. arguments]) with { Environment = environment };
            var process = ManagedProcess.Start(spec, resolver, request.Role, request.RunId, request.LogDirectory, registry, job);
            logger.LogInformation("Started {Role} PID {Pid} on {Url}", request.Role, process.ProcessId, baseUrl);
            var readiness = await ReadinessProbe.WaitAsync(process, new Uri(baseUrl + request.HealthPath), request.ReadinessTimeout, Http, cancellationToken);
            if (readiness.Ready)
            {
                return new StartOutcome(true, process, port, baseUrl, FailureKind.None, readiness.Detail, readiness.Attempts, readiness.Elapsed);
            }

            await process.DisposeAsync();
            last = new StartOutcome(false, null, port, baseUrl, readiness.Failure, readiness.Detail, readiness.Attempts, readiness.Elapsed);
            if (readiness.Failure != FailureKind.PortInUse)
            {
                break;
            }
        }

        return last!;
    }
}
