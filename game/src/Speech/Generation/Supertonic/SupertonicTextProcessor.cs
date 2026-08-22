// Adapted from the Supertonic C# reference implementation (csharp/Helper.cs, UnicodeProcessor),
// Copyright (c) 2025 Supertone Inc. Licensed under the MIT License.
// https://github.com/supertone-inc/supertonic
using System.Text;
using System.Text.RegularExpressions;

namespace AlleyCat.Speech.Generation.Supertonic;

/// <summary>
/// Text-encoder inputs produced from one processed utterance.
/// </summary>
/// <param name="TokenIds">Encoder token IDs, one per UTF-16 code unit of the processed text.</param>
/// <param name="PaddingMask">Padding mask aligned with <see cref="TokenIds" />.</param>
public sealed record SupertonicTextEncoding(long[] TokenIds, float[] PaddingMask);

/// <summary>
/// Encodes utterance text into Supertonic text-encoder inputs by normalising the source string and
/// mapping its characters through a <see cref="SupertonicUnicodeIndexer" />.
/// </summary>
public sealed class SupertonicTextProcessor
{
    private static readonly string[] _availableLanguages =
    [
        "en", "ko", "ja", "ar", "bg", "cs", "da", "de", "el", "es", "et", "fi", "fr", "hi", "hr",
        "hu", "id", "it", "lt", "lv", "nl", "pl", "pt", "ro", "ru", "sk", "sl", "sv", "tr", "uk",
        "vi", "na",
    ];

    private static readonly Dictionary<string, string> _symbolReplacements = new()
    {
        { "–", "-" }, // En dash.
        { "‑", "-" }, // Non-breaking hyphen.
        { "—", "-" }, // Em dash.
        { "_", " " },
        { "\u201C", "\"" }, // Left double quote.
        { "\u201D", "\"" }, // Right double quote.
        { "\u2018", "'" }, // Left single quote.
        { "\u2019", "'" }, // Right single quote.
        { "´", "'" }, // Acute accent.
        { "`", "'" }, // Grave accent.
        { "[", " " },
        { "]", " " },
        { "|", " " },
        { "/", " " },
        { "#", " " },
        { "→", " " },
        { "←", " " },
    };

    private static readonly Dictionary<string, string> _expressionReplacements = new()
    {
        { "@", " at " },
        { "e.g.,", "for example, " },
        { "i.e.,", "that is, " },
    };

    private static readonly Regex _specialSymbolPattern = new(@"[♥☆♡©\\]", RegexOptions.Compiled);

    private static readonly Regex _multipleWhitespacePattern = new(@"\s+", RegexOptions.Compiled);

    private static readonly Regex _terminalPunctuationPattern =
        new(@"[.!?;:,'""“”‘’)\]}…。」』】〉》›»]$", RegexOptions.Compiled);

    private readonly SupertonicUnicodeIndexer _indexer;

    /// <summary>
    /// Initialises the processor with the token table used by the text encoder.
    /// </summary>
    /// <param name="indexer">Unicode-indexer loaded from model assets.</param>
    public SupertonicTextProcessor(SupertonicUnicodeIndexer indexer)
    {
        ArgumentNullException.ThrowIfNull(indexer);
        _indexer = indexer;
    }

    /// <summary>
    /// Normalises the supplied text and encodes it into token IDs plus a padding mask.
    /// </summary>
    /// <param name="text">Utterance text to encode.</param>
    /// <param name="language">Language tag, for example <c>en</c>.</param>
    /// <returns>Token IDs (one per UTF-16 code unit) and a single-row padding mask of equal length.</returns>
    public SupertonicTextEncoding Encode(string text, string language)
    {
        ArgumentNullException.ThrowIfNull(text);
        string processed = Preprocess(text, language);

        long[] tokenIds = new long[processed.Length];
        for (int index = 0; index < processed.Length; index++)
        {
            if (_indexer.TryGetTokenId(processed[index], out long tokenId))
            {
                tokenIds[index] = tokenId;
            }
        }

        return new SupertonicTextEncoding(tokenIds, BuildPaddingMask(tokenIds.Length));
    }

