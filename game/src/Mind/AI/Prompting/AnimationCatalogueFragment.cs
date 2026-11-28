using System.Text;
using AlleyCat.Animations;

namespace AlleyCat.Mind.AI.Prompting;

/// <summary>
/// Formats the static per-character animation catalogue fragment (ANIM-003 Mind Integration, Prompt Catalogue
/// Fragment): each non-empty category under its in-character heading, each compatible entry once as its backticked id
/// followed by its tags. Descriptions, paths and pack names are never listed. Locomotion is listed by variant (entry).
/// </summary>
internal static class AnimationCatalogueFragment
{
    /// <summary>Formats the fragment; returns an empty string when both categories are empty.</summary>
    public static string Format(
        IReadOnlyList<AnimationEntry> stances,
        IReadOnlyList<AnimationEntry> gaits,
        string stanceHeading,
        string gaitHeading)
    {
        ArgumentNullException.ThrowIfNull(stances);
        ArgumentNullException.ThrowIfNull(gaits);
        StringBuilder builder = new();
        AppendCategory(builder, stanceHeading, stances);
        AppendCategory(builder, gaitHeading, gaits);
        return builder.ToString().TrimEnd();
    }

    private static void AppendCategory(StringBuilder builder, string heading, IReadOnlyList<AnimationEntry> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        if (builder.Length > 0)
        {
            _ = builder.AppendLine();
        }

        _ = builder.AppendLine(heading.Trim());
        foreach (AnimationEntry entry in entries)
        {
            _ = builder.Append("- `").Append(entry.ID).Append('`');
            if (entry.Tags.Count > 0)
            {
                _ = builder.Append(": ").Append(string.Join(", ", entry.Tags));
            }

            _ = builder.AppendLine();
        }
    }
}
