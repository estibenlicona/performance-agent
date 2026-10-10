using PerfAgent.Core.Contracts;
using PerfAgent.Core.Discovery;
using PerfAgent.Core.Manifest;
using PerfAgent.Core.Runs;
using PerfAgent.Core.Triage;
using PerfAgent.Reporting;

namespace PerfAgent.UnitTests;

public sealed class ReportingTests
{
    private static LabRun Run(string id, double p95, double rps, int vus = 16, string hash = "h1", string? commit = "abc", string manifestSha = "m1")
    {
        var run = new LabRun
        {
            RunId = id,
            ManifestPath = "scenarios/lab-cpu.yaml",
            ManifestSha256 = manifestSha,
            Profile = EnvironmentProfileKind.LocalIsolated,
            Target = new ApiTarget { ProjectPath = "lab/PerfLab.Api/PerfLab.Api.csproj", ProjectName = "PerfLab.Api", Revision = new SourceRevision(commit, "main", false, null) },
            Environment = new EnvironmentProfile { Kind = EnvironmentProfileKind.LocalIsolated, EnvironmentLabel = "lab", Dependencies = [], Decision = PreflightDecision.Continue },
            Versions = new ToolVersions { TargetRuntime = "10.0.12", OperatingSystem = "Windows", ProcessorCount = 4, K6 = "v2.3.0" },
        };
        run.LoadTests.Add(new LoadTestRun
        {
            Id = "smoke-1", Kind = LoadTestKind.Smoke, Load = new LoadProfile("shared-iterations", 1, 10, 30, 0), StartedAt = DateTimeOffset.UtcNow,
            Status = LoadTestStatus.Passed, Latency = new LatencyStats(8, 5, 7, 9, 10, 11, 12), TotalRequests = 10, ScenarioHash = "s",
        });
        run.LoadTests.Add(new LoadTestRun
        {
            Id = "baseline-1", Kind = LoadTestKind.Baseline, Load = new LoadProfile("constant-vus", vus, null, 20, 0), StartedAt = DateTimeOffset.UtcNow,
            Status = LoadTestStatus.Passed, Latency = new LatencyStats(p95 / 2, 1, p95 / 2, p95 * 0.9, p95, p95 * 1.2, p95 * 3), TotalRequests = (long)(rps * 20),
            RequestsPerSecond = rps, ErrorRate = 0, ScenarioHash = hash,
            Endpoints = [new EndpointStats("cpu|report", "GET", "/api/cpu/report", (long)(rps * 20), 0, new LatencyStats(null, null, null, null, p95, null, null), (long)(rps * 20), 0)],
        });
        return run;
    }

    [Fact]
    public void ComparableRunsReportDirectionalDeltas()
    {
        var before = Run("20261009-010000-aaaaaa", p95: 40, rps: 700, commit: "abc", manifestSha: "m1");
        var after = Run("20261009-020000-bbbbbb", p95: 4, rps: 6000, commit: "def", manifestSha: "m2");

        var comparison = RunComparer.Compare(before, after);

        Assert.True(comparison.Comparable, comparison.Summary);
        Assert.Equal("better", comparison.Deltas.Single(d => d.Metric == "p95").Direction);
        Assert.Equal("better", comparison.Deltas.Single(d => d.Metric == "throughput").Direction);
        Assert.Contains(comparison.ChangesUnderEvaluation, c => c.Contains("Revisión", StringComparison.Ordinal));
        Assert.Contains(comparison.ChangesUnderEvaluation, c => c.Contains("Manifiesto", StringComparison.Ordinal));
    }

