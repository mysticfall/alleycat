using System.Globalization;
using System.Text;
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
    /// Validates a Unicode local identifier without normalising or changing its ordinal value (CORE-009 TR-2).
    /// </summary>
    public static void ValidateId(string? id, string parameterName)
    {
        if (string.IsNullOrEmpty(id) || !IsValidLocalId(id))
        {
            throw new ArgumentException(
                $"ID must start with a lowercase or uncased Unicode letter and contain letters, decimal digits, "
                + $"combining marks after a letter or digit, and single underscores between non-empty segments; received '{id ?? "<null>"}'.",
                parameterName);
        }
    }

    /// <summary>
    /// Validates an ASCII lower <c>snake_case</c> type identifier.
    /// </summary>
    public static void ValidateType(string? type, string parameterName)
    {
        if (string.IsNullOrEmpty(type) || !LowerSnakeCaseIdentifier().IsMatch(type))
        {
            throw new ArgumentException(
                $"Type must be a non-empty ASCII lower snake_case identifier; received '{type ?? "<null>"}'.",
                parameterName);
        }
    }

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

    private static bool IsValidLocalId(string value)
    {
        bool first = true;
        bool segmentHasBase = false;
        // Rune categories are culture-independent and support supplementary-plane letters. Invalid UTF-16
        // enumerates as the replacement rune, which is not in any accepted category.
        foreach (Rune rune in value.EnumerateRunes())
        {
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.LowercaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
                || (!first && category == UnicodeCategory.DecimalDigitNumber))
            {
                segmentHasBase = true;
            }
            else if (segmentHasBase && category is UnicodeCategory.NonSpacingMark
                or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
            {
                // A sequence of marks retains the preceding base in this segment.
            }
            else if (rune.Value == '_' && segmentHasBase)
            {
                segmentHasBase = false;
            }
            else
            {
                return false;
            }

            first = false;
        }

        return segmentHasBase;
    }

    [GeneratedRegex(@"\A[a-z][a-z0-9]*(?:_[a-z0-9]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex LowerSnakeCaseIdentifier();
}
