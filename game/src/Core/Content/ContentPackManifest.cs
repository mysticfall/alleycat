using Godot;

namespace AlleyCat.Core.Content;

/// <summary>
/// Optional per-pack metadata declared at <c>res://content/&lt;pack-id&gt;/manifest.tres</c>, distinct from the
/// selection manifest (<see cref="ContentManifest" />) at <c>res://content/manifest.tres</c>.
/// </summary>
/// <remarks>
/// The pack manifest exposes optional declarations: the pack's spoken language and its start scene. Language
/// metadata stays infrastructure-independent: packs never declare service models, endpoints, or credentials.
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

    /// <summary>Declared start scene of the pack, overriding the conventional <c>start.tscn</c>.</summary>
    /// <remarks>
    /// Declaration is optional and backward-compatible: a missing manifest, an omitted value, or a blank value
    /// resolves to the pack's conventional start scene (<see cref="ContentPaths.StartSceneFileName" />).
    /// </remarks>
    [Export(PropertyHint.File, "*.tscn")]
    public string StartScene { get; set; } = string.Empty;

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

    /// <summary>
    /// Normalises a declared start scene to the resource path to probe for a pack's start scene.
    /// </summary>
    /// <remarks>
    /// Outer whitespace is trimmed. A blank value resolves to the pack's conventional start scene
    /// (<paramref name="packRoot" /> + <see cref="ContentPaths.StartSceneFileName" />), so packs that declare
    /// nothing are unaffected. A value beginning with <c>res://</c> is used as-is. Any other value is
    /// pack-relative and resolves against the pack root: <c>scenes/intro.tscn</c> resolves to
    /// <c>res://content/&lt;pack-id&gt;/scenes/intro.tscn</c>. Pure, Godot-free, and never throwing, because
    /// every accepted form resolves to a path.
    /// </remarks>
    /// <param name="declaredStartScene">Declared start-scene value, or null/blank for the conventional scene.</param>
    /// <param name="packRoot">Pack root path with a trailing slash, for example <c>res://content/&lt;pack-id&gt;/</c>.</param>
    /// <returns>The resource path to probe for the pack's start scene.</returns>
    public static string ResolveDeclaredStartScenePath(string? declaredStartScene, string packRoot)
    {
        string declared = (declaredStartScene ?? string.Empty).Trim();
        if (declared.Length == 0)
        {
            declared = ContentPaths.StartSceneFileName;
        }

        return declared.StartsWith("res://", StringComparison.Ordinal) ? declared : packRoot + declared;
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
