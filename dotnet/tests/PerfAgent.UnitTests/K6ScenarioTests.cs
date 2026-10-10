using PerfAgent.Adapters.K6Mcp;
using PerfAgent.Core.Contracts;
using PerfAgent.Core.Discovery;
using PerfAgent.Core.Manifest;
using PerfAgent.Core.Runs;

namespace PerfAgent.UnitTests;

public sealed class K6ScenarioTests : IDisposable
{
    private const string BaseUrl = "http://127.0.0.1:54321";
    private static readonly LoadProfile Baseline = new("constant-vus", 10, null, 30, 0);
    private readonly TestWorkspace _ws = new();

    public void Dispose() => _ws.Dispose();

    private static IReadOnlyList<PlannedEndpoint> Endpoints(string? bodyContains = null, string method = "GET") =>
    [
        new PlannedEndpoint("cpu-report", method, "/api/cpu/report", "/api/cpu/report", 200, 1, bodyContains, null, "declared", []),
        new PlannedEndpoint("product", "GET", "/api/products/1", "/api/products/{id}", 200, 3, null, null, "openapi", []),
    ];

    private static (K6Scenario Scenario, ScenarioRecord Record) Generate(IReadOnlyList<PlannedEndpoint>? endpoints = null, string baseUrl = BaseUrl)
    {
        var scenario = K6ScriptGenerator.Generate(LoadTestKind.Baseline, baseUrl, endpoints ?? Endpoints(), Baseline,
            new Dictionary<string, List<string>> { ["http_req_duration"] = ["p(95)<500"] });
        var record = new ScenarioRecord(LoadTestKind.Baseline, "k6/baseline.js", scenario.ScriptSha256, scenario.ScenarioHash, Baseline, scenario.Endpoints.Select(e => e.Name).ToList(), DateTimeOffset.UtcNow);
        return (scenario, record);
    }

    [Fact]
    public void GeneratedScriptTargetsOnlyTheLabInstanceAndBlocksOtherDestinations()
    {
        var (scenario, _) = Generate();

        Assert.StartsWith(K6ScriptGenerator.HeaderMarker, scenario.Script, StringComparison.Ordinal);
        Assert.Contains($"const BASE_URL = \"{BaseUrl}\";", scenario.Script, StringComparison.Ordinal);
        Assert.Contains("blockHostnames: ['*']", scenario.Script, StringComparison.Ordinal);
        Assert.Contains("\"128.0.0.0/1\"", scenario.Script, StringComparison.Ordinal);
        Assert.Contains("\"::/0\"", scenario.Script, StringComparison.Ordinal);
        Assert.Contains("executor: 'constant-vus', vus: 10, duration: '30s'", scenario.Script, StringComparison.Ordinal);
        Assert.Contains("\"http_req_duration{name:cpu-report}\": [\"max>=0\"]", scenario.Script, StringComparison.Ordinal);
        Assert.Contains("\"http_req_duration\": [\"p(95)<500\"]", scenario.Script, StringComparison.Ordinal);
    }

    [Fact]
    public void ScenarioHashIgnoresThePortSoRunsAreComparable()
    {
        var (a, _) = Generate(baseUrl: "http://127.0.0.1:50000");
        var (b, _) = Generate(baseUrl: "http://127.0.0.1:60000");

        Assert.NotEqual(a.ScriptSha256, b.ScriptSha256);
        Assert.Equal(a.ScenarioHash, b.ScenarioHash);
    }

    [Fact]
    public void UserDataIsEmbeddedAsEscapedJsonAndSuspiciousContentIsRejected()
    {
        var (scenario, record) = Generate(Endpoints(bodyContains: "x\"]; require('fs'); //\u2028"));

        // La comilla queda escapada: el valor no puede cerrar el literal ni el array.
        Assert.Contains("x\\\"]; require", scenario.Script, StringComparison.Ordinal);
        Assert.DoesNotContain("\"x\"]; require", scenario.Script, StringComparison.Ordinal);
        Assert.DoesNotContain("\u2028", scenario.Script, StringComparison.Ordinal);
        Assert.Contains("\\u2028", scenario.Script, StringComparison.Ordinal);

        // Defensa en profundidad: el validador estático rechaza construcciones peligrosas aunque estén en datos.
        var decision = K6ScriptValidator.Validate(scenario.Script, record, _ws.Engine, new RunManifest(), new Uri(BaseUrl));
        Assert.Contains(decision.Violations, v => v.Code == "k6.forbidden-construct");
    }

    [Fact]
    public void GeneratedScriptPassesStaticValidation()
    {
        var (scenario, record) = Generate();

        var decision = K6ScriptValidator.Validate(scenario.Script, record, _ws.Engine, new RunManifest(), new Uri(BaseUrl));

        Assert.True(decision.Allowed, decision.Explain());
    }

