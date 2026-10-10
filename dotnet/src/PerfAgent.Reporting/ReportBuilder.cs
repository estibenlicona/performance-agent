using PerfAgent.Core.Contracts;
using PerfAgent.Core.Runs;
using PerfAgent.Core.Triage;

namespace PerfAgent.Reporting;

/// <summary>Construye el informe reproducible de una ejecución a partir de su estado persistido y del triage.</summary>
public static class ReportBuilder
{
    public static DiagnosticReport Build(LabRun run, TriageResult triage) => new()
    {
        RunId = run.RunId,
        GeneratedAt = DateTimeOffset.UtcNow,
        Phase = run.Phase,
        Verdict = triage.Verdict,
        Failure = run.Failure,
        FailureDetail = run.FailureDetail,
        ManifestPath = run.ManifestPath,
        ManifestSha256 = run.ManifestSha256,
        Target = run.Target,
        Environment = run.Environment,
        Versions = run.Versions,
        LoadTests = run.LoadTests,
        RuntimeSnapshots = run.RuntimeSnapshots,
        Correlations = triage.Correlations,
        Observations = triage.Observations,
        Hypotheses = triage.Hypotheses,
        Limitations = run.Limitations.Concat(triage.Limitations).Distinct().ToList(),
        OpenQuestions = triage.OpenQuestions,
        NextExperiments = triage.NextExperiments,
        Recommendations = triage.Recommendations,
        ProviderInvocations = run.ProviderInvocations,
        Cleanup = run.Cleanup,
        Events = run.Events,
        Artifacts = run.LoadTests.SelectMany(l => l.Artifacts)
            .Concat(run.RuntimeSnapshots.SelectMany(r => r.Artifacts))
            .Concat(run.Scenarios.Values.Select(s => s.ScriptPath))
            .Concat(new[] { run.Api?.StdoutLog, run.Api?.StderrLog }.OfType<string>())
            .Distinct()
            .ToList(),
    };
}
