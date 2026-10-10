using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PerfAgent.Adapters.DotNetDiagnosticsMcp;
using PerfAgent.Adapters.K6Mcp;
using PerfAgent.Adapters.Process;
using PerfAgent.Core.Policy;
using PerfAgent.Core.Upstream;
using PerfAgent.Mcp.Configuration;

namespace PerfAgent.Mcp.Lab;

/// <summary>Composición del laboratorio a partir de la configuración (servidor MCP, modo CLI y pruebas de integración).</summary>
public static class LabServiceFactory
{
    public static LabService Create(IConfiguration configuration, ILoggerFactory loggerFactory)
    {
        var options = configuration.GetSection(PerfLabOptions.SectionName).Get<PerfLabOptions>() ?? new PerfLabOptions();
        var providers = configuration.GetSection(CommunityProvidersOptions.SectionName).Get<CommunityProvidersOptions>() ?? new CommunityProvidersOptions();
        var root = WorkspaceLocator.Locate(options.WorkspaceRoot);
        var workspace = new Workspace(root, SafetyPolicy.LoadOrDefault(root));

        var k6Executable = Full(root, providers.K6.Executable);
        var resolver = ExecutableResolver.Default(k6Executable);
        var runner = new ProcessCommandRunner(resolver, loggerFactory.CreateLogger<ProcessCommandRunner>());

        // mcp-k6 se ejecuta en un directorio propio: k6 (con entorno reducido por mcp-k6) lee de ahí
        // .config/k6/config.json, donde se desactiva el usage report.
        var k6WorkDir = Path.Combine(workspace.ArtifactsRoot, "providers", "mcp-k6");
        Directory.CreateDirectory(Path.Combine(k6WorkDir, ".config", "k6"));
        File.WriteAllText(Path.Combine(k6WorkDir, ".config", "k6", "config.json"), JsonSerializer.Serialize(new { noUsageReport = true }));
        var path = Path.GetDirectoryName(k6Executable) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        var k6Provider = new UpstreamMcpProvider(
            new UpstreamProviderOptions
            {
                Name = K6Executor.ProviderName,
                Command = Full(root, providers.K6Mcp.Command),
                Arguments = providers.K6Mcp.Arguments,
                WorkingDirectory = k6WorkDir,
                Environment = new Dictionary<string, string?> { ["PATH"] = path, ["LOG_LEVEL"] = "WARN" },
                RequiredTools = ["info", "validate_script", "run_script", "list_sections", "get_documentation"],
                AllowedTools = ["info", "validate_script", "run_script", "list_sections", "get_documentation"],
                ExpectedVersion = providers.K6Mcp.ExpectedVersion,
            },
            loggerFactory);

        var diagnosticsArtifacts = Path.Combine(workspace.ArtifactsRoot, "providers", "dotnet-diagnostics");
        Directory.CreateDirectory(diagnosticsArtifacts);
        var diagnosticsProvider = new UpstreamMcpProvider(
            new UpstreamProviderOptions
            {
                Name = DiagnosticsCatalog.ProviderName,
                Command = providers.DotNetDiagnosticsMcp.Command,
                Arguments = providers.DotNetDiagnosticsMcp.Arguments,
                WorkingDirectory = providers.DotNetDiagnosticsMcp.WorkingDirectory is { } wd ? Full(root, wd) : root,
                Environment = new Dictionary<string, string?>
                {
                    ["MCP_ARTIFACT_ROOT"] = diagnosticsArtifacts,
                    ["DOTNET_NOLOGO"] = "1",
                    ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                },
                RequiredTools = DiagnosticsCatalog.RequiredTools,
                AllowedTools = DiagnosticsCatalog.AllowedTools,
                ExpectedVersion = providers.DotNetDiagnosticsMcp.ExpectedVersion,
                InitializationTimeout = TimeSpan.FromSeconds(90),
            },
            loggerFactory);

        return new LabService(workspace, runner, resolver, k6Provider, diagnosticsProvider, options, providers, loggerFactory);
    }

    private static string Full(string root, string path) =>
        Path.IsPathRooted(path) || !path.Contains('/', StringComparison.Ordinal) && !path.Contains('\\', StringComparison.Ordinal) && !path.StartsWith('.')
            ? path
            : Path.GetFullPath(Path.Combine(root, path));
}
