using Microsoft.Extensions.DependencyInjection;

namespace AlleyCat.Core;

/// <summary>
/// Registers services owned by a scene node or resource before the global game service provider is built.
/// </summary>
public interface IServiceRegistrar
{
    /// <summary>
    /// Adds this registrar's owned services to the supplied collection.
    /// </summary>
    /// <param name="services">Service collection populated during <see cref="Game" /> startup.</param>
    void RegisterServices(IServiceCollection services);
}
