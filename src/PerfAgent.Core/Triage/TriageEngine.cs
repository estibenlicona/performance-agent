using System.Globalization;
using PerfAgent.Core.Contracts;
using PerfAgent.Core.Runs;

namespace PerfAgent.Core.Triage;

public sealed record TriageResult(
    DiagnosisVerdict Verdict,
    IReadOnlyList<Observation> Observations,
    IReadOnlyList<Hypothesis> Hypotheses,
    IReadOnlyList<TimeCorrelation> Correlations,
    IReadOnlyList<NextAction> NextExperiments,
    IReadOnlyList<string> OpenQuestions,
    IReadOnlyList<string> Limitations,
    IReadOnlyList<string> Recommendations);

/// <summary>
/// Triage por reglas interpretables. Cada regla separa observación (hecho medido), hipótesis, evidencia
/// a favor y en contra, evidencia que falta y siguiente acción. Una hipótesis solo pasa a <c>confirmed</c>
/// con una evidencia discriminante (muestra de CPU, asignaciones por sitio, aritmética de dependencias);
/// si faltan datos, el resultado es inconcluso.
/// </summary>
public static class TriageEngine
{
    private static readonly string[] BlockingFrameMarkers =
    [
        "Thread.Sleep", "Task.Wait", "Task.InternalWait", "TaskAwaiter.GetResult", "ManualResetEventSlim.Wait",
        "Monitor.Wait", "Monitor.Enter", "Monitor.ReliableEnter", "SpinThenBlockingWait", "WaitHandle.Wait", "Thread.Join",
        "SemaphoreSlim.Wait(", "Task`1.get_Result", "Task.GetResultCore", "WaitOneNoCheck",
    ];

    // Marcos generados por el compilador para lambdas de endpoints y el punto de entrada: enrutan, no trabajan.
    private static readonly string[] GlueFrameMarkers =
    [
        "Program.<Main>", "<Main>$", "lambda_method", ">b__", "RequestDelegateFactory",
    ];

    public static TriageResult Analyze(LabRun run, TriageThresholds? thresholds = null) =>
        new TriageSession(run, thresholds ?? TriageThresholds.Default).Run();

    /// <summary>Correlación temporal entre una prueba de carga y la captura del runtime de la misma ventana.</summary>
    public static TimeCorrelation Correlate(LoadTestRun load, RuntimeSnapshot runtime, double minOverlapRatio)
    {
        var loadStart = load.StartedAt;
        var loadEnd = load.EndedAt ?? load.StartedAt;
        var rtStart = runtime.StartedAt;
        var rtEnd = runtime.EndedAt ?? runtime.StartedAt;
        var overlapStart = loadStart > rtStart ? loadStart : rtStart;
        var overlapEnd = loadEnd < rtEnd ? loadEnd : rtEnd;
        var overlap = Math.Max(0, (overlapEnd - overlapStart).TotalSeconds);
        var loadSeconds = Math.Max(0.001, (loadEnd - loadStart).TotalSeconds);
        var ratio = Math.Min(1, overlap / loadSeconds);
        var assessment = ratio >= minOverlapRatio
            ? "Ventanas comparables: la captura del runtime cubre la prueba de carga."
            : ratio > 0
                ? "Ventanas parcialmente solapadas: interpreta las señales del runtime con cautela."
                : "Sin solapamiento: las señales del runtime no corresponden a esta prueba.";
        return new TimeCorrelation(load.Id, runtime.Id, loadStart, loadEnd, rtStart, rtEnd, Math.Round(overlap, 2), Math.Round(ratio, 3), assessment);
    }

    public static bool IsBlockingFrame(string frame) =>
        BlockingFrameMarkers.Any(marker => frame.Contains(marker, StringComparison.Ordinal));

    /// <summary>Marcos de usuario que solo enrutan (Main, lambdas de endpoints, máquinas de estado async).</summary>
    public static bool IsGlueFrame(string frame) =>
        GlueFrameMarkers.Any(marker => frame.Contains(marker, StringComparison.Ordinal));

    private sealed class TriageSession(LabRun run, TriageThresholds t)
    {
        private readonly List<Observation> _observations = [];
        private readonly List<Hypothesis> _hypotheses = [];
        private readonly List<TimeCorrelation> _correlations = [];
        private readonly List<NextAction> _next = [];
        private readonly List<string> _questions = [];
        private readonly List<string> _limitations = [];
        private readonly List<string> _recommendations = [];
        private int _obsCounter;
        private int _hypCounter;

        private LoadTestRun? _smoke;
        private LoadTestRun? _baseline;
        private RuntimeSnapshot? _counters;
        private RuntimeSummary? _rt;
        private bool _latencyDegraded;
        private bool _latencySlow;
        private bool _errorsHigh;
        private bool _latencyThresholdFailed;
        private bool _gcPressure;
        private bool _cpuSaturated;
        private bool _starvation;

        private bool LatencySymptom => _latencyDegraded || _latencySlow || _latencyThresholdFailed;

        public TriageResult Run()
        {
            AddEnvironmentLimitations();
            var verdict = Gate();
            if (verdict is not null)
            {
                return Build(verdict.Value);
            }

            ObserveLoad();
            ObserveRuntime();
            ObserveDependencies();

            // La presión severa del runtime también es un síntoma, aunque la latencia aún no se degrade con esta carga.
            _gcPressure = GcSignals().Count >= 2;
            _cpuSaturated = _rt?.CpuPercentAvg >= t.CpuHighPercent;
            _starvation = _rt?.ThreadPoolStarvationAdjustments > 0;
            var hasSymptom = LatencySymptom || _errorsHigh || _gcPressure || _cpuSaturated || _starvation;
            if (!LatencySymptom && (_gcPressure || _cpuSaturated || _starvation))
            {
                AddObservation("capacity",
                    "La latencia no se degrada con la carga probada, pero el runtime muestra presión de recursos: es un riesgo de capacidad con más carga o en máquinas más pequeñas.",
                    [new EvidenceItem("k6", "baseline.http_req_duration.p95", "p95 bajo carga", _baseline?.Latency?.P95, "ms", Artifact(_baseline))]);
            }

            if (hasSymptom)
            {
                HypothesizeCpu();
                HypothesizeThreadPool();
                HypothesizeGc();
                HypothesizeContention();
                HypothesizeDependency();
                HypothesizeErrors();
            }
            else
            {
                NoteGcWithoutSymptom();
            }

            HypothesizeMemoryGrowth();
            return Build(DecideVerdict(hasSymptom));
        }

