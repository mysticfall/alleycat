using System.ClientModel;
using AlleyCat.Core.Configuration;
using AlleyCat.Core.Logging;
using AlleyCat.Testing;
using Godot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;

namespace AlleyCat.Mind.AI.Provider;

/// <summary>
/// OpenAI client adapter used by the Mind runtime.
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
/// OpenAI-compatible chat-client provider for agent sessions.
/// </summary>
[GlobalClass]
public partial class OpenAIClientProvider : ClientProvider
{
    private const string ConfigSection = "AI";
    private const string DefaultConfigPath = GameConfiguration.DefaultBaseConfigPath;
    private const string DefaultModel = "gpt-4o-mini";
    private const string DefaultCompatibleBackendApiKey = "unused-api-key";

    internal const OpenAIChatClientKind DefaultChatClientKind = OpenAIChatClientKind.Responses;

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
    public OpenAIChatClientKind ChatClientKind { get; set; } = DefaultChatClientKind;

    /// <inheritdoc />
    public override IChatClient CreateChatClient()
    {
        // Fail closed in integration-test sessions: a scene-started mind defaults to this provider, so an
        // ordinary test session must never reach production settings loading or transport construction — only
        // the separate AITest path used by explicitly opted-in live tests may construct live clients
        // (TEST-001 UR-7).
        if (RuntimeContext.IsIntegrationTest())
        {
            throw new InvalidOperationException(
                "Production AI chat clients are disabled in integration-test sessions. Scene-started minds "
                + "must inject an offline ClientProvider; opt-in live LLM tests must use the AITest "
                + "configuration boundary through LiveLLMClientFactory.");
        }

        OpenAIClientProviderSettings settings = _settings ??= OpenAIClientProviderSettings.Load(ConfigPath);
        return CreateChatClient(ChatClientKind, settings);
    }

    internal static IChatClient CreateChatClient(
        OpenAIChatClientKind chatClientKind,
        OpenAIClientProviderSettings settings,
        OpenAIClientOptions? clientOptions = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return chatClientKind switch
        {
            OpenAIChatClientKind.ChatCompletions => CreateChatCompletionsChatClient(settings, clientOptions),
            OpenAIChatClientKind.Responses => CreateResponsesChatClient(settings, clientOptions),
            _ => throw new InvalidOperationException($"Unsupported OpenAI chat client kind '{chatClientKind}'."),
        };
    }

    private static IChatClient CreateChatCompletionsChatClient(
        OpenAIClientProviderSettings settings,
        OpenAIClientOptions? clientOptions)
    {
        // The Chat Completions protocol documents no reasoning-summary field, so a configured summary value must
        // fail containment locally before any request rather than silently disappearing or inventing a field.
        return settings.ReasoningVerbosity is { } summary
            ? throw new InvalidOperationException(
                $"Config key '{settings.SectionName}/ReasoningVerbosity' ('{summary}') is not supported on the "
                + "Chat Completions endpoint, which has no reasoning-summary setting. Unset it to use Chat "
                + "Completions, or use the Responses endpoint.")
            : new ChatClientBuilder(settings.CreateChatClient(clientOptions).AsIChatClient())
            .ConfigureOptions(options => ApplyReasoningEffort(options, settings))
            .Build();
    }

    /// <summary>
    /// Applies the configured reasoning effort to one request's options. The Chat Completions path carries no
    /// summary control: a configured summary never reaches it.
    /// </summary>
    private static void ApplyReasoningEffort(ChatOptions options, OpenAIClientProviderSettings settings)
    {
        if (settings.ReasoningEffortLevel is { } effort)
        {
            options.Reasoning = new ReasoningOptions { Effort = effort };
        }
    }
#pragma warning disable OPENAI001 // The OpenAI Responses APIs are experimental in the SDK.
    private static IChatClient CreateResponsesChatClient(
        OpenAIClientProviderSettings settings,
        OpenAIClientOptions? clientOptions)
    {
        ResponsesClient responsesClient = settings.CreateOpenAIClient(clientOptions).GetResponsesClient();
        return CreateStatelessResponsesChatClient(
            responsesClient,
            settings.Model,
            settings.ReasoningEffortLevel,
            settings.ReasoningVerbosity);
    }

