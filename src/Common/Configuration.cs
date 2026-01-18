using LanguageExt;
using Microsoft.Extensions.Configuration;
using static LanguageExt.Prelude;
using Error = LanguageExt.Common.Error;

namespace AlleyCat.Common;

public static class ConfigurationExtensions
{
    extension(IConfiguration configuration)
    {
        public Eff<IConfigurationSection> RequireSection(string name)
        {
            var section = configuration.GetSection(name);

            return section.Exists()
                ? SuccessEff(section)
                : Error.New("No such config section exists: '{name}'");
        }

        public Eff<T> RequireValue<T>(string name) =>
            configuration
                .GetValue<T>(name)
                .Require($"Configuration value is not set: '{name}'");
    }
}