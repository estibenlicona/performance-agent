using System.Diagnostics;

namespace PerfAgent.Adapters.Process;

/// <param name="CpuPercent">CPU del proceso como porcentaje de toda la máquina (0–100).</param>
public sealed record ResourceSample(DateTimeOffset Timestamp, double CpuPercent, double WorkingSetMb);

/// <summary>
/// Muestreo a nivel de sistema operativo (CPU y working set, cada segundo) del proceso de laboratorio que la
/// fachada inició. Complementa al proveedor de diagnóstico, cuyos counters son agregados sin marcas de tiempo:
/// aporta la serie temporal para correlacionar con la ventana de k6. No usa EventPipe ni duplica analizadores CLR.
/// </summary>
public sealed class ProcessResourceSampler : IAsyncDisposable
{
    private readonly List<ResourceSample> _samples = [];
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    private ProcessResourceSampler(int processId, TimeSpan interval) => _loop = Task.Run(() => LoopAsync(processId, interval, _cts.Token));

    public static ProcessResourceSampler Start(int processId, TimeSpan interval) => new(processId, interval);

    public async Task<IReadOnlyList<ResourceSample>> StopAsync()
    {
        await _cts.CancelAsync();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
            // Fin normal.
        }

        lock (_lock)
        {
            return _samples.ToList();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts.Dispose();
    }

    private async Task LoopAsync(int processId, TimeSpan interval, CancellationToken cancellationToken)
    {
        using var process = System.Diagnostics.Process.GetProcessById(processId);
        var cores = Environment.ProcessorCount;
        var lastCpu = process.TotalProcessorTime;
        var lastWall = Stopwatch.GetTimestamp();
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                process.Refresh();
                if (process.HasExited)
                {
                    return;
                }

                var cpu = process.TotalProcessorTime;
                var now = Stopwatch.GetTimestamp();
                var wall = Stopwatch.GetElapsedTime(lastWall, now).TotalMilliseconds;
                var percent = wall <= 0 ? 0 : (cpu - lastCpu).TotalMilliseconds / (wall * cores) * 100;
                lastCpu = cpu;
                lastWall = now;
                lock (_lock)
                {
                    _samples.Add(new ResourceSample(DateTimeOffset.UtcNow, Math.Round(Math.Clamp(percent, 0, 100), 2), Math.Round(process.WorkingSet64 / 1024d / 1024d, 2)));
                }
            }
            catch (InvalidOperationException)
            {
                return;
            }
        }
    }
}