    internal static float[] BuildPaddingMask(int length)
    {
        float[] mask = new float[length];
        Array.Fill(mask, 1f);
        return mask;
    }

    private static string Preprocess(string text, string language)
    {
        text = text.Normalize(NormalizationForm.FormKD);
        text = RemoveEmojis(text);

        foreach ((string source, string replacement) in _symbolReplacements)
        {
            text = text.Replace(source, replacement, StringComparison.Ordinal);
        }

        text = _specialSymbolPattern.Replace(text, string.Empty);

        foreach ((string source, string replacement) in _expressionReplacements)
        {
            text = text.Replace(source, replacement, StringComparison.Ordinal);
        }

        text = FixSpacingAroundPunctuation(text);

        while (text.Contains("\"\"", StringComparison.Ordinal))
        {
            text = text.Replace("\"\"", "\"", StringComparison.Ordinal);
        }

        while (text.Contains("''", StringComparison.Ordinal))
        {
            text = text.Replace("''", "'", StringComparison.Ordinal);
        }

        while (text.Contains("``", StringComparison.Ordinal))
        {
            text = text.Replace("``", "`", StringComparison.Ordinal);
        }

        text = _multipleWhitespacePattern.Replace(text, " ").Trim();

        if (!_terminalPunctuationPattern.IsMatch(text))
        {
            text += ".";
        }

        ValidateLanguage(language);

        return $"<{language}>{text}</{language}>";
    }

    private static string FixSpacingAroundPunctuation(string text)
    {
        text = text.Replace(" ,", ",", StringComparison.Ordinal);
        text = text.Replace(" .", ".", StringComparison.Ordinal);
        text = text.Replace(" !", "!", StringComparison.Ordinal);
        text = text.Replace(" ?", "?", StringComparison.Ordinal);
        text = text.Replace(" ;", ";", StringComparison.Ordinal);
        text = text.Replace(" :", ":", StringComparison.Ordinal);
        return text.Replace(" '", "'", StringComparison.Ordinal);
    }

    private static void ValidateLanguage(string language)
    {
        if (!_availableLanguages.Contains(language))
        {
            throw new ArgumentException(
                $"Invalid language: {language}. Available: {string.Join(", ", _availableLanguages)}");
        }
    }

    private static bool IsEmoji(int codePoint)
        => codePoint is (>= 0x1F600 and <= 0x1F64F)
            or (>= 0x1F300 and <= 0x1F5FF)
            or (>= 0x1F680 and <= 0x1F6FF)
            or (>= 0x1F700 and <= 0x1F77F)
            or (>= 0x1F780 and <= 0x1F7FF)
            or (>= 0x1F800 and <= 0x1F8FF)
            or (>= 0x1F900 and <= 0x1F9FF)
            or (>= 0x1FA00 and <= 0x1FA6F)
            or (>= 0x1FA70 and <= 0x1FAFF)
            or (>= 0x2600 and <= 0x26FF)
            or (>= 0x2700 and <= 0x27BF)
            or (>= 0x1F1E6 and <= 0x1F1FF);

    private static string RemoveEmojis(string text)
    {
        StringBuilder result = new(text.Length);

        for (int index = 0; index < text.Length; index++)
        {
            int codePoint;
            if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                codePoint = char.ConvertToUtf32(text[index], text[index + 1]);
                index++;
            }
            else
            {
                codePoint = text[index];
            }

            if (!IsEmoji(codePoint))
            {
                _ = codePoint > 0xFFFF
                    ? result.Append(char.ConvertFromUtf32(codePoint))
                    : result.Append((char)codePoint);
            }
        }

        return result.ToString();
    }
}