        private DiagnosisVerdict? Gate()
        {
            if (run.Environment?.Decision == PreflightDecision.Blocked)
            {
                AddObservation("environment", "El preflight bloqueó la ejecución: no hay conclusiones de rendimiento.",
                    run.Environment.BlockingReasons.Select(r => new EvidenceItem("preflight", "decision", r)).ToList());
                _questions.Add("Resuelve los bloqueos del preflight (dependencias requeridas, conectividad o autenticación) y repite el diagnóstico.");
                return DiagnosisVerdict.EnvironmentFailure;
            }

            if (run.Failure is not FailureKind.None and not FailureKind.Cancelled)
            {
                AddObservation("environment", $"Fallo de ambiente ({Kebab(run.Failure)}): no es un problema de rendimiento de la aplicación.",
                    [new EvidenceItem("process", Kebab(run.Failure), run.FailureDetail ?? "Sin detalle.")]);
                return DiagnosisVerdict.EnvironmentFailure;
            }

            _smoke = run.LatestLoadTest(LoadTestKind.Smoke);
            _baseline = run.LatestLoadTest(LoadTestKind.Baseline);
            if (_smoke is null)
            {
                _questions.Add("Falta el smoke test: ejecútalo antes de la carga (run_k6_test kind=smoke).");
                return DiagnosisVerdict.NotEvaluated;
            }

            switch (_smoke.Status)
            {
                case LoadTestStatus.FunctionalFailure:
                    AddObservation("functional", "El smoke test detectó respuestas funcionales inválidas; no se ejecutó carga.", FunctionalEvidence(_smoke));
                    AddHypothesis("FUNC-001", "Respuesta funcional inválida",
                        "El endpoint no cumple el contrato esperado (estado o contenido) con datos sintéticos; es un problema funcional o de datos de prueba, no de rendimiento.",
                        HypothesisStatus.Supported, Confidence.Medium, FunctionalEvidence(_smoke), [],
                        ["Revisar expectedStatus/bodyContains del escenario y los logs saneados de la API."], null);
                    return DiagnosisVerdict.FunctionalFailure;
                case LoadTestStatus.NetworkError:
                    AddObservation("environment", "El smoke test no pudo conectar con la API (errores de red): fallo de ambiente.",
                        [new EvidenceItem("k6", "status-0", _smoke.StatusDetail ?? "Errores de conexión.", _smoke.FailedRequests, "requests", Artifact(_smoke))]);
                    return DiagnosisVerdict.EnvironmentFailure;
                case LoadTestStatus.GeneratorError:
                case LoadTestStatus.Aborted:
                case LoadTestStatus.Blocked:
                    AddObservation("generator", "El generador de carga falló, se interrumpió o fue bloqueado: el resultado no es una medición válida.",
                        [new EvidenceItem("k6", "status", _smoke.StatusDetail ?? Kebab(_smoke.Status), _smoke.ExitCode, null, Artifact(_smoke))]);
                    return DiagnosisVerdict.EnvironmentFailure;
            }

            if (_baseline is null)
            {
                ObserveSlowAtLowLoad();
                _questions.Add("Falta la prueba de carga baseline: sin ella no se puede evaluar la degradación bajo concurrencia.");
                return _latencySlow ? DiagnosisVerdict.Inconclusive : DiagnosisVerdict.NotEvaluated;
            }

            if (_baseline.Status is LoadTestStatus.GeneratorError or LoadTestStatus.Aborted or LoadTestStatus.Blocked)
            {
                AddObservation("generator", "La prueba de carga no produjo una medición válida (fallo del generador, cancelación o bloqueo).",
                    [new EvidenceItem("k6", "status", _baseline.StatusDetail ?? Kebab(_baseline.Status), _baseline.ExitCode, null, Artifact(_baseline))]);
                return DiagnosisVerdict.Inconclusive;
            }

            return null;
        }

        private void ObserveSlowAtLowLoad()
        {
            foreach (var e in _smoke!.Endpoints.Where(e => e.Latency?.P95 >= t.SlowRequestMs))
            {
                _latencySlow = true;
                AddObservation("latency", $"El endpoint '{e.Name}' es lento incluso sin concurrencia (p95 {Ms(e.Latency!.P95)} con {_smoke.Load.Vus} VU).",
                    [new EvidenceItem("k6", $"http_req_duration{{name:{e.Name}}}.p95", "p95 en el smoke test", e.Latency.P95, "ms", Artifact(_smoke))]);
            }
        }

        private void ObserveLoad()
        {
            var smoke = _smoke!;
            var baseline = _baseline!;
            ObserveSlowAtLowLoad();

            var ratio = Ratio(baseline.Latency?.P95, smoke.Latency?.P95);
            var delta = baseline.Latency?.P95 - smoke.Latency?.P95;
            if (ratio >= t.LatencyDegradationRatio && delta >= t.LatencyDegradationMinDeltaMs)
            {
                _latencyDegraded = true;
                AddObservation("latency",
                    $"La latencia p95 se degrada bajo carga: {Ms(smoke.Latency?.P95)} con {smoke.Load.Vus} VU frente a {Ms(baseline.Latency?.P95)} con {baseline.Load.Vus} VUs (x{Fmt(ratio)}).",
                    [
                        new EvidenceItem("k6", "smoke.http_req_duration.p95", "p95 en el smoke test", smoke.Latency?.P95, "ms", Artifact(smoke)),
                        new EvidenceItem("k6", "baseline.http_req_duration.p95", "p95 bajo carga", baseline.Latency?.P95, "ms", Artifact(baseline)),
                        new EvidenceItem("k6", "p95.ratio", "Relación baseline/smoke", Round(ratio), "x"),
                    ]);
            }

            foreach (var failed in baseline.Thresholds.Where(th => !th.Passed))
            {
                if (failed.Metric.StartsWith("http_req_duration", StringComparison.Ordinal))
                {
                    _latencyThresholdFailed = true;
                }

                AddObservation(failed.Metric.StartsWith("http_req_failed", StringComparison.Ordinal) ? "errors" : "latency",
                    $"Threshold incumplido: {failed.Metric} {failed.Expression}.",
                    [new EvidenceItem("k6", failed.Metric, $"Threshold {failed.Expression} incumplido", null, null, Artifact(baseline))]);
            }

            if (baseline.ErrorRate >= t.ErrorRate)
            {
                _errorsHigh = true;
                AddObservation("errors",
                    $"Tasa de error bajo carga {Pct(baseline.ErrorRate)} ({baseline.FailedRequests} de {baseline.TotalRequests} peticiones). Clases: {FormatClasses(baseline.StatusClasses)}.",
                    [
                        new EvidenceItem("k6", "http_req_failed.rate", "Tasa de peticiones fallidas", Round(baseline.ErrorRate * 100), "%", Artifact(baseline)),
                        .. baseline.StatusClasses.Where(kv => kv.Key != "2xx" && kv.Value > 0)
                            .Select(kv => new EvidenceItem("k6", $"status.{kv.Key}", $"Respuestas {kv.Key}", kv.Value, "requests", Artifact(baseline))),
                    ]);
            }

            AddObservation("throughput",
                $"Throughput bajo carga: {Fmt(baseline.RequestsPerSecond)} req/s con {baseline.Load.Vus} VUs durante {baseline.Load.DurationSeconds}s ({baseline.TotalRequests} peticiones; p50 {Ms(baseline.Latency?.Med)}, p95 {Ms(baseline.Latency?.P95)}, p99 {Ms(baseline.Latency?.P99)}).",
                [new EvidenceItem("k6", "http_reqs.rate", "Peticiones por segundo", Round(baseline.RequestsPerSecond), "req/s", Artifact(baseline))]);

            if (baseline.DroppedIterations > 0)
            {
                AddObservation("generator", $"k6 descartó {baseline.DroppedIterations} iteraciones: el generador no sostuvo la carga planificada.",
                    [new EvidenceItem("k6", "dropped_iterations", "Iteraciones descartadas", baseline.DroppedIterations, "iterations", Artifact(baseline))]);
                _limitations.Add("El generador no sostuvo la carga planificada (dropped_iterations > 0): la medición subestima la carga ofrecida.");
            }

            ObserveTrend(baseline);
        }

