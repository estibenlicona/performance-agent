using PerfAgent.Core.Contracts;
using PerfAgent.Core.Manifest;
using PerfAgent.Core.Policy;

namespace PerfAgent.UnitTests;

/// <summary>Paso 1 del MVP: el sistema rechaza ejecuciones fuera de alcance o de límites y explica por qué, antes de iniciar nada.</summary>
public sealed class PolicyEngineTests : IDisposable
{
    private readonly TestWorkspace _ws = new();

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void ValidManifestIsAllowedAndNormalized()
    {
        var result = _ws.Validate(TestWorkspace.ValidManifest);

        Assert.True(result.Decision.Allowed, result.Decision.Explain());
        Assert.NotNull(result.Manifest);
        Assert.Equal(EnvironmentProfileKind.LocalIsolated, result.Manifest.Profile);
        Assert.Equal(new LoadProfile("constant-vus", 10, null, 30, 0), result.Manifest.Baseline);
        Assert.Equal(5, result.Manifest.Smoke.Iterations);
        Assert.Contains("pricing", result.Manifest.ContractFullPaths.Keys);
    }

    [Theory]
    [InlineData("    vus: 10\n    duration: 30s", "    vus: 500\n    duration: 30s", "load.vus-exceeded")]
    [InlineData("    vus: 10\n    duration: 30s", "    vus: 10\n    duration: 20m", "load.duration-exceeded")]
    [InlineData("environment: lab", "environment: production", "target.production")]
    [InlineData("environment: lab", "environment: orders-prod", "target.production")]
    [InlineData("project: lab/Api/Api.csproj", "project: ../outside/Api.csproj", "path.outside-allowed-roots")]
    [InlineData("project: lab/Api/Api.csproj", "project: lab/Api/Missing.csproj", "api.project-not-found")]
    [InlineData("    path: /api/products/{id}", "    path: https://evil.example.com/x", "endpoints.path")]
    [InlineData("    method: GET", "    method: POST", "endpoints.write-not-authorized")]
    [InlineData("    Lab__Faults__Cpu: \"true\"", "    DOTNET_STARTUP_HOOKS: \"evil.dll\"", "settings.reserved")]
    [InlineData("    Lab__Faults__Cpu: \"true\"", "    ASPNETCORE_URLS: \"http://0.0.0.0:80\"", "settings.reserved")]
    [InlineData("    Lab__Faults__Cpu: \"true\"", "    Payments__ApiKey: \"abc\"", "settings.secret-key")]
    [InlineData("    Lab__Faults__Cpu: \"true\"", "    Upstream__Url: \"https://user:p4ss@host/x\"", "settings.secret-value")]
    [InlineData("    http_req_duration: ['p(95)<500']", "    http_req_duration: [\"p(95)<500'); require('fs'); //\"]", "load.threshold")]
    [InlineData("profile: local-isolated", "profile: production-like", "manifest.profile")]
    public void OutOfScopeOrOverLimitManifestsAreRejectedWithReason(string original, string replacement, string expectedCode)
    {
        var yaml = TestWorkspace.ValidManifest.Replace(original, replacement, StringComparison.Ordinal);
        Assert.NotEqual(TestWorkspace.ValidManifest, yaml);

        var result = _ws.Validate(yaml);

        Assert.False(result.Decision.Allowed);
        Assert.Null(result.Manifest);
        var violation = Assert.Single(result.Decision.Violations, v => v.Code == expectedCode);
        Assert.False(string.IsNullOrWhiteSpace(violation.Message));
    }

    [Fact]
    public void RemoteDependencyRequiresHybridProfileAndAllowlistedHost()
    {
        var yaml = TestWorkspace.ValidManifest.Replace(
            "    mode: mock\n    contract: lab/contracts/pricing.json",
            "    mode: remote\n    url: https://pricing.dev.example.com\n    healthPath: /health",
            StringComparison.Ordinal);

        var result = _ws.Validate(yaml);

        Assert.Contains(result.Decision.Violations, v => v.Code == "dependencies.remote-in-isolated");
        Assert.Contains(result.Decision.Violations, v => v.Code == "dependencies.remote-host-not-allowed");
    }

    [Fact]
    public void RemoteDependencyAllowedWhenHybridAndAllowlisted()
    {
        using var ws = new TestWorkspace(new SafetyPolicy { AllowedProjectRoots = ["lab"], AllowedRemoteDependencyHosts = ["*.dev.example.com"] });
        var yaml = TestWorkspace.ValidManifest
            .Replace("profile: local-isolated", "profile: local-hybrid", StringComparison.Ordinal)
            .Replace("    mode: mock\n    contract: lab/contracts/pricing.json", "    mode: remote\n    url: https://pricing.dev.example.com", StringComparison.Ordinal);

        var result = ws.Validate(yaml);

        Assert.True(result.Decision.Allowed, result.Decision.Explain());
    }

