namespace AlleyCat.Contact;

/// <summary>
/// One contact's classification state machine, hosted by the classification engine per active contact
/// (INTR-005 TR5, TR6). The entry classification happens at contact Begin from approach kinematics —
/// Impact/Hit at or above the policy's approach threshold, otherwise the Touch gentle-begin default. Later
/// transitions confirm Press after a press window, revise Touch to Caress/Stroke after a tangential-motion
/// window — emitted as one revision transition — and settle any class into Rest/Lean after a settled window.
/// Transitions are emitted only when the class changes: steady contact emits nothing after its entry
/// transition, and contact End emits nothing because the raw layer owns lifecycle observability.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Intensity.</strong> The carried intensity is class-appropriate (INTR-005 TR8): Impact/Hit and Touch
/// report the approach normal speed at Begin in m/s; Press and Rest/Lean report the penetration depth at
/// confirmation in m; Caress/Stroke reports the tangential slip speed at confirmation in m/s.
/// </para>
/// <para>
/// <strong>Transition graph and precedence.</strong> On a single update the machine evaluates Rest/Lean first,
/// then Caress/Stroke, then Press. The settled and caress conditions cannot hold simultaneously because the
/// policy validates the settled threshold strictly below the caress slip threshold. Rest/Lean is terminal for
/// the classification: a classification stays Rest until the raw contact ends — the raw lifecycle itself persists
/// while the pair stays detected, so resumed motion keeps the same contact id and classification. Caress/Stroke is
/// likewise absorbing except for settling into Rest/Lean.
/// </para>
/// <para>
/// <strong>Purity.</strong> The machine is pure C# with no Godot types so the classification semantics stay
/// unit-testable below the engine boundary; the monitor maps Godot physics observations into raw samples.
/// </para>
/// </remarks>
public sealed class ContactClassificationStateMachine
{
    private readonly ContactClassificationPolicy _policy;
    private readonly ContactParty _source;

    private readonly ContactParty _receiver;

    private float _beginApproachSpeed;
    private TimeSpan _lastDuration;

    private float _caressSeconds;

    private float _pressSeconds;

    private float _settledSeconds;

    private bool _begun;

    private bool _ended;