        private void ObserveTrend(LoadTestRun baseline)
        {
            var buckets = baseline.Timeline.Where(b => b.P95Ms is not null).ToList();
            if (buckets.Count < 9)
            {
                return;
            }

            var third = buckets.Count / 3;
            var first = buckets.Take(third).Average(b => b.P95Ms!.Value);
            var last = buckets.Skip(buckets.Count - third).Average(b => b.P95Ms!.Value);
            var trend = Ratio(last, first);
            if (trend >= t.LatencyTrendRatio)
            {
                AddObservation("latency",
                    $"La latencia crece durante la prueba: p95 medio {Ms(first)} en el primer tercio frente a {Ms(last)} en el último (x{Fmt(trend)}). Posible saturación o acumulación (colas, memoria).",
                    [new EvidenceItem("k6", "timeline.p95.trend", "Relación último/primer tercio", Round(trend), "x", Artifact(baseline))]);
            }
        }

        private void ObserveRuntime()
        {
            var baseline = _baseline!;
            _counters = run.RuntimeSnapshots.LastOrDefault(s => s.Kind == "counters" && s.LoadTestId == baseline.Id)
                ?? run.RuntimeSnapshots.LastOrDefault(s => s.Kind == "counters");
            if (_counters?.Summary is null)
            {
                _limitations.Add("No hay métricas del runtime para la ventana de carga: las hipótesis de CPU, GC o ThreadPool no se pueden evaluar.");
                _questions.Add("¿Por qué no se capturaron métricas del runtime? Revisa el estado del proveedor de diagnóstico (inspect_runtime).");
                return;
            }

            _rt = _counters.Summary;
            var correlation = Correlate(baseline, _counters, t.MinOverlapRatio);
            _correlations.Add(correlation);
            if (correlation.OverlapRatio < t.MinOverlapRatio)
            {
                _limitations.Add($"La captura del runtime cubre solo el {Pct(correlation.OverlapRatio)} de la ventana de carga.");
            }

            foreach (var missing in _counters.UnavailableSignals)
            {
                _limitations.Add($"Señal del runtime no disponible: {missing}.");
            }

            var art = Artifact(_counters);
            if (_rt.CpuPercentAvg is { } cpu)
            {
                var level = cpu >= t.CpuHighPercent ? "alta" : cpu < t.CpuLowPercent ? "baja" : "moderada";
                AddObservation("cpu", $"CPU del proceso {level} durante la carga: media {Pct1(cpu)}, máxima {Pct1(_rt.CpuPercentMax)} de la máquina ({_rt.ProcessorCount?.ToString(CultureInfo.InvariantCulture) ?? "?"} núcleos lógicos).",
                    [new EvidenceItem("runtime", "cpu.avg", "CPU media del proceso", Round(cpu), "%", art), new EvidenceItem("runtime", "cpu.max", "CPU máxima", Round(_rt.CpuPercentMax), "%", art)]);
            }

            var gcSignals = GcSignals();
            if (gcSignals.Count > 0)
            {
                AddObservation("gc", "Presión de memoria/GC durante la carga: " + string.Join("; ", gcSignals.Select(s => s.Description)) + ".", gcSignals);
            }

            // El crecimiento de hilos por sí solo es el ajuste normal (hill climbing); solo cola o starvation indican presión.
            if (QueueSignal() || _rt.ThreadPoolStarvationAdjustments > 0)
            {
                AddObservation("threadpool",
                    $"ThreadPool bajo presión: cola {Fmt(_rt.ThreadPoolQueueLengthLast)} al final de la ventana (máx. {Fmt(_rt.ThreadPoolQueueLengthMax)}); hilos {Fmt(_rt.ThreadPoolThreadsFirst)} → {Fmt(_rt.ThreadPoolThreadsMax)}; {_rt.ThreadPoolStarvationAdjustments?.ToString(CultureInfo.InvariantCulture) ?? "n/d"} ajustes por starvation.",
                    [
                        new EvidenceItem("runtime", "threadpool.queue.last", "Longitud de la cola (último intervalo)", Round(_rt.ThreadPoolQueueLengthLast), "items", art),
                        new EvidenceItem("runtime", "threadpool.queue.max", "Longitud máxima de la cola", Round(_rt.ThreadPoolQueueLengthMax), "items", art),
                        new EvidenceItem("runtime", "threadpool.threads.growth", "Hilos añadidos durante la ventana", Round(ThreadGrowth()), "threads", art),
                        new EvidenceItem("runtime", "threadpool.starvation_adjustments", "Ajustes de hilos con motivo Starvation (eventos del runtime)", _rt.ThreadPoolStarvationAdjustments, "events", art),
                    ]);
            }

            if (_rt.LockContentionsPerSec >= t.LockContentionsPerSec)
            {
                AddObservation("contention", $"Contención de locks: {Fmt(_rt.LockContentionsPerSec)}/s.",
                    [new EvidenceItem("runtime", "monitor.lock_contentions.rate", "Contenciones por segundo", Round(_rt.LockContentionsPerSec), "1/s", art)]);
            }

            if (_rt.ExceptionsPerSec >= t.ExceptionsPerSec)
            {
                AddObservation("errors", $"Excepciones en el runtime: {Fmt(_rt.ExceptionsPerSec)}/s.",
                    [new EvidenceItem("runtime", "exceptions.rate", "Excepciones por segundo", Round(_rt.ExceptionsPerSec), "1/s", art)]);
            }
        }

        private void ObserveDependencies()
        {
            foreach (var group in _baseline!.DependencyCalls.GroupBy(c => c.Dependency))
            {
                var calls = group.Sum(c => c.Requests);
                var failures = group.Sum(c => c.Failures);
                var avg = WeightedAvg(group.ToList());
                var perRequest = _baseline.TotalRequests > 0 ? (double)calls / _baseline.TotalRequests : 0;
                AddObservation("dependency",
                    $"Dependencia '{group.Key}': {calls} llamadas durante la carga ({Fmt(perRequest)} por petición a la API), latencia media {Ms(avg)}, {failures} fallos (medido por el mock).",
                    [
                        new EvidenceItem("mock", $"{group.Key}.calls_per_request", "Llamadas a la dependencia por petición", Round(perRequest), "calls"),
                        new EvidenceItem("mock", $"{group.Key}.avg_latency", "Latencia media servida por el mock", Round(avg), "ms"),
                        new EvidenceItem("mock", $"{group.Key}.failures", "Respuestas de error del mock", failures, "requests"),
                    ]);
            }
        }

