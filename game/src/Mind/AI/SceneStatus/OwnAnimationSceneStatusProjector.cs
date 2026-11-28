using AlleyCat.Animations;
using Godot;

namespace AlleyCat.Mind.AI.SceneStatus;

/// <summary>
/// Projects the agent's own current stance and gait — each catalogue category's effective entry — for the
/// current-scene status (ANIM-003 Mind Integration, Scene Status Line; AI-003 TR5).
/// </summary>
[GlobalClass]
public sealed partial class OwnAnimationSceneStatusProjector : SceneStatusProjector
{
    /// <summary>Stable binding ID for the own stance-and-gait status projection.</summary>
    public const string ProjectorIDValue = "own-stance-and-gait";

    private AnimationCatalogue? _catalogue;
    private Character.Character? _resolvedFor;

    /// <summary>Creates the projector with its stable binding ID.</summary>
    public OwnAnimationSceneStatusProjector()
    {
        ProjectorID = ProjectorIDValue;
    }

    /// <inheritdoc />
    public override Type ProjectionType => typeof(OwnAnimationSceneStatus);

    /// <inheritdoc />
    public override ISceneStatusProjection Project(SceneStatusBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        AnimationCatalogue? catalogue = ResolveCatalogue(context.Character);
        // An unbuilt catalogue is never built here: projection may run away from the main thread, and the character's
        // navigation owns the build.
        return catalogue is not { IsBuilt: true }
            ? new OwnAnimationSceneStatus(null, null)
            : new OwnAnimationSceneStatus(
                ToStatus(catalogue.Idle),
                ToStatus(catalogue.Locomotion));
    }

    private AnimationCatalogue? ResolveCatalogue(Character.ICharacter character)
    {
        if (character is not Character.Character node)
        {
            return null;
        }

        if (!ReferenceEquals(_resolvedFor, node))
        {
            // Resolved once per character, on the first projection: the session-start validation pass on the main
            // thread.
            AnimationCatalogue[] catalogues = [.. node.GetChildren().OfType<AnimationCatalogue>()];
            _catalogue = catalogues.Length == 1 ? catalogues[0] : null;
            _resolvedFor = node;
        }

        return _catalogue;
    }

    private static OwnAnimationEntryStatus? ToStatus(AnimationCategoryView view)
        => view.TryGetEffective(out AnimationEntry? entry) ? new OwnAnimationEntryStatus(entry.ID, entry.Description) : null;
}

/// <summary>Immutable root rendered by the own stance-and-gait current-scene-status section.</summary>
/// <param name="Stance">The effective idle entry, or null when the character has none.</param>
/// <param name="Gait">The effective locomotion entry, or null when the character has none.</param>
public sealed record OwnAnimationSceneStatus(OwnAnimationEntryStatus? Stance, OwnAnimationEntryStatus? Gait)
    : ISceneStatusProjection;

/// <summary>One of the agent's own effective catalogue entries.</summary>
/// <param name="ID">The entry id, rendered backticked.</param>
/// <param name="Description">The entry's visible account.</param>
public sealed record OwnAnimationEntryStatus(string ID, string Description);
