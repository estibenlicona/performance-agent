using Microsoft.Extensions.Logging.Abstractions;
using PerfAgent.Adapters.DotNetDiagnosticsMcp;
using PerfAgent.Adapters.K6Mcp;
using PerfAgent.Core.Upstream;

namespace PerfAgent.IntegrationTests;

/// <summary>
/// Pruebas de contrato contra los proveedores comunitarios FIJADOS: catálogo real (tools/list), versión, y la
/// allowlist de la fachada (las herramientas genéricas o de riesgo no se pueden invocar).
/// </summary>
public sealed class ProviderContractTests
{
    private static readonly string[] RequiredK6Tools = ["info", "validate_script", "run_script", "list_sections", "get_documentation"];

    [Fact]
    public async Task PinnedProvidersExposeTheRequiredCatalogAndVersions()
    {
        Repo.RequirePrerequisites();
        await using var lab = Repo.CreateLab();

        var status = await lab.GetStatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal("ok", status.Status);
        var k6 = Assert.Single(status.Data!.Providers, p => p.Provider == K6Executor.ProviderName);
        Assert.True(k6.Available, k6.Error);
        Assert.Equal("0.7.0", k6.ServerVersion);
        Assert.Subset(k6.Tools.ToHashSet(), RequiredK6Tools.ToHashSet());
        var diagnostics = Assert.Single(status.Data.Providers, p => p.Provider == DiagnosticsCatalog.ProviderName);
        Assert.True(diagnostics.Available, diagnostics.Error);
        Assert.Equal("0.28.0", diagnostics.ServerVersion);
        Assert.Subset(diagnostics.Tools.ToHashSet(), DiagnosticsCatalog.RequiredTools.ToHashSet());
        Assert.StartsWith("k6.exe v2.3.0", status.Data.K6Version, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("collect_process_dump")]
    [InlineData("inspect_heap")]
    [InlineData("get_bytes")]
    [InlineData("collect_thread_snapshot")]
    [InlineData("capture_method_bytes")]
    public async Task RiskyDiagnosticsToolsCannotBeInvokedThroughTheFacade(string tool)
    {
        await using var provider = new UpstreamMcpProvider(
            new UpstreamProviderOptions
            {
                Name = DiagnosticsCatalog.ProviderName,
                Command = "dotnet",
                Arguments = ["tool", "run", "dotnet-diagnostics-mcp", "--stdio"],
                WorkingDirectory = Repo.Root,
                RequiredTools = DiagnosticsCatalog.RequiredTools,
                AllowedTools = DiagnosticsCatalog.AllowedTools,
            },
            NullLoggerFactory.Instance);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            provider.CallAsync(tool, new Dictionary<string, object?> { ["processId"] = Environment.ProcessId }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GenericK6ToolsOutsideTheAllowlistCannotBeInvoked()
    {
        await using var provider = new UpstreamMcpProvider(
            new UpstreamProviderOptions
            {
                Name = K6Executor.ProviderName,
                Command = Repo.McpK6,
                RequiredTools = RequiredK6Tools,
                AllowedTools = RequiredK6Tools,
            },
            NullLoggerFactory.Instance);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            provider.CallAsync("search_terraform", new Dictionary<string, object?>(), TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MissingRequiredToolMakesTheProviderUnavailableWithAnExplanation()
    {
        Repo.RequirePrerequisites();
        await using var provider = new UpstreamMcpProvider(
            new UpstreamProviderOptions
            {
                Name = K6Executor.ProviderName,
                Command = Repo.McpK6,
                Environment = new Dictionary<string, string?> { ["PATH"] = Path.GetDirectoryName(Repo.K6) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH") },
                RequiredTools = [.. RequiredK6Tools, "tool_that_does_not_exist"],
                AllowedTools = RequiredK6Tools,
            },
            NullLoggerFactory.Instance);

        var status = await provider.EnsureConnectedAsync(TestContext.Current.CancellationToken);

        Assert.False(status.Available);
        Assert.Contains("tool_that_does_not_exist", status.MissingTools);
        Assert.Contains("catálogo real", status.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnexpectedProviderVersionIsReported()
    {
        Repo.RequirePrerequisites();
        await using var provider = new UpstreamMcpProvider(
            new UpstreamProviderOptions
            {
                Name = K6Executor.ProviderName,
                Command = Repo.McpK6,
                Environment = new Dictionary<string, string?> { ["PATH"] = Path.GetDirectoryName(Repo.K6) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH") },
                RequiredTools = RequiredK6Tools,
                AllowedTools = RequiredK6Tools,
                ExpectedVersion = "9.9.9",
            },
            NullLoggerFactory.Instance);

        var status = await provider.EnsureConnectedAsync(TestContext.Current.CancellationToken);

        Assert.False(status.Available);
        Assert.False(status.VersionMatches);
        Assert.Contains("9.9.9", status.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiagnosticsProviderInspectsTheGivenPidAndRecoversFromTimeouts()
    {
        Repo.RequirePrerequisites();
        var ct = TestContext.Current.CancellationToken;
        await using var provider = new UpstreamMcpProvider(
            new UpstreamProviderOptions
            {
                Name = DiagnosticsCatalog.ProviderName,
                Command = "dotnet",
                Arguments = ["tool", "run", "dotnet-diagnostics-mcp", "--stdio"],
                WorkingDirectory = Repo.Root,
                RequiredTools = DiagnosticsCatalog.RequiredTools,
                AllowedTools = DiagnosticsCatalog.AllowedTools,
                ExpectedVersion = "0.28.0",
                InitializationTimeout = TimeSpan.FromSeconds(90),
            },
            NullLoggerFactory.Instance);
        var adapter = new DotNetDiagnosticsAdapter(provider, NullLogger<DotNetDiagnosticsAdapter>.Instance);
        var pid = Environment.ProcessId;
        var artifacts = Path.Combine(Path.GetTempPath(), "perflab-contract-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            // Una captura que dura más que el timeout de la fachada: error explícito y proveedor reciclado.
            var timeout = await Assert.ThrowsAsync<ProviderCallException>(() => provider.CallAsync(
                "collect_events",
                new Dictionary<string, object?> { ["kind"] = "counters", ["processId"] = pid, ["durationSeconds"] = 20 },
                TimeSpan.FromSeconds(3),
                ct));
            Assert.Contains("Timeout", timeout.Message, StringComparison.Ordinal);
            Assert.False(timeout.Invocation.Success);

            // El proveedor se reconecta y la inspección del PID indicado devuelve datos estructurados del runtime.
            var info = await adapter.InspectAsync("rt-test-info", pid, "info", 5, artifacts, ct);
            Assert.Equal(pid, info.ProcessId);
            Assert.StartsWith("10.", info.RuntimeVersion, StringComparison.Ordinal);
            Assert.Equal(pid.ToString(System.Globalization.CultureInfo.InvariantCulture), Assert.Single(info.Providers).Arguments["processId"]);
            Assert.All(Directory.EnumerateFiles(artifacts), f => System.Text.Json.JsonDocument.Parse(File.ReadAllText(f)).Dispose());

            // Proceso objetivo no disponible: error estructurado del proveedor, convertido en un fallo explícito.
            var gone = await Assert.ThrowsAsync<ProviderCallException>(() => adapter.InspectAsync("rt-test-gone", 999_999, "info", 5, artifacts, ct));
            Assert.False(string.IsNullOrWhiteSpace(gone.Message));
            Assert.False(gone.Invocation.Success);
        }
        finally
        {
            if (Directory.Exists(artifacts))
            {
                Directory.Delete(artifacts, recursive: true);
            }
        }
    }
}
