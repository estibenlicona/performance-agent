using System.Text.Json;
using Microsoft.Extensions.Logging;
using PerfAgent.Core.Contracts;
using PerfAgent.Core.Security;
using PerfAgent.Core.Upstream;

namespace PerfAgent.Adapters.DotNetDiagnosticsMcp;

/// <summary>La llamada requería una aprobación de riesgo alto/crítico del proveedor; la fachada nunca la concede.</summary>
public sealed class ProviderSafetyBlockedException(string tool, string detail) : Exception($"{tool}: {detail}");

/// <summary>
/// Adaptador de <c>pedrosakuma/dotnet-diagnostics</c>. Recibe siempre el PID del proceso de laboratorio desde la
/// fachada (el modelo no puede elegir otro proceso), restringe herramientas, vistas y tipos de captura a la
/// superficie aprobada y guarda las respuestas saneadas como artefactos.
/// </summary>
public sealed class DotNetDiagnosticsAdapter(UpstreamMcpProvider provider, ILogger<DotNetDiagnosticsAdapter> logger)
{
    public UpstreamMcpProvider Provider => provider;

    public Task<ProviderStatus> StatusAsync(CancellationToken cancellationToken) => provider.EnsureConnectedAsync(cancellationToken);

    /// <summary>inspect_process con vistas de bajo riesgo (info, capabilities, preflight, triage).</summary>
    public async Task<RuntimeSnapshot> InspectAsync(string snapshotId, int processId, string view, int durationSeconds, string artifactDirectory, CancellationToken cancellationToken)
    {
        if (!DiagnosticsCatalog.InspectViews.Contains(view))
        {
            throw new ArgumentException($"Vista '{view}' no permitida.", nameof(view));
        }

        var started = DateTimeOffset.UtcNow;
        var args = new Dictionary<string, object?> { ["view"] = view, ["processId"] = processId };
        if (view == "triage")
        {
            args["durationSeconds"] = durationSeconds;
        }

        var (root, invocation, artifact) = await CallAsync("inspect_process", args, TimeSpan.FromSeconds(durationSeconds + 60), artifactDirectory, $"{snapshotId}-inspect-{view}", cancellationToken);
        var data = root.GetProperty("data");
        var findings = new List<string> { $"[summary] {Clean(Str(root, "summary"))}" };
        RuntimeSummary? summary = null;
        if (view == "triage" && data.TryGetProperty("triage", out var triage))
        {
            var (s, f) = RuntimeEvidenceNormalizer.FromTriage(triage);
            summary = s;
            findings.AddRange(f);
        }

        return new RuntimeSnapshot
        {
            Id = snapshotId,
            ProcessId = processId,
            Kind = view,
            StartedAt = started,
            EndedAt = DateTimeOffset.UtcNow,
            RuntimeVersion = RuntimeVersion(root),
            Summary = summary,
            ProviderFindings = findings,
            Sources = [$"{view}: {DiagnosticsCatalog.ProviderName}/inspect_process"],
            Providers = [invocation],
            Artifacts = [artifact],
        };
    }

