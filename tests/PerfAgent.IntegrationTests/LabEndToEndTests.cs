using System.Globalization;
using PerfAgent.Adapters.DotNetDiagnosticsMcp;
using PerfAgent.Adapters.K6Mcp;
using PerfAgent.Core;
using PerfAgent.Core.Contracts;
using PerfAgent.Core.Runs;
using PerfAgent.Core.Triage;
using PerfAgent.Mcp.Lab;

namespace PerfAgent.IntegrationTests;

public sealed class LabFixture : IAsyncLifetime
{
    public LabService Lab { get; private set; } = null!;

    public ValueTask InitializeAsync()
    {
        Lab = Repo.CreateLab();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await Lab.DisposeAsync();

    public static LabRun LoadRun(string runId, string? workspaceRoot = null) =>
        PerfJson.Deserialize<LabRun>(File.ReadAllText(Path.Combine(workspaceRoot ?? Repo.Root, "artifacts", "runs", runId, "run.json")))!;
}

/// <summary>
/// Pasos 8–10 del MVP: con una sola orden, cada fallo conocido del laboratorio produce el síntoma esperado y una
/// causa confirmada con evidencia discriminante (o "sin degradación" en el control sano), delegando realmente en
/// los proveedores comunitarios fijados y dejando el ambiente limpio.
/// </summary>
[Trait("Category", "E2E")]
public sealed class LabEndToEndTests(LabFixture fixture) : IClassFixture<LabFixture>
{
    public static TheoryData<string, DiagnosisVerdict, string?, string?> Scenarios => new()
    {
        { "scenarios/lab-cpu.yaml", DiagnosisVerdict.CauseConfirmed, "CPU-001", "CountPrimesNaive" },
        { "scenarios/lab-blocking.yaml", DiagnosisVerdict.CauseConfirmed, "TP-001", "BlockThread" },
        { "scenarios/lab-alloc.yaml", DiagnosisVerdict.CauseConfirmed, "GC-001", "BuildReportWithExcessiveAllocations" },
        { "scenarios/lab-dependency-nplus1.yaml", DiagnosisVerdict.CauseConfirmed, "DEP-001", "secuenciales" },
        { "scenarios/lab-dependency-errors.yaml", DiagnosisVerdict.CauseConfirmed, "ERR-002", "propaga" },
        { "scenarios/local-baseline.yaml", DiagnosisVerdict.NoDegradationDetected, null, null },
    };

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task KnownFaultIsDiagnosedWithEvidenceAndTheLabIsCleanedUp(string manifest, DiagnosisVerdict verdict, string? rule, string? fragment)
    {
        Repo.RequirePrerequisites();

        var result = await fixture.Lab.RunDiagnosisAsync(manifest, confirm: false, maxFollowUps: 2, progress: null, TestContext.Current.CancellationToken);

        Assert.True(result.Status == "ok", result.Summary);
        Assert.Equal(verdict, result.Data!.Verdict);
        var run = LabFixture.LoadRun(result.RunId!);

        if (rule is not null)
        {
            var hypothesis = Assert.Single(run.Hypotheses, h => h.RuleId == rule);
            Assert.Equal(HypothesisStatus.Confirmed, hypothesis.Status);
            Assert.Contains(fragment!, hypothesis.Statement, StringComparison.Ordinal);
        }

        // Nunca se afirma una causa sin evidencia: toda hipótesis confirmada cita evidencia, y ninguna sin síntomas.
        Assert.All(run.Hypotheses.Where(h => h.Status == HypothesisStatus.Confirmed), h => Assert.NotEmpty(h.SupportingEvidence));
        if (verdict == DiagnosisVerdict.NoDegradationDetected)
        {
            Assert.DoesNotContain(run.Hypotheses, h => h.Status is HypothesisStatus.Confirmed or HypothesisStatus.Supported);
        }

        // Delegación real en los proveedores comunitarios, con el PID del proceso de laboratorio.
        var pid = run.Api!.ProcessId!.Value.ToString(CultureInfo.InvariantCulture);
        Assert.Contains(run.ProviderInvocations, p => p is { Provider: K6Executor.ProviderName, Tool: "run_script", IsFallback: false, Success: true });

        // mcp-k6 recibió exactamente la carga validada por la política.
        var baselineLoad = run.LatestLoadTest(LoadTestKind.Baseline)!;
        Assert.Equal(baselineLoad.Load.Vus.ToString(CultureInfo.InvariantCulture), baselineLoad.Provider!.Arguments["vus"]);
        Assert.Equal($"{baselineLoad.Load.DurationSeconds}s", baselineLoad.Provider.Arguments["duration"]);
        Assert.StartsWith("sha256:", baselineLoad.Provider.Arguments["script"], StringComparison.Ordinal);
        Assert.Contains(run.ProviderInvocations, p => p is { Provider: K6Executor.ProviderName, Tool: "validate_script" });
        Assert.Contains(run.ProviderInvocations, p => p.Provider == DiagnosticsCatalog.ProviderName && p.Tool == "collect_batch" && p.Arguments.GetValueOrDefault("processId") == pid);
        Assert.All(run.ProviderInvocations.Where(p => p.Provider == DiagnosticsCatalog.ProviderName && p.Arguments.ContainsKey("processId")),
            p => Assert.Equal(pid, p.Arguments["processId"]));

        // Métricas del runtime correlacionadas con la ventana de k6.
        var baseline = run.LatestLoadTest(LoadTestKind.Baseline)!;
        var counters = Assert.Single(run.RuntimeSnapshots, s => s.Id == baseline.RuntimeSnapshotId);
        Assert.True(TriageEngine.Correlate(baseline, counters, 0.8).OverlapRatio >= 0.8, "La captura del runtime no cubre la ventana de carga.");
        Assert.NotNull(counters.Summary?.CpuPercentAvg);
        Assert.NotNull(baseline.Latency?.P95);

        // Fidelidad explícita de dependencias simuladas.
        if (manifest.Contains("dependency", StringComparison.Ordinal))
        {
            Assert.Contains(run.Environment!.Dependencies, d => d.Fidelity == FidelityStatus.Simulated);
        }

        // Ambiente limpio e informe reproducible.
        Assert.NotNull(run.Cleanup);
        Assert.Empty(run.Cleanup!.FailedToStop);
        Assert.False(ProcessAssertions.IsAlive(run.Api.ProcessId));
        ProcessAssertions.NoOrphans(fixture.Lab, run.RunId);
        Assert.True(File.Exists(Path.Combine(Repo.Root, result.Data.Report!.MarkdownPath)));
        Assert.True(File.Exists(Path.Combine(Repo.Root, result.Data.Report.JsonPath)));
    }

    [Fact]
    public async Task FixedVersionIsComparableAndMeasurablyBetter()
    {
        Repo.RequirePrerequisites();
        var ct = TestContext.Current.CancellationToken;

        var faulty = await fixture.Lab.RunDiagnosisAsync("scenarios/lab-cpu.yaml", false, 0, null, ct);
        var fixedRun = await fixture.Lab.RunDiagnosisAsync("scenarios/lab-cpu-fixed.yaml", false, 0, null, ct);
        var comparison = await fixture.Lab.CompareRunsAsync(faulty.RunId!, fixedRun.RunId!, ct);

        Assert.Equal("ok", comparison.Status);
        Assert.True(comparison.Data!.Comparable, comparison.Summary);
        Assert.Equal("better", comparison.Data.Deltas.Single(d => d.Metric == "p95").Direction);
        Assert.Equal("better", comparison.Data.Deltas.Single(d => d.Metric == "throughput").Direction);
        Assert.Contains(comparison.Data.ChangesUnderEvaluation, c => c.Contains("Manifiesto", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(Repo.Root, "artifacts", "runs", fixedRun.RunId!, "comparisons", $"{faulty.RunId}-vs-{fixedRun.RunId}.md")));
    }
}
