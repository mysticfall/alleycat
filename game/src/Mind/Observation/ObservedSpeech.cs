using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Mind.Attention;

namespace AlleyCat.Mind.Observation;

/// <summary>
/// Speech observed from the owning character, a recognised other character, or an unknown speaker, together with
/// the observer-relative set of characters the observing character inferred the speaker addressed (AI-001
/// TR-13..15).
/// </summary>
public sealed record ObservedSpeech : ObservedAction
{
    /// <summary>Self speech keeps its historic importance: it never pressures scheduling (AI-001 TR-14).</summary>
    private const float SelfImportance = 0f;

    /// <summary>
    /// Addressed-to-me speech is highly important: it must stay at or above Mind's default observation-importance
    /// threshold while also requiring a fresh turn.
    /// </summary>
    private const float AddressedToMeImportance = 2f;

    /// <summary>
    /// Undecided speech is notable: this value crosses Mind's default observation-importance threshold of 1 so it
    /// wakes a waiting NPC without interrupting active reasoning.
    /// </summary>
    private const float UndecidedImportance = 1f;

    /// <summary>
    /// Addressed-to-another speech is low-importance: this value stays below Mind's default observation-importance
    /// threshold of 1 so bystanders catch up through accumulation or their next request.
    /// </summary>
    private const float AddressedToAnotherImportance = 0.25f;

    /// <summary>Undecided speech keeps the historic flat attention contribution on the speaker (AI-001 TR-14).</summary>
    private const float UndecidedSpeakerContribution = 0.5f;

    /// <summary>Addressed-to-me speech raises the speaker above the undecided baseline contribution.</summary>
    private const float AddressedToMeSpeakerContribution = 0.75f;

    /// <summary>Overheard speech keeps a base attention contribution on the speaker.</summary>
    private const float OverheardSpeakerContribution = 0.5f;

    /// <summary>Overheard speech applies one smaller attention contribution to each inferred addressee.</summary>
    private const float OverheardAddresseeContribution = 0.25f;

    /// <summary>The unified stable semantic key shared by every observed-speech perspective (AI-001 TR-13).</summary>
    public const string TypeKeyValue = "speech.observed";

    /// <summary>Validates and creates one observed-speech observation.</summary>
    /// <param name="actorId">Exact recognised actor FullId, or <see langword="null"/> when the speaker is unknown.</param>
    /// <param name="content">Observed speech content.</param>
    /// <param name="addressees">
    /// Canonical FullIds of every character the observer inferred the speaker addressed, or <see langword="null"/>
    /// or empty when the addressee is undecided. Ignored for an unknown speaker, which is always undecided because
    /// facing cannot be resolved without a character node (AI-001 TR-15, AI-006 TR-4).
    /// </param>
    public ObservedSpeech(string? actorId, string content, IReadOnlyList<string>? addressees = null)
        : base(actorId)
    {
        Content = content;
        Addressees = actorId is null ? [] : ValidateAndCopyAddressees(addressees);
    }

    /// <summary>Gets the observed speech content.</summary>
    public string Content
    {
        get;
    }

    /// <summary>
    /// Gets the immutable, ordered set of canonical character FullIds the observer inferred the speaker addressed.
    /// Empty means undecided (AI-001 TR-13).
    /// </summary>
    public IReadOnlyList<string> Addressees
    {
        get;
    }

    /// <inheritdoc />
    public override string TypeKey => TypeKeyValue;

    /// <summary>
    /// Derives this observation's observer-relative addressee state: self speech dominates, an unknown speaker is
    /// always undecided, and a recognised speaker's set decides between addressed-to-me, addressed-to-another, and
    /// undecided (AI-001 TR-14).
    /// </summary>
    public SpeechAddresseeState GetAddresseeState(ObservationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        string observerId = context.Character.FullId;
        if (string.Equals(ActorId, observerId, StringComparison.Ordinal))
        {
            return SpeechAddresseeState.Self;
        }

        if (ActorId is null || Addressees.Count == 0)
        {
            return SpeechAddresseeState.Undecided;
        }

        foreach (string addressee in Addressees)
        {
            if (string.Equals(addressee, observerId, StringComparison.Ordinal))
            {
                return SpeechAddresseeState.AddressedToMe;
            }
        }

        return SpeechAddresseeState.AddressedToAnother;
    }

