using PerfAgent.Core.Manifest;
using PerfAgent.Core.Policy;

namespace PerfAgent.UnitTests;

/// <summary>Workspace temporal y hermético con un proyecto web de ejemplo, contratos y manifiestos.</summary>
public sealed class TestWorkspace : IDisposable
{
    public TestWorkspace(SafetyPolicy? policy = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "perflab-tests", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path.Combine(Root, "lab", "Api"));
        Directory.CreateDirectory(Path.Combine(Root, "lab", "contracts"));
        Directory.CreateDirectory(Path.Combine(Root, "scenarios"));
        File.WriteAllText(Path.Combine(Root, "lab", "Api", "Api.csproj"), """<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        File.WriteAllText(Path.Combine(Root, "lab", "contracts", "pricing.json"), """{ "name": "pricing", "version": 1, "routes": [ { "method": "GET", "path": "/prices/{id}", "status": 200, "latencyMs": 10 } ] }""");
        Policy = policy ?? new SafetyPolicy { AllowedProjectRoots = ["lab"] };
        Workspace = new Workspace(Root, Policy);
        Engine = new PolicyEngine(Workspace);
    }

    public string Root { get; }

    public SafetyPolicy Policy { get; }

    public Workspace Workspace { get; }

    public PolicyEngine Engine { get; }

    public const string ValidManifest = """
        schemaVersion: 1
        name: test
        profile: local-isolated
        environment: lab
        api:
          project: lab/Api/Api.csproj
          healthPath: /health
          settings:
            Lab__Faults__Cpu: "true"
        endpoints:
          - name: products
            method: GET
            path: /api/products/{id}
            pathParameters:
              id: "42"
            expectedStatus: 200
        dependencies:
          pricing:
            mode: mock
            contract: lab/contracts/pricing.json
            injectAs: Dependencies__Pricing__BaseUrl
        load:
          endpointSource: declared
          smoke:
            vus: 1
            iterations: 5
            maxDuration: 30s
          baseline:
            executor: constant-vus
            vus: 10
            duration: 30s
          thresholds:
            http_req_failed: ['rate<0.01']
            http_req_duration: ['p(95)<500']
        """;

    public ManifestValidation Validate(string yaml)
    {
        var path = Path.Combine(Root, "scenarios", $"m-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, yaml);
        return Engine.Validate(ManifestLoader.Load(path, Workspace.ToRelative(path)));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Limpieza best-effort del directorio temporal.
        }
    }
}

public static class Fixtures
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    public static System.Text.Json.JsonElement Json(string name) =>
        System.Text.Json.JsonDocument.Parse(Read(name)).RootElement.Clone();
}
