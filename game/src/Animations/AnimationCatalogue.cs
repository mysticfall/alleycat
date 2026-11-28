using AlleyCat.Core.Content;
using AlleyCat.Core.Logging;
using AlleyCat.Rigging.Installation;
using Godot;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Animations;

/// <summary>
/// A character's animation catalogue: composes ordered <see cref="AnimationVariantLayer" /> nodes into per-category
/// views (ANIM-003 TR6–TR11). The catalogue is built once before first use and stays fixed for the session.
/// </summary>
[GlobalClass]
public partial class AnimationCatalogue : Node
{
    private IdleAnimationView? _idle;
    private LocomotionAnimationView? _locomotion;
    private IReadOnlyList<AnimationEntry> _entries = [];

    /// <summary>Template default idle id. Player templates declare none.</summary>
    [Export]
    public string DefaultIdle { get; set; } = string.Empty;

    /// <summary>Template default locomotion id.</summary>
    [Export]
    public string DefaultLocomotion { get; set; } = string.Empty;

    /// <summary>Whether the catalogue has been built.</summary>
    public bool IsBuilt => _idle is not null;

    /// <summary>
    /// Diagnostic and test view of all composed entries in catalogue order, before character compatibility
    /// filtering. Consumers must use <see cref="Idle" /> and <see cref="Locomotion" /> instead (ANIM-003 TR9).
    /// </summary>
    internal IReadOnlyList<AnimationEntry> Entries => EnsureBuilt()._entries;

    /// <summary>The idle view.</summary>
    public IdleAnimationView Idle => EnsureBuilt()._idle!;

    /// <summary>The locomotion view.</summary>
    public LocomotionAnimationView Locomotion => EnsureBuilt()._locomotion!;

    /// <summary>
    /// Returns the single catalogue that is a direct child of a character.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when there is not exactly one catalogue.</exception>
    public static AnimationCatalogue RequireFor(Node character)
    {
        ArgumentNullException.ThrowIfNull(character);
        AnimationCatalogue[] catalogues = [.. character.GetChildren().OfType<AnimationCatalogue>()];
        return catalogues.Length == 1
            ? catalogues[0]
            : throw new InvalidOperationException(
                $"Character '{character.Name}' must have exactly one {nameof(AnimationCatalogue)} child, found {catalogues.Length}.");
    }

    /// <summary>
    /// Builds the catalogue against the character's native reference skeleton. Later calls are no-ops.
    /// </summary>
    public void Build(Skeleton3D skeleton)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        if (!IsBuilt)
        {
            ContentContext active = Game.Instance.GetRequiredService<IContentResolver>().GetCurrentContentContext();
            PackedScene? template = null;
            if (GetParent() is Character.Character character)
            {
                RigRoleTemplateSceneInstaller? installer = character.GetNodeOrNull<RigRoleTemplateSceneInstaller>("PlayerCharacterInstaller")
                    ?? character.GetNodeOrNull<RigRoleTemplateSceneInstaller>("NPCCharacterInstaller");
                template = installer?.Template;
            }

            Build(skeleton, active, GameLoggerResolver.ResolveRequired<AnimationCatalogue>(), template);
        }
    }

    internal void Build(Skeleton3D skeleton, ContentContext active, ILogger logger, PackedScene? template = null)
    {
        if (IsBuilt)
        {
            return;
        }

        AnimationVariantLayer[] variantLayers = [.. GetChildren().OfType<AnimationVariantLayer>()];
        if (template is not null)
        {
            AnimationVariantLoader.RequireTemplateOwners(template, active);
            variantLayers = AnimationVariantSelection.InTemplateOrder(template, variantLayers);
        }

        IReadOnlyList<AnimationEntry> entries = AnimationVariantSelection.Compose(variantLayers, active, GodotAnimationVariantResources.Instance);

        List<AnimationEntry> idles = [];
        Dictionary<AnimationEntry, Animation> clips = [];
        List<AnimationEntry> locomotion = [];
        bool presentable = skeleton.GetParent() is RetargetModifier3D;
        foreach (AnimationEntry entry in entries)
        {
            switch (entry.Category)
            {
                case AnimationCategory.Idle:
                    Animation clip = ResourceLoader.Load<Animation>(entry.ResourcePath);
                    IReadOnlyList<string> unresolved = IdleClipCompatibility.FindUnresolvedMappedTracks(
                        ReadBoneTracks(clip), skeleton.Name, bone => skeleton.FindBone(bone) >= 0);
                    if (unresolved.Count > 0)
                    {
                        logger.LogWarning(
                            "Animation catalogue: excluding {Entry} for skeleton {Skeleton}; unresolved SOMA-mapped tracks: {Tracks}",
                            entry, skeleton.Name, string.Join(", ", unresolved));
                        continue;
                    }

                    idles.Add(entry);
                    clips.Add(entry, clip);
                    break;
                case AnimationCategory.Locomotion:
                    if (!presentable)
                    {
                        logger.LogWarning(
                            "Animation catalogue: excluding {Entry} for skeleton {Skeleton}; the character has no MotionBricks presentation rig",
                            entry, skeleton.Name);
                        continue;
                    }

                    locomotion.Add(entry);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported animation category {entry.Category}.");
            }
        }

        var locomotionView = new LocomotionAnimationView(locomotion.AsReadOnly(), DefaultLocomotion, logger);
        var idleView = new IdleAnimationView(idles.AsReadOnly(), clips, DefaultIdle, logger);
        _entries = entries;
        _locomotion = locomotionView;
        _idle = idleView;
        logger.LogInformation(
            "Animation catalogue {Catalogue} built from {LayerCount} layers: {IdleCount} idle and {LocomotionCount} locomotion entries compatible",
            Name, variantLayers.Length, idles.Count, locomotion.Count);
    }

    private AnimationCatalogue EnsureBuilt()
    {
        if (!IsBuilt)
        {
            Node? parent = GetParent();
            Skeleton3D skeleton = parent is Character.Character character
                ? character.RequireMotionTarget()
                : throw new InvalidOperationException(
                    $"Animation catalogue '{Name}' is not under a character; call {nameof(Build)} with the native skeleton.");
            Build(skeleton);
        }

        return this;
    }

    /// <summary>Reads a clip's bone tracks as (skeleton node name, bone name) pairs.</summary>
    internal static IEnumerable<(string SkeletonNode, string Bone)> ReadBoneTracks(Animation clip)
    {
        for (int i = 0; i < clip.GetTrackCount(); i++)
        {
            Animation.TrackType type = clip.TrackGetType(i);
            if (type is not (Animation.TrackType.Position3D or Animation.TrackType.Rotation3D or Animation.TrackType.Scale3D))
            {
                continue;
            }

            NodePath path = clip.TrackGetPath(i);
            if (path.GetSubNameCount() == 0 || path.GetNameCount() == 0)
            {
                continue;
            }

            yield return (path.GetName(path.GetNameCount() - 1), path.GetSubName(0));
        }
    }
}