    internal static IChatClient CreateStatelessResponsesChatClient(
        ResponsesClient responsesClient,
        string model,
        ReasoningEffort? reasoningEffort = null,
        string? reasoningSummary = null)
    {
        ArgumentNullException.ThrowIfNull(responsesClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        return new ResponsesReasoningTextNormalisingChatClient(
            new ChatClientBuilder(responsesClient.AsIChatClient(model))
                .ConfigureOptions(options =>
                {
                    // Explicit raw options keep Responses stateless and force the MEAI adapter to
                    // replay supplied history rather than deriving a previous_response_id. The raw
                    // representation is also the single owner of the reasoning object: mixing the MEAI
                    // abstraction's Reasoning with it clobbers fields, and its Output vocabulary
                    // (none/summary/full) cannot express every documented summary value.
                    options.ConversationId = null;
                    options.RawRepresentationFactory = _ => CreateReasoningResponsesRepresentation(
                        reasoningEffort,
                        reasoningSummary);
                })
                .Build());
    }

    private static CreateResponseOptions CreateReasoningResponsesRepresentation(
        ReasoningEffort? reasoningEffort,
        string? reasoningSummary)
    {
        CreateResponseOptions raw = new()
        {
            PreviousResponseId = null,
            StoredOutputEnabled = false,
        };

        if (reasoningEffort is null && reasoningSummary is null)
        {
            return raw;
        }

        raw.ReasoningOptions = new ResponseReasoningOptions();
        if (reasoningEffort is { } effort)
        {
            ApplyResponsesEffort(raw, effort);
        }

        if (reasoningSummary is { } summary)
        {
            raw.ReasoningOptions.ReasoningSummaryVerbosity = summary switch
            {
                "auto" => ResponseReasoningSummaryVerbosity.Auto,
                "concise" => ResponseReasoningSummaryVerbosity.Concise,
                "detailed" => ResponseReasoningSummaryVerbosity.Detailed,
                _ => throw new InvalidOperationException(
                    $"Unsupported reasoning summary verbosity '{summary}'."),
            };
        }

        return raw;
    }

#pragma warning disable SCME0001 // The System.ClientModel JSON patch API is experimental.
    private static void ApplyResponsesEffort(CreateResponseOptions raw, ReasoningEffort effort)
    {
        switch (effort)
        {
            case ReasoningEffort.None:
                raw.ReasoningOptions.ReasoningEffortLevel = ResponseReasoningEffortLevel.None;
                break;
            case ReasoningEffort.Low:
                raw.ReasoningOptions.ReasoningEffortLevel = ResponseReasoningEffortLevel.Low;
                break;
            case ReasoningEffort.Medium:
                raw.ReasoningOptions.ReasoningEffortLevel = ResponseReasoningEffortLevel.Medium;
                break;
            case ReasoningEffort.High:
                raw.ReasoningOptions.ReasoningEffortLevel = ResponseReasoningEffortLevel.High;
                break;
            case ReasoningEffort.ExtraHigh:
                // The typed level has no member for the documented 'xhigh' value, so the JSON patch carries that
                // one verbatim; the initialised parent reasoning object keeps the patch anchored.
                raw.Patch.Set(ReasoningEffortJsonPath, "xhigh");
                break;
            default:
                throw new InvalidOperationException($"Unsupported reasoning effort '{effort}'.");
        }
    }
#pragma warning restore SCME0001

    private static ReadOnlySpan<byte> ReasoningEffortJsonPath => "$.reasoning.effort"u8;
#pragma warning restore OPENAI001

    internal sealed record OpenAIClientProviderSettings(
        string Host,
        string? ApiKey,
        string Model,
        int? TimeoutSeconds,
        ReasoningEffort? ReasoningEffortLevel = null,
        string? ReasoningVerbosity = null)
    {
        public string GetApiKeyOrDefault()
            => string.IsNullOrWhiteSpace(ApiKey) ? DefaultCompatibleBackendApiKey : ApiKey.Trim();

        public ChatClient CreateChatClient(OpenAIClientOptions? clientOptions = null)
            => new(Model, new ApiKeyCredential(GetApiKeyOrDefault()), clientOptions ?? CreateClientOptions());

        public OpenAIClient CreateOpenAIClient(OpenAIClientOptions? clientOptions = null)
            => new(new ApiKeyCredential(GetApiKeyOrDefault()), clientOptions ?? CreateClientOptions());

        public Uri CreateEndpointUri()
        {
            string endpointUrl = Host.Trim();
            if (string.IsNullOrWhiteSpace(endpointUrl))
            {
                throw new InvalidOperationException(
                    $"Missing '{SectionName}/Host' in OpenAI client config '{ConfigPathDescription}'.");
            }

            if (!Uri.TryCreate(endpointUrl, UriKind.Absolute, out Uri? endpointUri))
            {
                throw new InvalidOperationException(
                    $"Config key '{SectionName}/Host' must be a valid absolute endpoint URL. Got '{endpointUrl}'.");
            }

            _ = endpointUri.AbsolutePath.Length == 0
                || string.Equals(endpointUri.AbsolutePath, "/", StringComparison.Ordinal)
                ? throw new InvalidOperationException(
                    $"Config key '{SectionName}/Host' must include the API base path (for example 'https://api.openai.com/v1'). Got '{endpointUrl}'.")
                : 0;

            return endpointUri;
        }

        private OpenAIClientOptions CreateClientOptions()
            => OpenAIClientOptionsFactory.Create(CreateEndpointUri(), TimeoutSeconds);

        internal string SectionName { get; init; } = ConfigSection;

        private string ConfigPathDescription { get; init; } = DefaultConfigPath;

        public static OpenAIClientProviderSettings Load(string configPath)
            => Load(LoadConfiguration(configPath), configPath);

        internal static OpenAIClientProviderSettings Load(AIOptions options, string configPathDescription = DefaultConfigPath)
        {
            ArgumentNullException.ThrowIfNull(options);

            return Load(options, configPathDescription, ConfigSection);
        }

        private static OpenAIClientProviderSettings Load(
            AIOptions options,
            string configPathDescription,
            string sectionName)
            => new(
                Clean(options.Host) ?? string.Empty,
                Clean(options.ApiKey),
                Clean(options.Model) ?? DefaultModel,
                options.Timeout,
                ParseReasoningEffort(Clean(options.ReasoningEffort), sectionName),
                ParseReasoningVerbosity(Clean(options.ReasoningVerbosity), sectionName))
            {
                ConfigPathDescription = configPathDescription,
                SectionName = sectionName,
            };

        internal static OpenAIClientProviderSettings Load(
            string configPath,
            Func<IConfiguration> defaultConfigurationLoader,
            Func<string, IConfiguration> customConfigurationLoader)
            => Load(LoadConfiguration(configPath, defaultConfigurationLoader, customConfigurationLoader), configPath);

        internal static OpenAIClientProviderSettings Load(IConfiguration configuration, string configPathDescription)
            => Load(configuration, configPathDescription, ConfigSection);

        /// <summary>
        /// Loads settings from an arbitrary configuration section, carrying the section name into diagnostics.
        /// </summary>
        /// <remarks>
        /// Production defaulting still applies (model fallback and compatible-backend key fallback); consumers
        /// needing strict validation — such as live test configuration — must validate the section before loading.
        /// </remarks>
        internal static OpenAIClientProviderSettings Load(
            IConfiguration configuration,
            string configPathDescription,
            string sectionName)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentException.ThrowIfNullOrWhiteSpace(sectionName);

            AIOptions options = new();
            configuration.GetSection(sectionName).Bind(options);
            return Load(options, configPathDescription, sectionName);
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

        /// <summary>
        /// Parses the configured reasoning effort case-insensitively, mapping <c>xhigh</c> onto the SDK's
        /// <see cref="ReasoningEffort.ExtraHigh" /> wire value.
        /// </summary>
        private static ReasoningEffort? ParseReasoningEffort(string? value, string sectionName)
            => value?.ToLowerInvariant() switch
            {
                null => null,
                "none" => ReasoningEffort.None,
                "low" => ReasoningEffort.Low,
                "medium" => ReasoningEffort.Medium,
                "high" => ReasoningEffort.High,
                "xhigh" => ReasoningEffort.ExtraHigh,
                _ => throw new InvalidOperationException(
                    $"Config key '{sectionName}/ReasoningEffort' must be one of 'none', 'low', 'medium', 'high', or "
                    + $"'xhigh'. Got '{value}'."),
            };

        /// <summary>
        /// Parses the configured reasoning-summary verbosity case-insensitively into its canonical lowercase wire
        /// value. The vocabulary follows the Responses endpoint's documented <c>reasoning.summary</c> values, so an
        /// effort-style value such as <c>medium</c> is rejected.
        /// </summary>
        private static string? ParseReasoningVerbosity(string? value, string sectionName)
            => value?.ToLowerInvariant() switch
            {
                null => null,
                "auto" or "concise" or "detailed" => value.ToLowerInvariant(),
                _ => throw new InvalidOperationException(
                    $"Config key '{sectionName}/ReasoningVerbosity' must be one of 'auto', 'concise', or "
                    + $"'detailed'. Got '{value}'."),
            };

    }
}
