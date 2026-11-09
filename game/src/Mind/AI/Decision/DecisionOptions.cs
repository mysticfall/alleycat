using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace AlleyCat.Mind.AI.Decision;

/// <summary>Explicit wire dialect, independent of hostname.</summary>
public enum DecisionAPIKind
{
    /// <summary>Map-based state/questions dialect.</summary>
    OpenRouter,
    /// <summary>Named-array input/questions dialect.</summary>
    OpenAI
}

/// <summary>Independent Decision section. No conversational AI defaults are inherited.</summary>
public sealed class DecisionOptions
{
    /// <summary>OpenRouter or OpenAI.</summary>
    public string? APIKind
    {
        get; init;
    }
    /// <summary>API base URL including its version prefix, not the operation.</summary>
    public string? Host
    {
        get; init;
    }
    /// <summary>Independent provider-specific model identifier.</summary>
    public string? Model
    {
        get; init;
    }
    /// <summary>User-owned bearer credential; never logged.</summary>
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
            "OpenRouter" => DecisionAPIKind.OpenRouter,
            "OpenAI" => DecisionAPIKind.OpenAI,
            _ => throw Invalid("APIKind")
        };
        if (!Uri.TryCreate(Host?.Trim(), UriKind.Absolute, out Uri? host) ||
            host.Scheme is not ("https" or "http") || host.UserInfo.Length != 0 ||
            host.Query.Length != 0 || host.Fragment.Length != 0 || host.AbsolutePath.Trim('/') is "" ||
            Uri.UnescapeDataString(host.AbsolutePath).TrimEnd('/').EndsWith("/decisions", StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid("Host");
        }

        if (string.IsNullOrWhiteSpace(Model))
        {
            throw Invalid("Model");
        }

        if (string.IsNullOrWhiteSpace(ApiKey) || ApiKey.Any(char.IsControl))
        {
            throw Invalid("ApiKey");
        }
        // CancellationTokenSource's timer has a finite millisecond range.
        return Timeout is not double seconds || !double.IsFinite(seconds) || seconds <= 0 || seconds > 4294967
            ? throw Invalid("Timeout")
            : new DecisionSettings(kind, new Uri(host.AbsoluteUri.TrimEnd('/') + "/decisions"), Model.Trim(),
            ApiKey.Trim(), TimeSpan.FromSeconds(seconds));
    }

    private static DecisionException Invalid(string key) => new(DecisionFailure.Configuration, $"Invalid or missing Decision:{key}.");
}

internal sealed record DecisionSettings(DecisionAPIKind Kind, Uri Endpoint, string Model, string ApiKey, TimeSpan Timeout);
