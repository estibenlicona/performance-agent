using System.Text.Json;
using ModelContextProtocol.Client;

namespace PerfAgent.IntegrationTests;

/// <summary>
/// El servidor perf-lab tal como lo arranca Copilot (stdio): expone solo las herramientas de la fachada, con salida
/// estructurada, y ninguna herramienta genérica de los proveedores comunitarios.
/// </summary>
public sealed class McpFacadeTests
{
    private static readonly string[] ExpectedTools =
    [
        "analyze_run", "collect_runtime_evidence", "compare_runs", "discover_api", "generate_k6_scenario", "generate_report",
        "get_lab_status", "inspect_runtime", "list_runs", "preflight_environment", "run_diagnosis", "run_k6_test",
        "search_k6_docs", "start_lab", "stop_lab", "validate_k6_scenario", "validate_manifest",
    ];

    private static readonly string[] UpstreamTools =
    [
        "run_script", "validate_script", "info", "search_terraform", "inspect_process", "collect_events", "collect_sample",
        "collect_batch", "query_snapshot", "collect_process_dump", "inspect_heap", "get_bytes", "collect_thread_snapshot",
    ];

    private static async Task<McpClient> StartServerAsync(CancellationToken cancellationToken)
    {
        var dll = Path.Combine(Repo.Root, "dotnet", "src", "PerfAgent.Mcp", "bin", Repo.Configuration, "net10.0", "PerfAgent.Mcp.dll");
        if (!File.Exists(dll))
        {
            Assert.Skip($"No existe {dll}: compila la solución.");
        }

        return await McpClient.CreateAsync(
            new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "perf-lab",
                Command = "dotnet",
                Arguments = [dll],
                WorkingDirectory = Repo.Root,
                EnvironmentVariables = new Dictionary<string, string?> { ["PERFLAB_WORKSPACE"] = Repo.Root },
            }),
            new McpClientOptions { InitializationTimeout = TimeSpan.FromSeconds(60) },
            cancellationToken: cancellationToken);
    }

    [Fact]
    public async Task AgentSeesOnlyPerfLabToolsWithStructuredOutput()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var client = await StartServerAsync(ct);

        var tools = await client.ListToolsAsync(cancellationToken: ct);

        Assert.Equal("perf-lab", client.ServerInfo.Name);
        Assert.Equal(ExpectedTools, tools.Select(t => t.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(tools, t => UpstreamTools.Contains(t.Name));
        Assert.All(tools, t => Assert.NotNull(t.ReturnJsonSchema));
        Assert.True(tools.Single(t => t.Name == "get_lab_status").ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.False(tools.Single(t => t.Name == "run_k6_test").ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.All(tools, t => Assert.False(t.ProtocolTool.Annotations?.DestructiveHint ?? true));

        // Ninguna herramienta acepta PIDs, URLs de destino ni comandos.
        var parameters = tools.SelectMany(t => t.JsonSchema.TryGetProperty("properties", out var p) ? p.EnumerateObject().Select(x => x.Name) : []).ToHashSet();
        Assert.DoesNotContain("processId", parameters);
        Assert.DoesNotContain("url", parameters);
        Assert.DoesNotContain("baseUrl", parameters);
        Assert.DoesNotContain("script", parameters);
        Assert.DoesNotContain("command", parameters);
    }

    [Fact]
    public async Task PolicyViolationsAreExplainedOverMcp()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var client = await StartServerAsync(ct);

        var outside = await client.CallToolAsync("validate_manifest", new Dictionary<string, object?> { ["manifestPath"] = "../../outside.yaml" }, cancellationToken: ct);
        var unknownRun = await client.CallToolAsync("run_k6_test", new Dictionary<string, object?> { ["runId"] = "20990101-000000-abcdef", ["kind"] = "smoke" }, cancellationToken: ct);
        var badKind = await client.CallToolAsync("run_k6_test", new Dictionary<string, object?> { ["runId"] = "20990101-000000-abcdef", ["kind"] = "stress" }, cancellationToken: ct);
        var badCapture = await client.CallToolAsync("collect_runtime_evidence", new Dictionary<string, object?> { ["runId"] = "20990101-000000-abcdef", ["kind"] = "dump" }, cancellationToken: ct);

        Assert.Equal("blocked", Status(outside));
        Assert.Contains("path.outside-allowed-roots", outside.StructuredContent!.Value.GetRawText(), StringComparison.Ordinal);
        Assert.Equal("not-found", Status(unknownRun));
        Assert.Equal("blocked", Status(badKind));
        Assert.Equal("blocked", Status(badCapture));
        Assert.Contains("diagnostics.kind", badCapture.StructuredContent!.Value.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task K6DocumentationIsServedReadOnlyThroughTheFacade()
    {
        Repo.RequirePrerequisites();
        var ct = TestContext.Current.CancellationToken;
        await using var client = await StartServerAsync(ct);

        var result = await client.CallToolAsync("search_k6_docs", new Dictionary<string, object?> { ["slug"] = "using-k6/thresholds" }, cancellationToken: ct);

        if (Status(result) == "unavailable")
        {
            Assert.Skip("La documentación de k6 requiere red para descargar el paquete de docs de mcp-k6.");
        }

        Assert.Equal("ok", Status(result));
        var data = result.StructuredContent!.Value.GetProperty("data");
        Assert.Contains("threshold", data.GetProperty("content").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("get_documentation", data.GetProperty("provider").GetProperty("tool").GetString());
    }

    private static string? Status(ModelContextProtocol.Protocol.CallToolResult result) =>
        result.StructuredContent is { } json && json.TryGetProperty("status", out var status) ? status.GetString() : null;
}
