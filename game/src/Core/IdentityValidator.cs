using System.Text.RegularExpressions;

namespace AlleyCat.Core;

/// <summary>
/// Validates canonical object identity values at runtime registration boundaries.
/// </summary>
public static partial class IdentityValidator
{
    /// <summary>
    /// Validates both parts of an identifiable object's identity.
    /// </summary>
    /// <param name="identifiable">The identifiable object to validate.</param>
    /// <param name="parameterName">The parameter name to include in validation exceptions.</param>
    public static void Validate(IIdentifiable identifiable, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(identifiable);
        string type = identifiable.Type;
        string id = identifiable.Id;
        string fullId = identifiable.FullId;
        ValidateType(type, parameterName);
        ValidateId(id, parameterName);
        ValidateFullId(fullId, parameterName);

        string expectedFullId = $"{type}:{id}";
        if (!string.Equals(fullId, expectedFullId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"FullId '{fullId}' must exactly match Type '{type}' and ID '{id}' as '{expectedFullId}'.",
                parameterName);
        }
    }

    /// <summary>
    /// Validates a local lower <c>snake_case</c> identifier.
    /// </summary>
    public static void ValidateId(string? id, string parameterName)
        => ValidateIdentifier(id, "ID", parameterName);

    /// <summary>
    /// Validates a lower <c>snake_case</c> type identifier.
    /// </summary>
    public static void ValidateType(string? type, string parameterName)
        => ValidateIdentifier(type, "Type", parameterName);

    /// <summary>
    /// Validates a canonical typed identity in <c>Type:Id</c> form.
    /// </summary>
    public static void ValidateFullId(string? fullId, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullId, parameterName);

        int separator = fullId.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator != fullId.LastIndexOf(':') || separator == fullId.Length - 1)
        {
            throw new ArgumentException(
                $"FullId must be a canonical Type:Id value; received '{fullId}'.",
                parameterName);
        }

        ValidateType(fullId[..separator], parameterName);
        ValidateId(fullId[(separator + 1)..], parameterName);
    }

    private static void ValidateIdentifier(string? value, string label, string parameterName)
    {
        if (string.IsNullOrEmpty(value) || !LowerSnakeCaseIdentifier().IsMatch(value))
        {
            throw new ArgumentException(
                $"{label} must be a non-empty lower snake_case identifier; received '{value ?? "<null>"}'.",
                parameterName);
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9]*(?:_[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex LowerSnakeCaseIdentifier();
}
