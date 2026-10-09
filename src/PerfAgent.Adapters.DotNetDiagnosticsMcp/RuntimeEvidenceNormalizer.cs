using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PerfAgent.Core.Contracts;
using PerfAgent.Core.Security;
using PerfAgent.Core.Triage;

namespace PerfAgent.Adapters.DotNetDiagnosticsMcp;

/// <summary>
/// Normaliza las salidas estructuradas de dotnet-diagnostics (DiagnosticResult&lt;T&gt;) al modelo interno.
/// Los textos derivados del proceso objetivo (nombres de métodos, hallazgos) se tratan como datos no confiables:
/// se sanean y se acotan, nunca se interpretan como instrucciones.
/// </summary>
public static partial class RuntimeEvidenceNormalizer
{
    private const double BytesPerMb = 1_000_000d;

    private static readonly string[] FrameworkModulePrefixes =
    [
        "System.", "Microsoft.", "netstandard", "mscorlib", "Anonymously Hosted", "[", "UNMANAGED", "Unknown", "coreclr", "clr", "ntdll", "kernel",
    ];

    /// <summary>Resumen a partir del batch [counters, gc, threadpool] y de la línea temporal de hilos.</summary>
    public static (RuntimeSummary Summary, List<MetricSeries> Series, List<string> Unavailable, List<string> Findings) FromLoadWindow(
        JsonElement? counters, JsonElement? gc, JsonElement? threadPool, JsonElement? threadPoolTimeline)
    {
        var unavailable = new List<string>();
        var series = new List<MetricSeries>();
        var findings = new List<string>();

        double? Last(string name) => counters is { } c ? CounterValue(c, "counters", name) : null;
        double? Max(string name) => counters is { } c ? CounterValue(c, "maxCounters", name) ?? CounterValue(c, "counters", name) : null;
        double? Interval(string name) => counters is { } c ? CounterInterval(c, name) : null;
        double? PerSecond(string name, Func<string, double?> pick)
        {
            var value = pick(name);
            var interval = Interval(name);
            return value is null ? null : value / (interval is > 0 ? interval.Value : 1);
        }

        if (counters is null)
        {
            unavailable.Add("counters del runtime (EventPipe)");
        }

        var processorCount = counters is { } cc && cc.TryGetProperty("processorCount", out var pc) && pc.ValueKind == JsonValueKind.Number ? pc.GetInt32() : (int?)null;
        long? gen0 = null, gen1 = null, gen2 = null;
        double? pauseTotal = null, pauseMax = null, heapFirst = null, heapMax = null, heapLast = null;
        if (gc is { } g)
        {
            if (g.TryGetProperty("generations", out var gens) && gens.ValueKind == JsonValueKind.Array)
            {
                gen0 = 0;
                gen1 = 0;
                gen2 = 0;
                foreach (var gen in gens.EnumerateArray())
                {
                    var count = gen.GetProperty("count").GetInt64();
                    switch (gen.GetProperty("generation").GetInt32())
                    {
                        case 0: gen0 = count; break;
                        case 1: gen1 = count; break;
                        default: gen2 += count; break;
                    }
                }
            }

            pauseTotal = DurationMs(g, "totalPauseTime");
            pauseMax = DurationMs(g, "maxPauseTime");
            if (g.TryGetProperty("heapStats", out var heap) && heap.ValueKind == JsonValueKind.Array && heap.GetArrayLength() > 0)
            {
                var points = heap.EnumerateArray()
                    .Where(h => h.TryGetProperty("timestamp", out _) && h.TryGetProperty("totalHeapSizeBytes", out _))
                    .Select(h => new MetricPoint(h.GetProperty("timestamp").GetDateTimeOffset(), Math.Round(h.GetProperty("totalHeapSizeBytes").GetDouble() / BytesPerMb, 3)))
                    .OrderBy(p => p.Timestamp)
                    .ToList();
                if (points.Count > 0)
                {
                    series.Add(new MetricSeries("gc.heap.size_after_gc", "MB", "dotnet-diagnostics/gc-events", points, MetricAggregate.From(points)));
                    heapFirst = points[0].Value;
                    heapMax = points.Max(p => p.Value);
                    heapLast = points[^1].Value;
                }
            }

            if (Str(g, "pauseMeasurementStatus") is { } pauseStatus && pauseStatus != "no-detected-loss")
            {
                findings.Add($"[gc] Medición de pausas: {pauseStatus}.");
            }
        }
        else
        {
            unavailable.Add("eventos de GC (recuentos por generación)");
        }

        heapFirst ??= Last("gc-heap-size");
        heapMax ??= Max("gc-heap-size");
        heapLast ??= Last("gc-heap-size");

        double? tpFirst = null, tpMax = null;
        long? starvation = null, cooperative = null;
        if (threadPool is { } tp && tp.TryGetProperty("evidence", out var evidence))
        {
            starvation = Long(evidence, "confirmedStarvationAdjustments");
            cooperative = Long(evidence, "confirmedCooperativeBlockingAdjustments");
        }
        else
        {
            unavailable.Add("eventos del ThreadPool (ajustes por starvation)");
        }

        if (threadPoolTimeline is { } tl && tl.TryGetProperty("workerThreads", out var workers) && workers.ValueKind == JsonValueKind.Array)
        {
            // Los recuentos 0 son huecos inferidos por el colector (un ThreadPool activo nunca tiene 0 hilos).
            var points = workers.EnumerateArray()
                .Select(w => new MetricPoint(w.GetProperty("timestamp").GetDateTimeOffset(), w.GetProperty("count").GetDouble()))
                .Where(p => p.Value > 0)
                .OrderBy(p => p.Timestamp)
                .ToList();
            if (points.Count > 0)
            {
                series.Add(new MetricSeries("threadpool.worker_threads", "threads", "dotnet-diagnostics/threadpool-events", points, MetricAggregate.From(points)));
                tpFirst = points[0].Value;
                tpMax = points.Max(p => p.Value);
            }
        }

        tpMax ??= Max("threadpool-thread-count");
        tpFirst ??= Last("threadpool-thread-count");

        var summary = new RuntimeSummary
        {
            CpuPercentAvg = null,
            CpuPercentMax = Round(Max("cpu-usage")),
            ProcessorCount = processorCount,
            WorkingSetMbMax = Round(Max("working-set")),
            GcHeapMbFirst = Round(heapFirst),
            GcHeapMbMax = Round(heapMax),
            GcHeapMbLast = Round(heapLast),
            AllocationRateMbPerSec = Round(PerSecond("alloc-rate", Last) / BytesPerMb),
            AllocationRateMbPerSecMax = Round(PerSecond("alloc-rate", Max) / BytesPerMb),
            TimeInGcPercentMax = Round(Max("time-in-gc")),
            GcPauseMsTotal = Round(pauseTotal),
            GcPauseMsMax = Round(pauseMax),
            Gen0Collections = gen0,
            Gen1Collections = gen1,
            Gen2Collections = gen2,
            ThreadPoolThreadsFirst = tpFirst,
            ThreadPoolThreadsMax = tpMax,
            ThreadPoolQueueLengthLast = Last("threadpool-queue-length"),
            ThreadPoolQueueLengthMax = Max("threadpool-queue-length"),
            ThreadPoolStarvationAdjustments = starvation,
            ThreadPoolCooperativeBlockingAdjustments = cooperative,
            LockContentionsPerSec = Round(PerSecond("monitor-lock-contention-count", Last)),
            ExceptionsPerSec = Round(PerSecond("exception-count", Last)),
            ServerRequestsPerSec = Round(PerSecond("requests-per-second", Last)),
            ServerCurrentRequestsMax = Max("current-requests"),
        };
        return (summary, series, unavailable, findings);
    }

