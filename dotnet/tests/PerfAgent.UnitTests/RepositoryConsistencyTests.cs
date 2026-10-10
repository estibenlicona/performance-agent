using System.Text.Json;
using System.Text.RegularExpressions;
using PerfAgent.Core.Manifest;
using PerfAgent.Core.Policy;

namespace PerfAgent.UnitTests;

/// <summary>
/// La configuración versionada (Copilot, MCP, versiones fijadas, política, esquema y manifiestos) es coherente:
/// el agente solo ve perf-lab, las versiones de proveedores coinciden en todos los archivos y todos los
/// escenarios de ejemplo cumplen la política real del repositorio.
/// </summary>
public sealed partial class RepositoryConsistencyTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, SafetyPolicy.FileName)))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Raíz del repositorio no encontrada.");
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root, .. parts]));

    private static (Dictionary<string, string> Fields, string Body) Frontmatter(string markdown)
    {
        var match = FrontmatterRegex().Match(markdown.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.True(match.Success, "Falta el frontmatter YAML.");
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        string? listKey = null;
        foreach (var line in match.Groups["yaml"].Value.Split('\n'))
        {
            if (line.StartsWith("  - ", StringComparison.Ordinal) && listKey is not null)
            {
                fields[listKey] = (fields.GetValueOrDefault(listKey) + "," + line[4..].Trim()).Trim(',');
            }
            else if (line.IndexOf(':', StringComparison.Ordinal) is > 0 and var colon)
            {
                listKey = line[..colon].Trim();
                fields[listKey] = line[(colon + 1)..].Trim();
            }
        }

        return (fields, match.Groups["body"].Value);
    }

    [Fact]
    public void CopilotSeesOnlyThePerfLabServer()
    {
        using var mcp = JsonDocument.Parse(Read(".mcp.json"));
        var servers = mcp.RootElement.GetProperty("mcpServers").EnumerateObject().Select(s => s.Name).ToList();

        Assert.Equal(["perf-lab"], servers);
        Assert.False(File.Exists(Path.Combine(Root, ".vscode", "mcp.json")), "No dupliques servidores en .vscode/mcp.json.");
        var perfLab = mcp.RootElement.GetProperty("mcpServers").GetProperty("perf-lab");
        Assert.Contains("dotnet/src/PerfAgent.Mcp/PerfAgent.Mcp.csproj", perfLab.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
    }

    [Fact]
    public void AgentToolsAreLimitedToPerfLabAndReadOnlyBuiltIns()
    {
        var (fields, body) = Frontmatter(Read(".github", "agents", "performance-diagnostics.agent.md"));

        var tools = fields["tools"].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(["perf-lab/*", "read", "search"], tools);
        Assert.Equal("vscode", fields["target"]);
        Assert.False(string.IsNullOrWhiteSpace(fields["description"]));
        Assert.Contains("dotnet-performance-diagnosis", body, StringComparison.Ordinal);
        Assert.True(body.Length < 30_000, "GitHub limita el cuerpo del agente a 30.000 caracteres.");
    }

    [Fact]
    public void SkillFrontmatterFollowsTheAgentSkillsSpec()
    {
        var (fields, _) = Frontmatter(Read(".github", "skills", "dotnet-performance-diagnosis", "SKILL.md"));

        Assert.Equal("dotnet-performance-diagnosis", fields["name"]);
        Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", fields["name"]);
        Assert.InRange(fields["description"].Length, 1, 1024);
    }

    [Fact]
    public void PromptFileTargetsTheCustomAgent()
    {
        var (fields, _) = Frontmatter(Read(".github", "prompts", "diagnose-api-performance.prompt.md"));
        var (agent, _) = Frontmatter(Read(".github", "agents", "performance-diagnostics.agent.md"));

        Assert.Equal(agent["name"], fields["agent"]);
        Assert.False(fields.ContainsKey("mode"), "'mode' está obsoleto: usa 'agent'.");
    }

    [Fact]
    public void PinnedVersionsAgreeAcrossFiles()
    {
        using var tools = JsonDocument.Parse(Read("eng", "tools.json"));
        using var appsettings = JsonDocument.Parse(Read("dotnet", "src", "PerfAgent.Mcp", "appsettings.json"));
        using var dotnetTools = JsonDocument.Parse(Read(".config", "dotnet-tools.json"));
        var providers = appsettings.RootElement.GetProperty("CommunityProviders");

        var k6 = tools.RootElement.GetProperty("k6").GetProperty("version").GetString()!;
        var mcpK6 = tools.RootElement.GetProperty("mcpK6").GetProperty("version").GetString()!;
        var diagnostics = tools.RootElement.GetProperty("dotnetDiagnosticsMcp").GetProperty("version").GetString()!;

        Assert.Equal(k6, providers.GetProperty("K6").GetProperty("ExpectedVersion").GetString());
        Assert.Contains($"/k6/{k6}/", providers.GetProperty("K6").GetProperty("Executable").GetString(), StringComparison.Ordinal);
        Assert.Equal(mcpK6, providers.GetProperty("K6Mcp").GetProperty("ExpectedVersion").GetString());
        Assert.Contains($"/mcp-k6/{mcpK6}/", providers.GetProperty("K6Mcp").GetProperty("Command").GetString(), StringComparison.Ordinal);
        Assert.Equal(diagnostics, providers.GetProperty("DotNetDiagnosticsMcp").GetProperty("ExpectedVersion").GetString());
        Assert.Equal(diagnostics, dotnetTools.RootElement.GetProperty("tools").GetProperty("dotnet-diagnostics-mcp").GetProperty("version").GetString());
        foreach (var tool in new[] { "k6", "mcpK6" })
        {
            Assert.Matches("^[0-9a-f]{64}$", tools.RootElement.GetProperty(tool).GetProperty("assets").GetProperty("windows-amd64").GetProperty("sha256").GetString()!);
        }
    }

    [Fact]
    public void RepositoryPolicyIsRestrictiveByDefault()
    {
        var policy = SafetyPolicy.LoadOrDefault(Root);

        Assert.Equal(["127.0.0.1"], policy.AllowedLoadTargetHosts);
        Assert.Empty(policy.AllowedRemoteDependencyHosts);
        Assert.Equal("deny", policy.WriteOperations);
        Assert.False(policy.Diagnostics.AllowDumps);
        Assert.False(policy.Diagnostics.AllowHeapInspection);
        Assert.False(policy.Diagnostics.AllowMethodParameterCapture);
        Assert.True(policy.Load.MaxDurationSeconds <= 270, "mcp-k6 corta run_script a los 5 minutos.");
    }

    [Fact]
    public void CommittedManifestSchemaIsUpToDate()
    {
        var committed = Read("scenarios", "run-manifest.v1.schema.json").Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.True(committed == ManifestSchema.Generate(),
            "scenarios/run-manifest.v1.schema.json está desactualizado: dotnet run --project dotnet/src/PerfAgent.Mcp -- schema");
    }

    public static TheoryData<string> ScenarioFiles() =>
        new(Directory.EnumerateFiles(Path.Combine(Root, "scenarios"), "*.yaml").Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal));

    [Theory]
    [MemberData(nameof(ScenarioFiles))]
    public void ScenarioManifestsComplyWithTheRepositoryPolicy(string file)
    {
        var workspace = new Workspace(Root, SafetyPolicy.LoadOrDefault(Root));
        var path = Path.Combine(Root, "scenarios", file);
        var validation = new PolicyEngine(workspace).Validate(ManifestLoader.Load(path, $"scenarios/{file}"));

        if (file.EndsWith(".example.yaml", StringComparison.Ordinal))
        {
            // Los ejemplos documentan algo que la política por defecto no permite todavía (p. ej. un host remoto).
            Assert.False(validation.Decision.Allowed);
            Assert.All(validation.Decision.Violations, v => Assert.Equal("dependencies.remote-host-not-allowed", v.Code));
        }
        else
        {
            Assert.True(validation.Decision.Allowed, validation.Decision.Explain());
        }
    }

    [GeneratedRegex(@"^---\n(?<yaml>.*?)\n---\n(?<body>.*)$", RegexOptions.Singleline)]
    private static partial Regex FrontmatterRegex();
}
