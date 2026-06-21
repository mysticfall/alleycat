using System.ClientModel;
using AlleyCat.Core.Configuration;
using AlleyCat.Core.Logging;
using Godot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;

namespace AlleyCat.Mind.AI.Provider;

/// <summary>
/// OpenAI client adapter used by the Agent Framework runtime.
/// </summary>
public enum OpenAIChatClientKind
{
    /// <summary>
    /// Use the OpenAI chat-completions client adapter.
    /// </summary>
    ChatCompletions,

    /// <summary>
    /// Use the OpenAI responses client adapter.
    /// </summary>
    Responses,
}

/// <summary>
/// OpenAI-compatible chat-client provider for Agent Framework turn execution.
/// </summary>
[GlobalClass]
public partial class OpenAIClientProvider : ClientProvider
{
    private const string ConfigSection = "AI";
    private const string DefaultConfigPath = GameConfiguration.DefaultBaseConfigPath;
    private const string DefaultModel = "gpt-4o-mini";
    private const string DefaultCompatibleBackendApiKey = "unused-api-key";

    private OpenAIClientProviderSettings? _settings;

    /// <summary>
    /// Config file used to resolve OpenAI-compatible AI settings.
    /// </summary>
    [Export(PropertyHint.File, "*.json")]
    public string ConfigPath { get; set; } = DefaultConfigPath;

    /// <summary>
    /// OpenAI client adapter used to expose the backend as an <see cref="IChatClient" />.
    /// </summary>
    [Export]
    public OpenAIChatClientKind ChatClientKind { get; set; } = OpenAIChatClientKind.ChatCompletions;

    /// <inheritdoc />
    public override IChatClient CreateChatClient()
    {
        OpenAIClientProviderSettings settings = _settings ??= OpenAIClientProviderSettings.Load(ConfigPath);
        return ChatClientKind switch
        {
            OpenAIChatClientKind.ChatCompletions => settings.CreateChatClient().AsIChatClient(),
            OpenAIChatClientKind.Responses => CreateResponsesChatClient(settings),
            _ => throw new InvalidOperationException($"Unsupported OpenAI chat client kind '{ChatClientKind}'."),
        };
    }

#pragma warning disable OPENAI001 // The OpenAI Responses APIs are experimental in the SDK.
    private static IChatClient CreateResponsesChatClient(OpenAIClientProviderSettings settings)
    {
        ResponsesClient responsesClient = settings.CreateOpenAIClient().GetResponsesClient();
        return responsesClient.AsIChatClient(settings.Model);
    }
#pragma warning restore OPENAI001

    internal sealed record OpenAIClientProviderSettings(
        string Host,
        string? ApiKey,
        string Model,
        int? TimeoutSeconds)
    {
        public string GetApiKeyOrDefault()
            => string.IsNullOrWhiteSpace(ApiKey) ? DefaultCompatibleBackendApiKey : ApiKey.Trim();

        public ChatClient CreateChatClient()
            => new(Model, new ApiKeyCredential(GetApiKeyOrDefault()), CreateClientOptions());

        public OpenAIClient CreateOpenAIClient()
            => new(new ApiKeyCredential(GetApiKeyOrDefault()), CreateClientOptions());

        public Uri CreateEndpointUri()
        {
            string endpointUrl = Host.Trim();
            if (string.IsNullOrWhiteSpace(endpointUrl))
            {
                throw new InvalidOperationException(
                    $"Missing '{ConfigSection}/Host' in OpenAI client config '{ConfigPathDescription}'.");
            }

            if (!Uri.TryCreate(endpointUrl, UriKind.Absolute, out Uri? endpointUri))
            {
                throw new InvalidOperationException(
                    $"Config key '{ConfigSection}/Host' must be a valid absolute endpoint URL. Got '{endpointUrl}'.");
            }

            _ = endpointUri.AbsolutePath.Length == 0
                || string.Equals(endpointUri.AbsolutePath, "/", StringComparison.Ordinal)
                ? throw new InvalidOperationException(
                    $"Config key '{ConfigSection}/Host' must include the API base path (for example 'https://api.openai.com/v1'). Got '{endpointUrl}'.")
                : 0;

            return endpointUri;
        }

        private OpenAIClientOptions CreateClientOptions()
            => OpenAIClientOptionsFactory.Create(CreateEndpointUri(), TimeoutSeconds);

        private string ConfigPathDescription { get; init; } = DefaultConfigPath;

        public static OpenAIClientProviderSettings Load(string configPath)
            => Load(LoadConfiguration(configPath), configPath);

        internal static OpenAIClientProviderSettings Load(AIOptions options, string configPathDescription = DefaultConfigPath)
        {
            ArgumentNullException.ThrowIfNull(options);

            return new OpenAIClientProviderSettings(
                Clean(options.Host) ?? string.Empty,
                Clean(options.ApiKey),
                Clean(options.Model) ?? DefaultModel,
                options.Timeout)
            {
                ConfigPathDescription = configPathDescription,
            };
        }

        internal static OpenAIClientProviderSettings Load(
            string configPath,
            Func<IConfiguration> defaultConfigurationLoader,
            Func<string, IConfiguration> customConfigurationLoader)
            => Load(LoadConfiguration(configPath, defaultConfigurationLoader, customConfigurationLoader), configPath);

        internal static OpenAIClientProviderSettings Load(IConfiguration configuration, string configPathDescription)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            AIOptions options = new();
            configuration.GetSection(ConfigSection).Bind(options);
            return Load(options, configPathDescription);
        }

        private static IConfiguration LoadConfiguration(string configPath)
            => LoadConfiguration(
                configPath,
                ResolveDefaultConfiguration,
                path => GameConfiguration.BuildFile(new GodotPathResolver(), path));

        private static IConfiguration ResolveDefaultConfiguration()
            => Game.Instance.GetRequiredService<IConfiguration>();

        private static IConfiguration LoadConfiguration(
            string configPath,
            Func<IConfiguration> defaultConfigurationLoader,
            Func<string, IConfiguration> customConfigurationLoader)
            => string.Equals(configPath, DefaultConfigPath, StringComparison.Ordinal)
                ? defaultConfigurationLoader()
                : customConfigurationLoader(configPath);

        private static string? Clean(string? value)
        {
            string? text = value?.Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

    }
}
