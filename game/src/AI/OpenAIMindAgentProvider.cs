using System.ClientModel;
using System.Globalization;
using AlleyCat.Core;
using Godot;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;

namespace AlleyCat.AI;

/// <summary>
/// OpenAI client adapter used by the Mind provider.
/// </summary>
public enum OpenAIMindChatClientKind
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
/// OpenAI-compatible chat-client provider for the prototype Mind component.
/// </summary>
[GlobalClass]
public partial class OpenAIMindAgentProvider : MindAgentProvider
{
    private const string ConfigSection = "AI";
    private const string DefaultConfigPath = ConfigProvider.DefaultBaseConfigPath;
    private const string DefaultModel = "gpt-4o-mini";
    private const string DefaultCompatibleBackendApiKey = "unused-api-key";

    private OpenAIMindAgentProviderSettings? _settings;

    /// <summary>
    /// Config file used to resolve OpenAI-compatible AI settings.
    /// </summary>
    [Export(PropertyHint.File, "*.cfg")]
    public string ConfigPath { get; set; } = DefaultConfigPath;

    /// <summary>
    /// OpenAI client adapter used to expose the backend as an <see cref="IChatClient" />.
    /// </summary>
    [Export]
    public OpenAIMindChatClientKind ChatClientKind { get; set; } = OpenAIMindChatClientKind.ChatCompletions;

    /// <inheritdoc />
    protected override IChatClient CreateChatClient()
    {
        OpenAIMindAgentProviderSettings settings = _settings ??= OpenAIMindAgentProviderSettings.Load(ConfigPath);
        return ChatClientKind switch
        {
            OpenAIMindChatClientKind.ChatCompletions => settings.CreateChatClient().AsIChatClient(),
            OpenAIMindChatClientKind.Responses => CreateResponsesChatClient(settings),
            _ => throw new InvalidOperationException($"Unsupported OpenAI chat client kind '{ChatClientKind}'."),
        };
    }

#pragma warning disable OPENAI001 // The OpenAI Responses APIs are experimental in the SDK.
    private static IChatClient CreateResponsesChatClient(OpenAIMindAgentProviderSettings settings)
    {
        ResponsesClient responsesClient = settings.CreateOpenAIClient().GetResponsesClient();
        return responsesClient.AsIChatClient(settings.Model);
    }
#pragma warning restore OPENAI001

    internal sealed record OpenAIMindAgentProviderSettings(
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
                    $"Missing '{ConfigSection}/Host' in OpenAI Mind config '{ConfigPathDescription}'.");
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
        {
            OpenAIClientOptions options = new()
            {
                Endpoint = CreateEndpointUri(),
            };

            if (TimeoutSeconds is int timeoutSeconds)
            {
                options.NetworkTimeout = TimeSpan.FromSeconds(timeoutSeconds);
            }

            return options;
        }

        private string ConfigPathDescription { get; init; } = DefaultConfigPath;

        public static OpenAIMindAgentProviderSettings Load(string configPath)
            => Load(configPath, () => ConfigProvider.LoadMerged(), ConfigProvider.Load);

        internal static OpenAIMindAgentProviderSettings Load(
            string configPath,
            Func<ConfigProvider> mergedConfigLoader,
            Func<string, ConfigProvider> singleConfigLoader)
            => Load(LoadConfigProvider(configPath, mergedConfigLoader, singleConfigLoader), configPath);

        internal static OpenAIMindAgentProviderSettings Load(ConfigProvider configProvider, string configPathDescription)
        {
            return new OpenAIMindAgentProviderSettings(
                GetString(configProvider, nameof(Host)),
                GetOptionalString(configProvider, nameof(ApiKey)),
                GetOptionalString(configProvider, nameof(Model)) ?? DefaultModel,
                GetOptionalInt(configProvider, "Timeout"))
            {
                ConfigPathDescription = configPathDescription,
            };
        }

        private static ConfigProvider LoadConfigProvider(
            string configPath,
            Func<ConfigProvider> mergedConfigLoader,
            Func<string, ConfigProvider> singleConfigLoader)
            => string.Equals(configPath, DefaultConfigPath, StringComparison.Ordinal)
                ? mergedConfigLoader()
                : singleConfigLoader(configPath);

        private static string GetString(ConfigProvider configProvider, string key)
            => GetOptionalString(configProvider, key) ?? string.Empty;

        private static string? GetOptionalString(ConfigProvider configProvider, string key)
        {
            string? text = configProvider.GetValue(ConfigSection, key)?.Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        private static int? GetOptionalInt(ConfigProvider configProvider, string key)
        {
            string? text = configProvider.GetValue(ConfigSection, key)?.Trim();
            return string.IsNullOrWhiteSpace(text)
                ? null
                : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                    ? parsed
                    : throw new InvalidOperationException(
                        $"Config key '{ConfigSection}/{key}' must be a valid integer. Got '{text}'.");
        }
    }
}
