using AlleyCat.Motion;
using Godot;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Animations;

/// <summary>
/// Per-category view over a character's catalogue. Lists only entries compatible with the character, in catalogue
/// order, and resolves the effective entry (ANIM-003 TR9, TR10).
/// </summary>
public abstract class AnimationCategoryView
{
    private readonly ILogger _logger;
    private AnimationEntry? _effective;
    private bool _resolved;

    private protected AnimationCategoryView(
        AnimationCategory category,
        IReadOnlyList<AnimationEntry> entries,
        string? templateDefault,
        ILogger logger)
    {
        Category = category;
        Entries = entries;
        TemplateDefault = string.IsNullOrEmpty(templateDefault) ? null : templateDefault;
        _logger = logger;
    }

    /// <summary>The category this view covers.</summary>
    public AnimationCategory Category
    {
        get;
    }

    /// <summary>Compatible entries in catalogue order.</summary>
    public IReadOnlyList<AnimationEntry> Entries
    {
        get;
    }

    /// <summary>The character template default id, if declared.</summary>
    public string? TemplateDefault
    {
        get;
    }

    /// <summary>The agent preference id; it wins over the template default when it names a compatible entry.</summary>
    public string? Preference
    {
        get;
        set
        {
            string? normalised = string.IsNullOrEmpty(value) ? null : value;
            if (normalised != field)
            {
                field = normalised;
                _resolved = false;
            }
        }
    }

    /// <summary>
    /// The effective entry: agent preference, then template default, then the first compatible entry.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the category has no compatible entry.</exception>
    public AnimationEntry Effective => TryGetEffective(out AnimationEntry? entry)
        ? entry
        : throw new InvalidOperationException(
            $"Animation catalogue has no compatible {Category.ToName()} entry for this character.");

    /// <summary>Resolves the effective entry without throwing.</summary>
    public bool TryGetEffective([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AnimationEntry? entry)
    {
        if (!_resolved)
        {
            _effective = AnimationDefaultResolver.Resolve(Category, Entries, Preference, TemplateDefault, _logger);
            _resolved = true;
        }

        entry = _effective;
        return entry is not null;
    }

    /// <summary>Finds a compatible entry by id.</summary>
    public AnimationEntry? Find(string id)
    {
        foreach (AnimationEntry entry in Entries)
        {
            if (entry.ID == id)
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>Returns compatible entries carrying a tag, in catalogue order.</summary>
    public IEnumerable<AnimationEntry> FindByTag(string tag) => Entries.Where(entry => entry.Tags.Contains(tag));

    private protected AnimationEntry Require(string? id)
        => id is null
            ? Effective
            : Find(id) ?? throw new KeyNotFoundException(
                $"Animation catalogue has no compatible {Category.ToName()} entry '{id}' for this character.");
}

/// <summary>
/// Idle view: resolves entries to native clips on the reference skeleton.
/// </summary>
public sealed class IdleAnimationView : AnimationCategoryView
{
    private readonly IReadOnlyDictionary<AnimationEntry, Animation> _clips;

    internal IdleAnimationView(
        IReadOnlyList<AnimationEntry> entries,
        IReadOnlyDictionary<AnimationEntry, Animation> clips,
        string? templateDefault,
        ILogger logger)
        : base(AnimationCategory.Idle, entries, templateDefault, logger)
    {
        _clips = clips;
    }

    /// <summary>Returns the native clip of an entry id, or of the effective entry when no id is given.</summary>
    public Animation GetClip(string? id = null) => _clips[Require(id)];
}

/// <summary>
/// Locomotion view: resolves entries to ANIM-002 reviewed sets, loaded by the existing set loader.
/// </summary>
public sealed class LocomotionAnimationView : AnimationCategoryView
{
    internal LocomotionAnimationView(IReadOnlyList<AnimationEntry> entries, string? templateDefault, ILogger logger)
        : base(AnimationCategory.Locomotion, entries, templateDefault, logger)
    {
    }

    /// <summary>
    /// Loads a new caller-owned reviewed-set sampler for an entry id, or for the effective entry when no id is given.
    /// </summary>
    public ReviewedRoleSampler CreateSampler(SomaMotionData calibration, string? id = null)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        return new ReviewedRoleSampler(ProjectSettings.GlobalizePath(Require(id).ResourcePath), calibration);
    }
}
