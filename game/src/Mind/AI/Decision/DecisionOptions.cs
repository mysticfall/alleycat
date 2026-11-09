using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace AlleyCat.Mind.AI.Decision;

/// <summary>Explicit wire dialect, independent of hostname.</summary>
public enum DecisionAPIKind
{
    /// <summary>Map-based state/questions dialect.</summary>
    OpenRouter,
    /// <summary>Named-array input/questions dialect.</summary>
    OpenAI,
    /// <summary>Local unauthenticated map-based dialect on llama.cpp's <c>/v1/systemone</c> surface.</summary>
    SystemOne
}

/// <summary>Independent Decision section. No conversational AI defaults are inherited.</summary>
public sealed class DecisionOptions
{
    /// <summary>Zero-configuration SystemOne base: the local Docker <c>kev-decision-api</c> service.</summary>
    public const string DefaultSystemOneHost = "http://localhost:8081/v1";
    /// <summary>Deadline applied when <c>Timeout</c> is absent on the SystemOne dialect.</summary>
    public const double DefaultTimeoutSeconds = 30;

    /// <summary>OpenRouter, OpenAI or SystemOne; absent defaults to SystemOne.</summary>
    public string? APIKind
    {
        get; init;
    }
    /// <summary>API base URL including its version prefix, not the operation.</summary>
    public string? Host
    {
        get; init;
    }
    /// <summary>Independent provider-specific model identifier; unused by SystemOne.</summary>
    public string? Model
    {
        get; init;
    }
    /// <summary>User-owned bearer credential; never logged. Unused by SystemOne.</summary>
    public string? ApiKey
    {
        get; init;
    }
    /// <summary>Finite positive seconds covering dispatch and body reading.</summary>
    public double? Timeout
    {
        get; init;
    }

    /// <summary>Reads only Decision keys from the already merged shared configuration.</summary>
    public static DecisionOptions FromConfiguration(IConfiguration configuration)
    {
        IConfigurationSection section = configuration.GetSection("Decision");
        double? timeout = null;
        if (section["Timeout"] is string raw)
        {
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                throw Invalid("Timeout");
            }

            timeout = parsed;
        }
        return new DecisionOptions
        {
            APIKind = section["APIKind"],
            Host = section["Host"],
            Model = section["Model"],
            ApiKey = section["ApiKey"],
            Timeout = timeout
        };
    }

    internal DecisionSettings Validate()
    {
        DecisionAPIKind kind = APIKind?.Trim() switch
        {
            null => DecisionAPIKind.SystemOne,
            "OpenRouter" => DecisionAPIKind.OpenRouter,
            "OpenAI" => DecisionAPIKind.OpenAI,
            "SystemOne" => DecisionAPIKind.SystemOne,
            _ => throw Invalid("APIKind")
        };
        // Absent keys default only on the local SystemOne dialect; hosted kinds keep requiring every setting.
        bool local = kind == DecisionAPIKind.SystemOne;
        string operation = local ? "systemone" : "decisions";
        string? host = Host?.Trim() ?? (local ? DefaultSystemOneHost : null);
        if (!Uri.TryCreate(host, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme is not ("https" or "http") || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath.Trim('/') is "" ||
            EndsWithOperationSegment(uri.AbsolutePath))
        {
            throw Invalid("Host");
        }

        string model;
        string? apiKey = null;
        if (local)
        {
            model = Model?.Trim() ?? string.Empty;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(Model))
            {
                throw Invalid("Model");
            }

            model = Model.Trim();
            if (string.IsNullOrWhiteSpace(ApiKey) || ApiKey.Any(char.IsControl))
            {
                throw Invalid("ApiKey");
            }

            apiKey = ApiKey.Trim();
        }

        double? seconds = Timeout ?? (local ? DefaultTimeoutSeconds : null);
        // CancellationTokenSource's timer has a finite millisecond range.
        return seconds is not double value || !double.IsFinite(value) || value <= 0 || value > 4294967
            ? throw Invalid("Timeout")
            : new DecisionSettings(kind, new Uri(uri.AbsoluteUri.TrimEnd('/') + '/' + operation), model, apiKey,
            TimeSpan.FromSeconds(value));
    }

    /// <summary>A base URL never ends with a supported dialect's operation segment.</summary>
    private static bool EndsWithOperationSegment(string absolutePath)
    {
        string path = Uri.UnescapeDataString(absolutePath).TrimEnd('/');
        return path.EndsWith("/decisions", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/systemone", StringComparison.OrdinalIgnoreCase);
    }

    private static DecisionException Invalid(string key) => new(DecisionFailure.Configuration, $"Invalid or missing Decision:{key}.");
}

internal sealed record DecisionSettings(DecisionAPIKind Kind, Uri Endpoint, string Model, string? ApiKey, TimeSpan Timeout);
