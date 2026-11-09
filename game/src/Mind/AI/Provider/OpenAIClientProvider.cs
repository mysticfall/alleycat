using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
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
    private OpenAIChatClientKind? _constructedChatClientKind;

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

    /// <summary>
    /// The effective chat client kind of the last constructed client (AI-002 TR-36): the configuration override
    /// when one is set, otherwise this resource's own <see cref="ChatClientKind" />. Never resolved before the
    /// first construction, so reading it cannot trigger production settings loading.
    /// </summary>
    public override OpenAIChatClientKind? EffectiveChatClientKind
        => _constructedChatClientKind ?? ChatClientKind;

    /// <summary>
    /// Pins every request of this provider's constructed clients to the subject character's own llama-server
    /// slot (AI-002 TR-37–TR-38) by injecting a top-level <c>"id_slot"</c> field whose value is derived from the
    /// character's canonical identity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Enable only against backends that tolerate extra top-level request fields — llama.cpp and similar
    /// OpenAI-compatible servers. Strict providers such as the real OpenAI API may reject requests carrying the
    /// field; the default <see langword="false" /> injects nothing, so request bodies stay byte-identical to an
    /// unconfigured provider's.
    /// </para>
    /// <para>
    /// The slot value follows the subject character automatically: it is derived deterministically from the
    /// character's <c>FullId</c> at client construction and stays fixed for that client's lifetime, so authors
    /// enable this switch once per provider and never assign slot numbers. Concurrent characters sharing one
    /// provider resource pin to their own slots; a derivation collision merely shares a slot, which llama-server
    /// serves by wrapping the value modulo its slot count.
    /// </para>
    /// </remarks>
    [Export]
    public bool EnableSlotPinning
    {
        get; set;
    }

    /// <inheritdoc />
    public override IChatClient CreateChatClient(string subjectFullId)
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
        OpenAIChatClientKind effectiveKind = ResolveEffectiveChatClientKind(settings.ChatClientKind, ChatClientKind);
        _constructedChatClientKind = effectiveKind;
        // The slot pin resolves here — once per subject character, fixed for the constructed client's lifetime
        // (AI-002 TR-37): the value follows the character, not the resource, so minds sharing one provider
        // resource pin to their own slots.
        return CreateChatClient(effectiveKind, settings, slotId: ResolveSlotId(EnableSlotPinning, subjectFullId));
    }

    /// <summary>
    /// Resolves the construction-fixed slot id (AI-002 TR-37): <see langword="null" /> — no injection, bodies
    /// byte-identical to an unconfigured provider's — unless the switch is enabled, in which case the value is
    /// derived from the subject character's canonical identity.
    /// </summary>
    internal static int? ResolveSlotId(bool enableSlotPinning, string subjectFullId)
        => enableSlotPinning ? DeriveSlotId(subjectFullId) : null;

    /// <summary>
    /// Derives the llama-server slot id for a subject character (AI-002 TR-38): a deterministic FNV-1a 64-bit
    /// hash over the identity's UTF-8 bytes, folded to a non-negative 31-bit integer. The mapping is stable
    /// across sessions, process launches, and machines — .NET's per-process randomised
    /// <c>string.GetHashCode()</c> is unsuitable — and collisions between different characters are permitted:
    /// llama-server wraps an out-of-range id modulo its slot count, so a colliding pair merely shares a slot.
    /// </summary>
    internal static int DeriveSlotId(string fullId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullId);

        const ulong FnvOffsetBasis = 14695981039346656037UL;
        const ulong FnvPrime = 1099511628211UL;
        ulong hash = FnvOffsetBasis;
        foreach (byte octet in Encoding.UTF8.GetBytes(fullId))
        {
            hash ^= octet;
            hash *= FnvPrime;
        }

        return (int)((hash ^ (hash >> 32)) & 0x7FFF_FFFF);
    }

    /// <summary>
    /// Resolves the effective chat client kind (AI-002 TR-36): a configured nonblank value overrides the provider
    /// resource property; an unset or blank value falls back to it, so existing provider resources behave
    /// unchanged unless the configuration key is set.
    /// </summary>
    internal static OpenAIChatClientKind ResolveEffectiveChatClientKind(
        OpenAIChatClientKind? configuredKind,
        OpenAIChatClientKind resourceKind)
        => configuredKind ?? resourceKind;

    internal static IChatClient CreateChatClient(
        OpenAIChatClientKind chatClientKind,
        OpenAIClientProviderSettings settings,
        OpenAIClientOptions? clientOptions = null,
        int? slotId = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (slotId is { } derivedSlotId)
        {
            // Provider- and character-scoped (AI-002 TR-37): the policy joins the pipeline once at construction
            // and stamps every outgoing request body of this client — on both endpoint kinds — with the fixed
            // derived value.
            clientOptions ??= settings.CreateClientOptions();
            clientOptions.AddPolicy(new SlotPinningRequestPolicy(derivedSlotId), PipelinePosition.BeforeTransport);
        }

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

    /// <summary>
    /// Pipeline policy stamping every outgoing request body with the construction-fixed slot pin
    /// (<c>"id_slot"</c>, AI-002 TR-37): it runs before the transport — once per message, so exact transport
    /// retries reuse the already-rewritten body — and applies identically to the Chat Completions and Responses
    /// kinds, whose request bodies are JSON objects. Rewriting preserves every original top-level field; an
    /// <c>id_slot</c> field that somehow pre-exists is replaced, never duplicated.
    /// </summary>
    private sealed class SlotPinningRequestPolicy(int slotId) : PipelinePolicy
    {
        private static ReadOnlySpan<byte> IdSlotPropertyName => "id_slot"u8;

        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            RewriteRequestContent(message);
            ProcessNext(message, pipeline, currentIndex);
        }

        public override async ValueTask ProcessAsync(
            PipelineMessage message,
            IReadOnlyList<PipelinePolicy> pipeline,
            int currentIndex)
        {
            RewriteRequestContent(message);
            await ProcessNextAsync(message, pipeline, currentIndex);
        }

        private void RewriteRequestContent(PipelineMessage message)
        {
            BinaryContent? content = message.Request.Content;
            if (content is null)
            {
                // No body means nothing to pin; chat request bodies are never null, so this only guards
                // degenerate pipeline reuse.
                return;
            }

            using var buffered = new MemoryStream();
            content.WriteTo(buffered, message.CancellationToken);
            message.Request.Content = BinaryContent.Create(BinaryData.FromBytes(AddSlotField(buffered.ToArray())));
        }

        private byte[] AddSlotField(byte[] body)
        {
            using JsonDocument document = ParseBody(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    "Slot pinning could not inject 'id_slot' because the outgoing request body is not a JSON "
                    + "object.");
            }

            using var rewritten = new MemoryStream();
            using (var writer = new Utf8JsonWriter(rewritten))
            {
                writer.WriteStartObject();
                writer.WriteNumber(IdSlotPropertyName, slotId);
                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                {
                    if (property.NameEquals(IdSlotPropertyName))
                    {
                        continue;
                    }

                    property.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            return rewritten.ToArray();
        }

        private static JsonDocument ParseBody(byte[] body)
        {
            try
            {
                return JsonDocument.Parse(body);
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException(
                    "Slot pinning could not parse the outgoing request body to inject 'id_slot'.", exception);
            }
        }
    }

    internal sealed record OpenAIClientProviderSettings(
        string Host,
        string? ApiKey,
        string Model,
        int? TimeoutSeconds,
        ReasoningEffort? ReasoningEffortLevel = null,
        string? ReasoningVerbosity = null,
        OpenAIChatClientKind? ChatClientKind = null)
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

        /// <summary>
        /// Creates the default endpoint options; also materialised directly when slot pinning must attach its
        /// pipeline policy to a caller-supplied-options-free construction (AI-002 TR-37).
        /// </summary>
        internal OpenAIClientOptions CreateClientOptions()
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
                ParseReasoningVerbosity(Clean(options.ReasoningVerbosity), sectionName),
                ParseChatClientKind(Clean(options.ChatClientKind), sectionName))
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

        /// <summary>
        /// Parses the configured chat client kind case-insensitively (AI-002 TR-35): unset or blank means no
        /// override — the provider resource property applies — and an unrecognised nonblank value is rejected
        /// locally before any provider request.
        /// </summary>
        private static OpenAIChatClientKind? ParseChatClientKind(string? value, string sectionName)
            => value?.ToLowerInvariant() switch
            {
                null => null,
                "chatcompletions" => OpenAIChatClientKind.ChatCompletions,
                "responses" => OpenAIChatClientKind.Responses,
                _ => throw new InvalidOperationException(
                    $"Config key '{sectionName}/ChatClientKind' must be one of 'chatcompletions' or 'responses'. "
                    + $"Got '{value}'."),
            };

    }
}
