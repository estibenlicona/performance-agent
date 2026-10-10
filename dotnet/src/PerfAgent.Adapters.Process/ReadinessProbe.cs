using System.Diagnostics;
using System.Net;
using PerfAgent.Core.Contracts;

namespace PerfAgent.Adapters.Process;

public sealed record ReadinessResult(bool Ready, FailureKind Failure, string Detail, int Attempts, TimeSpan Elapsed, int? LastStatusCode);

/// <summary>Espera a que el endpoint de salud responda 2xx, distinguiendo proceso terminado, timeout y errores de configuración.</summary>
public static class ReadinessProbe
{
    public static HttpClient CreateLoopbackClient() => new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromSeconds(30),
        ConnectTimeout = TimeSpan.FromSeconds(2),
    })
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    public static async Task<ReadinessResult> WaitAsync(ManagedProcess process, Uri healthUrl, TimeSpan timeout, HttpClient http, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var attempts = 0;
        int? lastStatus = null;
        string? lastError = null;
        while (watch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                await Task.Delay(300, CancellationToken.None);
                var (kind, detail) = StartupFailureClassifier.Classify(process.StdoutTail(60), process.StderrTail(60), process.ExitCode);
                return new ReadinessResult(false, kind, detail, attempts, watch.Elapsed, lastStatus);
            }

            attempts++;
            try
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptCts.CancelAfter(TimeSpan.FromSeconds(2));
                using var response = await http.GetAsync(healthUrl, attemptCts.Token);
                lastStatus = (int)response.StatusCode;
                if (response.IsSuccessStatusCode)
                {
                    return new ReadinessResult(true, FailureKind.None, $"Lista tras {attempts} intentos ({watch.Elapsed.TotalSeconds:0.0} s).", attempts, watch.Elapsed, lastStatus);
                }
            }
            catch (HttpRequestException ex)
            {
                lastError = ex.Message;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = "timeout de la petición de salud";
            }

            await Task.Delay(500, cancellationToken);
        }

        return lastStatus switch
        {
            (int)HttpStatusCode.NotFound => new ReadinessResult(false, FailureKind.ConfigurationError,
                $"El endpoint de salud {healthUrl.AbsolutePath} devolvió 404: configura api.healthPath con una ruta existente.", attempts, watch.Elapsed, lastStatus),
            (int)HttpStatusCode.Unauthorized or (int)HttpStatusCode.Forbidden => new ReadinessResult(false, FailureKind.AuthenticationError,
                $"El endpoint de salud devolvió {lastStatus}: requiere autenticación. Usa un endpoint de salud anónimo para readiness.", attempts, watch.Elapsed, lastStatus),
            >= 500 => Classify(process, attempts, watch.Elapsed, lastStatus, $"El endpoint de salud devolvió {lastStatus} durante {timeout.TotalSeconds:0} s."),
            _ => new ReadinessResult(false, FailureKind.ReadinessTimeout,
                $"La API no estuvo lista en {timeout.TotalSeconds:0} s (último error: {lastError ?? lastStatus?.ToString() ?? "sin respuesta"}).", attempts, watch.Elapsed, lastStatus),
        };
    }

    private static ReadinessResult Classify(ManagedProcess process, int attempts, TimeSpan elapsed, int? lastStatus, string prefix)
    {
        var (kind, detail) = StartupFailureClassifier.Classify(process.StdoutTail(80), process.StderrTail(80), process.ExitCode);
        return kind == FailureKind.ProcessExited
            ? new ReadinessResult(false, FailureKind.ReadinessTimeout, prefix, attempts, elapsed, lastStatus)
            : new ReadinessResult(false, kind, $"{prefix} {detail}", attempts, elapsed, lastStatus);
    }
}