    /// <inheritdoc />
    protected override string RenderBody(ICharacter character)
    {
        ArgumentNullException.ThrowIfNull(character);
        return GetAddresseeState(new ObservationContext(character)) switch
        {
            SpeechAddresseeState.Self => $"I said: {Content}",
            SpeechAddresseeState.Undecided => ActorId is { } actorId
                ? $"Heard `{actorId}` say: {Content}"
                : $"Heard an unknown speaker say: {Content}",
            SpeechAddresseeState.AddressedToMe => $"Heard `{ActorId}` say to me: {Content}",
            // Canonical IDs read as handles, not prose, in every model-visible surface (AI-004 requirement 51).
            SpeechAddresseeState.AddressedToAnother => $"Heard `{ActorId}` say to {RenderAddresseeList()}: {Content}",
            _ => throw new InvalidOperationException($"Unhandled addressee state for '{TypeKeyValue}'."),
        };
    }

    /// <summary>
    /// Renders the addressee set as backticked canonical handles joined by commas and a final
    /// <c>and</c> (AI-001 TR-15, AI-004 requirement 51).
    /// </summary>
    private string RenderAddresseeList()
        => Addressees.Count == 1
            ? $"`{Addressees[0]}`"
            : $"`{string.Join("`, `", Addressees.Take(Addressees.Count - 1))}` and `{Addressees[^1]}`";

    /// <inheritdoc />
    public override float CalculateImportance(ObservationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return GetAddresseeState(context) switch
        {
            SpeechAddresseeState.Self => SelfImportance,
            SpeechAddresseeState.AddressedToMe => AddressedToMeImportance,
            SpeechAddresseeState.Undecided => UndecidedImportance,
            SpeechAddresseeState.AddressedToAnother => AddressedToAnotherImportance,
            _ => throw new InvalidOperationException($"Unhandled addressee state for '{TypeKeyValue}'."),
        };
    }

    /// <summary>
    /// Only addressed-to-me speech requires a fresh turn: it bypasses thresholds and invalidates stale reasoning.
    /// Self, overheard, and undecided speech never claim the observer's turn (AI-001 TR-14).
    /// </summary>
    public override bool RequiresFreshTurn(ObservationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return GetAddresseeState(context) == SpeechAddresseeState.AddressedToMe;
    }

    /// <inheritdoc />
    public override IReadOnlyList<AttentionEffect> GetAttentionEffects(ObservationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return GetAddresseeState(context) switch
        {
            SpeechAddresseeState.Self => [],
            // An unknown speaker contributes no attention effect because there is no canonical identity to key;
            // a recognised undecided speaker keeps the flat baseline contribution on the speaker only.
            SpeechAddresseeState.Undecided => ActorId is { } undecidedActorId
                ? [new AttentionEffect(undecidedActorId, UndecidedSpeakerContribution)]
                : [],
            SpeechAddresseeState.AddressedToMe => [new AttentionEffect(ActorId!, AddressedToMeSpeakerContribution)],
            SpeechAddresseeState.AddressedToAnother => BuildOverheardEffects(),
            _ => throw new InvalidOperationException($"Unhandled addressee state for '{TypeKeyValue}'."),
        };
    }

    /// <summary>
    /// Builds overheard-speech attention: one base contribution on the speaker followed by one smaller
    /// contribution on each inferred addressee, in addressee-set order (AI-001 TR-14).
    /// </summary>
    private IReadOnlyList<AttentionEffect> BuildOverheardEffects()
    {
        var effects = new AttentionEffect[1 + Addressees.Count];
        effects[0] = new AttentionEffect(ActorId!, OverheardSpeakerContribution);
        for (int index = 0; index < Addressees.Count; index++)
        {
            effects[index + 1] = new AttentionEffect(Addressees[index], OverheardAddresseeContribution);
        }

        return effects;
    }

    /// <summary>
    /// Validates every addressee as a canonical character FullId without duplicates, then defensively copies the
    /// set so later caller-side mutation cannot rewrite a committed observation (AI-001 TR-13).
    /// </summary>
    private static IReadOnlyList<string> ValidateAndCopyAddressees(IReadOnlyList<string>? addressees)
    {
        if (addressees is not { Count: > 0 })
        {
            return [];
        }

        string[] copy = new string[addressees.Count];
        for (int index = 0; index < addressees.Count; index++)
        {
            string addressee = addressees[index]
                ?? throw new ArgumentException($"Addressee at index {index} cannot be null.", nameof(addressees));
            IdentityValidator.ValidateFullId(addressee, nameof(addressees));
            for (int earlier = 0; earlier < index; earlier++)
            {
                if (string.Equals(copy[earlier], addressee, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"Addressee '{addressee}' appears more than once; the addressee value is a set.",
                        nameof(addressees));
                }
            }

            copy[index] = addressee;
        }

        return Array.AsReadOnly(copy);
    }
}
