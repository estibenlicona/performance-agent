using System.Text.Json;
using PerfAgent.MockServer;

// Simulador HTTP explícito para dependencias declaradas con mode: mock.
// Su comportamiento (rutas, estado, cuerpo, latencia y tasa de error) sale de un contrato
// versionado; no inventa respuestas. Uso: PerfAgent.MockServer --contract <ruta> --urls http://127.0.0.1:<puerto>
var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var contractPath = builder.Configuration["contract"]
    ?? throw new InvalidOperationException("Missing --contract <path>.");
var contract = MockContract.Load(contractPath);

var app = builder.Build();
var stats = new MockStats();

app.MapGet("/__mock/health", () => Results.Text("ok"));
app.MapGet("/__mock/stats", () => Results.Json(stats.Snapshot(contract), MockJson.Options));

foreach (var route in contract.Routes)
{
    app.MapMethods(route.Path, [route.Method], async (HttpContext context) =>
    {
        var started = TimeProvider.System.GetTimestamp();
        var delay = route.Latency.Sample(Random.Shared);
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, context.RequestAborted);
        }

        var fail = route.ErrorRate > 0 && Random.Shared.NextDouble() < route.ErrorRate;
        stats.Record(route, fail, TimeProvider.System.GetElapsedTime(started));
        if (fail)
        {
            return Results.StatusCode(route.ErrorStatus);
        }

        return route.Body is { } body
            ? Results.Text(body.GetRawText(), "application/json", statusCode: route.Status)
            : Results.StatusCode(route.Status);
    });
}

app.Logger.LogWarning("PerfAgent.MockServer serving contract '{Name}' v{Version} with {Count} routes.",
    contract.Name, contract.Version, contract.Routes.Count);
app.Run();

internal static class MockJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = false };
}
