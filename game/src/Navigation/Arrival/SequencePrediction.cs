using System.Collections.Immutable;
using AlleyCat.Control.Locomotion;
using Godot;

namespace AlleyCat.Navigation.Arrival;

/// <summary>
/// Planar actor pose: world position on the ground plane as <c>(X, Z)</c> and yaw about world +Y in radians
/// (positive turns left, Godot's <see cref="Basis"/> convention; the actor faces its local -Z).
/// </summary>
/// <param name="Position">World <c>(X, Z)</c> position.</param>
/// <param name="Yaw">Yaw about world +Y, in radians.</param>
public readonly record struct SequencePose(Vector2 Position, float Yaw)
{
    /// <summary>
    /// Maps an actor-semantic <c>[right, forward]</c> vector to a world <c>(X, Z)</c> vector for an actor facing
    /// <paramref name="yaw"/>.
    /// </summary>
    public static Vector2 SemanticToWorld(Vector2 semantic, double yaw)
    {
        (double sin, double cos) = Math.SinCos(yaw);
        return new Vector2(
            (float)((semantic.X * cos) - (semantic.Y * sin)),
            (float)((-semantic.X * sin) - (semantic.Y * cos)));
    }
}

/// <summary>A running crossfade at the start of a prediction, as the state machine holds it after the last tick.</summary>
/// <param name="FromRole">Role of the clip fading out.</param>
/// <param name="FromPositionSeconds">Playback position of the fading-out clip after the last tick.</param>
/// <param name="FadePositionSeconds">The state machine's crossfade position (single precision, as the engine keeps it).</param>
/// <param name="FadeLengthSeconds">The crossfade's length (single precision, as the engine keeps it).</param>
public readonly record struct SequenceCrossfadeProgress(
    string FromRole,
    double FromPositionSeconds,
    float FadePositionSeconds,
    float FadeLengthSeconds);

/// <summary>
/// Where a predicted sequence starts: either a <c>Start</c> teleport into its first segment, or a clip already playing
/// (optionally while it still fades in from another clip) that leaves at a given playback position.
/// </summary>
public sealed record SequenceStart
{
    private SequenceStart(SequencePose pose)
    {
        Pose = pose;
    }

    /// <summary>Actor pose before the first predicted tick.</summary>
    public SequencePose Pose
    {
        get;
    }

    /// <summary>
    /// Role of the playing clip, or <see langword="null"/> for a teleport start, whose first segment then enters with a
    /// zero crossfade.
    /// </summary>
    public string? CurrentRole
    {
        get; private init;
    }

    /// <summary>Playback position of the playing clip after the last tick.</summary>
    public double CurrentPositionSeconds
    {
        get; private init;
    }

    /// <summary>
    /// Playback position at which the playing clip leaves: the first segment's crossfade starts when playback reaches
    /// it (see <see cref="SequenceSegment.ExitSeconds"/>); without segments, the prediction ends there.
    /// </summary>
    public double ExitSeconds
    {
        get; private init;
    }

    /// <summary>Additional whole cycles a looping clip plays before it leaves (see <see cref="SequenceSegment.ExitCycles"/>).</summary>
    public int ExitCycles
    {
        get; private init;
    }

    /// <summary>A crossfade into the playing clip that is still running, if any.</summary>
    public SequenceCrossfadeProgress? Fading
    {
        get; private init;
    }

    /// <summary>A teleport start: the first segment is entered through <c>Start</c> with a zero crossfade.</summary>
    public static SequenceStart Teleport(SequencePose pose) => new(pose);

    /// <summary>A start while a clip is playing, optionally still fading in.</summary>
    /// <param name="pose">Actor pose before the first predicted tick.</param>
    /// <param name="role">Role of the playing clip.</param>
    /// <param name="positionSeconds">Its playback position after the last tick.</param>
    /// <param name="exitSeconds">Playback position at which it leaves.</param>
    /// <param name="exitCycles">Additional whole cycles before it leaves (looping clips only).</param>
    /// <param name="fading">A crossfade into it that is still running, if any.</param>
    public static SequenceStart Playing(
        SequencePose pose,
        string role,
        double positionSeconds,
        double exitSeconds,
        int exitCycles = 0,
        SequenceCrossfadeProgress? fading = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        return new SequenceStart(pose)
        {
            CurrentRole = role,
            CurrentPositionSeconds = positionSeconds,
            ExitSeconds = exitSeconds,
            ExitCycles = exitCycles,
            Fading = fading,
        };
    }
}