    /// <summary>Hallazgos del triage del proveedor (señales observadas e hipótesis), citados como datos.</summary>
    public static (RuntimeSummary? Summary, List<string> Findings) FromTriage(JsonElement triage)
    {
        var findings = new List<string>();
        if (Str(triage, "assessment") is { } assessment)
        {
            findings.Add($"[assessment] {assessment}");
        }

        foreach (var signal in Array(triage, "observedSignals"))
        {
            findings.Add($"[observed] {Str(signal, "name")} ({Str(signal, "level")}): {Clean(Str(signal, "summary"))}");
        }

        foreach (var hypothesis in Array(triage, "hypotheses"))
        {
            findings.Add($"[hypothesis] {Str(hypothesis, "name")} (confidence {Str(hypothesis, "confidence")}): {Clean(Str(hypothesis, "summary"))} Next: {Clean(Str(hypothesis, "nextStep"))}");
        }

        RuntimeSummary? summary = null;
        if (triage.TryGetProperty("evidence", out var e))
        {
            summary = new RuntimeSummary
            {
                CpuPercentMax = Round(Double(e, "cpuUsage")),
                TimeInGcPercentMax = Round(Double(e, "timeInGc")),
                ThreadPoolQueueLengthLast = Double(e, "threadPoolQueueLength"),
                AllocationRateMbPerSec = Round(Double(e, "allocRate") / BytesPerMb),
                GcHeapMbLast = Round(Double(e, "gcHeapSize")),
                ProcessorCount = (int?)Double(e, "logicalProcessorCount"),
            };
        }

        return (summary, findings);
    }