    [Fact]
    public void RunsWithDifferentLoadOrScenarioAreNotComparable()
    {
        var before = Run("20261009-010000-aaaaaa", p95: 40, rps: 700, vus: 16);

        var differentLoad = RunComparer.Compare(before, Run("20261009-020000-bbbbbb", p95: 40, rps: 700, vus: 32));
        var differentScenario = RunComparer.Compare(before, Run("20261009-020000-cccccc", p95: 40, rps: 700, hash: "h2"));

        Assert.False(differentLoad.Comparable);
        Assert.Contains(differentLoad.MaterialDifferences, d => d.StartsWith("Carga distinta", StringComparison.Ordinal));
        Assert.False(differentScenario.Comparable);
        Assert.StartsWith("Comparación rechazada", differentScenario.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void SmallChangesAreTreatedAsNoise()
    {
        var comparison = RunComparer.Compare(Run("20261009-010000-aaaaaa", 40, 700), Run("20261009-020000-bbbbbb", 41, 690));

        Assert.All(comparison.Deltas.Where(d => d.Metric is "p95" or "throughput"), d => Assert.Equal("same", d.Direction));
    }

    [Fact]
    public void MarkdownReportSeparatesObservationsHypothesesAndLimitations()
    {
        var run = Run("20261009-010000-aaaaaa", 40, 700);
        run.RuntimeSnapshots.Add(new RuntimeSnapshot
        {
            Id = "rt-1-counters", ProcessId = 1, Kind = "counters", LoadTestId = "baseline-1", StartedAt = DateTimeOffset.UtcNow, EndedAt = DateTimeOffset.UtcNow.AddSeconds(20),
            Summary = new RuntimeSummary { CpuPercentAvg = 85, CpuPercentMax = 99, ThreadPoolQueueLengthMax = 1, ThreadPoolQueueLengthLast = 0, ThreadPoolStarvationAdjustments = 0 },
        });
        var triage = TriageEngine.Analyze(run);
        var report = ReportBuilder.Build(run, triage);

        var md = MarkdownReportRenderer.Render(report);

        Assert.Contains("## Observaciones (síntomas medidos)", md, StringComparison.Ordinal);
        Assert.Contains("## Hipótesis", md, StringComparison.Ordinal);
        Assert.Contains("**Evidencia a favor:**", md, StringComparison.Ordinal);
        Assert.Contains("## Limitaciones", md, StringComparison.Ordinal);
        Assert.Contains("cpu\\|report", md, StringComparison.Ordinal);
        Assert.Contains("## Ambiente y fidelidad", md, StringComparison.Ordinal);
    }
}

public sealed class EndpointPlannerTests : IDisposable
{
    private readonly TestWorkspace _ws = new();

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void OpenApiFromTheLabIsReadSafely()
    {
        var candidates = EndpointPlanner.ReadOpenApi(Fixtures.Read("perflab.openapi.json"));

        Assert.Contains(candidates, c => c is { Method: "GET", PathTemplate: "/api/cpu/report", Name: "GetCpuReport" });
        Assert.Contains(candidates, c => c is { Method: "POST", PathTemplate: "/api/orders", RequiresBody: true });
        var product = Assert.Single(candidates, c => c.PathTemplate == "/api/products/{id}");
        Assert.Equal("integer", Assert.Single(product.PathParameters).SchemaType);
        Assert.DoesNotContain(candidates, c => c.PathTemplate == "/health");
    }

    [Fact]
    public void WritesAreExcludedAndIntegerPathParametersGetSyntheticValues()
    {
        var manifest = new RunManifest { Load = { EndpointSource = "openapi" } };

        var plan = EndpointPlanner.Plan(manifest, _ws.Engine, EndpointPlanner.ReadOpenApi(Fixtures.Read("perflab.openapi.json")), null);

        Assert.Contains(plan.Excluded, e => e.Method == "POST" && e.Reason.Contains("escritura", StringComparison.Ordinal));
        var product = Assert.Single(plan.Endpoints, e => e.PathTemplate == "/api/products/{id}");
        Assert.Equal("/api/products/1", product.Path);
        Assert.Contains(product.Notes, n => n.Contains("inferido", StringComparison.Ordinal));
        Assert.All(plan.Endpoints, e => Assert.Contains(e.Method, new[] { "GET", "HEAD" }));
    }

    [Fact]
    public void IncludeFilterSelectsEndpointsByName()
    {
        var manifest = new RunManifest { Load = { EndpointSource = "openapi", Include = ["GetCpuReport"] } };

        var plan = EndpointPlanner.Plan(manifest, _ws.Engine, EndpointPlanner.ReadOpenApi(Fixtures.Read("perflab.openapi.json")), null);

        Assert.Equal("GetCpuReport", Assert.Single(plan.Endpoints).Name);
    }

    [Fact]
    public void AuthenticatedOrQueryRequiredOperationsAreExcluded()
    {
        const string doc = """
            {
              "openapi": "3.1.1",
              "security": [ { "bearer": [] } ],
              "paths": {
                "/secure": { "get": { "operationId": "Secure", "responses": { "200": {} } } },
                "/public": { "get": { "operationId": "Public", "security": [], "responses": { "204": {} } } },
                "/search": { "get": { "operationId": "Search", "security": [], "parameters": [ { "name": "q", "in": "query", "required": true } ] } },
                "/by-name/{name}": { "get": { "operationId": "ByName", "security": [], "parameters": [ { "name": "name", "in": "path", "required": true, "schema": { "type": "string" } } ] } }
              }
            }
            """;

        var plan = EndpointPlanner.Plan(new RunManifest { Load = { EndpointSource = "openapi" } }, _ws.Engine, EndpointPlanner.ReadOpenApi(doc), null);

        var only = Assert.Single(plan.Endpoints);
        Assert.Equal("Public", only.Name);
        Assert.Equal(204, only.ExpectedStatus);
        Assert.Contains(plan.Excluded, e => e.Path == "/secure" && e.Reason.Contains("autenticación", StringComparison.Ordinal));
        Assert.Contains(plan.Excluded, e => e.Path == "/search");
        Assert.Contains(plan.Excluded, e => e.Path == "/by-name/{name}");
    }

    [Fact]
    public void MissingOpenApiRequiresDeclaredEndpoints()
    {
        var plan = EndpointPlanner.Plan(new RunManifest(), _ws.Engine, null, "404");

        Assert.True(plan.IsEmpty);
        Assert.Contains(plan.Notes, n => n.Contains("Declara endpoints", StringComparison.Ordinal));
    }
}