        private void HypothesizeCpu()
        {
            if (!_latencyDegraded && !_latencyThresholdFailed && !_cpuSaturated)
            {
                return;
            }

            if (_rt?.CpuPercentAvg is not { } cpu)
            {
                AddHypothesis("CPU-001", "Trabajo de CPU en la ruta de la petición",
                    "La degradación podría deberse a saturación de CPU, pero no hay datos de CPU del proceso.",
                    HypothesisStatus.Unresolved, Confidence.Low, [], [], ["CPU del proceso durante la carga."],
                    Next("inspect_runtime", "Comprobar que el proveedor de diagnóstico puede leer el proceso."));
                return;
            }

            var art = Artifact(_counters);
            var support = new List<EvidenceItem>();
            var against = new List<EvidenceItem>();
            var cpuEvidence = new EvidenceItem("runtime", "cpu.avg", $"CPU media {Pct1(cpu)} de la máquina", Round(cpu), "%", art);
            HypothesisStatus status;
            Confidence confidence;
            if (cpu >= t.CpuHighPercent)
            {
                support.Add(cpuEvidence);
                status = HypothesisStatus.Supported;
                confidence = Confidence.Medium;
            }
            else if (cpu < t.CpuLowPercent)
            {
                against.Add(cpuEvidence);
                status = HypothesisStatus.Contradicted;
                confidence = Confidence.Medium;
            }
            else
            {
                support.Add(cpuEvidence with { Description = $"CPU media moderada ({Pct1(cpu)}): no concluyente" });
                status = HypothesisStatus.New;
                confidence = Confidence.Low;
            }

            if (_rt.TimeInGcPercentMax >= t.TimeInGcPercent)
            {
                support.Add(new EvidenceItem("runtime", "gc.time_in_gc.max", "Parte de la CPU se consume en GC (ver hipótesis de GC)", Round(_rt.TimeInGcPercentMax), "%", art));
            }

            var missing = new List<string>();
            var limitations = new List<string> { "k6 se ejecuta en la misma máquina y compite por la CPU con la API." };
            var hot = CpuHotSpot();
            if (hot is not null && status == HypothesisStatus.Supported)
            {
                support.Add(new EvidenceItem("runtime", "cpu-sample.hotspot", $"{hot.Value.Frame} concentra {Pct1(hot.Value.Percent)} de las muestras de pila bajo carga", Round(hot.Value.Percent), "%", Artifact(hot.Value.Snapshot)));
                status = HypothesisStatus.Confirmed;
                confidence = Confidence.High;
                _recommendations.Add($"Reduce el trabajo de CPU por petición en {hot.Value.Frame} ({Pct1(hot.Value.Percent)} de las muestras): cachea resultados deterministas o saca el cálculo de la ruta de la petición. Valida el cambio repitiendo el mismo escenario y comparando con compare_runs.");
            }
            else if (status != HypothesisStatus.Contradicted)
            {
                if (run.RuntimeSnapshots.Any(s => s.Kind == "cpu-sample"))
                {
                    against.Add(new EvidenceItem("runtime", "cpu-sample", "La muestra de CPU no muestra un método de usuario dominante (la CPU puede ir a GC, nativo o al framework)"));
                }

                missing.Add("Muestra de CPU bajo carga con un método de código de usuario dominante.");
            }

            var effect = LatencySymptom ? "La degradación bajo carga" : "La saturación de CPU bajo carga";
            var statement = status switch
            {
                HypothesisStatus.Confirmed => $"{effect} se explica por trabajo de CPU en {hot!.Value.Frame}.",
                HypothesisStatus.Contradicted => "La CPU del proceso es baja durante la degradación: la saturación de CPU no la explica.",
                _ => $"{effect} podría deberse a trabajo de CPU en la ruta de la petición.",
            };
            AddHypothesis("CPU-001", "Trabajo de CPU en la ruta de la petición", statement, status, confidence, support, against, missing,
                status is HypothesisStatus.Supported or HypothesisStatus.New && !run.RuntimeSnapshots.Any(s => s.Kind == "cpu-sample")
                    ? CaptureNext("cpu-sample", "Identificar los métodos que consumen CPU bajo carga.")
                    : null,
                limitations);
        }

        private void HypothesizeThreadPool()
        {
            if (!LatencySymptom && !_starvation)
            {
                return;
            }

            if (_rt is null)
            {
                return;
            }

            if (_rt.ThreadPoolQueueLengthMax is null && _rt.ThreadPoolStarvationAdjustments is null)
            {
                AddHypothesis("TP-001", "Bloqueo de hilos / starvation del ThreadPool",
                    "No hay señales del ThreadPool para evaluar bloqueos síncronos.",
                    HypothesisStatus.Unresolved, Confidence.Low, [], [], ["Longitud de cola, hilos y ajustes del ThreadPool."], null);
                return;
            }

            var art = Artifact(_counters);
            var support = new List<EvidenceItem>();
            var against = new List<EvidenceItem>();
            var queueSignal = QueueSignal();
            var growth = ThreadGrowth();
            var starvation = _rt.ThreadPoolStarvationAdjustments ?? 0;
            if (queueSignal)
            {
                support.Add(new EvidenceItem("runtime", "threadpool.queue", $"Cola del ThreadPool {Fmt(_rt.ThreadPoolQueueLengthLast)} al final (máx. {Fmt(_rt.ThreadPoolQueueLengthMax)})", Round(_rt.ThreadPoolQueueLengthMax), "items", art));
            }
            else
            {
                against.Add(new EvidenceItem("runtime", "threadpool.queue", "La cola del ThreadPool se mantiene prácticamente vacía", Round(_rt.ThreadPoolQueueLengthMax), "items", art));
            }

            if (starvation > 0)
            {
                support.Add(new EvidenceItem("runtime", "threadpool.starvation_adjustments", $"El runtime añadió hilos por starvation {starvation} veces en la ventana", starvation, "events", art));
            }

            if (_rt.ThreadPoolCooperativeBlockingAdjustments is > 0 and var cooperative)
            {
                support.Add(new EvidenceItem("runtime", "threadpool.cooperative_blocking_adjustments",
                    $"El runtime registró {cooperative} ajustes por bloqueo cooperativo: hilos del pool esperando síncronamente tareas (Task.Wait/.Result)", cooperative, "events", art));
            }

            if (growth >= t.ThreadPoolThreadGrowth)
            {
                support.Add(new EvidenceItem("runtime", "threadpool.threads.growth", $"El ThreadPool creció de {Fmt(_rt.ThreadPoolThreadsFirst)} a {Fmt(_rt.ThreadPoolThreadsMax)} hilos", Round(growth), "threads", art));
            }

            var cpuLow = _rt.CpuPercentAvg < t.CpuLowPercent;
            if (_rt.CpuPercentAvg is { } cpu)
            {
                if (cpuLow)
                {
                    support.Add(new EvidenceItem("runtime", "cpu.avg", $"CPU baja ({Pct1(cpu)}) mientras hay trabajo encolado: los hilos esperan en lugar de trabajar", Round(cpu), "%", art));
                }
                else if (cpu >= t.CpuHighPercent)
                {
                    against.Add(new EvidenceItem("runtime", "cpu.avg", $"CPU alta ({Pct1(cpu)}): la cola puede ser consecuencia de saturación de CPU, no de bloqueos", Round(cpu), "%", art));
                }
            }

            var strong = starvation > 0 || growth >= t.ThreadPoolThreadGrowth;
            var status = (queueSignal || strong, strong || cpuLow) switch
            {
                (true, true) => HypothesisStatus.Supported,
                (true, false) => HypothesisStatus.New,
                _ => HypothesisStatus.Contradicted,
            };
            if (status == HypothesisStatus.Supported && _rt.CpuPercentAvg >= t.CpuHighPercent && starvation == 0)
            {
                status = HypothesisStatus.New;
            }

            var confidence = status == HypothesisStatus.Supported ? (starvation > 0 && cpuLow ? Confidence.High : Confidence.Medium) : Confidence.Low;
            var missing = new List<string>();
            var blocking = BlockingEvidence();
            if (blocking is not null && status is HypothesisStatus.Supported or HypothesisStatus.New)
            {
                support.Add(blocking.Value.Evidence);
                status = HypothesisStatus.Confirmed;
                confidence = Confidence.High;
                _recommendations.Add($"Elimina la espera síncrona en {blocking.Value.UserFrame} ({blocking.Value.BlockingFrame}): usa await de extremo a extremo y evita Task.Wait/.Result/Thread.Sleep en la ruta de la petición. Valida con el mismo escenario y compare_runs.");
            }
            else if (status != HypothesisStatus.Contradicted)
            {
                missing.Add("Pilas bajo carga (muestra de CPU/hilos) que localicen la llamada bloqueante en código de usuario.");
            }

            var statement = status switch
            {
                HypothesisStatus.Confirmed => $"Hilos del ThreadPool bloqueados en {blocking!.Value.BlockingFrame} desde {blocking.Value.UserFrame} (espera síncrona / sync-over-async) provocan starvation bajo carga.",
                HypothesisStatus.Contradicted => "No hay presión en el ThreadPool: el bloqueo de hilos no explica la degradación.",
                _ => "Llamadas bloqueantes (sync-over-async, esperas síncronas) podrían estar agotando el ThreadPool bajo carga.",
            };
            AddHypothesis("TP-001", "Bloqueo de hilos / starvation del ThreadPool", statement, status, confidence, support, against, missing,
                status is HypothesisStatus.Supported or HypothesisStatus.New && blocking is null && !run.RuntimeSnapshots.Any(s => s.Kind == "cpu-sample")
                    ? CaptureNext("cpu-sample", "Muestrear pilas bajo carga para localizar esperas bloqueantes.")
                    : null);
        }

