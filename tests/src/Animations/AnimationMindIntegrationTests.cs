using AlleyCat.Animations;
using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.AI.Tool;
using AlleyCat.Mind.Observation;
using AlleyCat.Mind.Perception;
using AlleyCat.Vision;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Animations;

/// <summary>
/// ANIM-003 Mind Integration pure contracts: tool preference requests (MIND-AC-001), the static catalogue fragment
/// (MIND-AC-002) and the observation trigger ledger, payload, rendering and lifetime policy (MIND-AC-003).
/// </summary>
public sealed class AnimationMindIntegrationTests
{
    private static readonly AnimationEntry _sad = Entry("sad", AnimationCategory.Idle, ["sad", "slumped"],
        "stands with slumped shoulders");
    private static readonly AnimationEntry _slump = Entry("slump", AnimationCategory.Idle, ["tired"],
        "stands slumped, weight sagging onto one hip");
    private static readonly AnimationEntry _feminine = Entry("feminine", AnimationCategory.Locomotion,
        ["relaxed", "feminine"], "walks with an easy, relaxed gait");
    private static readonly AnimationEntry _stroll = Entry("stroll", AnimationCategory.Locomotion, ["leisurely"],
        "strolls along at an unhurried pace");

    /// <summary>MIND-AC-001: a valid id sets the preference of each tool's own category and is accepted.</summary>
    [Fact]
    public void PreferenceRequest_ValidIds_SetEachCategoryPreference()
    {
        FakeVariantControl control = new(["sad", "slump"], ["feminine", "stroll"]);

        AnimationPreferenceOutcome stance = AnimationPreferenceRequest.Apply(control, AnimationCategory.Idle, "slump");
        AnimationPreferenceOutcome gait = AnimationPreferenceRequest.Apply(control, AnimationCategory.Locomotion, "`stroll`");

        Assert.True(stance.Accepted);
        Assert.True(gait.Accepted);
        Assert.Equal("slump", control.IdlePreference);
        Assert.Equal("stroll", control.LocomotionPreference);
        Assert.Contains("`slump`", stance.Message, StringComparison.Ordinal);
        Assert.Contains("`stroll`", gait.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// MIND-AC-001: unknown, other-category (incompatible) and blank ids are refused in character and leave the
    /// preference unchanged.
    /// </summary>
    [Theory]
    [InlineData(AnimationCategory.Idle, "dance")]
    [InlineData(AnimationCategory.Idle, "stroll")]
    [InlineData(AnimationCategory.Idle, "  ")]
    [InlineData(AnimationCategory.Locomotion, "sprint")]
    [InlineData(AnimationCategory.Locomotion, "slump")]
    [InlineData(AnimationCategory.Locomotion, "")]
    public void PreferenceRequest_UnknownOrIncompatibleIds_AreRefusedWithPreferenceUnchanged(
        AnimationCategory category, string id)
    {
        FakeVariantControl control = new(["sad", "slump"], ["feminine", "stroll"])
        {
            IdlePreference = "sad",
            LocomotionPreference = "feminine",
        };

        AnimationPreferenceOutcome outcome = AnimationPreferenceRequest.Apply(control, category, id);

        Assert.False(outcome.Accepted);
        Assert.Equal("sad", control.IdlePreference);
        Assert.Equal("feminine", control.LocomotionPreference);
        Assert.StartsWith("You cannot", outcome.Message, StringComparison.Ordinal);
        Assert.Contains(category == AnimationCategory.Idle ? "stance" : "gait", outcome.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// MIND-AC-002: the fragment lists ids and tags per category under the supplied headings, locomotion by variant,
    /// without descriptions, paths or pack names, and omits empty categories.
    /// </summary>
    [Fact]
    public void CatalogueFragment_ListsIdsAndTagsPerCategory()
    {
        string fragment = AnimationCatalogueFragment.Format([_sad, _slump], [_feminine, _stroll], "Stances:", "Gaits:");

        Assert.Equal(
            "Stances:\n- `sad`: sad, slumped\n- `slump`: tired\n\nGaits:\n- `feminine`: relaxed, feminine\n- `stroll`: leisurely",
            fragment.ReplaceLineEndings("\n"));
        Assert.DoesNotContain(_sad.Description, fragment, StringComparison.Ordinal);
        Assert.DoesNotContain("res://", fragment, StringComparison.Ordinal);
        Assert.DoesNotContain("pack", fragment, StringComparison.Ordinal);

        Assert.Equal("Gaits:\n- `stroll`: leisurely",
            AnimationCatalogueFragment.Format([], [_stroll], "Stances:", "Gaits:").ReplaceLineEndings("\n"));
        Assert.Equal(string.Empty, AnimationCatalogueFragment.Format([], [], "Stances:", "Gaits:"));
    }

    /// <summary>
    /// MIND-AC-003: the first event per subject and category is a change; afterwards only a target that differs from
    /// the last delivered entry is. Walking off in the same gait and stopping back into the same idle are not changes.
    /// </summary>
    [Fact]
    public void Ledger_TriggersOnlyWhenTargetDiffersFromLastDelivered()
    {
        AnimationChangeLedger ledger = new();

        Assert.True(Deliver(ledger, "char:b", _sad)); // first delivered event (idle→idle or any kind)
        Assert.True(Deliver(ledger, "char:b", _feminine)); // first gait seen: idle→locomotion
        Assert.False(Deliver(ledger, "char:b", _sad)); // locomotion→idle back into the same idle
        Assert.False(Deliver(ledger, "char:b", _feminine)); // idle→locomotion in the same gait
        Assert.True(Deliver(ledger, "char:b", _stroll)); // locomotion variant switch
        Assert.True(Deliver(ledger, "char:b", _slump)); // locomotion→idle into a different idle
        Assert.False(Deliver(ledger, "char:b", _slump));
        Assert.True(Deliver(ledger, "char:b", _sad)); // idle→idle back

        // Keyed per subject: another subject's first event is its own change.
        Assert.True(Deliver(ledger, "char:c", _sad));
        Assert.Equal("sad", ledger.GetLastObserved("char:b", AnimationCategory.Idle));
        Assert.Equal("stroll", ledger.GetLastObserved("char:b", AnimationCategory.Locomotion));
    }

    /// <summary>
    /// MIND-AC-003: an interruption raises a new event only for a different target; it is observed only when it differs
    /// from the last delivered entry, and the abandoned target leaves nothing behind.
    /// </summary>
    [Fact]
    public void Ledger_InterruptionObservedOnlyWhenNewTargetDiffers()
    {
        AnimationChangeLedger ledger = new();
        Assert.True(Deliver(ledger, "char:b", _sad));

        // sad → slump starts, interrupted back to sad: slump was delivered, sad differs from it.
        Assert.True(Deliver(ledger, "char:b", _slump));
        Assert.True(Deliver(ledger, "char:b", _sad));

        // An undelivered (unfocused) event is never recorded, so a later event to the last delivered entry is silent.
        Assert.True(ledger.IsChange("char:b", AnimationCategory.Idle, _slump));
        Assert.False(ledger.IsChange("char:b", AnimationCategory.Idle, _sad));
        Assert.Equal("sad", ledger.GetLastObserved("char:b", AnimationCategory.Idle));
    }

    /// <summary>MIND-AC-003: the observation renders the subject and description only, never the id or tags.</summary>
    [Fact]
    public void Observation_RendersDescriptionOnly()
    {
        ObservedAnimationChange change = new("char:b", AnimationCategory.Idle, _slump.Description);

        string rendered = change.Render(new FakeCharacter());

        Assert.Equal("Saw that `char:b` now stands slumped, weight sagging onto one hip.", rendered);
        Assert.DoesNotContain("slump`", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("tired", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// MIND-AC-003: the lifetime policy is event-eligible and keyed by subject and category; equal values suppress and
    /// changed values supersede.
    /// </summary>
    [Fact]
    public void LifetimePolicy_KeyedBySubjectAndCategory_EventEligible()
    {
        IObservationLifetimePolicy policy = new ObservationLifetimePolicyRegistry(
            InitialObservationLifetimePolicies.Create()).Resolve(typeof(ObservedAnimationChange));
        AcceptedObservationEntry stance = Accepted(1, new ObservedAnimationChange("char:b", AnimationCategory.Idle, _sad.Description));
        AcceptedObservationEntry gait = Accepted(2, new ObservedAnimationChange("char:b", AnimationCategory.Locomotion, _feminine.Description));
        AcceptedObservationEntry other = Accepted(3, new ObservedAnimationChange("char:c", AnimationCategory.Idle, _sad.Description));

        _ = Assert.IsType<AnimationChangeObservationLifetimePolicy>(policy);
        Assert.True(policy.IsEventEligible(stance.Payload));
        Assert.False(policy.HasFiniteLifetime);
        Assert.True(policy.Evaluate(new ObservedAnimationChange("char:b", AnimationCategory.Idle, _sad.Description),
            [stance, gait, other], 4d).Suppress);
        ObservationLifetimePolicyDecision changed = policy.Evaluate(
            new ObservedAnimationChange("char:b", AnimationCategory.Idle, _slump.Description), [stance, gait, other], 4d);
        Assert.False(changed.Suppress);
        Assert.Equal([1L], changed.SupersededSequenceIDs);
    }

    /// <summary>
    /// MIND-AC-003 (first look): only the displayed category is described at focus. A standing subject shows its stance
    /// (native idle or a bridge into an idle), a moving subject its gait; nothing is described when neither is displayed.
    /// </summary>
    [Fact]
    public void FocusCheck_DescribesOnlyTheDisplayedCategory()
    {
        Assert.Same(_slump, AnimationChangePerception.DisplayedEntry(new DisplayedControl(AnimationCategory.Idle, _slump, _stroll)));
        Assert.Same(_stroll, AnimationChangePerception.DisplayedEntry(new DisplayedControl(AnimationCategory.Locomotion, _slump, _stroll)));
        Assert.Null(AnimationChangePerception.DisplayedEntry(new DisplayedControl(null, _slump, _stroll)));
    }

    /// <summary>
    /// MIND-AC-003 (first look and re-focus, ledger semantics): a first-look delivery records the last observed entry, so
    /// a later event with the same entry is silent; a re-focus on an unchanged subject is silent; a re-focus after a
    /// change missed out of focus delivers the new entry.
    /// </summary>
    [Fact]
    public void Ledger_FocusChecksShareTheLastObservedEntry()
    {
        AnimationChangeLedger ledger = new();

        Assert.True(Deliver(ledger, "char:b", _sad)); // first look at a static NPC
        Assert.False(Deliver(ledger, "char:b", _sad)); // later event with the same entry
        Assert.False(Deliver(ledger, "char:b", _sad)); // re-focus, unchanged
        Assert.True(Deliver(ledger, "char:b", _slump)); // re-focus after a missed change
        Assert.Equal("slump", ledger.GetLastObserved("char:b", AnimationCategory.Idle));
        Assert.Null(ledger.GetLastObserved("char:b", AnimationCategory.Locomotion));
    }

    private static bool Deliver(AnimationChangeLedger ledger, string subject, AnimationEntry target)
    {
        if (!ledger.IsChange(subject, target.Category, target))
        {
            return false;
        }

        ledger.RecordDelivered(subject, target.Category, target);
        return true;
    }

    private static AcceptedObservationEntry Accepted(long sequence, ObservedAnimationChange change)
        => new(sequence, sequence, change, new ObservationSchedulingMetadata(0f, false), IsRetained: true);

    private static AnimationEntry Entry(string id, AnimationCategory category, string[] tags, string description)
        => new(id, category, tags, description, "pack", $"{id}.tres", $"res://{id}.tres");

    private sealed class FakeVariantControl(string[] idles, string[] gaits) : IAnimationVariantControl
    {
        public AnimationCategory? DisplayedCategory => null;

        public string? IdlePreference
        {
            get; set;
        }

        public string? LocomotionPreference
        {
            get; set;
        }

        public AnimationEntry? CurrentIdle => null;

        public AnimationEntry? CurrentLocomotion => null;

        // A preference request never waits for, or listens to, a transition.
        public event Action<AnimationTransition>? TransitionStarted
        {
            add => throw new InvalidOperationException("A preference request must not subscribe to transitions.");
            remove => throw new InvalidOperationException("A preference request must not subscribe to transitions.");
        }

        public bool SetIdlePreference(string? id)
        {
            if (id is not null && !idles.Contains(id))
            {
                return false;
            }

            IdlePreference = id;
            return true;
        }

        public bool SetLocomotionPreference(string? id)
        {
            if (id is not null && !gaits.Contains(id))
            {
                return false;
            }

            LocomotionPreference = id;
            return true;
        }
    }

    private sealed class DisplayedControl(AnimationCategory? displayed, AnimationEntry idle, AnimationEntry gait)
        : IAnimationVariantControl
    {
        public AnimationCategory? DisplayedCategory => displayed;

        public AnimationEntry? CurrentIdle => idle;

        public AnimationEntry? CurrentLocomotion => gait;

        public event Action<AnimationTransition>? TransitionStarted
        {
            add
            {
            }

            remove
            {
            }
        }

        public bool SetIdlePreference(string? id) => false;

        public bool SetLocomotionPreference(string? id) => false;
    }

    private sealed class FakeCharacter : ICharacter
    {
        public string Id { get; set; } = "observer";

        public IReadOnlyList<IComponent> Components { get; } = [];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public Transform3D GlobalTransform => Transform3D.Identity;
    }
}
