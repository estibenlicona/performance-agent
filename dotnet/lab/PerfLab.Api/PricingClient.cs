using System.Globalization;
using Microsoft.Extensions.Options;

namespace PerfLab.Api;

/// <summary>Cliente de la dependencia de precios (HTTP contra un mock declarado o simulación en proceso).</summary>
public interface IPricingClient
{
    string Kind { get; }

    Task<decimal> GetPriceAsync(int productId, CancellationToken cancellationToken);

    Task<IReadOnlyList<decimal>> GetPricesAsync(IReadOnlyList<int> productIds, CancellationToken cancellationToken);
}

public sealed class DependencyFailedException(string message) : Exception(message);

public static class PricingClientRegistration
{
    public const string BaseUrlKey = "Dependencies:Pricing:BaseUrl";

    public static IServiceCollection AddPricingClient(this IServiceCollection services, IConfiguration configuration)
    {
        var baseUrl = configuration[BaseUrlKey];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            services.AddSingleton<IPricingClient, InProcessPricingClient>();
            return services;
        }

        services.AddHttpClient<IPricingClient, HttpPricingClient>(client =>
        {
            client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(5);
        });
        return services;
    }
}

public sealed class HttpPricingClient(HttpClient httpClient) : IPricingClient
{
    public string Kind => "http";

    public async Task<decimal> GetPriceAsync(int productId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(
            string.Create(CultureInfo.InvariantCulture, $"prices/{productId}"), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new DependencyFailedException($"Pricing dependency returned {(int)response.StatusCode}.");
        }

        var price = await response.Content.ReadFromJsonAsync<PriceDto>(cancellationToken);
        return price?.Price ?? throw new DependencyFailedException("Pricing dependency returned an empty body.");
    }

    public async Task<IReadOnlyList<decimal>> GetPricesAsync(IReadOnlyList<int> productIds, CancellationToken cancellationToken)
    {
        var ids = string.Join(',', productIds.Select(id => id.ToString(CultureInfo.InvariantCulture)));
        using var response = await httpClient.GetAsync($"prices?ids={ids}", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new DependencyFailedException($"Pricing dependency returned {(int)response.StatusCode}.");
        }

        var prices = await response.Content.ReadFromJsonAsync<List<PriceDto>>(cancellationToken);
        if (prices is null || prices.Count == 0)
        {
            throw new DependencyFailedException("Pricing dependency returned an empty list.");
        }

        // El mock devuelve una lista fija; se adapta al número de artículos solicitado.
        return productIds.Select((_, i) => prices[i % prices.Count].Price).ToList();
    }

    private sealed record PriceDto(int Id, decimal Price);
}

public sealed class InProcessPricingClient(IOptions<LabOptions> options) : IPricingClient
{
    private readonly int _latencyMs = options.Value.InProcessDependencyLatencyMs;

    public string Kind => "in-process-simulation";

    public async Task<decimal> GetPriceAsync(int productId, CancellationToken cancellationToken)
    {
        await Task.Delay(_latencyMs, cancellationToken);
        return productId * 1.5m;
    }

    public async Task<IReadOnlyList<decimal>> GetPricesAsync(IReadOnlyList<int> productIds, CancellationToken cancellationToken)
    {
        await Task.Delay(_latencyMs, cancellationToken);
        return productIds.Select(id => id * 1.5m).ToList();
    }
}
