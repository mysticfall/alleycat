namespace AlleyCat.Speech.Voice;

/// <summary>
/// Optional voice admission capability (SPCH-005 TR-37), implemented by voices whose submissions can host the
/// caller-owned admission transaction — today the production <see cref="AIVoice" />.
/// </summary>
/// <remarks>
/// <para>
/// Consumers discover this as an optional capability of the resolved <see cref="IVoice" /> projection — never
/// through a cast to a concrete voice class (SPCH-005 TR-37). A voice without this capability keeps the ordinary
/// cancellable submission path (SPCH-005 TR-38): its speech is never refused at a suppression cue and is not
/// arbitration-protected.
/// </para>
/// <para>
/// The capability keeps the delegate-based <see cref="SpeechAdmissionTransaction" /> dependency inversion, so
/// Speech defines no Mind dependency: queue admission and the agent runner's protected-admission state commit as
/// one transaction under the normative lock order — the capability implementation's submission lock first, then
/// the agent-runner state lock.
/// </para>
/// </remarks>
internal interface IAdmissionCapableVoice
{
    /// <summary>
    /// Submits speech as an explicitly cancellable submission whose queue admission is arbitrated against
    /// attended start/resume suppression holds (SPCH-005 TR-37; AI-002 TR-14).
    /// </summary>
    /// <param name="speech">Speech text to submit.</param>
    /// <param name="cancellationToken">Caller-supplied cancellation observed through generation, conversion, and
    /// preparation until playback hand-off.</param>
    /// <param name="admission">Runner-owned admission transaction committing queue admission and protected state
    /// atomically under the normative lock order.</param>
    /// <returns>True when the submission was admitted and playback hand-off completed; false when a matching
    /// attended cue linearised first, in which case nothing was admitted — no TTS request, queue item, hearing
    /// event, or self-observation — and the caller reports its not-delivered outcome.</returns>
    ValueTask<bool> SpeakCancellableAdmittedAsync(
        string speech,
        CancellationToken cancellationToken,
        SpeechAdmissionTransaction admission);
}
