using System.Globalization;
using System.Text.Json;
using PerfAgent.Core.Contracts;

namespace PerfAgent.Adapters.K6Mcp;

/// <summary>
/// Línea temporal por segundo a partir de la salida <c>--out json</c> de k6 (solo disponible en el fallback CLI:
/// <c>run_script</c> de mcp-k6 no admite salidas adicionales).
/// </summary>
public static class K6JsonOutputParser
{
    public static IReadOnlyList<TimelineBucket> ParseTimeline(string path, int bucketSeconds = 1)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var durations = new SortedDictionary<long, List<double>>();
        var failed = new Dictionary<long, long>();
        var vus = new Dictionary<long, int>();
        foreach (var line in File.ReadLines(path))
        {
            if (!line.Contains("\"type\":\"Point\"", StringComparison.Ordinal))
            {
                continue;
            }

            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            var metric = root.GetProperty("metric").GetString();
            var data = root.GetProperty("data");
            var time = DateTimeOffset.Parse(data.GetProperty("time").GetString()!, CultureInfo.InvariantCulture);
            var bucket = time.ToUnixTimeSeconds() / bucketSeconds * bucketSeconds;
            var value = data.GetProperty("value").GetDouble();
            switch (metric)
            {
                case "http_req_duration":
                    if (!durations.TryGetValue(bucket, out var list))
                    {
                        durations[bucket] = list = [];
                    }

                    list.Add(value);
                    break;
                case "http_req_failed" when value > 0:
                    failed[bucket] = failed.GetValueOrDefault(bucket) + 1;
                    break;
                case "vus":
                    vus[bucket] = (int)value;
                    break;
            }
        }

        return durations.Select(kv =>
            {
                var sorted = kv.Value.Order().ToList();
                return new TimelineBucket(
                    DateTimeOffset.FromUnixTimeSeconds(kv.Key),
                    bucketSeconds,
                    sorted.Count,
                    failed.GetValueOrDefault(kv.Key),
                    Math.Round(sorted.Average(), 2),
                    Math.Round(Percentile(sorted, 0.95), 2),
                    vus.TryGetValue(kv.Key, out var v) ? v : null);
            })
            .ToList();
    }

    internal static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }

        var rank = p * (sorted.Count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        return sorted[lower] + ((sorted[upper] - sorted[lower]) * (rank - lower));
    }
}
