using Microsoft.Extensions.Options;

namespace PerfLab.Api;

public static class LabEndpoints
{
    public static void MapLabEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").WithTags("lab");

        // Referencia normal: respuesta pequeña sin trabajo significativo.
        api.MapGet("/baseline", () => Results.Ok(new BaselineResponse("ok", DateTimeOffset.UtcNow)))
            .WithName("GetBaseline");

        api.MapGet("/products/{id:int}", (int id) =>
                id is >= 1 and <= 1_000
                    ? Results.Ok(new ProductResponse(id, $"Product {id}", id * 1.5m))
                    : Results.NotFound())
            .WithName("GetProduct");

        api.MapGet("/cpu/report", (CpuReportService service, IOptionsSnapshot<LabOptions> options) =>
                Results.Ok(new CpuReportResponse(service.GetPrimeCount(options.Value.Faults.Cpu))))
            .WithName("GetCpuReport");

        api.MapGet("/alloc/report", (AllocationReportService service, IOptionsSnapshot<LabOptions> options) =>
            {
                var (length, checksum) = service.BuildReport(options.Value.Faults.Allocations);
                return Results.Ok(new AllocationReportResponse(length, checksum));
            })
            .WithName("GetAllocationReport");

        api.MapGet("/blocking/report", async (BlockingReportService service, IOptionsSnapshot<LabOptions> options, CancellationToken ct) =>
                Results.Ok(new BlockingReportResponse(await service.GetReportAsync(options.Value.Faults.Blocking, ct))))
            .WithName("GetBlockingReport");

        api.MapGet("/dependency/quote", async (IPricingClient pricing, IOptionsSnapshot<LabOptions> options, CancellationToken ct) =>
            {
                var lab = options.Value;
                var ids = Enumerable.Range(1, lab.QuoteItems).ToList();
                try
                {
                    decimal total;
                    if (lab.Faults.DependencyNPlusOne)
                    {
                        // N+1: una llamada secuencial a la dependencia por artículo.
                        total = 0;
                        foreach (var id in ids)
                        {
                            total += await pricing.GetPriceAsync(id, ct);
                        }
                    }
                    else
                    {
                        total = (await pricing.GetPricesAsync(ids, ct)).Sum();
                    }

                    return Results.Ok(new QuoteResponse(ids.Count, total, pricing.Kind));
                }
                catch (Exception ex) when (ex is DependencyFailedException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    return Results.Problem(
                        title: "Pricing dependency failed",
                        detail: ex.Message,
                        statusCode: StatusCodes.Status502BadGateway);
                }
            })
            .WithName("GetDependencyQuote");

        // Operación de escritura: la política de PerfAgent la excluye salvo autorización explícita.
        api.MapPost("/orders", (CreateOrderRequest request, OrderStore store) =>
                request.Quantity is > 0 and <= 100
                    ? Results.Created($"/api/orders/{store.Add(request).Id}", null)
                    : Results.BadRequest())
            .WithName("CreateOrder");
    }
}

public sealed record BaselineResponse(string Status, DateTimeOffset Timestamp);

public sealed record ProductResponse(int Id, string Name, decimal Price);

public sealed record CpuReportResponse(int PrimeCount);

public sealed record AllocationReportResponse(int Length, long Checksum);

public sealed record BlockingReportResponse(int WaitedMs);

public sealed record QuoteResponse(int Items, decimal Total, string PricingSource);