    /// <summary>Creates a machine for one contact pair, bound to the classification policy.</summary>
    /// <param name="policy">Validated classification tunables.</param>
    /// <param name="contact">Stable identity of the contact pair.</param>
    /// <param name="source">The contacting counterpart party.</param>
    /// <param name="receiver">The receiving body-part or surface party.</param>
    public ContactClassificationStateMachine(
        ContactClassificationPolicy policy,
        ContactId contact,
        ContactParty source,
        ContactParty receiver)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(receiver);
        _policy = policy;
        Contact = contact;
        _source = source;
        _receiver = receiver;
        _beginApproachSpeed = 0f;
    }

    /// <summary>Stable identity of the classified contact pair.</summary>
    public ContactId Contact
    {
        get;
    }

    /// <summary>The currently held class; meaningful once <see cref="Begin" /> has run.</summary>
    public ContactClass CurrentClass
    {
        get; private set;
    }

    /// <summary>
    /// Classifies the contact's entry from approach kinematics and returns the entry transition: Impact/Hit
    /// when the approach normal speed reaches the policy's impact threshold, otherwise Touch (INTR-005 TR6).
    /// </summary>
    /// <param name="sample">The contact's Begin measurement set.</param>
    /// <returns>The entry transition, whose previous class is <see langword="null" />.</returns>
    public ContactClassificationTransition? Begin(in RawContactSample sample)
    {
        if (_begun)
        {
            throw new InvalidOperationException("A contact state machine begins exactly once.");
        }

        _begun = true;
        _beginApproachSpeed = ContactMath.NormalApproachSpeed(sample.RelativeVelocity, sample.ContactNormal);
        CurrentClass = _beginApproachSpeed >= _policy.ImpactApproachSpeedThreshold
            ? ContactClass.Impact
            : ContactClass.Touch;
        _lastDuration = sample.Duration;
        return CreateTransition(null, CurrentClass, in sample);
    }

    /// <summary>
    /// Advances the machine by one Update, accumulating the confirmation windows from the sample's duration
    /// and returning the transition when the class changes, or <see langword="null" /> when the class holds.
    /// </summary>
    /// <param name="sample">The contact's current measurement set.</param>
    /// <returns>The emitted transition, or <see langword="null" /> when no class changed.</returns>
    public ContactClassificationTransition? Update(in RawContactSample sample)
    {
        ThrowIfNotLive("update");

        float deltaSeconds = MathF.Max(0f, (float)(sample.Duration - _lastDuration).TotalSeconds);
        if (sample.Duration > _lastDuration)
        {
            _lastDuration = sample.Duration;
        }

        bool settled = sample.RelativeVelocity.Length() <= _policy.RestSettledSpeedThreshold;
        bool pressing = sample.PenetrationDepth >= _policy.PressDepthThreshold
            || sample.PressRate >= _policy.PressRateThreshold;
        bool caressing = sample.TangentialSlipSpeed >= _policy.CaressSlipSpeedThreshold;

        _settledSeconds = settled ? _settledSeconds + deltaSeconds : 0f;
        _pressSeconds = pressing ? _pressSeconds + deltaSeconds : 0f;
        _caressSeconds = caressing ? _caressSeconds + deltaSeconds : 0f;

        if (CurrentClass != ContactClass.Rest && _settledSeconds >= _policy.RestDelaySeconds)
        {
            ContactClass previous = CurrentClass;
            CurrentClass = ContactClass.Rest;
            return CreateTransition(previous, CurrentClass, in sample);
        }

        if (CurrentClass == ContactClass.Touch && _caressSeconds >= _policy.CaressConfirmationSeconds)
        {
            ContactClass previous = CurrentClass;
            CurrentClass = ContactClass.Caress;
            return CreateTransition(previous, CurrentClass, in sample);
        }

        if (CurrentClass is ContactClass.Touch or ContactClass.Impact
            && _pressSeconds >= _policy.PressConfirmationSeconds)
        {
            ContactClass previous = CurrentClass;
            CurrentClass = ContactClass.Press;
            return CreateTransition(previous, CurrentClass, in sample);
        }

        return null;
    }

    /// <summary>
    /// Ends the contact. The classification emits nothing on End — classified events mark classification
    /// transitions only, and lifecycle observability belongs to the raw layer (INTR-005 TR6).
    /// </summary>
    public void End()
    {
        ThrowIfNotLive("end");
        _ended = true;
    }

    private ContactClassificationTransition CreateTransition(
        ContactClass? previousClass,
        ContactClass @class,
        in RawContactSample sample) =>
        new(Contact, _source, _receiver, previousClass, @class, IntensityFor(@class, in sample),
            sample.ContactNormal, sample.Duration);

    private float IntensityFor(ContactClass @class, in RawContactSample sample) => @class switch
    {
        ContactClass.Impact => _beginApproachSpeed,
        ContactClass.Touch => _beginApproachSpeed,
        ContactClass.Press => sample.PenetrationDepth,
        ContactClass.Caress => sample.TangentialSlipSpeed,
        ContactClass.Rest => sample.PenetrationDepth,
        _ => throw new ArgumentOutOfRangeException(nameof(@class), @class, "Unknown contact class."),
    };

    private void ThrowIfNotLive(string operation)
    {
        if (!_begun)
        {
            throw new InvalidOperationException(
                $"A contact state machine must begin before its first {operation}; the raw lifecycle owns ordering.");
        }

        if (_ended)
        {
            throw new InvalidOperationException($"A contact state machine cannot {operation} after its End.");
        }
    }
}
