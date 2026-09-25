using System.ClientModel.Primitives;
using AlleyCat.Mind.AI.Provider;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;

namespace AlleyCat.IntegrationTests.Support.AI;

/// <summary>
/// Creates live OpenAI-compatible chat clients for opt-in live LLM integration tests from the dedicated
/// <c>AITest</c> configuration section.
/// </summary>
/// <remarks>
/// <para>
/// Settings resolve only from the <c>AITest</c> section of the merged game configuration. The production
/// <c>AI</c> section is never consulted and none of its defaults apply: <c>AITest</c> must explicitly
/// configure <c>Host</c>, <c>Model</c>, and <c>ApiKey</c>, a supplied <c>Timeout</c> must be positive, and an
/// omitted <c>Timeout</c> keeps the client default.
/// </para>
/// <para>
/// Failure messages name configuration keys (for example <c>AITest:Timeout</c>) without echoing configured
/// values, so credentials never reach test output.
/// </para>
/// </remarks>
public static class LiveLLMClientFactory
{
    private const string ConfigSection = "AITest";
    private const string MergedConfigDescription = "merged AlleyCat game configuration";

    /// <summary>
    /// Creates target and judge clients from the live merged <see cref="Game" /> configuration.
    /// </summary>
    public static LiveLLMClientPair CreateLiveClients()
        => CreateLiveClients(Game.Instance.GetRequiredService<IConfiguration>());

    /// <summary>
    /// Creates target and judge clients from the given configuration's <c>AITest</c> section.
    /// </summary>
    /// <param name="configuration">Configuration whose <c>AITest</c> section supplies the live settings.</param>
    public static LiveLLMClientPair CreateLiveClients(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        AIOptions options = BindSection(configuration);
        Uri endpoint = Validate(options);
        var settings = OpenAIClientProvider.OpenAIClientProviderSettings.Load(
            configuration,
            MergedConfigDescription,
            ConfigSection);

        IChatClient target = CreateClient(settings, endpoint);
        try
        {
            IChatClient judge = CreateClient(settings, endpoint);
            return new LiveLLMClientPair(target, judge);
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }

    private static IChatClient CreateClient(
        OpenAIClientProvider.OpenAIClientProviderSettings settings,
        Uri endpoint)
        => OpenAIClientProvider.CreateChatClient(
            OpenAIClientProvider.DefaultChatClientKind,
            settings,
            CreateTestClientOptions(endpoint, settings.TimeoutSeconds));

    private static AIOptions BindSection(IConfiguration configuration)
    {
        AIOptions options = new();
        configuration.GetSection(ConfigSection).Bind(options);
        return options;
    }

    private static Uri Validate(AIOptions options)
    {
        ValidateRequiredValues(options);
        return ParseEndpoint(Clean(options.Host) ?? string.Empty);
    }

    private static void ValidateRequiredValues(AIOptions options)
    {
        if (string.IsNullOrEmpty(Clean(options.Host)))
        {
            throw new InvalidOperationException($"{ConfigSection}:Host must be configured for live LLM tests.");
        }

        if (string.IsNullOrEmpty(Clean(options.Model)))
        {
            throw new InvalidOperationException($"{ConfigSection}:Model must be configured for live LLM tests.");
        }

        if (string.IsNullOrEmpty(Clean(options.ApiKey)))
        {
            throw new InvalidOperationException($"{ConfigSection}:ApiKey must be configured for live LLM tests.");
        }

        if (options.Timeout <= 0)
        {
            throw new InvalidOperationException(
                $"{ConfigSection}:Timeout must be a positive number of seconds when supplied.");
        }
    }

    private static Uri ParseEndpoint(string host)
        => Uri.TryCreate(host, UriKind.Absolute, out Uri? endpoint)
            && (string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            ? endpoint.AbsolutePath.Length == 0 || string.Equals(endpoint.AbsolutePath, "/", StringComparison.Ordinal)
                ? throw new InvalidOperationException(
                    $"{ConfigSection}:Host must include the API base path (for example 'https://api.openai.com/v1').")
                : endpoint
            : throw new InvalidOperationException($"{ConfigSection}:Host must be a valid absolute HTTP(S) endpoint URL.");

    private static OpenAIClientOptions CreateTestClientOptions(Uri endpoint, int? timeoutSeconds)
    {
        OpenAIClientOptions options = new()
        {
            Endpoint = endpoint,
            // Live tests fail fast on transport errors instead of silently retrying requests.
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
            ClientLoggingOptions = new ClientLoggingOptions
            {
                // Test-local clients stay out of the production logging pipeline: no logger factory is
                // attached and SDK message bodies remain disabled.
                EnableMessageContentLogging = false,
            },
        };

        if (timeoutSeconds is int timeout)
        {
            options.NetworkTimeout = TimeSpan.FromSeconds(timeout);
        }

        return options;
    }

    private static string? Clean(string? value)
    {
        string? text = value?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