        private void HypothesizeGc()
        {
            if (_rt is null)
            {
                return;
            }

            var signals = GcSignals();
            var art = Artifact(_counters);
            var status = signals.Count switch
            {
                >= 2 => HypothesisStatus.Supported,
                1 => HypothesisStatus.New,
                _ => HypothesisStatus.Contradicted,
            };
            if (status == HypothesisStatus.Contradicted && !_latencyDegraded)
            {
                return;
            }

            var against = new List<EvidenceItem>();
            if (signals.Count == 0)
            {
                against.Add(new EvidenceItem("runtime", "gc", $"Asignación {Fmt(_rt.AllocationRateMbPerSec)} MB/s, tiempo en GC máx. {Pct1(_rt.TimeInGcPercentMax)}, {_rt.Gen2Collections?.ToString(CultureInfo.InvariantCulture) ?? "n/d"} GCs gen2: sin presión significativa", Round(_rt.AllocationRateMbPerSec), "MB/s", art));
            }

            var missing = new List<string>();
            var confidence = status == HypothesisStatus.Supported ? Confidence.Medium : Confidence.Low;
            var allocation = AllocationHotSpot();
            if (allocation is not null && status is HypothesisStatus.Supported or HypothesisStatus.New)
            {
                signals.Add(new EvidenceItem("runtime", "allocation-sample.site", $"{allocation.Value.Frame} concentra {Pct1(allocation.Value.Percent)} de los bytes asignados muestreados", Round(allocation.Value.Percent), "%", Artifact(allocation.Value.Snapshot)));
                status = HypothesisStatus.Confirmed;
                confidence = Confidence.High;
                _recommendations.Add($"Reduce asignaciones en {allocation.Value.Frame}: reutiliza buffers (ArrayPool), evita objetos >85 KB por petición y la concatenación repetida de cadenas (StringBuilder). Valida con el mismo escenario y compare_runs.");
            }
            else if (status != HypothesisStatus.Contradicted)
            {
                missing.Add("Muestreo de asignaciones bajo carga que identifique el sitio que más memoria asigna.");
            }

            var impact = LatencySymptom ? "que degrada la latencia" : "que consume CPU y limitará la capacidad aunque con esta carga la latencia aún no se degrade";
            AddHypothesis("GC-001", "Presión de asignaciones / GC",
                status switch
                {
                    HypothesisStatus.Contradicted => "Las señales de GC no muestran presión significativa: el GC no explica la degradación.",
                    HypothesisStatus.Confirmed => $"Las asignaciones excesivas en {allocation!.Value.Frame} generan presión de GC (pausas y CPU de GC) {impact}.",
                    _ => $"Las asignaciones por petición podrían estar generando presión de GC (pausas y CPU de GC) {impact}.",
                },
                status, confidence, signals, against, missing,
                status is HypothesisStatus.Supported or HypothesisStatus.New && !run.RuntimeSnapshots.Any(s => s.Kind == "gc-events")
                    ? CaptureNext("gc-events", "Capturar eventos de GC y muestreo de asignaciones bajo carga.")
                    : null);
        }

        private void NoteGcWithoutSymptom()
        {
            var signals = GcSignals();
            if (signals.Count > 0)
            {
                _recommendations.Add("Sin degradación de latencia, pero hay presión de GC: revisa las asignaciones por petición antes de aumentar la carga.");
            }
        }

        /// <summary>
        /// El counter de contención solo abre la hipótesis: las esperas internas de Task.Wait o de temporizadores
        /// también cuentan. La captura de contención decide por la duración real de las esperas y sus sitios.
        /// </summary>
        private void HypothesizeContention()
        {
            if (_rt?.LockContentionsPerSec is not { } rate || rate < t.LockContentionsPerSec)
            {
                return;
            }

            var counter = new EvidenceItem("runtime", "monitor.lock_contentions.rate", "Contenciones de Monitor por segundo (counter)", Round(rate), "1/s", Artifact(_counters));
            var blockingContext = _hypotheses.Any(h => h.RuleId == "TP-001" && h.Status is HypothesisStatus.Confirmed or HypothesisStatus.Supported)
                ? "Puede ser un efecto secundario de las esperas síncronas (Task.Wait usa monitores internamente)."
                : null;
            var capture = run.RuntimeSnapshots.LastOrDefault(s => s.Kind == "contention-events" && s.Summary?.LockContentionEvents is not null);
            if (capture?.Summary is not { } c)
            {
                AddHypothesis("LOCK-001", "Contención de locks",
                    "La contención de locks bajo carga podría serializar trabajo; el counter por sí solo no indica si las esperas son relevantes." + (blockingContext is null ? string.Empty : " " + blockingContext),
                    HypothesisStatus.New, Confidence.Low, [counter], [],
                    ["Duración de las esperas por lock y sitios de llamada (captura de contención bajo carga)."],
                    CaptureNext("contention-events", "Medir duración y sitios de la contención bajo carga."));
                return;
            }

            var apiP95 = _baseline?.Latency?.P95 ?? 0;
            var art = Artifact(capture);
            var waits = new EvidenceItem("runtime", "contention.wait.p95",
                $"{c.LockContentionEvents} esperas por lock en la captura; p95 {Fmt(c.LockContentionP95Ms)} ms, máx. {Fmt(c.LockContentionMaxMs)} ms, total {Fmt(c.LockContentionTotalMs)} ms (latencia p95 de la API: {Ms(apiP95)})",
                c.LockContentionP95Ms, "ms", art);
            var userSite = capture.HotSpots.FirstOrDefault(h => h.IsUserCode);
            HypothesisStatus status;
            Confidence confidence;
            var support = new List<EvidenceItem> { counter };
            var against = new List<EvidenceItem>();
            if (userSite is not null && c.LockContentionP95Ms >= 0.1 * apiP95)
            {
                support.Add(waits);
                support.Add(new EvidenceItem("runtime", "contention.site", $"Sitio de contención en código de usuario: {userSite.Frame} ({Pct1(userSite.ExclusivePercent)} de los eventos)", userSite.ExclusivePercent, "%", art));
                status = HypothesisStatus.Confirmed;
                confidence = Confidence.High;
                _recommendations.Add($"Reduce el alcance del lock en {userSite.Frame} o sustitúyelo por estructuras concurrentes/particionadas; valida con compare_runs.");
            }
            else if (c.LockContentionP95Ms < Math.Max(5, 0.05 * apiP95))
            {
                against.Add(waits with { Description = waits.Description + ": esperas breves frente a la latencia observada" });
                status = HypothesisStatus.Contradicted;
                confidence = Confidence.Medium;
            }
            else
            {
                support.Add(waits);
                status = HypothesisStatus.Supported;
                confidence = Confidence.Low;
            }

            AddHypothesis("LOCK-001", "Contención de locks",
                status switch
                {
                    HypothesisStatus.Confirmed => $"La contención del lock en {userSite!.Frame} serializa el trabajo bajo carga.",
                    HypothesisStatus.Contradicted => "La contención existe pero sus esperas son breves frente a la latencia: no explica la degradación." + (blockingContext is null ? string.Empty : " " + blockingContext),
                    _ => "La contención de locks bajo carga produce esperas relevantes; los sitios de llamada no están resueltos.",
                },
                status, confidence, support, against,
                status is HypothesisStatus.Supported ? ["Sitios de llamada de la contención (pilas)."] : [],
                null);
        }

