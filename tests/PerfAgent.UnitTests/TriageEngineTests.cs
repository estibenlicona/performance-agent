using PerfAgent.Core.Contracts;
using PerfAgent.Core.Runs;
using PerfAgent.Core.Triage;

namespace PerfAgent.UnitTests;

/// <summary>
/// Paso 9 del MVP: reglas interpretables que distinguen síntoma observado de causa confirmada y terminan en
/// <c>inconclusive</c> cuando falta evidencia. Incluye regresiones de falsos diagnósticos detectados en el laboratorio.
/// </summary>
public sealed class TriageEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 9, 6, 0, 0, TimeSpan.Zero);

    private static LabRun NewRun(params DependencyCheck[] dependencies) => new()
    {
        RunId = "20261009-060000-abcdef",
        ManifestPath = "scenarios/test.yaml",
        Environment = new EnvironmentProfile
        {
            Kind = EnvironmentProfileKind.LocalIsolated,
            EnvironmentLabel = "lab",
            Dependencies = dependencies,
            Decision = PreflightDecision.Continue,
        },
    };

    private static LoadTestRun Load(
        LoadTestKind kind, double p95, double avg, long total, double rps, int vus = 10, double errorRate = 0,
        Dictionary<string, long>? classes = null, IReadOnlyList<DependencyCallStats>? calls = null, long checksFailed = 0) => new()
    {
        Id = kind == LoadTestKind.Smoke ? "smoke-1" : "baseline-1",
        Kind = kind,
        Load = kind == LoadTestKind.Smoke ? new LoadProfile("shared-iterations", 1, 10, 30, 0) : new LoadProfile("constant-vus", vus, null, 20, 0),
        StartedAt = T0,
        EndedAt = T0.AddSeconds(20),
        Status = checksFailed > 0 && kind == LoadTestKind.Smoke ? LoadTestStatus.FunctionalFailure : LoadTestStatus.Passed,
        Latency = new LatencyStats(avg, 0.5, avg * 0.9, p95 * 0.9, p95, p95 * 1.2, p95 * 2),
        TotalRequests = total,
        RequestsPerSecond = rps,
        FailedRequests = (long)(total * errorRate),
        ErrorRate = errorRate,
        ChecksPassed = total - checksFailed,
        ChecksFailed = checksFailed,
        StatusClasses = classes ?? new Dictionary<string, long> { ["2xx"] = total },
        DependencyCalls = calls ?? [],
        Endpoints = [new EndpointStats("ep", "GET", "/api/x", total, errorRate, new LatencyStats(avg, null, null, null, p95, null, null), total - checksFailed, checksFailed)],
        Artifacts = [$"artifacts/runs/x/k6/{kind.ToString().ToLowerInvariant()}-1.summary.json"],
    };

    private static RuntimeSnapshot Counters(RuntimeSummary summary) => new()
    {
        Id = "rt-1-counters",
        ProcessId = 1234,
        Kind = "counters",
        LoadTestId = "baseline-1",
        StartedAt = T0.AddSeconds(-1),
        EndedAt = T0.AddSeconds(19),
        Summary = summary,
        Artifacts = ["artifacts/runs/x/runtime/rt-1-counters.json"],
    };

    private static RuntimeSnapshot Sample(string kind, IReadOnlyList<HotSpot> spots, IReadOnlyList<HotSpot>? path = null) => new()
    {
        Id = $"rt-2-{kind}",
        ProcessId = 1234,
        Kind = kind,
        StartedAt = T0.AddSeconds(30),
        EndedAt = T0.AddSeconds(40),
        HotSpots = spots,
        HotPath = path ?? [],
        Artifacts = [$"artifacts/runs/x/runtime/rt-2-{kind}.json"],
    };

    private static readonly RuntimeSummary CpuBound = new()
    {
        CpuPercentAvg = 84, CpuPercentMax = 98, ProcessorCount = 4, ThreadPoolQueueLengthLast = 1, ThreadPoolQueueLengthMax = 3,
        ThreadPoolThreadsFirst = 13, ThreadPoolThreadsMax = 13, ThreadPoolStarvationAdjustments = 0, AllocationRateMbPerSec = 15,
        TimeInGcPercentMax = 0, Gen2Collections = 0, LockContentionsPerSec = 0,
    };

    private static readonly RuntimeSummary Blocking = new()
    {
        CpuPercentAvg = 2, CpuPercentMax = 6, ProcessorCount = 4, ThreadPoolQueueLengthLast = 0, ThreadPoolQueueLengthMax = 23,
        ThreadPoolThreadsFirst = 5, ThreadPoolThreadsMax = 33, ThreadPoolStarvationAdjustments = 19, ThreadPoolCooperativeBlockingAdjustments = 59,
        AllocationRateMbPerSec = 4, TimeInGcPercentMax = 0, Gen2Collections = 0, LockContentionsPerSec = 10,
    };

    private static HotSpot User(string frame, double inclusive, double exclusive = 0) => new(frame, "PerfLab.Api.dll", inclusive, exclusive, IsUserCode: true);

    private static HotSpot Bcl(string frame, double inclusive, double exclusive = 0, bool blocking = false) => new(frame, "System.Private.CoreLib.dll", inclusive, exclusive, IsUserCode: false, blocking);

    private static Hypothesis Rule(TriageResult r, string ruleId) => Assert.Single(r.Hypotheses, h => h.RuleId == ruleId);

    [Fact]
    public void BlockedPreflightIsAnEnvironmentFailureWithoutPerformanceHypotheses()
    {
        var run = NewRun();
        run.Environment = run.Environment! with { Decision = PreflightDecision.Blocked, BlockingReasons = ["DNS falló para inventory.dev"] };

        var result = TriageEngine.Analyze(run);

        Assert.Equal(DiagnosisVerdict.EnvironmentFailure, result.Verdict);
        Assert.Empty(result.Hypotheses);
    }

    [Fact]
    public void BuildFailureIsNotReportedAsAPerformanceProblem()
    {
        var run = NewRun();
        run.Fail(FailureKind.BuildFailed, "error CS1002");

        var result = TriageEngine.Analyze(run);

        Assert.Equal(DiagnosisVerdict.EnvironmentFailure, result.Verdict);
        Assert.Empty(result.Hypotheses);
        Assert.Contains(result.Observations, o => o.Category == "environment");
    }

    [Fact]
    public void FunctionalSmokeFailureStopsBeforeLoad()
    {
        var run = NewRun();
        run.LoadTests.Add(Load(LoadTestKind.Smoke, 10, 8, 10, 100, checksFailed: 10));

        var result = TriageEngine.Analyze(run);

        Assert.Equal(DiagnosisVerdict.FunctionalFailure, result.Verdict);
        Assert.Equal(HypothesisStatus.Supported, Rule(result, "FUNC-001").Status);
    }

    [Fact]
    public void CpuSaturationIsSupportedButNotConfirmedWithoutASample()
    {
        var run = NewRun();
        run.LoadTests.Add(Load(LoadTestKind.Smoke, 10, 8, 10, 150));
        run.LoadTests.Add(Load(LoadTestKind.Baseline, 40, 22, 14000, 700, vus: 16));
        run.RuntimeSnapshots.Add(Counters(CpuBound));

        var result = TriageEngine.Analyze(run);

        var cpu = Rule(result, "CPU-001");
        Assert.Equal(HypothesisStatus.Supported, cpu.Status);
        Assert.Equal("collect_runtime_evidence", cpu.NextAction?.Tool);
        Assert.Equal("cpu-sample", cpu.NextAction?.Arguments["kind"]);
        Assert.NotEmpty(cpu.MissingEvidence);
        Assert.Equal(DiagnosisVerdict.SymptomsObserved, result.Verdict);
        Assert.Contains(result.Observations, o => o.Category == "latency");
    }

    [Fact]
    public void CpuSampleWithADominantUserMethodConfirmsTheCause()
    {
        var run = NewRun();
        run.LoadTests.Add(Load(LoadTestKind.Smoke, 10, 8, 10, 150));
        run.LoadTests.Add(Load(LoadTestKind.Baseline, 40, 22, 14000, 700, vus: 16));
        run.RuntimeSnapshots.Add(Counters(CpuBound));
        run.RuntimeSnapshots.Add(Sample("cpu-sample",
            [Bcl("System.Threading.LowLevelLifoSemaphore.WaitForSignal(int32)", 32.5, 32.5, blocking: true), User("PerfLab.Api.CpuReportService.CountPrimesNaive(int32)", 15.9, 15.9)],
            [Bcl("System.Threading.Thread.StartCallback()", 97), Bcl("System.Threading.PortableThreadPool+WorkerThread.WorkerThreadStart()", 61), Bcl("System.Threading.LowLevelLifoSemaphore.WaitForSignal(int32)", 31.5, 31.5, blocking: true)]));

        var result = TriageEngine.Analyze(run);

        // Regresión: las esperas ociosas del ThreadPool no deben anular la confirmación de CPU.
        var cpu = Rule(result, "CPU-001");
        Assert.Equal(HypothesisStatus.Confirmed, cpu.Status);
        Assert.Contains("CountPrimesNaive", cpu.Statement, StringComparison.Ordinal);
        Assert.Equal(DiagnosisVerdict.CauseConfirmed, result.Verdict);
        Assert.Equal(HypothesisStatus.Contradicted, Rule(result, "TP-001").Status);
        Assert.NotEmpty(result.Recommendations);
    }

    [Fact]
    public void ThreadPoolStarvationWithLowCpuIsSupportedWithHighConfidence()
    {
        var run = NewRun();
        run.LoadTests.Add(Load(LoadTestKind.Smoke, 112, 105, 5, 9));
        run.LoadTests.Add(Load(LoadTestKind.Baseline, 2548, 900, 1332, 66, vus: 24));
        run.RuntimeSnapshots.Add(Counters(Blocking));

        var result = TriageEngine.Analyze(run);

        var tp = Rule(result, "TP-001");
        Assert.Equal(HypothesisStatus.Supported, tp.Status);
        Assert.Equal(Confidence.High, tp.Confidence);
        Assert.Contains(tp.SupportingEvidence, e => e.Signal == "threadpool.starvation_adjustments");
        Assert.Contains(tp.SupportingEvidence, e => e.Signal == "threadpool.cooperative_blocking_adjustments");
        Assert.Equal(HypothesisStatus.Contradicted, Rule(result, "CPU-001").Status);
    }

    [Fact]
    public void HotPathFromUserCodeToABlockingWaitConfirmsStarvation()
    {
        var run = NewRun();
        run.LoadTests.Add(Load(LoadTestKind.Smoke, 112, 105, 5, 9));
        run.LoadTests.Add(Load(LoadTestKind.Baseline, 2548, 900, 1332, 66, vus: 24));
        run.RuntimeSnapshots.Add(Counters(Blocking));
        run.RuntimeSnapshots.Add(Sample("cpu-sample",
            [Bcl("System.Threading.Tasks.Task.InternalWaitCore(int32,value class System.Threading.CancellationToken)", 61, 58, blocking: true)],
            [
                Bcl("System.Threading.Thread.StartCallback()", 97),
                User("PerfLab.Api.LabEndpoints+<>c+<<MapLabEndpoints>b__0_4>d.MoveNext()", 58.7),
                User("PerfLab.Api.BlockingReportService.GetReportAsync (async)", 58.6),
                User("PerfLab.Api.BlockingReportService.BlockThread(value class System.Threading.CancellationToken)", 58.6),
                Bcl("System.Threading.Tasks.Task.InternalWait(int32,value class System.Threading.CancellationToken)", 58.6, blocking: true),
                Bcl("System.Threading.Tasks.Task.InternalWaitCore(int32,value class System.Threading.CancellationToken)", 58.6, 58.6, blocking: true),
            ]));

        var result = TriageEngine.Analyze(run);

        var tp = Rule(result, "TP-001");
        Assert.Equal(HypothesisStatus.Confirmed, tp.Status);
        Assert.Contains("BlockThread", tp.Statement, StringComparison.Ordinal);
        Assert.DoesNotContain("MapLabEndpoints", tp.Statement, StringComparison.Ordinal);
        Assert.Equal(DiagnosisVerdict.CauseConfirmed, result.Verdict);
    }

    [Fact]
    public void IdleThreadPoolWaitsDoNotConfirmBlocking()
    {
        var run = NewRun();
        run.LoadTests.Add(Load(LoadTestKind.Smoke, 112, 105, 5, 9));
        run.LoadTests.Add(Load(LoadTestKind.Baseline, 2548, 900, 1332, 66, vus: 24));
        run.RuntimeSnapshots.Add(Counters(Blocking));
        run.RuntimeSnapshots.Add(Sample("cpu-sample",
            [Bcl("System.Threading.LowLevelLifoSemaphore.WaitForSignal(int32)", 40, 40, blocking: true), User("PerfLab.Api.Other.Work()", 20, 0)],
            [Bcl("System.Threading.Thread.StartCallback()", 97), Bcl("System.Threading.LowLevelLifoSemaphore.WaitForSignal(int32)", 40, 40, blocking: true)]));

        var result = TriageEngine.Analyze(run);

        Assert.Equal(HypothesisStatus.Supported, Rule(result, "TP-001").Status);
    }

    [Fact]
    public void ExcessiveAllocationsAreACapacitySymptomEvenWithoutLatencyDegradation()
    {
        var run = NewRun();
        run.LoadTests.Add(Load(LoadTestKind.Smoke, 7.9, 6, 10, 178));
        run.LoadTests.Add(Load(LoadTestKind.Baseline, 16.7, 8, 18389, 919, vus: 8));
        run.RuntimeSnapshots.Add(Counters(CpuBound with { CpuPercentAvg = 43, AllocationRateMbPerSec = 5238, TimeInGcPercentMax = 11, Gen2Collections = 2092 }));

        var supported = TriageEngine.Analyze(run);
        var gc = Rule(supported, "GC-001");
        Assert.Equal(HypothesisStatus.Supported, gc.Status);
        Assert.Contains(supported.Observations, o => o.Category == "capacity");
        Assert.Equal("gc-events", gc.NextAction?.Arguments["kind"]);

        run.RuntimeSnapshots.Add(Sample("gc-events", [User("PerfLab.Api.AllocationReportService.BuildReportWithExcessiveAllocations()", 99.5, 99.5)]));
        var confirmed = TriageEngine.Analyze(run);

        Assert.Equal(HypothesisStatus.Confirmed, Rule(confirmed, "GC-001").Status);
        Assert.Equal(DiagnosisVerdict.CauseConfirmed, confirmed.Verdict);
    }

    [Fact]
    public void NormalAllocationAtHighThroughputIsNotGcPressure()
    {
        // Regresión: 177 MB/s a ~10.000 req/s son ~17 KB por petición (normal en ASP.NET Core).
        var run = NewRun();
        run.LoadTests.Add(Load(LoadTestKind.Smoke, 9.8, 4, 8, 219));
        run.LoadTests.Add(Load(LoadTestKind.Baseline, 1.5, 0.7, 201682, 10082, vus: 8));
        run.RuntimeSnapshots.Add(Counters(CpuBound with { CpuPercentAvg = 38, AllocationRateMbPerSec = 177, Gen2Collections = 2 }));

        var result = TriageEngine.Analyze(run);

        Assert.Equal(DiagnosisVerdict.NoDegradationDetected, result.Verdict);
        Assert.DoesNotContain(result.Hypotheses, h => h.Status is HypothesisStatus.Supported or HypothesisStatus.Confirmed);
        Assert.Contains(result.Limitations, l => l.Contains("no implica ausencia de problemas", StringComparison.Ordinal));
    }

    [Fact]
    public void SubMillisecondNoiseIsNotDegradation()
    {
        var run = NewRun();
        run.LoadTests.Add(Load(LoadTestKind.Smoke, 0.5, 0.3, 10, 300));
        run.LoadTests.Add(Load(LoadTestKind.Baseline, 1.6, 0.6, 100000, 5000));
        run.RuntimeSnapshots.Add(Counters(CpuBound with { CpuPercentAvg = 30 }));

        Assert.Equal(DiagnosisVerdict.NoDegradationDetected, TriageEngine.Analyze(run).Verdict);
    }

    [Fact]
    public void DegradationWithoutRuntimeDataIsInconclusive()
    {
        var run = NewRun();
        run.LoadTests.Add(Load(LoadTestKind.Smoke, 10, 8, 10, 150));
        run.LoadTests.Add(Load(LoadTestKind.Baseline, 400, 200, 2000, 100));

        var result = TriageEngine.Analyze(run);

        Assert.Equal(DiagnosisVerdict.Inconclusive, result.Verdict);
        Assert.Equal(HypothesisStatus.Unresolved, Rule(result, "CPU-001").Status);
        Assert.Contains(result.Limitations, l => l.Contains("No hay métricas del runtime", StringComparison.Ordinal));
        Assert.NotEmpty(result.OpenQuestions);
    }

    [Fact]
    public void SequentialDependencyCallsThatExplainLatencyConfirmNPlusOne()
    {
        var run = NewRun(new DependencyCheck { Name = "pricing-api", Mode = DependencyMode.Mock, Required = true, Fidelity = FidelityStatus.Simulated });
        run.LoadTests.Add(Load(LoadTestKind.Smoke, 264, 245, 5, 4));
        run.LoadTests.Add(Load(LoadTestKind.Baseline, 269, 240, 839, 41,
            calls: [new DependencyCallStats("pricing-api", "GET /prices/{id}", 4195, 0, 47, "mock-stats")]));
        run.RuntimeSnapshots.Add(Counters(Blocking with { CpuPercentAvg = 3, ThreadPoolStarvationAdjustments = 0, ThreadPoolQueueLengthMax = 3, ThreadPoolThreadsFirst = 7, ThreadPoolThreadsMax = 7 }));

        var result = TriageEngine.Analyze(run);

        var dep = Rule(result, "DEP-001");
        Assert.Equal(HypothesisStatus.Confirmed, dep.Status);
        Assert.Contains("secuenciales", dep.Statement, StringComparison.Ordinal);
        Assert.Contains(dep.Limitations, l => l.Contains("mock", StringComparison.Ordinal));
        Assert.Contains(result.Limitations, l => l.Contains("simulada", StringComparison.Ordinal));
    }

    [Fact]
    public void ServerErrorsMatchingDependencyFailuresConfirmPropagation()
    {
        var run = NewRun(new DependencyCheck { Name = "pricing-api", Mode = DependencyMode.Mock, Required = true, Fidelity = FidelityStatus.Simulated });
        run.LoadTests.Add(Load(LoadTestKind.Smoke, 64, 50, 10, 18));
        run.LoadTests.Add(Load(LoadTestKind.Baseline, 64, 52, 3674, 183, errorRate: 0.0435,
            classes: new Dictionary<string, long> { ["2xx"] = 3514, ["5xx"] = 160 },
            calls: [new DependencyCallStats("pricing-api", "GET /prices", 3674, 160, 45, "mock-stats")]));
        run.RuntimeSnapshots.Add(Counters(CpuBound with { CpuPercentAvg = 4 }));

        var result = TriageEngine.Analyze(run);

        Assert.Equal(HypothesisStatus.Confirmed, Rule(result, "ERR-002").Status);
        Assert.DoesNotContain(result.Hypotheses, h => h.RuleId == "DEP-001");
        Assert.Contains(result.Observations, o => o.Category == "errors");
    }

    [Fact]
    public void PartialTimeOverlapIsReportedAsALimitation()
    {
        var run = NewRun();
        run.LoadTests.Add(Load(LoadTestKind.Smoke, 10, 8, 10, 150));
        run.LoadTests.Add(Load(LoadTestKind.Baseline, 40, 22, 14000, 700));
        run.RuntimeSnapshots.Add(Counters(CpuBound) with { StartedAt = T0.AddSeconds(18), EndedAt = T0.AddSeconds(35) });

        var result = TriageEngine.Analyze(run);

        var correlation = Assert.Single(result.Correlations);
        Assert.True(correlation.OverlapRatio < 0.2);
        Assert.Contains(result.Limitations, l => l.Contains("cubre solo", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("System.Threading.Tasks.Task.InternalWaitCore(int32)", true)]
    [InlineData("System.Threading.Thread.Sleep(int32)", true)]
    [InlineData("System.Threading.Monitor.Wait(class System.Object,int32)", true)]
    [InlineData("PerfLab.Api.CpuReportService.CountPrimesNaive(int32)", false)]
    public void BlockingFramesAreRecognized(string frame, bool blocking) => Assert.Equal(blocking, TriageEngine.IsBlockingFrame(frame));
}
