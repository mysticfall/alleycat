using Godot;

namespace AlleyCat.Core.Content;

/// <summary>
/// Optional per-pack metadata declared at <c>res://content/&lt;pack-id&gt;/manifest.tres</c>, distinct from the
/// selection manifest (<see cref="ContentManifest" />) at <c>res://content/manifest.tres</c>.
/// </summary>
/// <remarks>
/// The pack manifest initially exposes one optional spoken-language declaration. Language metadata stays
/// infrastructure-independent: packs never declare service models, endpoints, or credentials.
/// </remarks>
[GlobalClass]
public partial class ContentPackManifest : Resource
{
    /// <summary>Language code resolved when a pack declares no spoken language.</summary>
    public const string DefaultLanguage = "en";

    /// <summary>
    /// Supported spoken-language metadata codes. Initially English and Korean.
    /// </summary>
    public static readonly IReadOnlyList<string> SupportedLanguages = [DefaultLanguage, "ko"];

    /// <summary>Spoken language of the pack's content, for example <c>en</c> or <c>ko</c>.</summary>
    /// <remarks>
    /// Declaration is optional and backward-compatible: a missing manifest, an omitted value, or a blank value
    /// resolves to <see cref="DefaultLanguage" />.
    /// </remarks>
    [Export]
    public string Language { get; set; } = DefaultLanguage;

    /// <summary>
    /// Normalises a declared language to its canonical supported code.
    /// </summary>
    /// <remarks>
    /// Outer whitespace is trimmed, matching is case-insensitive, and a regional tag maps to its primary
    /// sublanguage code (for example <c>en-GB</c> resolves to <c>en</c>). A blank value resolves to
    /// <see cref="DefaultLanguage" />. A declared primary sublanguage outside
    /// <see cref="SupportedLanguages" /> throws so content resolution fails loudly instead of silently running in
    /// another spoken language. Pure and Godot-free so normalisation stays unit-testable.
    /// </remarks>
    /// <param name="declaredLanguage">Declared language value, or null/blank for the default.</param>
    /// <param name="packId">Pack identifier used to build the actionable failure message.</param>
    /// <param name="manifestPath">Per-pack manifest path used to build the actionable failure message.</param>
    /// <returns>The normalised lowercase language code.</returns>
    public static string ResolveDeclaredLanguage(string? declaredLanguage, string packId, string manifestPath)
    {
        string declared = (declaredLanguage ?? string.Empty).Trim();
        if (declared.Length == 0)
        {
            return DefaultLanguage;
        }

        string primarySublanguage = declared.Split('-', 2)[0].Trim().ToLowerInvariant();
        return SupportedLanguages.Contains(primarySublanguage)
            ? primarySublanguage
            : throw CreateUnsupportedLanguageException(declared, primarySublanguage, packId, manifestPath);
    }

    private static InvalidOperationException CreateUnsupportedLanguageException(
        string declared,
        string primarySublanguage,
        string packId,
        string manifestPath)
        => new(
            $"Content pack '{packId}' declares spoken language '{declared}' in '{manifestPath}', whose primary "
            + $"sublanguage '{primarySublanguage}' is not in the supported set: {string.Join(", ", SupportedLanguages)}. "
            + "Use a supported language or remove the Language declaration to fall back to "
            + $"{DefaultLanguage}.");
}
