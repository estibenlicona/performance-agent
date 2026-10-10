using Microsoft.AspNetCore.Http.Timeouts;
using PerfLab.Api;

// API de laboratorio con fallos de rendimiento conocidos y activables de forma individual.
// Solo usa datos sintéticos. Cada fallo está acotado (iteraciones, bytes y esperas máximas)
// para que una prueba nunca cuelgue indefinidamente ni consuma recursos sin límite.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddOptions<LabOptions>()
    .Bind(builder.Configuration.GetSection(LabOptions.SectionName))
    .Validate(o => o.IsWithinSafetyBounds(), "Lab options exceed the lab safety bounds.")
    .ValidateOnStart();
builder.Services.AddSingleton<CpuReportService>();
builder.Services.AddSingleton<AllocationReportService>();
builder.Services.AddSingleton<BlockingReportService>();
builder.Services.AddSingleton<OrderStore>();
builder.Services.AddPricingClient(builder.Configuration);
builder.Services.AddRequestTimeouts(o =>
    o.DefaultPolicy = new RequestTimeoutPolicy { Timeout = TimeSpan.FromSeconds(10) });

var app = builder.Build();

app.UseRequestTimeouts();
app.MapOpenApi();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .ExcludeFromDescription();

app.MapLabEndpoints();

var faults = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<LabOptions>>().Value.Faults;
app.Logger.LogInformation(
    "PerfLab.Api ready. Faults: Cpu={Cpu} Allocations={Allocations} Blocking={Blocking} DependencyNPlusOne={NPlusOne}",
    faults.Cpu, faults.Allocations, faults.Blocking, faults.DependencyNPlusOne);

app.Run();

public partial class Program;
