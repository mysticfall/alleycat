using AlleyCat.Core.Logging;
using Godot;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Animations;

/// <summary>
/// A character's animation catalogue: composes its enabled child <see cref="AnimationPack" /> nodes into per-category
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

    // Test seams; never exported or set by gameplay.
    internal IAnimationPackResources? ResourcesOverride
    {
        get; set;
    }

    internal ILogger? LoggerOverride
    {
        get; set;
    }

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
    /// <exception cref="InvalidOperationException">Thrown on duplicate enabled pack ids.</exception>
    /// <exception cref="AnimationPackValidationException">Thrown when an enabled pack fails validation.</exception>
    public void Build(Skeleton3D skeleton)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        if (IsBuilt)
        {
            return;
        }

        ILogger logger = LoggerOverride ?? GameLoggerResolver.ResolveRequired<AnimationCatalogue>();
        IAnimationPackResources resources = ResourcesOverride ?? GodotAnimationPackResources.Instance;

        List<AnimationPackLayer> layers = [];
        foreach (AnimationPack pack in GetChildren().OfType<AnimationPack>())
        {
            if (pack.Enabled)
            {
                layers.Add(new AnimationPackLayer(pack.LoadManifest(resources), pack.Priority));
            }
            else
            {
                logger.LogDebug("Animation catalogue {Catalogue}: pack node {Pack} is disabled", Name, pack.Name);
            }
        }

        IReadOnlyList<AnimationEntry> entries = AnimationCatalogueComposer.Compose(layers, logger);

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

        _entries = entries;
        _locomotion = new LocomotionAnimationView(locomotion.AsReadOnly(), DefaultLocomotion, logger);
        _idle = new IdleAnimationView(idles.AsReadOnly(), clips, DefaultIdle, logger);
        logger.LogInformation(
            "Animation catalogue {Catalogue} built from {PackCount} pack(s): {IdleCount} idle and {LocomotionCount} locomotion entries compatible",
            Name, layers.Count, idles.Count, locomotion.Count);
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