    /// <summary>
    /// Captura de la ventana de carga: un único collect_batch con counters, eventos de GC y del ThreadPool
    /// (misma ventana y mismo PID), más la línea temporal de hilos del ThreadPool.
    /// </summary>
    public async Task<RuntimeSnapshot> CaptureLoadWindowAsync(string snapshotId, int processId, int durationSeconds, string? loadTestId, string artifactDirectory, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var (root, invocation, artifact) = await CallAsync(
            "collect_batch",
            new Dictionary<string, object?>
            {
                ["requests"] = new object[]
                {
                    new { tool = "collect_events", kind = "counters" },
                    new { tool = "collect_events", kind = "gc" },
                    new { tool = "collect_events", kind = "threadpool" },
                },
                ["processId"] = processId,
                ["durationSeconds"] = Math.Max(3, durationSeconds),
                ["depth"] = "full",
            },
            TimeSpan.FromSeconds(durationSeconds + 90),
            artifactDirectory,
            $"{snapshotId}-batch",
            cancellationToken);

        JsonElement? counters = null, gc = null, threadPool = null;
        string? threadPoolHandle = null;
        var invocations = new List<ProviderInvocation> { invocation };
        var warnings = new List<string>();
        foreach (var result in Array(root.GetProperty("data"), "results"))
        {
            var kind = Str(result, "kind");
            if (result.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                warnings.Add($"{kind}: {Clean(Str(error, "message"))}");
                continue;
            }

            var data = result.GetProperty("data");
            if (data.ValueKind == JsonValueKind.String)
            {
                data = JsonDocument.Parse(data.GetString()!).RootElement.Clone();
            }

            switch (kind)
            {
                case "counters" when data.TryGetProperty("counters", out var c):
                    counters = c;
                    break;
                case "gc" when data.TryGetProperty("gc", out var g):
                    gc = g;
                    break;
                case "threadpool" when data.TryGetProperty("threadPool", out var tp):
                    threadPool = tp;
                    threadPoolHandle = Str(result, "handle");
                    break;
            }
        }

        JsonElement? timeline = null;
        var artifacts = new List<string> { artifact };
        if (threadPoolHandle is not null)
        {
            try
            {
                var (tlRoot, tlInvocation, tlArtifact) = await CallAsync(
                    "query_snapshot",
                    new Dictionary<string, object?> { ["handle"] = threadPoolHandle, ["view"] = "timeline" },
                    TimeSpan.FromSeconds(30), artifactDirectory, $"{snapshotId}-threadpool-timeline", cancellationToken);
                invocations.Add(tlInvocation);
                artifacts.Add(tlArtifact);
                timeline = tlRoot.GetProperty("data").TryGetProperty("payload", out var payload) ? payload : null;
            }
            catch (ProviderCallException ex)
            {
                invocations.Add(ex.Invocation);
                warnings.Add("Línea temporal del ThreadPool no disponible: " + ex.Message);
            }
        }

        var (summary, series, unavailable, findings) = RuntimeEvidenceNormalizer.FromLoadWindow(counters, gc, threadPool, timeline);
        findings.Insert(0, $"[summary] {Clean(Str(root, "summary"))}");
        foreach (var result in Array(root.GetProperty("data"), "results"))
        {
            findings.Add($"[{Str(result, "kind")}] {Clean(Str(result, "summary"))}");
        }

        var windowStart = counters is { } cs && cs.TryGetProperty("startedAt", out var sa) ? sa.GetDateTimeOffset() : started;
        var windowSeconds = counters is { } cd && RuntimeEvidenceNormalizer.DurationMs(cd, "duration") is { } ms ? ms / 1000 : durationSeconds;
        return new RuntimeSnapshot
        {
            Id = snapshotId,
            ProcessId = processId,
            Kind = "counters",
            LoadTestId = loadTestId,
            StartedAt = windowStart,
            EndedAt = windowStart.AddSeconds(windowSeconds),
            RuntimeVersion = RuntimeVersion(root),
            Summary = summary,
            Series = series,
            ProviderFindings = findings,
            UnavailableSignals = unavailable,
            Sources =
            [
                $"counters/gc/threadpool: {DiagnosticsCatalog.ProviderName}/collect_batch",
                "counters: último intervalo de 1 s y máximo de la ventana (no son medias)",
                "recuentos de GC y ajustes del ThreadPool: exactos para la ventana",
            ],
            Providers = invocations,
            Artifacts = artifacts,
            Warnings = warnings,
        };
    }

    /// <summary>Captura de seguimiento (bajo demanda) para discriminar hipótesis.</summary>
    public async Task<RuntimeSnapshot> CollectAsync(string snapshotId, int processId, string kind, int durationSeconds, string artifactDirectory, CancellationToken cancellationToken)
    {
        if (!DiagnosticsCatalog.EvidenceKinds.Contains(kind))
        {
            throw new ArgumentException($"Captura '{kind}' no permitida.", nameof(kind));
        }

        return kind switch
        {
            "cpu-sample" => await CpuSampleAsync(snapshotId, processId, durationSeconds, artifactDirectory, cancellationToken),
            "gc-events" => await GcEventsAsync(snapshotId, processId, durationSeconds, artifactDirectory, cancellationToken),
            _ => await EventsAsync(snapshotId, processId, kind, durationSeconds, artifactDirectory, cancellationToken),
        };
    }

