using System.Security.Cryptography;
using System.Text.RegularExpressions;
using PerfAgent.Core.Contracts;
using PerfAgent.Core.Policy;

namespace PerfAgent.Core.Runs;

public sealed record RunSummary(
    string RunId,
    DateTimeOffset CreatedAt,
    string? Scenario,
    RunPhase Phase,
    DiagnosisVerdict Verdict,
    double? BaselineP95Ms,
    double? BaselineRps,
    double? BaselineErrorRate);

/// <summary>Persistencia de cada ejecución como artefacto JSON versionado (sin base de datos en el MVP).</summary>
public sealed partial class RunStore(Workspace workspace)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string RunsRoot => Path.Combine(workspace.ArtifactsRoot, "runs");

    public static string NewRunId(DateTimeOffset now) =>
        $"{now:yyyyMMdd-HHmmss}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3))}";

    public static bool IsValidRunId(string? runId) => runId is not null && RunIdRegex().IsMatch(runId);

    public string RunDirectory(string runId)
    {
        if (!IsValidRunId(runId))
        {
            throw new ArgumentException($"runId '{runId}' no tiene un formato válido.", nameof(runId));
        }

        return Path.Combine(RunsRoot, runId);
    }

    public string RelativeArtifact(string fullPath) => workspace.ToRelative(fullPath);

    public async Task SaveAsync(LabRun run, CancellationToken cancellationToken = default)
    {
        var dir = RunDirectory(run.RunId);
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "run.json");
        var temp = target + ".tmp";
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await File.WriteAllTextAsync(temp, PerfJson.Serialize(run), cancellationToken);
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LabRun?> LoadAsync(string runId, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(RunDirectory(runId), "run.json");
        if (!File.Exists(path))
        {
            return null;
        }

        return PerfJson.Deserialize<LabRun>(await File.ReadAllTextAsync(path, cancellationToken));
    }

    public async Task<IReadOnlyList<RunSummary>> ListAsync(int limit, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(RunsRoot))
        {
            return [];
        }

        var summaries = new List<RunSummary>();
        foreach (var dir in Directory.EnumerateDirectories(RunsRoot).OrderDescending(StringComparer.Ordinal).Take(Math.Clamp(limit, 1, 200)))
        {
            var runId = Path.GetFileName(dir);
            if (!IsValidRunId(runId))
            {
                continue;
            }

            LabRun? run;
            try
            {
                run = await LoadAsync(runId, cancellationToken);
            }
            catch (System.Text.Json.JsonException)
            {
                continue;
            }

            if (run is null)
            {
                continue;
            }

            var baseline = run.LatestLoadTest(LoadTestKind.Baseline);
            summaries.Add(new RunSummary(run.RunId, run.CreatedAt, run.ScenarioName, run.Phase, run.Verdict,
                baseline?.Latency?.P95, baseline?.RequestsPerSecond, baseline?.ErrorRate));
        }

        return summaries;
    }

    [GeneratedRegex(@"^\d{8}-\d{6}-[0-9a-f]{6}$")]
    private static partial Regex RunIdRegex();
}
