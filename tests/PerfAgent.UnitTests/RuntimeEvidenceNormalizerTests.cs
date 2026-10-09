using System.Text.Json;
using PerfAgent.Adapters.DotNetDiagnosticsMcp;

namespace PerfAgent.UnitTests;

/// <summary>Normalización de salidas REALES de pedrosakuma/dotnet-diagnostics 0.28.0 capturadas en el laboratorio.</summary>
public sealed class RuntimeEvidenceNormalizerTests
{
    private static JsonElement BatchResult(JsonElement batch, string kind) =>
        batch.GetProperty("data").GetProperty("results").EnumerateArray().First(r => r.GetProperty("kind").GetString() == kind).GetProperty("data");

    [Fact]
    public void LoadWindowBatchFromTheBlockingScenarioIsNormalized()
    {
        var batch = Fixtures.Json("blocking.batch.json");
        var timeline = Fixtures.Json("blocking.threadpool-timeline.json").GetProperty("data").GetProperty("payload");

        var (summary, series, unavailable, _) = RuntimeEvidenceNormalizer.FromLoadWindow(
            BatchResult(batch, "counters").GetProperty("counters"),
            BatchResult(batch, "gc").GetProperty("gc"),
            BatchResult(batch, "threadpool").GetProperty("threadPool"),
            timeline);

        Assert.Empty(unavailable);
        Assert.Equal(4, summary.ProcessorCount);
        Assert.True(summary.ThreadPoolStarvationAdjustments > 0);
        Assert.True(summary.ThreadPoolCooperativeBlockingAdjustments > 0);
        Assert.True(summary.ThreadPoolThreadsMax > summary.ThreadPoolThreadsFirst);
        Assert.True(summary.ThreadPoolQueueLengthMax >= 10);
        Assert.True(summary.CpuPercentMax < 20);
        Assert.NotNull(summary.Gen0Collections);
        Assert.Contains(series, s => s.Name == "threadpool.worker_threads" && s.Points.All(p => p.Value > 0));
    }

    [Fact]
    public void CpuSampleWithBlockingHotPathIsNormalized()
    {
        var cpu = Fixtures.Json("blocking.cpu-sample.json").GetProperty("data").GetProperty("cpu");
        var top = Fixtures.Json("blocking.top-methods.json").GetProperty("data");
        var path = Fixtures.Json("blocking.hot-path.json").GetProperty("data");

        var (spots, hotPath, findings) = RuntimeEvidenceNormalizer.FromCpuSample(cpu, top, path);

        Assert.True(hotPath[^1].IsBlocking);
        Assert.Contains("Task.InternalWaitCore", hotPath[^1].Frame, StringComparison.Ordinal);
        Assert.Contains(hotPath, h => h.IsUserCode && h.Frame.StartsWith("PerfLab.Api.BlockingReportService.BlockThread", StringComparison.Ordinal));
        Assert.Contains(hotPath, h => h.Frame == "PerfLab.Api.BlockingReportService.GetReportAsync (async)");
        Assert.Contains(spots, s => s.IsUserCode && s.Frame.Contains("BlockThread", StringComparison.Ordinal));
        Assert.All(spots.Where(s => s.Module?.StartsWith("System.", StringComparison.Ordinal) == true), s => Assert.False(s.IsUserCode));
        Assert.Contains(findings, f => f.StartsWith("[hot-path]", StringComparison.Ordinal));
    }

    [Fact]
    public void CpuSampleOfTheCpuScenarioExposesTheUserHotMethod()
    {
        var cpu = Fixtures.Json("cpu.cpu-sample.json").GetProperty("data").GetProperty("cpu");
        var top = Fixtures.Json("cpu.top-methods.json").GetProperty("data");
        var path = Fixtures.Json("cpu.hot-path.json").GetProperty("data");

        var (spots, _, _) = RuntimeEvidenceNormalizer.FromCpuSample(cpu, top, path);

        var hot = spots.Where(s => s.IsUserCode).OrderByDescending(s => s.ExclusivePercent).First();
        Assert.Equal("PerfLab.Api.CpuReportService.CountPrimesNaive(int32)", hot.Frame);
        Assert.True(hot.ExclusivePercent >= 15);
    }