    [Theory]
    [InlineData("const BASE_URL = \"http://127.0.0.1:54321\";", "const BASE_URL = \"http://10.1.2.3:80\";", "k6.tampered")]
    [InlineData("export default function () {", "export default function () {\n  http.get('https://example.com/');", "k6.foreign-url")]
    [InlineData("import { Counter } from 'k6/metrics';", "import { Counter } from 'k6/metrics';\nimport exec from 'k6/x/exec';", "k6.import")]
    [InlineData("export default function () {", "export default function () {\n  open('/etc/passwd');", "k6.forbidden-construct")]
    [InlineData("  blockHostnames: ['*'],", "", "k6.network-guard")]
    public void TamperedOrUnsafeScriptsAreRejectedBeforeDelegation(string original, string replacement, string code)
    {
        var (scenario, record) = Generate();
        var script = scenario.Script.Replace(original, replacement, StringComparison.Ordinal);
        Assert.NotEqual(scenario.Script, script);

        var decision = K6ScriptValidator.Validate(script, record, _ws.Engine, new RunManifest(), new Uri(BaseUrl));

        Assert.False(decision.Allowed);
        Assert.Contains(decision.Violations, v => v.Code == code);
        Assert.Contains(decision.Violations, v => v.Code == "k6.tampered");
    }

    [Fact]
    public void WriteMethodsInTheScriptAreRejectedByDefaultPolicy()
    {
        var (scenario, record) = Generate(Endpoints(method: "DELETE"));

        var decision = K6ScriptValidator.Validate(scenario.Script, record, _ws.Engine, new RunManifest(), new Uri(BaseUrl));

        Assert.Contains(decision.Violations, v => v.Code == "k6.method");
    }

    [Fact]
    public void LoadOverPolicyLimitsIsRejectedAtExecutionTime()
    {
        var (scenario, record) = Generate();
        var tooMuch = record with { Load = new LoadProfile("constant-vus", 5000, null, 30, 0) };

        var decision = K6ScriptValidator.Validate(scenario.Script, tooMuch, _ws.Engine, new RunManifest(), new Uri(BaseUrl));

        Assert.Contains(decision.Violations, v => v.Code == "load.vus-exceeded");
    }

    [Fact]
    public void SmokeUsesSharedIterations()
    {
        var smoke = K6ScriptGenerator.Generate(LoadTestKind.Smoke, BaseUrl, Endpoints(), new LoadProfile("shared-iterations", 1, 5, 30, 0), new Dictionary<string, List<string>>());

        Assert.Contains("executor: 'shared-iterations', vus: 1, iterations: 5, maxDuration: '30s'", smoke.Script, StringComparison.Ordinal);
    }

    [Fact]
    public void RealK6SummaryIsNormalized()
    {
        var summary = K6SummaryParser.Parse(Fixtures.Read("cpu.k6-baseline.summary.json"),
            [new PlannedEndpoint("GetCpuReport", "GET", "/api/cpu/report", "/api/cpu/report", 200, 1, null, null, "openapi", [])]);

        Assert.True(summary.TotalRequests > 1000);
        Assert.NotNull(summary.Latency?.P95);
        Assert.True(summary.Latency!.P95 > summary.Latency.Med);
        Assert.Equal(0, summary.FailedRequests);
        Assert.Equal(summary.TotalRequests, summary.StatusClasses["2xx"]);
        var endpoint = Assert.Single(summary.Endpoints);
        Assert.Equal(summary.TotalRequests, endpoint.Requests);
        Assert.Contains(summary.Thresholds, t => t is { Metric: "http_req_failed", Expression: "rate<0.01", Passed: true });
        Assert.DoesNotContain(summary.Thresholds, t => t.Expression is "max>=0" or "rate>=0");
        Assert.NotNull(summary.TestRunDurationMs);
    }

    [Fact]
    public void FailedRequestsAndStatusClassesComeFromTheSummary()
    {
        var summary = K6SummaryParser.Parse(Fixtures.Read("errors.k6-baseline.summary.json"),
            [new PlannedEndpoint("dependency-quote", "GET", "/api/dependency/quote", "/api/dependency/quote", 200, 1, null, null, "declared", [])]);

        Assert.True(summary.FailedRequests > 0);
        Assert.Equal(summary.FailedRequests, summary.StatusClasses["5xx"]);
        Assert.Contains(summary.Thresholds, t => t is { Metric: "http_req_failed", Passed: false });
    }

    [Fact]
    public void SummaryIsExtractedFromNoisyStdout()
    {
        var stdout = "banner\nrunning (01.0s)\n" + K6ScriptGenerator.SummaryBegin + "{\"metrics\":{}}" + K6ScriptGenerator.SummaryEnd + "\ndone";

        Assert.Equal("{\"metrics\":{}}", K6SummaryParser.ExtractSummaryJson(stdout));
        Assert.DoesNotContain("metrics", K6SummaryParser.StripSummary(stdout), StringComparison.Ordinal);
        Assert.Null(K6SummaryParser.ExtractSummaryJson("no summary here"));
    }

    [Theory]
    [InlineData("30s", 30)]
    [InlineData("2m", 120)]
    [InlineData("1m30s", 90)]
    [InlineData("1h", 3600)]
    public void K6DurationsAreParsed(string text, int seconds)
    {
        Assert.True(K6Duration.TryParseSeconds(text, out var parsed));
        Assert.Equal(seconds, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("30")]
    [InlineData("abc")]
    [InlineData("-5s")]
    public void InvalidDurationsAreRejected(string text) => Assert.False(K6Duration.TryParseSeconds(text, out _));
}