/// <summary>
/// One clip segment of a predicted sequence: the clip is entered at <see cref="EntrySeconds"/> through a crossfade of
/// <see cref="CrossfadeSeconds"/> and leaves when its playback reaches <see cref="ExitSeconds"/>.
/// </summary>
/// <param name="Role">Clip role.</param>
/// <param name="EntrySeconds">
/// Entry position <c>b</c>: the clip's playback position after its entry tick (the runtime seeks it there).
/// </param>
/// <param name="CrossfadeSeconds">
/// Crossfade into this segment: the joined pair's seam-table crossfade duration
/// (<see cref="SeamPair.CrossfadeSeconds"/>), or exactly zero for the first segment of a teleport start.
/// </param>
/// <param name="ExitSeconds">
/// Seam outgoing time <c>a</c> of the join into the next segment: the next transition is requested after the tick that
/// leaves this clip at <c>a</c>, and on the next segment's entry tick this clip advances to <c>a</c> plus one tick. For
/// the last segment, the prediction ends on the tick after which playback is at this position.
/// </param>
/// <param name="ExitCycles">
/// Additional whole cycles a looping clip plays before it leaves; the exit is then
/// <c>posmod(a − b, length) + ExitCycles · length</c> of playback after entry. Zero for clips played once.
/// </param>
public readonly record struct SequenceSegment(
    string Role,
    double EntrySeconds,
    double CrossfadeSeconds,
    double ExitSeconds,
    int ExitCycles = 0);

/// <summary>Why a sequence cannot be predicted as issued.</summary>
public enum SequenceRejectionReason
{
    /// <summary>The physics delta is not finite and positive, or a teleport start has no segment.</summary>
    InvalidRequest,

    /// <summary>A role is not a clip of the predictor's binding.</summary>
    UnknownClip,

    /// <summary>An entry or exit position lies outside its clip, or the exit precedes the entry of a clip played once.</summary>
    InvalidTiming,

    /// <summary>A crossfade is zero outside the initial <c>Start</c>, non-zero for it, or not the seam table's duration.</summary>
    InvalidCrossfade,

    /// <summary>The join's clip pair is outside the planner set.</summary>
    NotInPlannerSet,

    /// <summary>The join <c>(a, b)</c> is not a usable seam aligned within the seam tolerance.</summary>
    NotAlignedSeam,

    /// <summary>The playback from entry to exit is not a whole number of physics ticks.</summary>
    OffTickGrid,

    /// <summary>The join would be requested while another crossfade is still running.</summary>
    CrossfadeStillRunning,
}

/// <summary>A rejected sequence: the reason, the segment whose join or timing failed (-1 for the start), and details.</summary>
public readonly record struct SequenceRejection(SequenceRejectionReason Reason, int Segment, string Detail);

/// <summary>A clip contributing to a predicted tick.</summary>
/// <param name="Role">Clip role.</param>
/// <param name="PositionSeconds">Playback position after the tick.</param>
/// <param name="Weight">Normalised blend weight the mixer applied to the clip's tracks.</param>
public readonly record struct PredictedClipSample(string Role, double PositionSeconds, double Weight);

/// <summary>One predicted physics tick, as the runtime evaluates and applies it.</summary>
/// <param name="Index">Tick index from the start of the prediction (0 = first predicted tick).</param>
/// <param name="Segment">Segment of the current clip (-1 for the clip playing at the start).</param>
/// <param name="IsEntryTick">Whether the current clip was entered (sought) on this tick.</param>
/// <param name="Current">The current state's clip.</param>
/// <param name="FadingFrom">The fading-out clip, when it still contributes.</param>
/// <param name="RootDelta">
/// Applied planar root motion in the actor frame after this tick's rotation, actor-semantic <c>[right, forward]</c>.
/// </param>
/// <param name="YawDelta">Applied root yaw, in radians (positive turns left).</param>
/// <param name="Pose">Accumulated actor pose after the tick.</param>
/// <param name="Planted">Planted state by the ANIM-002 Planted Combination Rule.</param>
/// <param name="LeftBall">Weight-blended pre-IK left ball, root-relative (actor-local axes).</param>
/// <param name="RightBall">Weight-blended pre-IK right ball, root-relative (actor-local axes).</param>
public readonly record struct PredictedTick(
    int Index,
    int Segment,
    bool IsEntryTick,
    PredictedClipSample Current,
    PredictedClipSample? FadingFrom,
    Vector2 RootDelta,
    float YawDelta,
    SequencePose Pose,
    PlantedFeetState Planted,
    Vector3 LeftBall,
    Vector3 RightBall)
{
    /// <summary>The tick's support phase.</summary>
    public SupportPhase Phase => SupportPhases.Of(Planted);
}

/// <summary>Support phase of a tick from its planted state.</summary>
public enum SupportPhase
{
    /// <summary>Both feet planted (double support).</summary>
    BothPlanted,

    /// <summary>The left foot swings while the right one is planted.</summary>
    LeftSwing,

    /// <summary>The right foot swings while the left one is planted.</summary>
    RightSwing,

    /// <summary>Both feet swing (flight or a blended double swing).</summary>
    BothSwing,
}

