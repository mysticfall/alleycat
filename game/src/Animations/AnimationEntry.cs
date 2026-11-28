namespace AlleyCat.Animations;

/// <summary>
/// One catalogue entry: the shared core plus its category payload path.
/// </summary>
/// <param name="ID">Lowercase entry identifier, unique per category within its pack.</param>
/// <param name="Category">The entry category; identity is the pair (<see cref="Category" />, <see cref="ID" />).</param>
/// <param name="Tags">Non-empty lowercase tags for agents; never shown to observers.</param>
/// <param name="Description">External, visible account of the motion shown to observers.</param>
/// <param name="PackID">Identifier of the owning animation pack.</param>
/// <param name="AuthoredPath">The content-root-relative <c>clip</c> or <c>set</c> path as authored.</param>
/// <param name="ResourcePath">The authored path resolved against the owning pack's content root.</param>
public sealed record AnimationEntry(
    string ID,
    AnimationCategory Category,
    IReadOnlyList<string> Tags,
    string Description,
    string PackID,
    string AuthoredPath,
    string ResourcePath)
{
    /// <summary>Human-readable identity used in diagnostics.</summary>
    public override string ToString() => $"{Category.ToName()}/{ID} (pack '{PackID}')";
}
