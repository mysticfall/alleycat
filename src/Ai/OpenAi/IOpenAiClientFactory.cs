using System.ClientModel;
using System.ClientModel.Primitives;
using AlleyCat.Env;
using AlleyCat.Common;
using AlleyCat.Service;
using LanguageExt;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenAI;
using static AlleyCat.Env.Prelude;

namespace AlleyCat.Ai.OpenAi;

public readonly struct ClientAndModel(OpenAIClient client, string model)
{
    public OpenAIClient Client => client;

    public string Model => model;
}

public interface IOpenAiClientFactory
{
    string? ConfigSection { get; }

    Eff<IEnv, ClientAndModel> CreateClient(
        ILoggerFactory? loggerFactory = null
    ) =>
        from sectionName in ConfigSection
            .Require("Config section is not set.")
        from config in service<IConfiguration>()
        from section in config.RequireSection(sectionName)
        from model in IO
            .pure(section.GetValue<string>("Model", "default"))
        from endpoint in IO
            .pure(section.GetValue<string>("Endpoint", "https://openrouter.ai/api/v1"))
            .Map(x => new Uri(x, UriKind.Absolute))
        from apiKey in IO
            .pure(section.GetValue<string>("ApiKey", "secret"))
            .Map(x => new ApiKeyCredential(x))
        let client = new OpenAIClient(apiKey, new OpenAIClientOptions
        {
            Endpoint = endpoint,
            ClientLoggingOptions = new ClientLoggingOptions
            {
                LoggerFactory = loggerFactory
            }
        })
        select new ClientAndModel(client, model);
}