    private async Task<RuntimeSnapshot> CpuSampleAsync(string snapshotId, int processId, int durationSeconds, string artifactDirectory, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var (root, invocation, artifact) = await CallAsync(
            "collect_sample",
            new Dictionary<string, object?> { ["kind"] = "cpu", ["processId"] = processId, ["durationSeconds"] = durationSeconds, ["depth"] = "detail", ["topN"] = 25 },
            TimeSpan.FromSeconds(durationSeconds + 90), artifactDirectory, $"{snapshotId}-cpu-sample", cancellationToken);
        var invocations = new List<ProviderInvocation> { invocation };
        var artifacts = new List<string> { artifact };
        var handle = Str(root, "handle");
        JsonElement? topMethods = null, hotPath = null;
        if (handle is not null)
        {
            topMethods = await QueryAsync(handle, "top-methods", new() { ["topN"] = 20 }, snapshotId, artifactDirectory, invocations, artifacts, cancellationToken);
            hotPath = await QueryAsync(handle, "hot-path", new() { ["hotPathThresholdPercent"] = 10 }, snapshotId, artifactDirectory, invocations, artifacts, cancellationToken);
        }

        var cpu = root.GetProperty("data").GetProperty("cpu");
        var (spots, path, findings) = RuntimeEvidenceNormalizer.FromCpuSample(cpu, topMethods, hotPath);
        findings.Insert(0, $"[summary] {Clean(Str(root, "summary"))}");
        return new RuntimeSnapshot
        {
            Id = snapshotId,
            ProcessId = processId,
            Kind = "cpu-sample",
            StartedAt = cpu.TryGetProperty("startedAt", out var sa) ? sa.GetDateTimeOffset() : started,
            EndedAt = DateTimeOffset.UtcNow,
            RuntimeVersion = RuntimeVersion(root),
            HotSpots = spots,
            HotPath = path,
            ProviderFindings = findings,
            Sources = [$"cpu-sample: {DiagnosticsCatalog.ProviderName}/collect_sample(kind=cpu) + query_snapshot(top-methods, hot-path)"],
            Providers = invocations,
            Artifacts = artifacts,
        };
    }

    private async Task<RuntimeSnapshot> GcEventsAsync(string snapshotId, int processId, int durationSeconds, string artifactDirectory, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var (root, invocation, artifact) = await CallAsync(
            "collect_batch",
            new Dictionary<string, object?>
            {
                ["requests"] = new object[] { new { tool = "collect_events", kind = "gc" }, new { tool = "collect_sample", kind = "allocation" } },
                ["processId"] = processId,
                ["durationSeconds"] = durationSeconds,
                ["depth"] = "full",
            },
            TimeSpan.FromSeconds(durationSeconds + 90), artifactDirectory, $"{snapshotId}-gc-batch", cancellationToken);
        JsonElement? gc = null;
        var spots = new List<HotSpot>();
        var findings = new List<string> { $"[summary] {Clean(Str(root, "summary"))}" };
        foreach (var result in Array(root.GetProperty("data"), "results"))
        {
            var data = result.TryGetProperty("data", out var d) ? d : default;
            if (data.ValueKind == JsonValueKind.String)
            {
                data = JsonDocument.Parse(data.GetString()!).RootElement.Clone();
            }

            findings.Add($"[{Str(result, "kind")}] {Clean(Str(result, "summary"))}");
            if (Str(result, "kind") == "gc" && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("gc", out var g))
            {
                gc = g;
            }
            else if (Str(result, "kind") == "allocation" && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("allocation", out var a))
            {
                var (s, f) = RuntimeEvidenceNormalizer.FromAllocationSample(a);
                spots = s;
                findings.AddRange(f);
            }
        }

        var (summary, series, unavailable, _) = RuntimeEvidenceNormalizer.FromLoadWindow(null, gc, null, null);
        return new RuntimeSnapshot
        {
            Id = snapshotId,
            ProcessId = processId,
            Kind = "gc-events",
            StartedAt = started,
            EndedAt = DateTimeOffset.UtcNow,
            RuntimeVersion = RuntimeVersion(root),
            Summary = summary with { },
            Series = series,
            HotSpots = spots,
            ProviderFindings = findings,
            UnavailableSignals = unavailable.Where(u => !u.StartsWith("counters", StringComparison.Ordinal) && !u.Contains("ThreadPool", StringComparison.Ordinal)).ToList(),
            Sources = [$"gc-events: {DiagnosticsCatalog.ProviderName}/collect_batch(collect_events gc + collect_sample allocation)"],
            Providers = [invocation],
            Artifacts = [artifact],
        };
    }

