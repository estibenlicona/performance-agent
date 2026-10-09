using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Options;

namespace PerfLab.Api;

/// <summary>Fallo <c>Cpu</c>: trabajo de CPU innecesario en cada petición (sin caché).</summary>
public sealed class CpuReportService(IOptions<LabOptions> options)
{
    private readonly LabOptions _options = options.Value;
    private int _cachedPrimeCount = -1;

    public int GetPrimeCount(bool faulty)
    {
        if (faulty)
        {
            return CountPrimesNaive(_options.CpuPrimeLimit);
        }

        var cached = Volatile.Read(ref _cachedPrimeCount);
        if (cached < 0)
        {
            cached = CountPrimesNaive(_options.CpuPrimeLimit);
            Volatile.Write(ref _cachedPrimeCount, cached);
        }

        return cached;
    }

    // División por tentativa deliberadamente ingenua: O(n·√n). NoInlining para que el
    // método aparezca con su nombre en las muestras de CPU.
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static int CountPrimesNaive(int limit)
    {
        var count = 0;
        for (var n = 2; n <= limit; n++)
        {
            var isPrime = true;
            for (var d = 2; d * d <= n; d++)
            {
                if (n % d == 0)
                {
                    isPrime = false;
                    break;
                }
            }

            if (isPrime)
            {
                count++;
            }
        }

        return count;
    }
}

/// <summary>Fallo <c>Allocations</c>: buffers grandes temporales (LOH) y concatenación cuadrática de cadenas.</summary>
public sealed class AllocationReportService(IOptions<LabOptions> options)
{
    private readonly LabOptions _options = options.Value;

    public (int Length, long Checksum) BuildReport(bool faulty) =>
        faulty ? BuildReportWithExcessiveAllocations() : BuildReportPooled();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private (int Length, long Checksum) BuildReportWithExcessiveAllocations()
    {
        long checksum = 0;
        for (var i = 0; i < _options.AllocationLargeBuffers; i++)
        {
            var buffer = new byte[_options.AllocationLargeBufferBytes];
            buffer[i % buffer.Length] = (byte)i;
            checksum += buffer[i % buffer.Length];
        }

        var report = string.Empty;
        for (var i = 0; i < _options.AllocationStringPieces; i++)
        {
            report += "line " + i + ";";
        }

        return (report.Length, checksum);
    }

    private (int Length, long Checksum) BuildReportPooled()
    {
        long checksum = 0;
        for (var i = 0; i < _options.AllocationLargeBuffers; i++)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(_options.AllocationLargeBufferBytes);
            try
            {
                buffer[i % _options.AllocationLargeBufferBytes] = (byte)i;
                checksum += buffer[i % _options.AllocationLargeBufferBytes];
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        var builder = new StringBuilder(_options.AllocationStringPieces * 12);
        for (var i = 0; i < _options.AllocationStringPieces; i++)
        {
            builder.Append("line ").Append(i).Append(';');
        }

        return (builder.Length, checksum);
    }
}

/// <summary>Fallo <c>Blocking</c>: espera de E/S simulada que bloquea un hilo del ThreadPool.</summary>
public sealed class BlockingReportService(IOptions<LabOptions> options)
{
    private readonly LabOptions _options = options.Value;

    public async Task<int> GetReportAsync(bool faulty, CancellationToken cancellationToken)
    {
        if (faulty)
        {
            BlockThread(cancellationToken);
            return _options.BlockingDelayMs;
        }

        await Task.Delay(_options.BlockingDelayMs, cancellationToken);
        return _options.BlockingDelayMs;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void BlockThread(CancellationToken cancellationToken)
    {
        if (_options.BlockingMode == "Sleep")
        {
            Thread.Sleep(_options.BlockingDelayMs);
            return;
        }

        // Sync-over-async: bloquea el hilo hasta que termina una operación asíncrona.
        Task.Delay(_options.BlockingDelayMs, cancellationToken).Wait(cancellationToken);
    }
}

/// <summary>Almacén sintético y acotado para la operación de escritura (que la política bloquea por defecto).</summary>
public sealed class OrderStore
{
    private const int MaxOrders = 10_000;
    private readonly ConcurrentQueue<Order> _orders = new();
    private int _nextId;

    public Order Add(CreateOrderRequest request)
    {
        var order = new Order(Interlocked.Increment(ref _nextId), request.ProductId, request.Quantity);
        _orders.Enqueue(order);
        while (_orders.Count > MaxOrders && _orders.TryDequeue(out _))
        {
        }

        return order;
    }
}

public sealed record Order(int Id, int ProductId, int Quantity);

public sealed record CreateOrderRequest(int ProductId, int Quantity);
