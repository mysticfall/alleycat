using AlleyCat.Rigging;
using Godot;

#pragma warning disable CS1591

namespace AlleyCat.IK;

/// <summary>Closed ANIM-002 contact-evidence refusal reasons exposed for playtest diagnostics.</summary>
public enum AuthoredContactRefusalReason
{
    None,
    MissingOrEmpty,
    UnknownOrMalformed,
    Stale,
    InvalidOrNonFinite,
    ContradictoryOrIncompatible,
    Inferred,
    InapplicableAsApplicable,
    WrongFoot,
    RoleOrIdentityMismatched,
}

/// <summary>Value-only side-specific ANIM-002 contact provider for the NPC Phase 1 slice.</summary>
[GlobalClass]
public partial class AuthoredPerFootContactProvider : IKTargetIntentProvider
{
    private AuthoredPerFootContactTrackV1? _track;
    private bool _loadAttempted;
    private AuthoredContactRefusalReason _loadRefusalReason;

    [Export]
    public LimbSide Side
    {
        get; set;
    }
    [Export]
    public Node3D? AnimationSynchronizedFootTarget
    {
        get; set;
    }
    [Export(PropertyHint.File, "*.json")]
    public string ContactTrackPath { get; set; } = "res://assets/characters/reference/female/animations/runtime/locomotion/sad/authored_per_foot_contacts_v1.json";
    [Export] public string Role { get; set; } = string.Empty;
    [Export] public string Variant { get; set; } = string.Empty;
    [Export] public string ExpectedIdentity { get; set; } = string.Empty;
    [Export] public string ExpectedGeneration { get; set; } = string.Empty;

    public AuthoredContactRefusalReason LastRefusalReason
    {
        get; private set;
    }
    public DerivedPerPhysicsContactSampleV1 LastDerivedSample
    {
        get; private set;
    }
    public bool LastDecisionApplicable
    {
        get; private set;
    }

    /// <summary>Contact evidence preserves the synchronised target in this deliberately policy-free first slice.</summary>
    public override bool ShouldApplyTargetTransform => false;

    public override IKTargetIntent GetTargetIntent()
        => TryGetTargetIntent(out IKTargetIntentResolution resolution)
            ? resolution.Intent
            : new IKTargetIntent(Transform3D.Identity, 0.0f);

    public override bool TryGetTargetIntent(out IKTargetIntentResolution resolution)
    {
        ulong tick = Engine.GetPhysicsFrames();
        AuthoredPerFootContactSampleV1? sample = ResolveSample();
        AuthoredContactRefusalReason refusal = _loadRefusalReason != AuthoredContactRefusalReason.None
            ? _loadRefusalReason
            : Validate(sample, Role, Side, Variant, ExpectedIdentity, ExpectedGeneration);
        LastRefusalReason = refusal;
        LastDecisionApplicable = refusal == AuthoredContactRefusalReason.None;
        LastDerivedSample = new DerivedPerPhysicsContactSampleV1(
            sample,
            tick,
            0.0d,
            sample?.Lifecycle ?? string.Empty,
            refusal == AuthoredContactRefusalReason.None ? "fresh" : "refused",
            refusal.ToString());
        Node3D? target = AnimationSynchronizedFootTarget;
        if (refusal != AuthoredContactRefusalReason.None || target is null || !IsInstanceValid(target))
        {
            if (refusal == AuthoredContactRefusalReason.None)
            {
                LastRefusalReason = AuthoredContactRefusalReason.MissingOrEmpty;
                LastDecisionApplicable = false;
            }

            resolution = default;
            return false;
        }

        resolution = new IKTargetIntentResolution(new IKTargetIntent(target.GlobalTransform, 1.0f), ShouldApplyTargetTransform);
        return true;
    }