        private void HypothesizeDependency()
        {
            var smoke = _smoke!;
            var baseline = _baseline!;
            var declared = run.Environment?.Dependencies ?? [];
            if (!LatencySymptom)
            {
                return;
            }

            if (baseline.DependencyCalls.Count == 0)
            {
                if (declared.Count > 0 && (_latencySlow || _latencyDegraded))
                {
                    AddHypothesis("DEP-001", "Latencia de dependencias",
                        "Las dependencias declaradas podrían aportar latencia, pero no hay tiempos por dependencia en esta ejecución.",
                        HypothesisStatus.Unresolved, Confidence.Low, [], [],
                        ["Tiempos de llamadas a dependencias (estadísticas del mock o trazas distribuidas, fase 2)."], null);
                }

                return;
            }

            foreach (var group in baseline.DependencyCalls.GroupBy(c => c.Dependency))
            {
                var calls = group.Sum(c => c.Requests);
                var avg = WeightedAvg(group.ToList()) ?? 0;
                var perRequest = baseline.TotalRequests > 0 ? (double)calls / baseline.TotalRequests : 0;
                var dependencyMsPerRequest = perRequest * avg;
                var apiAvg = baseline.Latency?.Avg ?? smoke.Latency?.Avg;
                var explained = apiAvg is > 0 ? dependencyMsPerRequest / apiAvg.Value : 0;
                var check = declared.FirstOrDefault(d => d.Name == group.Key);
                var support = new List<EvidenceItem>();
                var against = new List<EvidenceItem>();
                var evidence = new EvidenceItem("mock", $"{group.Key}.time_per_request",
                    $"{Fmt(perRequest)} llamadas × {Ms(avg)} ≈ {Ms(dependencyMsPerRequest)} por petición frente a {Ms(apiAvg)} de latencia media de la API ({Pct(explained)})",
                    Round(dependencyMsPerRequest), "ms");
                if (explained >= t.DependencyExplainedRatio)
                {
                    support.Add(evidence);
                }
                else
                {
                    against.Add(evidence);
                }

                if (_rt?.CpuPercentAvg is { } cpu && cpu >= t.CpuHighPercent)
                {
                    against.Add(new EvidenceItem("runtime", "cpu.avg", $"CPU alta ({Pct1(cpu)}): parte de la latencia puede ser CPU", Round(cpu), "%", Artifact(_counters)));
                }

                // Si la suma de latencias de N llamadas explica la latencia observada, las llamadas son secuenciales.
                var sequential = perRequest >= 2 && explained is >= 0.8 and <= 1.25;
                var status = explained >= t.DependencyExplainedRatio
                    ? (sequential ? HypothesisStatus.Confirmed : HypothesisStatus.Supported)
                    : explained < 0.3 ? HypothesisStatus.Contradicted : HypothesisStatus.New;
                var confidence = status switch
                {
                    HypothesisStatus.Confirmed => Confidence.High,
                    HypothesisStatus.Supported => Confidence.Medium,
                    _ => Confidence.Low,
                };
                var statement = status switch
                {
                    HypothesisStatus.Confirmed => $"La latencia está dominada por {Fmt(perRequest)} llamadas secuenciales a '{group.Key}' por petición (patrón N+1): la suma de sus latencias explica la latencia observada.",
                    HypothesisStatus.Supported => $"Las llamadas a '{group.Key}' explican la mayor parte de la latencia de la API.",
                    HypothesisStatus.Contradicted => $"Las llamadas a '{group.Key}' explican una parte menor de la latencia.",
                    _ => $"Las llamadas a '{group.Key}' podrían aportar latencia relevante.",
                };
                var limitations = new List<string>();
                if (check?.Fidelity == FidelityStatus.Simulated)
                {
                    limitations.Add($"'{group.Key}' es un mock: su latencia es la configurada en el contrato; la dependencia real puede comportarse distinto.");
                }

                if (status is HypothesisStatus.Confirmed or HypothesisStatus.Supported)
                {
                    _recommendations.Add(perRequest >= 2
                        ? $"Agrupa las llamadas a '{group.Key}' (endpoint por lotes) o paralelízalas con límites, y considera caché: hoy se hacen {Fmt(perRequest)} llamadas por petición."
                        : $"Reduce la latencia percibida de '{group.Key}' (caché, timeouts adecuados) y verifica su comportamiento contra la dependencia real.");
                }

                AddHypothesis("DEP-001", $"Latencia de la dependencia '{group.Key}'", statement, status, confidence, support, against,
                    status is HypothesisStatus.Confirmed ? [] : ["Tiempos por llamada correlacionados con cada petición (trazas distribuidas, fase 2)."],
                    null, limitations);
            }
        }

        private void HypothesizeErrors()
        {
            if (!_errorsHigh)
            {
                return;
            }

            var baseline = _baseline!;
            var classes = baseline.StatusClasses;
            var server = classes.GetValueOrDefault("5xx");
            var network = classes.GetValueOrDefault("0");
            var client = classes.GetValueOrDefault("4xx");
            var mockFailures = baseline.DependencyCalls.Sum(c => c.Failures);
            var art = Artifact(baseline);

            if (server > 0 && mockFailures > 0)
            {
                // Recuentos que coinciden (≥95%) son evidencia discriminante: cada fallo del mock produce un 5xx de la API.
                var ratio = (double)Math.Min(server, mockFailures) / Math.Max(server, mockFailures);
                var status = ratio >= 0.95 ? HypothesisStatus.Confirmed : ratio >= 0.5 ? HypothesisStatus.Supported : HypothesisStatus.New;
                AddHypothesis("ERR-002", "Errores propagados desde una dependencia",
                    $"Los {server} errores 5xx de la API se corresponden con {mockFailures} fallos servidos por la dependencia simulada: la API propaga los errores de la dependencia.",
                    status, status switch { HypothesisStatus.Confirmed => Confidence.High, HypothesisStatus.Supported => Confidence.Medium, _ => Confidence.Low },
                    [new EvidenceItem("k6", "status.5xx", "Respuestas 5xx de la API", server, "requests", art), new EvidenceItem("mock", "failures", "Fallos devueltos por el mock", mockFailures, "requests")],
                    [], ["Correlación por petición (trazas) para confirmar que cada 5xx proviene de un fallo de la dependencia."], null,
                    ["Los errores provienen de la tasa de error configurada en el mock; no representan necesariamente a la dependencia real."]);
                _recommendations.Add("Define el comportamiento ante fallos de la dependencia (timeouts, reintentos con backoff, circuit breaker o degradación controlada) y verifícalo contra la dependencia real.");
            }
            else if (server > 0)
            {
                var support = new List<EvidenceItem> { new("k6", "status.5xx", "Respuestas 5xx de la API", server, "requests", art) };
                if (_rt?.ExceptionsPerSec >= t.ExceptionsPerSec)
                {
                    support.Add(new EvidenceItem("runtime", "exceptions.rate", "Excepciones por segundo", Round(_rt.ExceptionsPerSec), "1/s", Artifact(_counters)));
                }

                AddHypothesis("ERR-001", "Errores del servidor bajo carga",
                    "La API devuelve errores 5xx bajo carga (excepciones, timeouts internos o agotamiento de recursos).",
                    support.Count > 1 ? HypothesisStatus.Supported : HypothesisStatus.New, Confidence.Low, support, [],
                    ["Tipos de excepción y pilas (logs saneados de la API o eventos de excepción)."],
                    CaptureNext("exception-events", "Capturar excepciones del runtime bajo carga."));
            }

            if (network > 0)
            {
                AddHypothesis("ERR-003", "Timeouts o conexiones rechazadas bajo carga",
                    "Parte de las peticiones no obtuvo respuesta (timeouts/conexiones rechazadas): posible saturación del servidor o del host compartido.",
                    HypothesisStatus.New, Confidence.Low,
                    [new EvidenceItem("k6", "status.0", "Peticiones sin respuesta HTTP", network, "requests", art)], [],
                    ["Estado del proceso y del host durante la prueba; logs saneados de la API."], null);
            }

            if (client > 0)
            {
                AddObservation("functional", $"{client} respuestas 4xx bajo carga: indican un problema funcional o de datos de prueba, no de rendimiento.",
                    [new EvidenceItem("k6", "status.4xx", "Respuestas 4xx", client, "requests", art)]);
            }
        }

