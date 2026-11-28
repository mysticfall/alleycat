using AlleyCat.Animations;
using AlleyCat.Core;
using AlleyCat.Mind.Observation;
using AlleyCat.Scene;
using AlleyCat.Vision;
using Godot;

namespace AlleyCat.Mind.Perception;

/// <summary>
/// Observes the stance and gait of the NPC this observer is focused on (ANIM-003 Mind Integration, Observation Of
/// Changes; AI-006 TR13): the displayed entry when focus is acquired (first look and re-focus), then every change. The
/// faculty listens to the transition-started event of the active look subject only: it
/// subscribes when a subject becomes the effective visual focus and releases the subscription when focus moves, when
/// the subject leaves the tree, or when this node exits. A change made while unfocused is therefore never delivered and
/// never updates the last observed entry. The player and the observer itself are never subjects.
/// </summary>
[GlobalClass]
public sealed partial class AnimationChangePerception : ActiveLookPerception
{
    private readonly Lock _gate = new();
    private readonly AnimationChangeLedger _ledger = new();
    private Subscription? _subscription;

    /// <summary>
    /// Resolves a subject's variant control. Production subjects are characters whose navigation implements it; tests
    /// may substitute a resolver.
    /// </summary>
    internal Func<IVisualSubject, IAnimationVariantControl?> VariantControlResolver
    {
        get; set;
    } = DefaultVariantControlResolver;

    /// <summary>Whether a subject's transition event is currently subscribed; diagnostic and test use.</summary>
    internal bool IsSubscribed
    {
        get
        {
            lock (_gate)
            {
                return _subscription is not null;
            }
        }
    }

    /// <summary>The last observed entry id for a subject and category; diagnostic and test use.</summary>
    internal string? GetLastObserved(string subjectId, AnimationCategory category)
    {
        lock (_gate)
        {
            return _ledger.GetLastObserved(subjectId, category);
        }
    }

    /// <inheritdoc />
    protected override void OnActiveSubjectAttached(IVisualSubject subject)
    {
        base.OnActiveSubjectAttached(subject);
        PerceptionContext? context = LatestContext;
        if (context is null
            || IsPlayer(subject)
            || IsPlayer(context.Character)
            || ReferenceEquals(subject, context.Character)
            || string.Equals(subject.FullId, context.Character.FullId, StringComparison.Ordinal))
        {
            return;
        }

        IdentityValidator.Validate(subject, nameof(subject));
        if (VariantControlResolver(subject) is not IAnimationVariantControl control)
        {
            return;
        }

        Subscription subscription = new(this, subject, control);
        lock (_gate)
        {
            ReleaseUnderLock();
            _subscription = subscription;
            subscription.Connect();
        }

        RunFocusCheck(subscription, control);
    }

    // First look and re-focus (ANIM-003 Mind Integration, Observation Of Changes item 4): on acquiring focus, describe
    // only the category the subject is displaying, when its entry differs from the last observed one.
    private void RunFocusCheck(Subscription subscription, IAnimationVariantControl control)
    {
        if (DisplayedEntry(control) is { } entry)
        {
            TryDeliver(subscription, entry.Category, entry);
        }
    }

    /// <summary>
    /// The entry of the category the subject is displaying: its idle while idle or bridging into one, its gait while
    /// moving or bridging into locomotion; <see langword="null" /> when neither is displayed. The other category is not
    /// visible, so it is never described at focus.
    /// </summary>
    internal static AnimationEntry? DisplayedEntry(IAnimationVariantControl control)
    {
        ArgumentNullException.ThrowIfNull(control);
        return control.DisplayedCategory switch
        {
            AnimationCategory.Idle => control.CurrentIdle,
            AnimationCategory.Locomotion => control.CurrentLocomotion,
            _ => null,
        };
    }

    /// <inheritdoc />
    protected override void OnActiveSubjectDetached(IVisualSubject subject)
    {
        base.OnActiveSubjectDetached(subject);
        Release(expected: null);
    }

    private static IAnimationVariantControl? DefaultVariantControlResolver(IVisualSubject subject)
        => subject is Character.Character character ? character.Navigation as IAnimationVariantControl : null;

    private static bool IsPlayer(object participant)
        => participant is Node node && IsInstanceValid(node) && node.IsInGroup(ISceneContext.PlayerGroupName);

    private void Release(Subscription? expected)
    {
        lock (_gate)
        {
            if (expected is null || ReferenceEquals(_subscription, expected))
            {
                ReleaseUnderLock();
            }
        }
    }

    private void ReleaseUnderLock()
    {
        Subscription? previous = _subscription;
        _subscription = null;
        previous?.Disconnect();
    }

    private void OnTransitionStarted(Subscription subscription, AnimationTransition transition)
    {
        IVisualSubject subject = subscription.Subject;
        if (subject is Node node && (!IsInstanceValid(node) || !node.IsInsideTree()))
        {
            Release(subscription);
            return;
        }

        // Delivery requires the subject to still be the effective focus: the visual subject of the live assigned cue.
        VisualCue? cue = ActiveCue;
        if (!ReferenceEquals(ActiveSubject, subject)
            || cue is null
            || !IsLiveCue(cue)
            || !ReferenceEquals(FindNearestSubject(cue), subject))
        {
            return;
        }

        TryDeliver(subscription, transition.Category, transition.Entry);
    }

    // Delivers one observation when the entry differs from the last observed entry for the subject and category, and
    // records it as the last observed entry (items 2–4).
    private void TryDeliver(Subscription subscription, AnimationCategory category, AnimationEntry entry)
    {
        string subjectId = subscription.Subject.FullId;
        lock (_gate)
        {
            if (!ReferenceEquals(_subscription, subscription) || !_ledger.IsChange(subjectId, category, entry))
            {
                return;
            }

            _ledger.RecordDelivered(subjectId, category, entry);
        }

        Emit(new ObservedAnimationChange(subjectId, category, entry.Description));
    }

    private sealed class Subscription(
        AnimationChangePerception owner,
        IVisualSubject subject,
        IAnimationVariantControl control)
    {
        private Node? _exitSource;

        public IVisualSubject Subject { get; } = subject;

        public void Connect()
        {
            control.TransitionStarted += HandleTransition;
            if (Subject is Node node && IsInstanceValid(node))
            {
                node.TreeExiting += HandleSubjectExiting;
                _exitSource = node;
            }
        }

        public void Disconnect()
        {
            control.TransitionStarted -= HandleTransition;
            Node? source = _exitSource;
            _exitSource = null;
            if (source is not null && IsInstanceValid(source))
            {
                source.TreeExiting -= HandleSubjectExiting;
            }
        }

        private void HandleTransition(AnimationTransition transition) => owner.OnTransitionStarted(this, transition);

        private void HandleSubjectExiting() => owner.Release(this);
    }
}
