using LanguageExt;
using static LanguageExt.Prelude;

namespace AlleyCat.Service;

public static class ServiceProviderExtensions
{
    extension(IServiceProvider provider)
    {
        public Option<T> FindService<T>() =>
            Optional((T?)provider.GetService(typeof(T)));

        public Eff<T> RequireService<T>() => liftEff(() =>
        {
            var service = provider.GetService(typeof(T));

            if (service is null)
            {
                throw new InvalidOperationException($"Service of type {typeof(T).FullName} not found.");
            }

            return (T)service;
        });
    }
}