    /// <summary>Hotspots de una muestra de CPU (top por tiempo propio + camino caliente).</summary>
    public static (List<HotSpot> HotSpots, List<HotSpot> HotPath, List<string> Findings) FromCpuSample(JsonElement cpu, JsonElement? topMethods, JsonElement? hotPath)
    {
        var findings = new List<string>();
        var total = Double(cpu, "totalSamples") ?? 0;
        if (cpu.TryGetProperty("selfSamples", out var self))
        {
            findings.Add($"[cpu-sample] {total:0} muestras de pila; en espera (heurística) {Double(self, "waitingSamples"):0}, desconocidas {Double(self, "unknownSamples"):0}. Las muestras de EventPipe miden frecuencia de pila, no tiempo de CPU del sistema operativo.");
        }

        var spots = new Dictionary<string, HotSpot>(StringComparer.Ordinal);
        foreach (var m in topMethods is { } tm ? Array(tm, "methods") : [])
        {
            var frame = PrettyFrame(Str(m, "method"));
            var module = Str(m, "module");
            spots[frame] = new HotSpot(frame, module, Round(Double(m, "inclusivePercent")), Round(Double(m, "exclusivePercent")), IsUserFrame(module, frame),
                m.TryGetProperty("waitReason", out _) || TriageEngine.IsBlockingFrame(frame));
        }

        foreach (var h in Array(cpu, "topHotspots"))
        {
            var frame = PrettyFrame(h.TryGetProperty("frame", out var f) ? Str(f, "method") : null);
            if (spots.ContainsKey(frame))
            {
                continue;
            }

            var module = h.TryGetProperty("frame", out var f2) ? Str(f2, "module") : null;
            var inclusive = total > 0 ? Double(h, "inclusiveSamples") / total * 100 : null;
            var exclusive = total > 0 ? Double(h, "exclusiveSamples") / total * 100 : null;
            spots[frame] = new HotSpot(frame, module, Round(inclusive), Round(exclusive), IsUserFrame(module, frame), TriageEngine.IsBlockingFrame(frame));
        }

        var path = new List<HotSpot>();
        var pathTotal = hotPath is { } root ? Double(root, "totalSamples") ?? total : total;
        foreach (var f in hotPath is { } hp ? Array(hp, "frames") : [])
        {
            var frame = PrettyFrame(Str(f, "method"));
            var module = Str(f, "module");
            var exclusive = pathTotal > 0 ? Double(f, "exclusiveSamples") / pathTotal * 100 : null;
            path.Add(new HotSpot(frame, module, Round(Double(f, "inclusivePercent")), Round(exclusive), IsUserFrame(module, frame), TriageEngine.IsBlockingFrame(frame)));
        }

        // Los marcos de usuario suelen tener tiempo propio 0 (la hoja es del framework): se conservan siempre.
        var ordered = spots.Values.OrderByDescending(s => s.ExclusivePercent ?? 0).ThenByDescending(s => s.InclusivePercent ?? 0).Take(25)
            .Concat(spots.Values.Where(s => s.IsUserCode))
            .Concat(path.Where(p => p.IsUserCode))
            .DistinctBy(s => s.Frame)
            .ToList();
        if (path.Count > 0)
        {
            findings.Add($"[hot-path] Hoja: {path[^1].Frame} ({path[^1].InclusivePercent:0.#}% inclusivo).");
        }

        return (ordered, path, findings);
    }

    /// <summary>Contención de locks: volumen y duración de las esperas, y sitios de llamada conocidos.</summary>
    public static (RuntimeSummary Summary, List<HotSpot> HotSpots) FromContention(JsonElement contention)
    {
        var total = Double(contention, "totalEvents") ?? 0;
        var sites = Array(contention, "events")
            .Select(e => (Method: Str(e, "callSiteMethod"), Module: Str(e, "callSiteModule")))
            .Where(e => e.Method is not null && e.Method != "(unknown)")
            .GroupBy(e => (e.Method, e.Module))
            .Select(g => new HotSpot(PrettyFrame(g.Key.Method), g.Key.Module, null, total > 0 ? Math.Round(g.Count() / total * 100, 2) : null, IsUserFrame(g.Key.Module, g.Key.Method ?? "?"), true))
            .OrderByDescending(h => h.ExclusivePercent)
            .Take(15)
            .ToList();
        var summary = new RuntimeSummary
        {
            LockContentionEvents = (long?)Double(contention, "totalEvents"),
            LockContentionTotalMs = Round(DurationMs(contention, "totalContentionDuration")),
            LockContentionP95Ms = Round(DurationMs(contention, "p95ContentionDuration")),
            LockContentionMaxMs = Round(DurationMs(contention, "maxContentionDuration")),
        };
        return (summary, sites);
    }

