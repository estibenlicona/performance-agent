namespace PerfAgent.Core.Contracts;

/// <summary>Evidencia del runtime .NET normalizada (independiente del proveedor upstream).</summary>
public sealed record RuntimeSnapshot
{
    public required string Id { get; init; }

    /// <summary>PID del proceso de laboratorio; siempre lo asigna la fachada, nunca el modelo.</summary>
    public required int ProcessId { get; init; }

    /// <summary>counters, triage, preflight, info, cpu-sample, gc-events, threadpool-events, contention-events, exception-events.</summary>
    public required string Kind { get; init; }

    public string? LoadTestId { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? EndedAt { get; init; }

    public string? RuntimeVersion { get; init; }

    public RuntimeSummary? Summary { get; init; }

    public IReadOnlyList<MetricSeries> Series { get; init; } = [];

    /// <summary>Métodos con más muestras (CPU o asignaciones), en % del total de muestras.</summary>
    public IReadOnlyList<HotSpot> HotSpots { get; init; } = [];

    /// <summary>Camino caliente de la raíz a la hoja (vista hot-path del proveedor).</summary>
    public IReadOnlyList<HotSpot> HotPath { get; init; } = [];

    /// <summary>Hallazgos textuales del proveedor (saneados y tratados como datos no confiables), citados tal cual.</summary>
    public IReadOnlyList<string> ProviderFindings { get; init; } = [];

    public IReadOnlyList<string> UnavailableSignals { get; init; } = [];

    /// <summary>Origen de cada grupo de señales (p. ej. <c>cpu: os-process-sampler</c>).</summary>
    public IReadOnlyList<string> Sources { get; init; } = [];

    public IReadOnlyList<ProviderInvocation> Providers { get; init; } = [];

    public IReadOnlyList<string> Artifacts { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Resumen de señales del runtime en la ventana capturada. Valores nulos = señal no disponible.
/// Los counters de EventPipe del proveedor son muestras puntuales (último intervalo de 1 s y máximo de la ventana);
/// los recuentos de GC y de ajustes del ThreadPool son exactos para la ventana; la CPU media sale del muestreo del SO.
/// </summary>
public sealed record RuntimeSummary
{
    /// <summary>CPU media del proceso en la ventana, % de toda la máquina (muestreo del SO cada segundo).</summary>
    public double? CpuPercentAvg { get; init; }

    public double? CpuPercentMax { get; init; }

    public int? ProcessorCount { get; init; }

    public double? WorkingSetMbMax { get; init; }

    public double? GcHeapMbFirst { get; init; }

    public double? GcHeapMbMax { get; init; }

    public double? GcHeapMbLast { get; init; }

    /// <summary>Tasa de asignación del último intervalo de 1 s (representativa bajo carga estable).</summary>
    public double? AllocationRateMbPerSec { get; init; }

    public double? AllocationRateMbPerSecMax { get; init; }

    public double? TimeInGcPercentMax { get; init; }

    public double? GcPauseMsTotal { get; init; }

    public double? GcPauseMsMax { get; init; }

    /// <summary>Recolecciones observadas en la ventana (exactas, colector de eventos de GC).</summary>
    public long? Gen0Collections { get; init; }

    public long? Gen1Collections { get; init; }

    public long? Gen2Collections { get; init; }

    public double? ThreadPoolThreadsFirst { get; init; }

    public double? ThreadPoolThreadsMax { get; init; }

    public double? ThreadPoolQueueLengthLast { get; init; }

    public double? ThreadPoolQueueLengthMax { get; init; }

    /// <summary>Ajustes del ThreadPool por starvation observados en la ventana (eventos del runtime).</summary>
    public long? ThreadPoolStarvationAdjustments { get; init; }

    public long? ThreadPoolCooperativeBlockingAdjustments { get; init; }

    public double? LockContentionsPerSec { get; init; }

    /// <summary>Eventos de contención capturados (colector de contención) y duración de las esperas por lock.</summary>
    public long? LockContentionEvents { get; init; }

    public double? LockContentionTotalMs { get; init; }

    public double? LockContentionP95Ms { get; init; }

    public double? LockContentionMaxMs { get; init; }

    public double? ExceptionsPerSec { get; init; }

    /// <summary>Peticiones/s vistas por ASP.NET Core en el servidor (último intervalo).</summary>
    public double? ServerRequestsPerSec { get; init; }

    public double? ServerCurrentRequestsMax { get; init; }
}

public sealed record MetricSeries(string Name, string Unit, string Source, IReadOnlyList<MetricPoint> Points, MetricAggregate Aggregate);

public sealed record MetricPoint(DateTimeOffset Timestamp, double Value);

public sealed record MetricAggregate(double? Min, double? Avg, double? Max, double? First, double? Last, int Samples)
{
    public static MetricAggregate From(IReadOnlyList<MetricPoint> points)
    {
        if (points.Count == 0)
        {
            return new MetricAggregate(null, null, null, null, null, 0);
        }

        return new MetricAggregate(
            Math.Round(points.Min(p => p.Value), 3),
            Math.Round(points.Average(p => p.Value), 3),
            Math.Round(points.Max(p => p.Value), 3),
            points[0].Value,
            points[^1].Value,
            points.Count);
    }
}

/// <param name="Frame">Método (con tipo), tal como lo reporta el proveedor.</param>
/// <param name="InclusivePercent">% de muestras en las que aparece el método en la pila.</param>
/// <param name="ExclusivePercent">% de muestras con el método como hoja (tiempo propio).</param>
/// <param name="IsBlocking">true si es una espera bloqueante conocida (Task.Wait, Thread.Sleep, Monitor.Wait...).</param>
public sealed record HotSpot(string Frame, string? Module, double? InclusivePercent, double? ExclusivePercent, bool IsUserCode, bool IsBlocking = false);