/// <summary>Support phase helpers.</summary>
public static class SupportPhases
{
    /// <summary>The support phase of a planted state.</summary>
    public static SupportPhase Of(PlantedFeetState planted) => (planted.Left, planted.Right) switch
    {
        (true, true) => SupportPhase.BothPlanted,
        (false, true) => SupportPhase.LeftSwing,
        (true, false) => SupportPhase.RightSwing,
        _ => SupportPhase.BothSwing,
    };

    /// <summary>Whether exactly one foot swings.</summary>
    public static bool IsSingleSwing(this SupportPhase phase) => phase is SupportPhase.LeftSwing or SupportPhase.RightSwing;
}

/// <summary>A maximal run of consecutive predicted ticks with one support phase.</summary>
/// <param name="Phase">Support phase.</param>
/// <param name="FirstTick">Index of the first tick.</param>
/// <param name="LastTick">Index of the last tick (inclusive).</param>
/// <param name="StartPose">Actor pose before the first tick.</param>
/// <param name="EndPose">Actor pose after the last tick; the difference is the window's authored travel.</param>
public readonly record struct SupportWindow(
    SupportPhase Phase,
    int FirstTick,
    int LastTick,
    SequencePose StartPose,
    SequencePose EndPose)
{
    /// <summary>Number of ticks in the window.</summary>
    public int TickCount => LastTick - FirstTick + 1;
}

/// <summary>
/// Result of <see cref="SequencePredictor.PredictSummary"/>: the end pose and a tally of the support windows of an
/// accepted sequence, or the rejection reason (without details) of a rejected one.
/// </summary>
/// <param name="Rejection">Why the sequence was rejected, or <see langword="null"/> when it was accepted.</param>
/// <param name="RejectedSegment">Segment whose join or timing failed (-1 for the start or when accepted).</param>
/// <param name="EndPose">Actor pose after the last tick (the start pose when rejected).</param>
/// <param name="TickCount">Number of predicted ticks.</param>
/// <param name="SingleSwingWindows">Number of single-swing support windows (one foot swinging).</param>
/// <param name="SingleSwingTicks">Number of ticks in single-swing windows.</param>
/// <param name="BothSwingWindows">Number of both-swing support windows.</param>
/// <param name="BothSwingTicks">Number of ticks in both-swing windows.</param>
public readonly record struct SequenceSummary(
    SequenceRejectionReason? Rejection,
    int RejectedSegment,
    SequencePose EndPose,
    int TickCount,
    int SingleSwingWindows,
    int SingleSwingTicks,
    int BothSwingWindows,
    int BothSwingTicks)
{
    /// <summary>Whether the sequence was accepted and predicted.</summary>
    public bool IsValid => Rejection is null;

    /// <summary>Support windows covering every tick, when requested; otherwise empty.</summary>
    public ImmutableArray<SupportWindow> Windows { get; init; } = [];

    internal static SequenceSummary Rejected(SequencePose start, SequenceRejectionReason reason, int segment)
        => new(reason, segment, start, 0, 0, 0, 0, 0);
}

/// <summary>
/// Result of <see cref="SequencePredictor.Predict"/>: the per-tick prediction and its support windows, or the
/// rejection of the sequence.
/// </summary>
public sealed class SequencePrediction
{
    internal SequencePrediction(
        SequencePose startPose,
        ImmutableArray<PredictedTick> ticks,
        ImmutableArray<SupportWindow> windows,
        ImmutableArray<int> entryTicks)
    {
        StartPose = startPose;
        Ticks = ticks;
        Windows = windows;
        EntryTicks = entryTicks;
    }

    internal SequencePrediction(SequencePose startPose, SequenceRejection rejection)
    {
        StartPose = startPose;
        Rejection = rejection;
        Ticks = [];
        Windows = [];
        EntryTicks = [];
    }

    /// <summary>Whether the sequence was accepted and predicted.</summary>
    public bool IsValid => Rejection is null;

    /// <summary>Why the sequence was rejected, when it was.</summary>
    public SequenceRejection? Rejection
    {
        get;
    }

    /// <summary>Actor pose before the first tick.</summary>
    public SequencePose StartPose
    {
        get;
    }

    /// <summary>Predicted ticks, in order.</summary>
    public ImmutableArray<PredictedTick> Ticks
    {
        get;
    }

    /// <summary>Support windows covering every tick, in order.</summary>
    public ImmutableArray<SupportWindow> Windows
    {
        get;
    }

    /// <summary>
    /// Entry tick of each segment. The runtime requests segment <c>i</c>'s transition after tick
    /// <c>EntryTicks[i] − 1</c> (before the first tick for index 0) and issues it on tick <c>EntryTicks[i]</c>.
    /// </summary>
    public ImmutableArray<int> EntryTicks
    {
        get;
    }

    /// <summary>Actor pose after the last tick.</summary>
    public SequencePose EndPose => Ticks.IsEmpty ? StartPose : Ticks[^1].Pose;
}
