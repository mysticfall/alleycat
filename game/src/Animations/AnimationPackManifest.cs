using AlleyCat.Core.Content;

namespace AlleyCat.Animations;

/// <summary>
/// A validated <c>anim003_pack_v1</c> animation pack.
/// </summary>
/// <param name="ID">The pack identifier.</param>
/// <param name="ManifestPath">The resource path of the <c>pack.json</c> manifest.</param>
/// <param name="Context">The content context that pack-relative paths resolved against.</param>
/// <param name="Entries">Entries in manifest order.</param>
public sealed record AnimationPackManifest(
    string ID,
    string ManifestPath,
    ContentContext Context,
    IReadOnlyList<AnimationEntry> Entries)
{
    /// <summary>The only supported manifest schema identifier.</summary>
    public const string Schema = "anim003_pack_v1";
}

/// <summary>
/// One pack validation finding. Diagnostics name the pack, entry, field and path.
/// </summary>
/// <param name="Pack">The pack id, or the manifest path when the id is unavailable.</param>
/// <param name="Entry">The offending entry id or index, if any.</param>
/// <param name="Field">The offending field, if any.</param>
/// <param name="Path">The offending authored path, or the manifest path for manifest-level findings.</param>
/// <param name="Message">What is wrong.</param>
public sealed record AnimationPackDiagnostic(string Pack, string? Entry, string? Field, string Path, string Message)
{
    /// <inheritdoc />
    public override string ToString()
        => $"Animation pack '{Pack}'{(Entry is null ? "" : $", entry '{Entry}'")}{(Field is null ? "" : $", field '{Field}'")}, path '{Path}': {Message}";
}

/// <summary>
/// Raised when an animation pack fails validation.
/// </summary>
public sealed class AnimationPackValidationException(IReadOnlyList<AnimationPackDiagnostic> diagnostics)
    : InvalidOperationException(string.Join(Environment.NewLine, diagnostics))
{
    /// <summary>All validation findings.</summary>
    public IReadOnlyList<AnimationPackDiagnostic> Diagnostics => diagnostics;
}
