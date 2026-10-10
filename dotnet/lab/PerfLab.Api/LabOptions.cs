namespace PerfLab.Api;

/// <summary>
/// Configuración del laboratorio. Los fallos se activan por configuración
/// (p. ej. variable de entorno <c>Lab__Faults__Cpu=true</c>) y están desactivados por defecto.
/// </summary>
public sealed class LabOptions
{
    public const string SectionName = "Lab";

    public FaultOptions Faults { get; set; } = new();

    /// <summary>Trabajo de CPU por petición cuando el fallo <c>Cpu</c> está activo.</summary>
    public int CpuPrimeLimit { get; set; } = 60_000;

    /// <summary>Bloques temporales grandes (LOH) por petición cuando el fallo <c>Allocations</c> está activo.</summary>
    public int AllocationLargeBuffers { get; set; } = 16;

    public int AllocationLargeBufferBytes { get; set; } = 128 * 1024;

    /// <summary>Fragmentos concatenados con <c>string +=</c> cuando el fallo <c>Allocations</c> está activo.</summary>
    public int AllocationStringPieces { get; set; } = 600;

    /// <summary>Espera simulada de E/S por petición (con o sin bloqueo, según el fallo <c>Blocking</c>).</summary>
    public int BlockingDelayMs { get; set; } = 100;

    /// <summary><c>SyncOverAsync</c> (Task.Wait) o <c>Sleep</c> (Thread.Sleep) cuando el fallo <c>Blocking</c> está activo.</summary>
    public string BlockingMode { get; set; } = "SyncOverAsync";

    /// <summary>Artículos por cotización: con el fallo N+1 se hace una llamada a la dependencia por artículo.</summary>
    public int QuoteItems { get; set; } = 5;

    /// <summary>Latencia de la dependencia simulada en proceso cuando no hay mock HTTP configurado.</summary>
    public int InProcessDependencyLatencyMs { get; set; } = 40;

    public bool IsWithinSafetyBounds() =>
        CpuPrimeLimit is > 0 and <= 2_000_000
        && AllocationLargeBuffers is >= 0 and <= 64
        && AllocationLargeBufferBytes is > 0 and <= 1024 * 1024
        && AllocationStringPieces is >= 0 and <= 10_000
        && BlockingDelayMs is >= 0 and <= 1_000
        && BlockingMode is "SyncOverAsync" or "Sleep"
        && QuoteItems is > 0 and <= 20
        && InProcessDependencyLatencyMs is >= 0 and <= 2_000;
}

public sealed class FaultOptions
{
    public bool Cpu { get; set; }

    public bool Allocations { get; set; }

    public bool Blocking { get; set; }

    public bool DependencyNPlusOne { get; set; }
}