    [Fact]
    public void AllocationSamplingIdentifiesTheAllocatingSite()
    {
        var batch = Fixtures.Json("alloc.gc-batch.json");
        var allocation = BatchResult(batch, "allocation").GetProperty("allocation");

        var (spots, findings) = RuntimeEvidenceNormalizer.FromAllocationSample(allocation);

        var top = spots[0];
        Assert.Equal("PerfLab.Api.AllocationReportService.BuildReportWithExcessiveAllocations()", top.Frame);
        Assert.True(top.IsUserCode);
        Assert.True(top.ExclusivePercent > 90);
        Assert.Contains(findings, f => f.Contains("System.String", StringComparison.Ordinal));
    }

    [Fact]
    public void GcEventsGiveExactCollectionCountsForTheWindow()
    {
        var batch = Fixtures.Json("alloc.gc-batch.json");

        var (summary, series, _, _) = RuntimeEvidenceNormalizer.FromLoadWindow(null, BatchResult(batch, "gc").GetProperty("gc"), null, null);

        Assert.True(summary.Gen2Collections > 100);
        Assert.True(summary.Gen0Collections > summary.Gen2Collections);
        Assert.NotNull(summary.GcPauseMsTotal);
        Assert.Contains(series, s => s.Name == "gc.heap.size_after_gc");
    }

    [Fact]
    public void ContentionCaptureMeasuresWaitDurations()
    {
        var contention = Fixtures.Json("blocking.contention.json").GetProperty("data").GetProperty("contention");

        var (summary, sites) = RuntimeEvidenceNormalizer.FromContention(contention);

        Assert.True(summary.LockContentionEvents > 0);
        Assert.True(summary.LockContentionP95Ms < 10);
        Assert.True(summary.LockContentionMaxMs >= summary.LockContentionP95Ms);
        Assert.DoesNotContain(sites, s => s.IsUserCode);
    }

    [Theory]
    [InlineData("PerfLab.Api.BlockingReportService+<GetReportAsync>d__2.MoveNext()", "PerfLab.Api.BlockingReportService.GetReportAsync (async)")]
    [InlineData("PerfLab.Api.CpuReportService.CountPrimesNaive(int32)", "PerfLab.Api.CpuReportService.CountPrimesNaive(int32)")]
    [InlineData("System.Threading.Tasks.Task.InternalWaitCore(int32,value class System.Threading.CancellationToken)", "System.Threading.Tasks.Task.InternalWaitCore(int32, CancellationToken)")]
    [InlineData("System.Threading.Monitor.Wait(class System.Object,int32)", "System.Threading.Monitor.Wait(Object, int32)")]
    public void AsyncStateMachinesArePrettified(string raw, string pretty) =>
        Assert.Equal(pretty, RuntimeEvidenceNormalizer.PrettyFrame(raw));

    [Theory]
    [InlineData("PerfLab.Api.dll", "PerfLab.Api.CpuReportService.CountPrimesNaive(int32)", true)]
    [InlineData("System.Private.CoreLib.dll", "System.Threading.Tasks.Task.Wait()", false)]
    [InlineData("Microsoft.AspNetCore.Routing", "Microsoft.AspNetCore.Routing.EndpointMiddleware.Invoke()", false)]
    [InlineData("Anonymously Hosted DynamicMethods Assembly", "dynamicClass.lambda_method5()", false)]
    [InlineData("PerfLab.Api", "0x7ffcf7930fa1", false)]
    [InlineData(null, "?", false)]
    public void UserFramesAreClassified(string? module, string frame, bool user) =>
        Assert.Equal(user, RuntimeEvidenceNormalizer.IsUserFrame(module, frame));
}