        private void HypothesizeMemoryGrowth()
        {
            if (_rt?.GcHeapMbFirst is not { } first || _rt.GcHeapMbLast is not { } last || first <= 0)
            {
                return;
            }

            var growth = last / first;
            if (growth < t.HeapGrowthRatio || last - first < t.HeapGrowthMinMb)
            {
                return;
            }

            AddHypothesis("MEM-001", "Crecimiento de memoria",
                $"El heap gestionado crece de {Fmt(first)} MB a {Fmt(last)} MB durante la ventana; podría ser una fuga o una caché sin límite, o simplemente calentamiento.",
                HypothesisStatus.Unresolved, Confidence.Low,
                [new EvidenceItem("runtime", "gc.heap.growth", "Relación heap final/inicial", Round(growth), "x", Artifact(_counters))], [],
                ["Una ejecución más larga (soak, fase 2) que muestre si el heap se estabiliza tras GCs de generación 2."], null);
        }

        private List<EvidenceItem> GcSignals()
        {
            var signals = new List<EvidenceItem>();
            if (_rt is null)
            {
                return signals;
            }

            // Las asignaciones se juzgan por petición: un servicio sano a 10.000 req/s asigna cientos de MB/s.
            var art = Artifact(_counters);
            var alloc = _rt.AllocationRateMbPerSec;
            var rps = _baseline?.RequestsPerSecond;
            var kbPerRequest = alloc is { } a && rps is > 0 ? a * 1000 / rps.Value : (double?)null;
            if (kbPerRequest >= t.AllocationKbPerRequest || alloc >= t.AllocationRateMbPerSec)
            {
                signals.Add(new EvidenceItem("runtime", "gc.allocation_per_request",
                    $"asignación {Fmt(alloc)} MB/s ≈ {Fmt(kbPerRequest)} KB por petición", Round(kbPerRequest), "KB/req", art));
            }

            if (_rt.TimeInGcPercentMax >= t.TimeInGcPercent)
            {
                signals.Add(new EvidenceItem("runtime", "gc.time_in_gc.max", $"tiempo en GC hasta {Pct1(_rt.TimeInGcPercentMax)}", Round(_rt.TimeInGcPercentMax), "%", art));
            }

            var requests = _baseline?.TotalRequests ?? 0;
            if (_rt.Gen2Collections is { } gen2 && gen2 >= t.Gen2MinCount && requests > 0 && gen2 * 1000.0 / requests >= t.Gen2Per1000Requests)
            {
                signals.Add(new EvidenceItem("runtime", "gc.gen2_collections", $"{gen2} GCs de generación 2 en la ventana ({Fmt(gen2 * 1000.0 / requests)} por cada 1.000 peticiones)", gen2, "collections", art));
            }

            return signals;
        }

        private bool QueueSignal() =>
            _rt is not null && (_rt.ThreadPoolQueueLengthLast >= t.ThreadPoolQueueLast || _rt.ThreadPoolQueueLengthMax >= t.ThreadPoolQueueMax);

        private double ThreadGrowth() =>
            _rt?.ThreadPoolThreadsMax is { } max && _rt.ThreadPoolThreadsFirst is { } first ? max - first : 0;

        /// <summary>Método de usuario que domina la muestra de CPU (hoja del camino caliente o mayor tiempo propio).</summary>
        private (string Frame, double? Percent, RuntimeSnapshot Snapshot)? CpuHotSpot()
        {
            foreach (var snapshot in run.RuntimeSnapshots.Where(s => s.Kind == "cpu-sample").Reverse())
            {
                if (BlockingDominates(snapshot))
                {
                    continue;
                }

                var leaf = snapshot.HotPath.LastOrDefault();
                if (leaf is { IsUserCode: true, IsBlocking: false } && !IsGlueFrame(leaf.Frame) && leaf.InclusivePercent >= t.HotSpotConfirmPercent)
                {
                    return (leaf.Frame, leaf.InclusivePercent, snapshot);
                }

                var top = snapshot.HotSpots
                    .Where(h => h.IsUserCode && !h.IsBlocking && !IsGlueFrame(h.Frame) && h.ExclusivePercent >= t.HotSpotConfirmPercent)
                    .OrderByDescending(h => h.ExclusivePercent)
                    .FirstOrDefault();
                if (top is not null)
                {
                    return (top.Frame, top.ExclusivePercent, snapshot);
                }
            }

            return null;
        }

        /// <summary>
        /// La muestra está dominada por bloqueo en la ruta de la petición solo si el camino caliente va de código de
        /// usuario a una espera. Las esperas ociosas del ThreadPool (LowLevelLifoSemaphore) o de hilos de fondo no cuentan.
        /// </summary>
        private bool BlockingDominates(RuntimeSnapshot snapshot) => BlockingPath(snapshot) is not null;

        private (HotSpot Leaf, HotSpot User)? BlockingPath(RuntimeSnapshot snapshot)
        {
            var path = snapshot.HotPath;
            if (path.Count == 0 || path[^1] is not { IsBlocking: true } leaf || leaf.InclusivePercent < t.HotSpotConfirmPercent)
            {
                return null;
            }

            var user = path.Take(path.Count - 1).LastOrDefault(h => h.IsUserCode && !h.IsBlocking && !IsGlueFrame(h.Frame));
            return user is null ? null : (leaf, user);
        }

        /// <summary>
        /// Espera bloqueante dominante y el marco de usuario que la provoca. Solo se acepta si el camino caliente
        /// demuestra la relación (código de usuario → espera): las esperas ociosas del ThreadPool o de hilos de fondo
        /// también aparecen en las muestras y no prueban nada por sí solas.
        /// </summary>
        private (EvidenceItem Evidence, string UserFrame, string BlockingFrame)? BlockingEvidence()
        {
            foreach (var snapshot in run.RuntimeSnapshots.Where(s => s.Kind == "cpu-sample").Reverse())
            {
                if (BlockingPath(snapshot) is not var (leaf, user))
                {
                    continue;
                }

                return (new EvidenceItem("runtime", "cpu-sample.blocking",
                    $"El camino caliente termina en {leaf.Frame} ({Pct1(leaf.InclusivePercent)} de las muestras de pila) y pasa por {user.Frame}",
                    Round(leaf.InclusivePercent), "%", Artifact(snapshot)), user.Frame, leaf.Frame);
            }

            return null;
        }

