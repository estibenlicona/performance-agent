using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PerfAgent.Adapters.Process;
using PerfAgent.Mcp.Lab;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace PerfAgent.IntegrationTests;

/// <summary>Repositorio real: herramientas fijadas en .tools/, API de laboratorio y manifiestos de scenarios/.</summary>
public static class Repo
{
    public static string Root { get; } = FindRoot();

    public static string Configuration { get; } = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";

    public static string K6 => Path.Combine(Root, ".tools", "k6", "2.3.0", "k6-v2.3.0-windows-amd64", "k6.exe");

    public static string McpK6 => Path.Combine(Root, ".tools", "mcp-k6", "0.7.0", "mcp-k6.exe");

    /// <summary>Motivo para omitir las pruebas que necesitan proveedores reales, o null si están instalados.</summary>
    public static string? MissingPrerequisite()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "Las herramientas fijadas de eng/bootstrap.ps1 son windows-amd64.";
        }

        if (!File.Exists(K6) || !File.Exists(McpK6))
        {
            return "Faltan k6/mcp-k6: ejecuta eng/bootstrap.ps1.";
        }

        var nugetRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        return Directory.Exists(Path.Combine(nugetRoot, "dotnet-diagnostics-mcp", "0.28.0"))
            ? null
            : "Falta dotnet-diagnostics-mcp 0.28.0: ejecuta dotnet tool restore.";
    }

    public static void RequirePrerequisites()
    {
        if (MissingPrerequisite() is { } reason)
        {
            Assert.Skip(reason);
        }
    }

    public static IConfiguration BuildConfiguration(string workspaceRoot) => new ConfigurationBuilder()
        .AddJsonFile(Path.Combine(Root, "dotnet", "src", "PerfAgent.Mcp", "appsettings.json"))
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PerfLab:WorkspaceRoot"] = workspaceRoot,
            ["PerfLab:MockServerProject"] = Path.Combine(Root, "dotnet", "src", "PerfAgent.MockServer", "PerfAgent.MockServer.csproj"),
            // El host de pruebas no se asigna a sí mismo al Job Object: los hijos se asignan individualmente.
            ["PerfLab:AssignServerToJobObject"] = "false",
            ["CommunityProviders:K6Mcp:Command"] = McpK6,
            ["CommunityProviders:K6:Executable"] = K6,
            ["CommunityProviders:DotNetDiagnosticsMcp:WorkingDirectory"] = Root,
        })
        .Build();

    public static LabService CreateLab(string? workspaceRoot = null) =>
        LabServiceFactory.Create(BuildConfiguration(workspaceRoot ?? Root), NullLoggerFactory.Instance);

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "perf-lab.policy.json")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("No se encontró la raíz del repositorio (perf-lab.policy.json).");
    }
}

/// <summary>Workspace temporal con política propia que apunta a la API de laboratorio del repositorio.</summary>
public sealed class TempLabWorkspace : IDisposable
{
    public TempLabWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "perflab-it", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Path.Combine(Root, "scenarios"));
        var lab = Path.Combine(Repo.Root, "dotnet", "lab").Replace('\\', '/');
        File.WriteAllText(Path.Combine(Root, "perf-lab.policy.json"), $$"""
            {
              "schemaVersion": 1,
              "allowedProjectRoots": [ "{{lab}}" ],
              "allowedManifestRoots": [ "scenarios" ],
              "allowedContractRoots": [ "{{lab}}/contracts" ],
              "allowedRemoteDependencyHosts": [ "unreachable.perflab.invalid" ],
              "load": { "maxVus": 50, "maxDurationSeconds": 240, "maxSmokeVus": 2, "maxSmokeIterations": 50, "maxEndpoints": 20 }
            }
            """);
    }

    public string Root { get; }

    public string LabProject => Path.Combine(Repo.Root, "dotnet", "lab", "PerfLab.Api", "PerfLab.Api.csproj").Replace('\\', '/');

    public string WriteManifest(string name, string yaml)
    {
        File.WriteAllText(Path.Combine(Root, "scenarios", name), yaml.Replace("{{project}}", LabProject, StringComparison.Ordinal));
        return $"scenarios/{name}";
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort.
        }
    }
}

public static class ProcessAssertions
{
    /// <summary>Ningún proceso registrado por el laboratorio (para esa ejecución) sigue vivo.</summary>
    public static void NoOrphans(LabService lab, string? runId)
    {
        var alive = lab.Registry.FindAlive(runId);
        Assert.True(alive.Count == 0, "Procesos huérfanos: " + string.Join(", ", alive.Select(p => $"{p.Role} PID {p.ProcessId}")));
    }

    public static bool IsAlive(int? pid)
    {
        if (pid is null)
        {
            return false;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid.Value);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

internal static class RegistryExtensions
{
    public static IReadOnlyList<RegisteredProcess> AliveFor(this ProcessRegistry registry, string runId) => registry.FindAlive(runId);
}
