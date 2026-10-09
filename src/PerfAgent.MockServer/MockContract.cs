using System.Collections.Concurrent;
using System.Text.Json;

namespace PerfAgent.MockServer;

/// <summary>Contrato declarativo de un mock (formato JSON versionado, ver docs/mock-contracts.md).</summary>
public sealed record MockContract(string Name, int Version, IReadOnlyList<MockRoute> Routes)
{
    private static readonly HashSet<string> AllowedMethods = ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE"];

    public static MockContract Load(string path)
    {
        using var stream = File.OpenRead(path);
        var document = JsonDocument.Parse(stream);
        return Parse(document.RootElement);
    }

    public static MockContract Parse(JsonElement root)
    {
        var name = root.GetProperty("name").GetString() ?? throw new InvalidDataException("Contract 'name' is required.");
        var version = root.TryGetProperty("version", out var v) ? v.GetInt32() : 1;
        var routes = new List<MockRoute>();
        foreach (var r in root.GetProperty("routes").EnumerateArray())
        {
            var method = (r.GetProperty("method").GetString() ?? "GET").ToUpperInvariant();
            if (!AllowedMethods.Contains(method))
            {
                throw new InvalidDataException($"Unsupported method '{method}'.");
            }

            var path = r.GetProperty("path").GetString() ?? throw new InvalidDataException("Route 'path' is required.");
            if (!path.StartsWith('/') || path.StartsWith("/__mock", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Invalid route path '{path}'.");
            }

            var status = r.TryGetProperty("status", out var s) ? s.GetInt32() : 200;
            var errorRate = r.TryGetProperty("errorRate", out var e) ? e.GetDouble() : 0;
            var errorStatus = r.TryGetProperty("errorStatus", out var es) ? es.GetInt32() : 503;
            if (errorRate is < 0 or > 1)
            {
                throw new InvalidDataException("errorRate must be between 0 and 1.");
            }

            var latency = r.TryGetProperty("latencyMs", out var l) ? LatencySpec.Parse(l) : LatencySpec.None;
            JsonElement? body = r.TryGetProperty("body", out var b) ? b.Clone() : null;
            routes.Add(new MockRoute(method, path, status, body, latency, errorRate, errorStatus));
        }

        return new MockContract(name, version, routes);
    }
}

public sealed record MockRoute(
    string Method,
    string Path,
    int Status,
    JsonElement? Body,
    LatencySpec Latency,
    double ErrorRate,
    int ErrorStatus)
{
    public string Key => $"{Method} {Path}";
}

/// <summary>Latencia uniforme entre <c>min</c> y <c>max</c> milisegundos (acotada a 10 s).</summary>
public sealed record LatencySpec(int MinMs, int MaxMs)
{
    public static readonly LatencySpec None = new(0, 0);

    public static LatencySpec Parse(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            var fixedMs = element.GetInt32();
            return Validate(new LatencySpec(fixedMs, fixedMs));
        }

        return Validate(new LatencySpec(element.GetProperty("min").GetInt32(), element.GetProperty("max").GetInt32()));
    }

    public TimeSpan Sample(Random random) =>
        TimeSpan.FromMilliseconds(MinMs == MaxMs ? MinMs : random.Next(MinMs, MaxMs + 1));

    private static LatencySpec Validate(LatencySpec spec) =>
        spec.MinMs < 0 || spec.MaxMs < spec.MinMs || spec.MaxMs > 10_000
            ? throw new InvalidDataException("latencyMs must satisfy 0 <= min <= max <= 10000.")
            : spec;
}

/// <summary>Estadísticas del propio mock: permiten demostrar en el informe qué comportamiento simuló.</summary>
public sealed class MockStats
{
    private readonly ConcurrentDictionary<string, RouteCounters> _routes = new();

    public void Record(MockRoute route, bool failed, TimeSpan elapsed)
    {
        var counters = _routes.GetOrAdd(route.Key, _ => new RouteCounters());
        Interlocked.Increment(ref counters.Requests);
        if (failed)
        {
            Interlocked.Increment(ref counters.Failures);
        }

        Interlocked.Add(ref counters.TotalMicroseconds, (long)elapsed.TotalMicroseconds);
    }

    public object Snapshot(MockContract contract) => new
    {
        contract = contract.Name,
        version = contract.Version,
        routes = contract.Routes.Select(r =>
        {
            var c = _routes.GetValueOrDefault(r.Key) ?? new RouteCounters();
            var requests = Interlocked.Read(ref c.Requests);
            return new
            {
                route = r.Key,
                configuredLatencyMs = new { min = r.Latency.MinMs, max = r.Latency.MaxMs },
                configuredErrorRate = r.ErrorRate,
                requests,
                failures = Interlocked.Read(ref c.Failures),
                avgLatencyMs = requests == 0 ? 0 : Interlocked.Read(ref c.TotalMicroseconds) / 1000.0 / requests,
            };
        }),
    };

    private sealed class RouteCounters
    {
        public long Requests;
        public long Failures;
        public long TotalMicroseconds;
    }
}