    public static AuthoredContactRefusalReason Validate(
        AuthoredPerFootContactSampleV1? sample,
        string role,
        LimbSide side,
        string variant,
        string identity,
        string generation)
    {
        return sample is null || HasMissingRequiredField(sample)
            ? AuthoredContactRefusalReason.MissingOrEmpty
            : sample.SchemaVersion != 1 || !IsKnownFoot(sample.Foot) || !IsKnownFoot(side)
            || sample.Lifecycle is not ("acquire" or "support" or "release")
            || !HasValidTimingSemantics(sample.Lifecycle, sample.Timing)
            ? AuthoredContactRefusalReason.UnknownOrMalformed
            : !HasFiniteTiming(sample.Timing) || !float.IsFinite(sample.Confidence) || sample.Confidence <= 0.0f
            ? AuthoredContactRefusalReason.InvalidOrNonFinite
            : sample.Validity == "stale"
            ? AuthoredContactRefusalReason.Stale
            : sample.Validity == "inferred" || sample.Reference.Contains("inferred", StringComparison.OrdinalIgnoreCase)
            ? AuthoredContactRefusalReason.Inferred
            : sample.Validity == "inapplicable"
            ? AuthoredContactRefusalReason.InapplicableAsApplicable
            : sample.Validity == "contradictory" || sample.Reference.Contains("contradictory", StringComparison.OrdinalIgnoreCase)
            ? AuthoredContactRefusalReason.ContradictoryOrIncompatible
            : sample.Validity == "invalid"
            ? AuthoredContactRefusalReason.InvalidOrNonFinite
            : sample.Validity != "valid"
            ? AuthoredContactRefusalReason.UnknownOrMalformed
            : sample.Foot != side
            ? AuthoredContactRefusalReason.WrongFoot
            : sample.Role != role || sample.Variant != variant || sample.Identity != identity || sample.Generation != generation
            ? AuthoredContactRefusalReason.RoleOrIdentityMismatched
            : AuthoredContactRefusalReason.None;
    }

    private AuthoredPerFootContactSampleV1? ResolveSample()
    {
        if (!_loadAttempted)
        {
            _loadAttempted = true;
            string json = Godot.FileAccess.GetFileAsString(ContactTrackPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                _loadRefusalReason = AuthoredContactRefusalReason.MissingOrEmpty;
                return null;
            }

            try
            {
                _track = AuthoredPerFootContactTrackV1.Load(json);
            }
            catch (Exception)
            {
                _track = null;
                _loadRefusalReason = AuthoredContactRefusalReason.UnknownOrMalformed;
            }
        }

        return _track is not null && _track.TryGet(Role, Side, out AuthoredPerFootContactSampleV1? sample) ? sample : null;
    }

    private static bool HasMissingRequiredField(AuthoredPerFootContactSampleV1 sample)
        => string.IsNullOrWhiteSpace(sample.Role)
            || string.IsNullOrWhiteSpace(sample.Lifecycle)
            || sample.Timing.Count == 0
            || string.IsNullOrWhiteSpace(sample.Validity)
            || string.IsNullOrWhiteSpace(sample.Provenance)
            || string.IsNullOrWhiteSpace(sample.Clip)
            || string.IsNullOrWhiteSpace(sample.Variant)
            || string.IsNullOrWhiteSpace(sample.Reference)
            || string.IsNullOrWhiteSpace(sample.Identity)
            || string.IsNullOrWhiteSpace(sample.Generation);

    private static bool IsKnownFoot(LimbSide foot) => foot is LimbSide.Left or LimbSide.Right;

    private static bool HasFiniteTiming(IReadOnlyList<double> timing)
    {
        foreach (double time in timing)
        {
            if (!double.IsFinite(time))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasValidTimingSemantics(string lifecycle, IReadOnlyList<double> timing)
    {
        int expectedCount = lifecycle == "support" ? 2 : 1;
        if (timing.Count != expectedCount)
        {
            return false;
        }
        foreach (double time in timing)
        {
            if (double.IsFinite(time) && (time < 0.0d || time > 1.0d))
            {
                return false;
            }
        }
        return lifecycle != "support" || timing[0] != timing[1];
    }
}
