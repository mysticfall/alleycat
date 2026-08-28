using System.Collections.ObjectModel;
using AlleyCat.Core;
namespace AlleyCat.Vision;

/// <summary>Immutable ordered snapshot of visible canonical subject identities.</summary>
public sealed record VisualSurveyPercept : IVisualPercept
{
    /// <summary>Creates a producer-owned snapshot of the supplied ordered identities.</summary>
    public VisualSurveyPercept(IEnumerable<string> subjectFullIDs)
    {
        ArgumentNullException.ThrowIfNull(subjectFullIDs);
        string[] snapshot = [.. subjectFullIDs];
        for (int index = 0; index < snapshot.Length; index++)
        {
            IdentityValidator.ValidateFullId(snapshot[index], nameof(subjectFullIDs));
        }

        SubjectFullIDs = new ReadOnlyCollection<string>(snapshot);
    }

    /// <summary>Gets visible canonical identities in acquisition order.</summary>
    public IReadOnlyList<string> SubjectFullIDs
    {
        get;
    }
}