    [Fact]
    public void RemoteUrlWithCredentialsOrProductionHostIsRejected()
    {
        using var ws = new TestWorkspace(new SafetyPolicy { AllowedProjectRoots = ["lab"], AllowedRemoteDependencyHosts = ["*.example.com"] });
        var yaml = TestWorkspace.ValidManifest
            .Replace("profile: local-isolated", "profile: local-hybrid", StringComparison.Ordinal)
            .Replace("    mode: mock\n    contract: lab/contracts/pricing.json", "    mode: remote\n    url: https://svc:secret@pricing-prod.example.com", StringComparison.Ordinal);

        var result = ws.Validate(yaml);

        Assert.Contains(result.Decision.Violations, v => v.Code == "dependencies.url-credentials");
        Assert.Contains(result.Decision.Violations, v => v.Code == "target.production");
    }

    [Fact]
    public void MemoryDumpsAreDisabledInMvp()
    {
        var result = _ws.Validate(TestWorkspace.ValidManifest + "\ndiagnostics:\n  memoryDump: approval-required\n");

        Assert.Contains(result.Decision.Violations, v => v.Code == "diagnostics.dumps");
    }

    [Fact]
    public void WritesNeedBothPolicyAndManifestAuthorization()
    {
        using var ws = new TestWorkspace(new SafetyPolicy { AllowedProjectRoots = ["lab"], WriteOperations = "allow-declared" });
        var yaml = TestWorkspace.ValidManifest.Replace("    method: GET", "    method: POST\n    body: '{\"productId\":1,\"quantity\":1}'", StringComparison.Ordinal);

        Assert.Contains(ws.Validate(yaml).Decision.Violations, v => v.Code == "endpoints.write-not-authorized");
        var authorized = ws.Validate(yaml + "\nsafety:\n  writeOperations: allow-declared\n");
        Assert.True(authorized.Decision.Allowed, authorized.Decision.Explain());
    }

    [Fact]
    public void UnknownManifestFieldsAreRejected()
    {
        var ex = Assert.Throws<ManifestFormatException>(() => ManifestLoader.Parse(TestWorkspace.ValidManifest + "\nunexpectedField: 1\n"));
        Assert.Contains("unexpectedField", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://127.0.0.1:5000", 5000, true, null)]
    [InlineData("http://10.0.0.5:5000", 5000, false, "target.host-not-allowed")]
    [InlineData("http://localhost:5000", 5000, false, "target.host-not-allowed")]
    [InlineData("http://127.0.0.1:5001", 5000, false, "target.port-mismatch")]
    [InlineData("ftp://127.0.0.1:5000", 5000, false, "target.scheme")]
    public void LoadTargetMustBeTheLabInstanceOnLoopback(string url, int port, bool allowed, string? code)
    {
        var decision = _ws.Engine.EvaluateLoadTarget(new Uri(url), port);

        Assert.Equal(allowed, decision.Allowed);
        if (code is not null)
        {
            Assert.Contains(decision.Violations, v => v.Code == code);
        }
    }

    [Theory]
    [InlineData("cpu-sample", 10, true)]
    [InlineData("gc-events", 60, true)]
    [InlineData("gc-events", 61, false)]
    [InlineData("dump", 10, false)]
    [InlineData("heap", 10, false)]
    [InlineData("method-params", 10, false)]
    public void RuntimeCapturesFollowDiagnosticsPolicy(string kind, int seconds, bool allowed) =>
        Assert.Equal(allowed, _ws.Engine.EvaluateCapture(kind, seconds).Allowed);

    [Theory]
    [InlineData("http_req_duration", "p(95)<500", true)]
    [InlineData("http_req_duration{name:cpu}", "p(99)<=1000", true)]
    [InlineData("http_req_failed", "rate<0.01", true)]
    [InlineData("checks", "rate==1", true)]
    [InlineData("http_req_duration", "p(95)<500'] }; require('fs'); //", false)]
    [InlineData("http_req_duration']; evil(); //", "p(95)<500", false)]
    public void ThresholdsAreStrictlyValidatedBecauseTheyAreEmbeddedInTheScript(string metric, string expression, bool valid) =>
        Assert.Equal(valid, PolicyEngine.IsValidThreshold(metric, expression));

    [Theory]
    [InlineData("/api/items", true)]
    [InlineData("/api/items?page=1", true)]
    [InlineData("api/items", false)]
    [InlineData("//evil.example.com/x", false)]
    [InlineData("/api/../secret", false)]
    [InlineData("/x\\y", false)]
    [InlineData("/redirect?to=https://evil", false)]
    public void OnlySafeRelativePathsAreAccepted(string path, bool safe) =>
        Assert.Equal(safe, PolicyEngine.IsSafeRelativePath(path));
}