        /// <summary>Sitio de código de usuario que concentra los bytes asignados (muestreo de asignaciones).</summary>
        private (string Frame, double? Percent, RuntimeSnapshot Snapshot)? AllocationHotSpot()
        {
            foreach (var snapshot in run.RuntimeSnapshots.Where(s => s.Kind == "gc-events").Reverse())
            {
                var top = snapshot.HotSpots
                    .Where(h => h.IsUserCode && !IsGlueFrame(h.Frame) && (h.ExclusivePercent ?? h.InclusivePercent) >= t.HotSpotConfirmPercent)
                    .OrderByDescending(h => h.ExclusivePercent ?? h.InclusivePercent)
                    .FirstOrDefault();
                if (top is not null)
                {
                    return (top.Frame, top.ExclusivePercent ?? top.InclusivePercent, snapshot);
                }
            }

            return null;
        }

        private DiagnosisVerdict DecideVerdict(bool hasSymptom)
        {
            if (_hypotheses.Any(h => h.Status == HypothesisStatus.Confirmed))
            {
                return DiagnosisVerdict.CauseConfirmed;
            }

            if (_hypotheses.Any(h => h.Status == HypothesisStatus.Supported))
            {
                return DiagnosisVerdict.SymptomsObserved;
            }

            if (hasSymptom)
            {
                _questions.Add("Se observaron síntomas sin una hipótesis sustentada: recopila la evidencia indicada en las hipótesis antes de concluir.");
                return DiagnosisVerdict.Inconclusive;
            }

            _limitations.Add($"Sin degradación detectada con la carga probada ({_baseline!.Load.Vus} VUs durante {_baseline.Load.DurationSeconds}s): no implica ausencia de problemas con más carga, otros datos u otro ambiente.");
            return DiagnosisVerdict.NoDegradationDetected;
        }

        private void AddEnvironmentLimitations()
        {
            _limitations.Add("Perfil local: k6 y la API comparten máquina y CPU; los valores absolutos no representan la capacidad en el ambiente objetivo.");
            foreach (var dependency in run.Environment?.Dependencies ?? [])
            {
                var text = dependency.Fidelity switch
                {
                    FidelityStatus.Simulated => $"La dependencia '{dependency.Name}' es simulada (mock): las conclusiones se limitan a la API con ese comportamiento declarado.",
                    FidelityStatus.Unavailable => $"La dependencia '{dependency.Name}' no estuvo disponible.",
                    FidelityStatus.NotVerified => $"La dependencia '{dependency.Name}' no se pudo verificar.",
                    _ => null,
                };
                if (text is not null)
                {
                    _limitations.Add(text);
                }
            }

            if (run.Target?.Runtime?.Compatibility == "untested")
            {
                _limitations.Add($"El framework {run.Target.Runtime.Framework} no está en la matriz probada: resultados no verificados.");
            }

            if (run.Endpoints is { Excluded.Count: > 0 } plan)
            {
                _limitations.Add($"{plan.Excluded.Count} endpoint(s) se excluyeron del escenario (ver plan de endpoints) y no se evaluaron.");
            }
        }

        private TriageResult Build(DiagnosisVerdict verdict)
        {
            foreach (var h in _hypotheses.Where(h => h.NextAction is not null))
            {
                if (_next.All(n => n.Tool != h.NextAction!.Tool || !n.Arguments.SequenceEqual(h.NextAction.Arguments)))
                {
                    _next.Add(h.NextAction!);
                }
            }

            return new TriageResult(verdict, _observations, _hypotheses, _correlations, _next, _questions.Distinct().ToList(),
                _limitations.Distinct().ToList(), _recommendations.Distinct().ToList());
        }

        private void AddObservation(string category, string description, IReadOnlyList<EvidenceItem> evidence) =>
            _observations.Add(new Observation($"OBS-{++_obsCounter:00}", category, description, evidence));

        private void AddHypothesis(
            string ruleId, string title, string statement, HypothesisStatus status, Confidence confidence,
            IReadOnlyList<EvidenceItem> support, IReadOnlyList<EvidenceItem> against, IReadOnlyList<string> missing,
            NextAction? next, IReadOnlyList<string>? limitations = null) =>
            _hypotheses.Add(new Hypothesis
            {
                Id = $"H-{++_hypCounter:00}",
                RuleId = ruleId,
                Title = title,
                Statement = statement,
                Status = status,
                Confidence = confidence,
                SupportingEvidence = support,
                ContradictingEvidence = against,
                MissingEvidence = missing,
                NextAction = next,
                Limitations = limitations ?? [],
            });

        private NextAction CaptureNext(string kind, string rationale) =>
            new("collect_runtime_evidence",
                new Dictionary<string, string> { ["runId"] = run.RunId, ["kind"] = kind, ["durationSeconds"] = "10", ["withLoad"] = "true" },
                rationale);

        private NextAction Next(string tool, string rationale) =>
            new(tool, new Dictionary<string, string> { ["runId"] = run.RunId }, rationale);

        private static IReadOnlyList<EvidenceItem> FunctionalEvidence(LoadTestRun smoke) =>
            smoke.Endpoints.Where(e => e.ChecksFailed > 0)
                .Select(e => new EvidenceItem("k6", $"checks{{name:{e.Name}}}", $"{e.ChecksFailed} comprobaciones fallidas en {e.Method} {e.Path}", e.ChecksFailed, "checks", Artifact(smoke)))
                .DefaultIfEmpty(new EvidenceItem("k6", "checks", $"{smoke.ChecksFailed} comprobaciones fallidas", smoke.ChecksFailed, "checks", Artifact(smoke)))
                .ToList();

        private static string? Artifact(LoadTestRun? load) => load?.Artifacts.FirstOrDefault();

        private static string? Artifact(RuntimeSnapshot? snapshot) => snapshot?.Artifacts.FirstOrDefault();

        private static double? WeightedAvg(IReadOnlyList<DependencyCallStats> calls)
        {
            var total = calls.Sum(c => c.Requests);
            return total == 0 ? null : calls.Sum(c => (c.AvgLatencyMs ?? 0) * c.Requests) / total;
        }

        private static double? Ratio(double? a, double? b) => a is { } x && b is { } y && y > 0 ? x / y : null;

        private static double? Round(double? v) => v is { } x ? Math.Round(x, 2) : null;

        private static string Fmt(double? v) => v is { } x ? x.ToString("0.##", CultureInfo.InvariantCulture) : "n/d";

        private static string Ms(double? v) => v is { } x ? x.ToString("0.#", CultureInfo.InvariantCulture) + " ms" : "n/d";

        private static string Pct(double? fraction) => fraction is { } x ? (x * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%" : "n/d";

        private static string Pct1(double? percent) => percent is { } x ? x.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "n/d";

        private static string FormatClasses(IReadOnlyDictionary<string, long> classes) =>
            classes.Count == 0 ? "n/d" : string.Join(", ", classes.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key}={kv.Value}"));

        private static string Kebab<TEnum>(TEnum value) where TEnum : struct, Enum =>
            System.Text.Json.JsonNamingPolicy.KebabCaseLower.ConvertName(value.ToString());
    }
}
