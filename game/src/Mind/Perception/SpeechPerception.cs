using AlleyCat.Character;
using AlleyCat.Mind.Observation;
using AlleyCat.Scene;
using AlleyCat.Speech;
using AlleyCat.Speech.Voice;
using AlleyCat.XR;
using Godot;

namespace AlleyCat.Mind.Perception;

/// <summary>
/// Interprets completed speech: resolves the speaking character, infers the observer-relative addressee set from
/// the speaker's facing, and emits one actor- and addressee-aware observation (AI-006 TR-4/11/12).
/// </summary>
[GlobalClass]
public sealed partial class SpeechPerception : Perception<SpeechPercept>
{
    /// <summary>
    /// Gets or sets the ground-plane half-angle in degrees of the speaker's facing cone used for addressee
    /// inference: every other current-scene character within this cone is an addressee, and an empty cone result
    /// leaves the addressee undecided. The value must be finite and within (0, 180]; it is validated before every
    /// use (AI-006 TR-11/12).
    /// </summary>
    [Export(PropertyHint.Range, "0.5,180,0.5")]
    public float FacingConeAngleDegrees
    {
        get;
        set;
    } = SpeechAddresseeInference.DefaultFacingConeAngleDegrees;

    /// <summary>
    /// Test seam for the player head/camera orientation source: when set, it replaces the production XR camera
    /// resolution for player speakers (AI-006 TR-12).
    /// </summary>
    internal Func<IXRCamera?>? PlayerCameraResolverForTest
    {
        get;
        set;
    }

    /// <inheritdoc/>
    public override ValueTask PerceiveAsync(
        SpeechPercept percept,
        PerceptionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(percept);
        ArgumentNullException.ThrowIfNull(context);
        IVoice observerVoice = context.Character.RequireVoice();
        if (string.Equals(percept.SourceVoiceID, observerVoice.Id, StringComparison.Ordinal))
        {
            return ValueTask.CompletedTask;
        }

        ICharacter? recognised = ResolveRecognisedCharacter(percept.SourceVoiceID, context);
        IReadOnlyList<string> addressees = recognised is null
            ? []
            : InferAddresseeSet(recognised, context);
        Emit(
            new ObservedSpeech(recognised?.FullId, percept.Content, addressees),
            new SpeechObservationTransport(
                percept.SourceVoiceID,
                percept.SpeechGroupID,
                percept.SegmentIndex,
                percept.Continued));
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Infers the observer-relative addressee set for one recognised speaker. Unrecognised speech is always
    /// undecided because facing cannot be resolved without a character node (AI-006 TR-4).
    /// </summary>
    private IReadOnlyList<string> InferAddresseeSet(ICharacter speaker, PerceptionContext context)
    {
        SpeechAddresseeInference.ValidateConeAngle(FacingConeAngleDegrees);
        IXRCamera? playerCamera = IsPlayerSpeaker(speaker) ? ResolvePlayerCamera() : null;
        return SpeechAddresseeInference.InferAddressees(
            speaker,
            context.Scene.Characters,
            FacingConeAngleDegrees,
            playerCamera);
    }

    /// <summary>
    /// Determines whether the speaker is the scene's player character, selecting the player head/camera
    /// orientation source before the gaze and body-forward fallbacks (AI-006 TR-12).
    /// </summary>
    private static bool IsPlayerSpeaker(ICharacter speaker)
        => speaker is Node node && node.IsInGroup(ISceneContext.PlayerGroupName);

    /// <summary>Resolves the player's XR camera, or <see langword="null"/> when no live camera is available.</summary>
    private IXRCamera? ResolvePlayerCamera()
        => PlayerCameraResolverForTest is { } resolver
            ? resolver()
            : Game.Instance?.GetService<XRManager>()?.Runtime is { } runtime
                && runtime.Camera is { } camera
                ? camera
                : null;

    private static ICharacter? ResolveRecognisedCharacter(string sourceVoiceID, PerceptionContext context)
    {
        if (string.IsNullOrWhiteSpace(sourceVoiceID))
        {
            return null;
        }

        ICharacter? recognised = null;
        foreach (ICharacter candidate in context.Scene.Characters)
        {
            if (!candidate.TryGetVoice(out IVoice? voice)
                || voice is null
                || string.IsNullOrWhiteSpace(voice.Id)
                || !string.Equals(voice.Id, sourceVoiceID, StringComparison.Ordinal))
            {
                continue;
            }

            if (recognised is not null)
            {
                throw new InvalidOperationException($"Voice ID '{sourceVoiceID}' ambiguously matches current-scene characters '{recognised.FullId}' and '{candidate.FullId}'.");
            }

            recognised = candidate;
        }

        return recognised;
    }
}