    /// <summary>Sitios de asignación por bytes (muestreo GCAllocationTick) y tipos más asignados.</summary>
    public static (List<HotSpot> HotSpots, List<string> Findings) FromAllocationSample(JsonElement allocation)
    {
        var totalBytes = Double(allocation, "totalBytes") ?? 0;
        var spots = Array(allocation, "topBySite")
            .Select(s =>
            {
                var frame = s.TryGetProperty("frame", out var f) ? PrettyFrame(Str(f, "method")) : "?";
                var module = s.TryGetProperty("frame", out var f2) ? Str(f2, "module") : null;
                var share = totalBytes > 0 ? Double(s, "totalBytes") / totalBytes * 100 : null;
                return new HotSpot(frame, module, Round(share), Round(share), IsUserFrame(module, frame));
            })
            .Take(20)
            .ToList();
        var findings = new List<string>
        {
            $"[allocation-sample] {totalBytes / BytesPerMb:0.#} MB asignados muestreados en la ventana.",
        };
        findings.AddRange(Array(allocation, "topByBytes").Take(5).Select(t =>
            $"[allocation-type] {Clean(Str(t, "typeName"))}: {Double(t, "totalBytes") / BytesPerMb:0.#} MB ({Double(t, "eventCount"):0} eventos)"));
        return (spots, findings);
    }

    public static bool IsUserModule(string? module) =>
        !string.IsNullOrWhiteSpace(module) && module != "?" && !FrameworkModulePrefixes.Any(p => module.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>Marco de código de usuario: módulo de la aplicación y método resuelto (no una dirección nativa).</summary>
    public static bool IsUserFrame(string? module, string frame) =>
        IsUserModule(module) && !frame.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && frame != "?";

    /// <summary><c>Ns.Type+&lt;Method&gt;d__N.MoveNext()</c> → <c>Ns.Type.Method (async)</c>.</summary>
    public static string PrettyFrame(string? method)
    {
        if (string.IsNullOrWhiteSpace(method))
        {
            return "?";
        }

        var match = AsyncStateMachineRegex().Match(method);
        var pretty = match.Success ? $"{match.Groups["type"].Value}.{match.Groups["method"].Value} (async)" : SimplifyParameters(method);
        return Clean(pretty);
    }

    /// <summary><c>M(int32,value class System.Threading.CancellationToken)</c> → <c>M(int32, CancellationToken)</c>.</summary>
    internal static string SimplifyParameters(string frame)
    {
        var open = frame.IndexOf('(', StringComparison.Ordinal);
        if (open < 0 || !frame.EndsWith(')'))
        {
            return frame;
        }

        var parameters = frame[(open + 1)..^1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(p =>
            {
                var type = p.Replace("value class ", string.Empty, StringComparison.Ordinal).Replace("class ", string.Empty, StringComparison.Ordinal);
                var generic = type.IndexOf('<', StringComparison.Ordinal);
                var head = generic < 0 ? type : type[..generic];
                var dot = head.LastIndexOf('.');
                return dot < 0 ? type : type[(dot + 1)..];
            });
        return $"{frame[..open]}({string.Join(", ", parameters)})";
    }

    internal static double? DurationMs(JsonElement element, string name) =>
        Str(element, name) is { } text && TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var span) ? span.TotalMilliseconds : null;

    private static double? CounterValue(JsonElement counters, string list, string name)
    {
        foreach (var c in Array(counters, list))
        {
            if (Str(c, "name") == name)
            {
                return Double(c, "value");
            }
        }

        return null;
    }

    private static double? CounterInterval(JsonElement counters, string name)
    {
        foreach (var c in Array(counters, "counters").Concat(Array(counters, "maxCounters")))
        {
            if (Str(c, "name") == name)
            {
                return Double(c, "intervalSec");
            }
        }

        return null;
    }

    private static IEnumerable<JsonElement> Array(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? Double(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static long? Long(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;

    private static double? Round(double? value) => value is { } v ? Math.Round(v, 2) : null;

    private static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var sanitized = SecretRedactor.Redact(text.Replace('\n', ' ').Replace('\r', ' '));
        return sanitized.Length <= 300 ? sanitized : sanitized[..300] + "…";
    }

    [GeneratedRegex(@"^(?<type>.+?)\+<(?<method>[^>]+)>d__\d+(?:`\d+)?(?:\[[^\]]*\])?\.MoveNext\(\)$")]
    private static partial Regex AsyncStateMachineRegex();
}
