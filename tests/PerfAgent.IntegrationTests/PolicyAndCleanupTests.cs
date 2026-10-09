using System.Diagnostics;
using PerfAgent.Core.Contracts;
using PerfAgent.Mcp.Lab;

namespace PerfAgent.IntegrationTests;

/// <summary>
/// Pasos 1, 4 y 5 del MVP sobre un workspace temporal con política propia: los rechazos ocurren antes de iniciar
/// procesos o generar tráfico, los fallos de ambiente se clasifican y el laboratorio queda limpio tras éxito, error
/// y cancelación.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PolicyAndCleanupTests : IAsyncLifetime
{
    private const string Base = """
        schemaVersion: 1
        profile: local-isolated
        environment: lab
        api:
          project: {{project}}
          healthPath: /health
          readinessTimeoutSeconds: 30
        endpoints:
          - name: baseline
            method: GET
            path: /api/baseline
            bodyContains: status
        load:
          endpointSource: declared
          smoke:
            vus: 1
            iterations: 3
            maxDuration: 20s
          baseline:
            executor: constant-vus
            vus: 4
            duration: 6s
        """;

    private readonly TempLabWorkspace _ws = new();
    private LabService _lab = null!;

    public ValueTask InitializeAsync()
    {
        _lab = Repo.CreateLab(_ws.Root);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _lab.DisposeAsync();
        _ws.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("environment: lab", "environment: production", "target.production")]
    [InlineData("    vus: 4\n    duration: 6s", "    vus: 500\n    duration: 6s", "load.vus-exceeded")]
    [InlineData("    path: /api/baseline", "    path: http://example.com/", "endpoints.path")]
    [InlineData("    method: GET", "    method: DELETE", "endpoints.write-not-authorized")]
    public async Task OutOfScopeRequestsAreRejectedBeforeAnyProcessStarts(string original, string replacement, string code)
    {
        var manifest = _ws.WriteManifest($"reject-{code}.yaml", Base.Replace(original, replacement, StringComparison.Ordinal));

        var result = await _lab.PreflightAsync(manifest, Ct);

        Assert.Equal("blocked", result.Status);
        Assert.Null(result.RunId);
        Assert.Contains(result.Violations, v => v.Code == code);
        Assert.Empty(_lab.Registry.List());
    }

    [Fact]
    public async Task UnreachableRequiredDependencyBlocksTheDiagnosisWithoutLoad()
    {
        Repo.RequirePrerequisites();
        var yaml = Base.Replace("profile: local-isolated", "profile: local-hybrid", StringComparison.Ordinal) + """

            dependencies:
              inventory-api:
                mode: remote
                required: true
                url: https://unreachable.perflab.invalid
                healthPath: /health
                injectAs: Dependencies__Inventory__BaseUrl
            """;
        var manifest = _ws.WriteManifest("unreachable.yaml", yaml);

        var preflight = await _lab.PreflightAsync(manifest, Ct);

        Assert.Equal("blocked", preflight.Status);
        Assert.NotNull(preflight.RunId);
        Assert.Equal(RunPhase.PreflightBlocked, preflight.Data!.Phase);
        var dependency = Assert.Single(preflight.Data.Environment!.Dependencies);
        Assert.Equal(FidelityStatus.Unavailable, dependency.Fidelity);
        Assert.Contains(dependency.Checks, c => c is { Name: "dns", Outcome: CheckOutcome.Failed });

        var start = await _lab.StartLabAsync(preflight.RunId!, null, Ct);
        Assert.Equal("blocked", start.Status);
        var report = await _lab.GenerateReportAsync(preflight.RunId!, Ct);
        Assert.Equal(DiagnosisVerdict.EnvironmentFailure, report.Data!.Verdict);
        Assert.Empty(_lab.Registry.List());
    }

    [Fact]
    public async Task WrongHealthPathIsAConfigurationFailureAndLeavesNoProcesses()
    {
        Repo.RequirePrerequisites();
        var manifest = _ws.WriteManifest("bad-health.yaml", Base
            .Replace("healthPath: /health", "healthPath: /no-such-health", StringComparison.Ordinal)
            .Replace("readinessTimeoutSeconds: 30", "readinessTimeoutSeconds: 8", StringComparison.Ordinal));
        var preflight = await _lab.PreflightAsync(manifest, Ct);
        Assert.Equal("ok", preflight.Status);

        var start = await _lab.StartLabAsync(preflight.RunId!, null, Ct);

        Assert.Equal("failed", start.Status);
        var run = LabFixture.LoadRun(preflight.RunId!, _ws.Root);
        Assert.Equal(FailureKind.ConfigurationError, run.Failure);
        Assert.Contains("404", run.FailureDetail, StringComparison.Ordinal);
        ProcessAssertions.NoOrphans(_lab, preflight.RunId);
        Assert.Null(_lab.ActiveRunId);
    }

    [Fact]
    public async Task LoadFollowsTheCostProgressionAndRejectsTamperedScripts()
    {
        Repo.RequirePrerequisites();
        var manifest = _ws.WriteManifest("progression.yaml", Base);
        var preflight = await _lab.PreflightAsync(manifest, Ct);
        var runId = preflight.RunId!;
        try
        {
            Assert.Equal("ok", (await _lab.StartLabAsync(runId, null, Ct)).Status);

            var baselineFirst = await _lab.RunLoadTestAsync(runId, LoadTestKind.Baseline, confirm: false, null, Ct);
            Assert.Equal("blocked", baselineFirst.Status);
            Assert.Contains(baselineFirst.Violations, v => v.Code == "load.smoke-required");

            var scenario = await _lab.GenerateScenarioAsync(runId, LoadTestKind.Smoke, Ct);
            var scriptPath = Path.Combine(_ws.Root, scenario.Data!.ScriptPath);
            await File.AppendAllTextAsync(scriptPath, "\n// cambio manual\n", Ct);

            var tampered = await _lab.RunLoadTestAsync(runId, LoadTestKind.Smoke, confirm: false, null, Ct);
            Assert.Equal("blocked", tampered.Status);
            Assert.Contains(tampered.Violations, v => v.Code == "k6.tampered");

            await _lab.GenerateScenarioAsync(runId, LoadTestKind.Smoke, Ct);
            var smoke = await _lab.RunLoadTestAsync(runId, LoadTestKind.Smoke, confirm: false, null, Ct);
            Assert.True(smoke.Status == "ok", smoke.Summary);
            Assert.Equal(LoadTestStatus.Passed, smoke.Data!.LoadTest.Status);
        }
        finally
        {
            await _lab.StopLabAsync(runId, CancellationToken.None);
        }

        ProcessAssertions.NoOrphans(_lab, runId);
    }

    [Fact]
    public async Task ProfileConfirmationIsRequiredBeforeLoad()
    {
        Repo.RequirePrerequisites();
        var manifest = _ws.WriteManifest("confirm.yaml", Base + "\nsafety:\n  requireConfirmation: [smoke]\n");
        var preflight = await _lab.PreflightAsync(manifest, Ct);
        var runId = preflight.RunId!;
        try
        {
            await _lab.StartLabAsync(runId, null, Ct);

            var pending = await _lab.RunLoadTestAsync(runId, LoadTestKind.Smoke, confirm: false, null, Ct);
            Assert.Equal("confirmation-required", pending.Status);
            Assert.Contains("127.0.0.1", pending.Summary, StringComparison.Ordinal);

            var confirmed = await _lab.RunLoadTestAsync(runId, LoadTestKind.Smoke, confirm: true, null, Ct);
            Assert.Equal("ok", confirmed.Status);
        }
        finally
        {
            await _lab.StopLabAsync(runId, CancellationToken.None);
        }
    }

    [Fact]
    public async Task CancellationDuringTheDiagnosisCleansUpEverything()
    {
        Repo.RequirePrerequisites();
        var manifest = _ws.WriteManifest("cancel.yaml", Base.Replace("    duration: 6s", "    duration: 40s", StringComparison.Ordinal));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var reachedBaseline = new TaskCompletionSource();
        var progress = new SynchronousProgress(message =>
        {
            if (message.Contains("baseline", StringComparison.Ordinal))
            {
                reachedBaseline.TrySetResult();
            }
        });

        var diagnosis = _lab.RunDiagnosisAsync(manifest, confirm: false, maxFollowUps: 0, progress, cts.Token);
        await reachedBaseline.Task.WaitAsync(TimeSpan.FromMinutes(3), Ct);
        await Task.Delay(TimeSpan.FromSeconds(5), Ct);
        await cts.CancelAsync();
        var result = await diagnosis;

        Assert.Equal("failed", result.Status);
        Assert.Contains(result.Data!.Steps, s => s.Step == "cancel");
        var run = LabFixture.LoadRun(result.RunId!, _ws.Root);
        Assert.NotNull(run.Cleanup);
        Assert.False(ProcessAssertions.IsAlive(run.Api?.ProcessId));
        ProcessAssertions.NoOrphans(_lab, run.RunId);
        Assert.True(await NoLabK6ProcessesAsync(), "k6 siguió en ejecución tras la cancelación.");
    }

    private static async Task<bool> NoLabK6ProcessesAsync()
    {
        for (var i = 0; i < 20; i++)
        {
            var running = Process.GetProcessesByName("k6").Where(p =>
            {
                try
                {
                    return p.MainModule?.FileName?.StartsWith(Path.Combine(Repo.Root, ".tools"), StringComparison.OrdinalIgnoreCase) == true;
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    return false;
                }
            }).ToList();
            if (running.Count == 0)
            {
                return true;
            }

            await Task.Delay(500);
        }

        return false;
    }

    private sealed class SynchronousProgress(Action<string> handler) : IProgress<string>
    {
        public void Report(string value) => handler(value);
    }
}
