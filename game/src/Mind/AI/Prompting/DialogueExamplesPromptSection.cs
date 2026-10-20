using AlleyCat.Core;
using AlleyCat.Core.Content;
using Godot;

namespace AlleyCat.Mind.AI.Prompting;

/// <summary>
/// Runtime-backed prompt section that injects the owning character's authored dialogue examples from
/// <c>lore/perspectives/&lt;type&gt;/&lt;id&gt;/dialogue.md</c> under the active content root, preceded by the
/// section's own static guidance. The dialogue file is read directly as opaque text — it is not lore access and
/// never routes through the lore query abstraction — so its content stays invisible to lore queries, the
/// session-start catalogue, and <c>read_lore</c> (AI-012 TR-4). A missing file is a normal authoring state and
/// yields empty content; an existing but unreadable dialogue or guidance file fails clearly, and an invalid
/// owner identity fails prompt building only when this section is used (AI-012 TR-6). The section's content is
/// template-opaque (AI-012 TR-3): apart from the sanctioned <c>{{ character.FullId }}</c> label token, which is
/// substituted with the resolved owner full ID, the authored text reaches the rendered instruction verbatim and
/// can never break session-start compilation.
/// </summary>
[GlobalClass]
public partial class DialogueExamplesPromptSection : PromptSection
{
    /// <summary>
    /// Godot resource path to the static guidance file rendered before the example content, by default
    /// <c>res://prompts/dialogue.md</c>. An empty or whitespace path disables guidance and yields the example
    /// content alone; a non-empty path whose file cannot be read fails prompt building clearly.
    /// </summary>
    [Export(PropertyHint.File, "*.md,*.txt")]
    public string GuidancePath { get; set; } = "res://prompts/dialogue.md";

    /// <inheritdoc />
    public override bool IsTemplateOpaque => true;

    /// <inheritdoc />
    public override Task<string> GetContentAsync(
        PromptSectionBuildContext buildContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(buildContext);
        cancellationToken.ThrowIfCancellationRequested();

        string observerFullId = buildContext.Character.FullId;
        string path = DialogueExamplesPaths.Resolve(
            buildContext.Scene.Content,
            observerFullId,
            nameof(DialogueExamplesPromptSection));

        if (!Godot.FileAccess.FileExists(path))
        {
            return Task.FromResult(string.Empty);
        }

        string exampleContent = ReadText(path, "dialogue examples file");

        // Exactly one substitution is sanctioned (AI-012 TR-3): the authored label token becomes the resolved
        // full ID, which identity validation guarantees carries no template syntax. Everything else is data.
        exampleContent = DialogueExamplesContent.SubstituteCharacterToken(exampleContent, observerFullId);

        string guidance = string.IsNullOrWhiteSpace(GuidancePath)
            ? string.Empty
            : ReadText(GuidancePath, "dialogue examples guidance file");

        return Task.FromResult(DialogueExamplesContent.AssembleSectionContent(guidance, exampleContent));
    }

    private static string ReadText(string path, string fileDescription)
    {
        using var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
        if (file is null)
        {
            Error error = Godot.FileAccess.GetOpenError();
            throw new InvalidOperationException(
                $"DialogueExamplesPromptSection could not read {fileDescription} '{path}'. Godot FileAccess error: {error}.");
        }

        return file.GetAsText();
    }
}

/// <summary>
/// Pure path-resolution and identity logic behind <see cref="DialogueExamplesPromptSection" />: maps an observer
/// <c>FullId</c> plus the active content root to the per-character dialogue examples file, using the same
/// perspective-root mapping as the lore backend (AI-004 TR-4) with no colon in the path (AI-012 TR-1). Identity is
/// validated only when the section is used, wrapping identity failures in a section-naming error (AI-012 TR-6).
/// </summary>
internal static class DialogueExamplesPaths
{
    /// <summary>File name of the per-character dialogue examples file inside the perspective folder.</summary>
    public const string FileName = "dialogue.md";

    /// <summary>
    /// Resolves <c>&lt;RootPath&gt;lore/perspectives/&lt;type&gt;/&lt;id&gt;/dialogue.md</c> for one observer and
    /// content root, validating the observer identity at the usage boundary.
    /// </summary>
    /// <param name="content">Active content context supplying the root path.</param>
    /// <param name="observerFullId">Observer FullId, for example <c>char:vadim</c>.</param>
    /// <param name="sectionName">Requesting section name used in identity failure messages.</param>
    /// <returns>The dialogue examples resource path, colon-free below the root scheme.</returns>
    public static string Resolve(ContentContext content, string observerFullId, string sectionName)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionName);

        try
        {
            IdentityValidator.ValidateFullId(observerFullId, nameof(observerFullId));
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                $"{sectionName} requires a non-empty, valid observer ID.",
                exception);
        }

        int separator = observerFullId.IndexOf(':', StringComparison.Ordinal);
        string observerType = observerFullId[..separator];
        string observerID = observerFullId[(separator + 1)..];
        return CombineResourcePath(
            content.RootPath,
            $"lore/perspectives/{observerType}/{observerID}/{FileName}");
    }

    private static string CombineResourcePath(string rootPath, string relativePath)
        => rootPath.EndsWith("/", StringComparison.Ordinal) ? rootPath + relativePath : rootPath + "/" + relativePath;
}

/// <summary>
/// Pure content logic behind <see cref="DialogueExamplesPromptSection" />: the single sanctioned character-label
/// substitution and the guidance-plus-examples assembly (AI-012 TR-2, TR-3).
/// </summary>
internal static class DialogueExamplesContent
{
    /// <summary>The only authored token the section substitutes: the character's own speaker label.</summary>
    public const string CharacterToken = "{{ character.FullId }}";

    /// <summary>
    /// Replaces every literal <c>{{ character.FullId }}</c> occurrence in example content with the resolved
    /// observer full ID, ordinal and exact-spacing; any other text — including lookalike expressions — passes
    /// through unchanged.
    /// </summary>
    /// <param name="exampleContent">Authored example text.</param>
    /// <param name="observerFullId">Validated observer full ID substituted for the label token.</param>
    /// <returns>The example text with every label token resolved.</returns>
    public static string SubstituteCharacterToken(string exampleContent, string observerFullId)
    {
        ArgumentNullException.ThrowIfNull(exampleContent);
        ArgumentNullException.ThrowIfNull(observerFullId);

        return exampleContent.Replace(CharacterToken, observerFullId, StringComparison.Ordinal);
    }

    /// <summary>
    /// Assembles the section content: guidance first, then the example content, separated by one blank line.
    /// Empty guidance yields the example content alone; an existing empty example file yields guidance alone.
    /// The caller handles missing example files before assembly, returning an empty section.
    /// </summary>
    /// <param name="guidance">Authored guidance text, or empty when no guidance is referenced.</param>
    /// <param name="exampleContent">Substituted text from an existing dialogue file.</param>
    /// <returns>The assembled section content.</returns>
    public static string AssembleSectionContent(string guidance, string exampleContent)
    {
        ArgumentNullException.ThrowIfNull(guidance);
        ArgumentNullException.ThrowIfNull(exampleContent);

        return guidance.Length == 0
            ? exampleContent
            : exampleContent.Length == 0 ? guidance : guidance + "\n\n" + exampleContent;
    }
}
