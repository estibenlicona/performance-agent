namespace PerfAgent.Core.Triage;

/// <summary>
/// Umbrales de las reglas de triage. Son heurísticas documentadas (docs/triage-rules.md), no SLOs:
/// solo deciden qué síntoma se observa y qué hipótesis merece una investigación de seguimiento.
/// </summary>
public sealed record TriageThresholds
{
    /// <summary>p95 del baseline / p95 del smoke a partir del cual la latencia se considera degradada bajo carga.</summary>
    public double LatencyDegradationRatio { get; init; } = 2.5;

    /// <summary>Aumento absoluto mínimo del p95 (ms) para hablar de degradación: evita falsos positivos por ruido en endpoints sub-milisegundo.</summary>
    public double LatencyDegradationMinDeltaMs { get; init; } = 20;

    /// <summary>p95 (ms) a partir del cual un endpoint se considera lento incluso sin concurrencia.</summary>
    public double SlowRequestMs { get; init; } = 150;

    public double ErrorRate { get; init; } = 0.01;

    /// <summary>CPU del proceso (% de toda la máquina) que indica saturación.</summary>
    public double CpuHighPercent { get; init; } = 70;

    public double CpuLowPercent { get; init; } = 40;

    /// <summary>Longitud de cola del ThreadPool en el último intervalo que indica trabajo pendiente.</summary>
    public double ThreadPoolQueueLast { get; init; } = 2;

    public double ThreadPoolQueueMax { get; init; } = 10;

    /// <summary>Hilos añadidos al ThreadPool durante la ventana (inyección por starvation).</summary>
    public double ThreadPoolThreadGrowth { get; init; } = 4;

    /// <summary>KB asignados por petición atendida a partir de los cuales se considera asignación excesiva.</summary>
    public double AllocationKbPerRequest { get; init; } = 256;

    /// <summary>Tasa de asignación absoluta (MB/s) que se considera excesiva con independencia del throughput.</summary>
    public double AllocationRateMbPerSec { get; init; } = 1000;

    public double TimeInGcPercent { get; init; } = 10;

    /// <summary>GCs de generación 2 por cada 1.000 peticiones (relativo a la carga, no al tiempo).</summary>
    public double Gen2Per1000Requests { get; init; } = 1;

    public long Gen2MinCount { get; init; } = 5;

    public double LockContentionsPerSec { get; init; } = 50;

    public double ExceptionsPerSec { get; init; } = 5;

    public double HeapGrowthRatio { get; init; } = 1.5;

    public double HeapGrowthMinMb { get; init; } = 50;

    /// <summary>p95 del último tercio / p95 del primer tercio de la línea temporal.</summary>
    public double LatencyTrendRatio { get; init; } = 1.5;

    /// <summary>% mínimo de muestras de un marco de código de usuario para confirmar un hotspot.</summary>
    public double HotSpotConfirmPercent { get; init; } = 15;

    /// <summary>Solapamiento mínimo entre la ventana de k6 y la del runtime para considerarlas comparables.</summary>
    public double MinOverlapRatio { get; init; } = 0.8;

    /// <summary>Fracción de la latencia explicada por llamadas a la dependencia para apoyar la hipótesis de dependencia.</summary>
    public double DependencyExplainedRatio { get; init; } = 0.6;

    public static TriageThresholds Default { get; } = new();
}
