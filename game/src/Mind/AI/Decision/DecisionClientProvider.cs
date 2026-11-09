using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Mind.AI.Decision;

/// <summary>Owns a reusable transport and lazily validates the independent configuration on explicit client acquisition.</summary>
public sealed class DecisionClientProvider : IDisposable
{
    private readonly HttpClient _http = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };
    private readonly Lazy<IDecisionClient> _client;

    /// <summary>Creates no network activity and permits a missing Decision section.</summary>
    public DecisionClientProvider(IConfiguration configuration, ILogger<DecisionClient> logger)
    {
        _client = new Lazy<IDecisionClient>(() => new DecisionClient(_http,
            DecisionOptions.FromConfiguration(configuration), logger));
    }

    /// <summary>Explicitly obtains the cached configured client; required settings are validated here.</summary>
    public IDecisionClient GetClient() => _client.Value;

    /// <summary>Disposes the shared transport, cancelling in-flight requests. Clients must not outlive this provider.</summary>
    public void Dispose() => _http.Dispose();
}

/// <summary>Subsystem-owned registration; core configuration does not depend on decision options.</summary>
public static class DecisionServiceCollectionExtensions
{
    /// <summary>Registers the provider without resolving configuration or triggering calls at startup.</summary>
    public static IServiceCollection AddDecisionClient(this IServiceCollection services)
        => services.AddSingleton<DecisionClientProvider>()
            .AddSingleton(provider => provider.GetRequiredService<DecisionClientProvider>().GetClient());
}