    private async Task<RuntimeSnapshot> EventsAsync(string snapshotId, int processId, string kind, int durationSeconds, string artifactDirectory, CancellationToken cancellationToken)
    {
        var eventKind = kind switch
        {
            "threadpool-events" => "threadpool",
            "contention-events" => "contention",
            _ => "exceptions",
        };
        var started = DateTimeOffset.UtcNow;
        var (root, invocation, artifact) = await CallAsync(
            "collect_events",
            new Dictionary<string, object?> { ["kind"] = eventKind, ["processId"] = processId, ["durationSeconds"] = durationSeconds, ["depth"] = "detail" },
            TimeSpan.FromSeconds(durationSeconds + 90), artifactDirectory, $"{snapshotId}-{eventKind}", cancellationToken);
        var findings = new List<string> { $"[summary] {Clean(Str(root, "summary"))}" };
        RuntimeSummary? summary = null;
        var spots = new List<HotSpot>();
        var data = root.GetProperty("data");
        if (eventKind == "threadpool" && data.TryGetProperty("threadPool", out var tp))
        {
            summary = RuntimeEvidenceNormalizer.FromLoadWindow(null, null, tp, null).Summary;
        }
        else if (eventKind == "contention" && data.TryGetProperty("contention", out var contention))
        {
            (summary, spots) = RuntimeEvidenceNormalizer.FromContention(contention);
            if (spots.Count == 0)
            {
                findings.Add("[contention] Sitios de llamada no resueltos por el proveedor (\"(unknown)\").");
            }
        }

        return new RuntimeSnapshot
        {
            Id = snapshotId,
            ProcessId = processId,
            Kind = kind,
            StartedAt = started,
            EndedAt = DateTimeOffset.UtcNow,
            RuntimeVersion = RuntimeVersion(root),
            Summary = summary,
            HotSpots = spots,
            ProviderFindings = findings,
            Sources = [$"{kind}: {DiagnosticsCatalog.ProviderName}/collect_events(kind={eventKind})"],
            Providers = [invocation],
            Artifacts = [artifact],
        };
    }

    private async Task<JsonElement?> QueryAsync(
        string handle, string view, Dictionary<string, object?> extra, string snapshotId, string artifactDirectory,
        List<ProviderInvocation> invocations, List<string> artifacts, CancellationToken cancellationToken)
    {
        if (!DiagnosticsCatalog.QueryViews.Contains(view))
        {
            throw new ArgumentException($"Vista '{view}' no permitida.", nameof(view));
        }

        var args = new Dictionary<string, object?>(extra) { ["handle"] = handle, ["view"] = view };
        try
        {
            var (root, invocation, artifact) = await CallAsync("query_snapshot", args, TimeSpan.FromSeconds(30), artifactDirectory, $"{snapshotId}-{view}", cancellationToken);
            invocations.Add(invocation);
            artifacts.Add(artifact);
            return root.GetProperty("data");
        }
        catch (ProviderCallException ex)
        {
            invocations.Add(ex.Invocation);
            logger.LogWarning("query_snapshot {View} failed: {Message}", view, ex.Message);
            return null;
        }
    }

    private async Task<(JsonElement Root, ProviderInvocation Invocation, string Artifact)> CallAsync(
        string tool, Dictionary<string, object?> arguments, TimeSpan timeout, string artifactDirectory, string artifactName, CancellationToken cancellationToken)
    {
        var call = await provider.CallAsync(tool, arguments, timeout, cancellationToken);
        Directory.CreateDirectory(artifactDirectory);
        var path = Path.Combine(artifactDirectory, $"{artifactName}.json");
        var raw = call.Structured?.GetRawText() ?? call.Text;
        await File.WriteAllTextAsync(path, SecretRedactor.RedactJson(raw), CancellationToken.None);
        if (call.Structured is not { ValueKind: JsonValueKind.Object } root)
        {
            throw new ProviderCallException(DiagnosticsCatalog.ProviderName, tool, "Respuesta sin contenido estructurado: " + Clean(call.Text), call.Invocation);
        }

        if (root.TryGetProperty("safetyApproval", out var approval) && approval.ValueKind == JsonValueKind.Object)
        {
            throw new ProviderSafetyBlockedException(tool, "el proveedor clasificó la llamada como de riesgo alto y pide aprobación; la fachada no la concede automáticamente.");
        }

        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            throw new ProviderCallException(DiagnosticsCatalog.ProviderName, tool, $"{Str(error, "kind")}: {Clean(Str(error, "message"))}", call.Invocation with { Success = false });
        }

        if (call.IsError)
        {
            throw new ProviderCallException(DiagnosticsCatalog.ProviderName, tool, Clean(call.Text), call.Invocation);
        }

        return (root, call.Invocation, path);
    }

    private static string? RuntimeVersion(JsonElement root) =>
        root.TryGetProperty("resolvedProcess", out var rp) ? Str(rp, "runtimeVersion") : null;

    private static IEnumerable<JsonElement> Array(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var sanitized = SecretRedactor.Redact(text.Replace('\n', ' ').Replace('\r', ' '));
        return sanitized.Length <= 400 ? sanitized : sanitized[..400] + "…";
    }
}
