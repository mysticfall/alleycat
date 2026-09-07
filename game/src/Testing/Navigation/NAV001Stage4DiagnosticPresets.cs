using Godot;

#pragma warning disable CS1591

namespace AlleyCat.Testing.Navigation;

/// <summary>Immutable editor/test-only route identity used by Stage 4 automation and manual playtesting.</summary>
public sealed record NAV001Stage4DiagnosticPreset(
    string Key,
    string DisplayName,
    Vector3 Destination,
    float YawRadians,
    Vector3 Facing,
    float DistanceMetres);

/// <summary>Canonical NAV-001 Stage 4 route presets.</summary>
public static class NAV001Stage4DiagnosticPresets
{
    public static readonly NAV001Stage4DiagnosticPreset TerminalSlide = new(
        "terminal-slide",
        "Terminal Slide",
        new(0.48621222f, 0f, -1.419342f),
        -0.33003297f,
        new(0.32407424f, 0f, -0.9460317f),
        1.5003114f);

    public static readonly NAV001Stage4DiagnosticPreset Oblique = new(
        "oblique",
        "Oblique",
        new(-0.49705806f, 0f, -0.61596584f),
        0.6789687f,
        new(-0.62799084f, 0f, -0.7782208f),
        0.7915053f);

    public static readonly NAV001Stage4DiagnosticPreset Settlement = new(
        "settlement",
        "Settlement",
        new(-0.09192729f, 0f, -1.8887208f),
        0.048633344f,
        new(-0.048614178f, 0f, -0.9988177f),
        1.8909565f);

    public static readonly NAV001Stage4DiagnosticPreset OriginalExact = new(
        "original-exact",
        "Original Exact / Right Finite",
        new(-0.7990171f, 0f, -1.8826954f),
        0.40136284f,
        new(-0.39067325f, 0f, -0.9205294f),
        2.0452311f);

    public static readonly NAV001Stage4DiagnosticPreset MirroredFinite = new(
        "mirrored-finite",
        "Mirrored Finite / Left",
        new(0.7990171f, 0f, -1.8826954f),
        -0.40136284f,
        new(0.39067325f, 0f, -0.9205294f),
        2.0452311f);

    public static IReadOnlyList<NAV001Stage4DiagnosticPreset> All
    {
        get;
    } =
    [
        TerminalSlide,
        Oblique,
        Settlement,
        OriginalExact,
        MirroredFinite,
    ];

    public static NAV001Stage4DiagnosticPreset Require(string key)
        => All.SingleOrDefault(preset => string.Equals(preset.Key, key, StringComparison.Ordinal))
            ?? throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown NAV-001 Stage 4 diagnostic preset.");
}

public enum NAV001ManualContactMode
{
    Active,
    Neutralised,
}

public enum NAV001ManualRunState
{
    Idle,
    Loading,
    Ready,
    Running,
    Completed,
    Failed,
}

public enum NAV001HumanPerceptualResult
{
    Unrecorded,
    Improved,
    Unchanged,
    Worse,
}

public enum NAV001RecordSource
{
    ManualHarness,
    AutomatedTest,
}

/// <summary>Value-only run lifecycle for deterministic manual harness selection and reset behaviour.</summary>
public sealed class NAV001Stage4ManualPlaytestSession
{
    public NAV001Stage4DiagnosticPreset Preset { get; private set; } = NAV001Stage4DiagnosticPresets.TerminalSlide;

    public NAV001ManualContactMode ContactMode { get; private set; } = NAV001ManualContactMode.Active;

    public NAV001ManualRunState State { get; private set; } = NAV001ManualRunState.Idle;

    public int RunNumber
    {
        get; private set;
    }

    public string Failure { get; private set; } = string.Empty;

    public void Select(string presetKey, NAV001ManualContactMode contactMode)
    {
        if (State is NAV001ManualRunState.Loading or NAV001ManualRunState.Running)
        {
            throw new InvalidOperationException("Cannot change a NAV-001 manual selection while a run is active.");
        }
        Preset = NAV001Stage4DiagnosticPresets.Require(presetKey);
        ContactMode = contactMode;
    }

    public void BeginFresh()
    {
        RunNumber++;
        Failure = string.Empty;
        State = NAV001ManualRunState.Loading;
    }

    public void MarkReady() => Transition(NAV001ManualRunState.Loading, NAV001ManualRunState.Ready);

    public void MarkRunning() => Transition(NAV001ManualRunState.Ready, NAV001ManualRunState.Running);

    public void MarkCompleted() => Transition(NAV001ManualRunState.Running, NAV001ManualRunState.Completed);

    public void MarkFailed(string failure)
    {
        Failure = string.IsNullOrWhiteSpace(failure) ? "Unknown harness failure." : failure;
        State = NAV001ManualRunState.Failed;
    }

    public void Reset()
    {
        Failure = string.Empty;
        State = NAV001ManualRunState.Idle;
    }

    private void Transition(NAV001ManualRunState expected, NAV001ManualRunState next)
    {
        if (State != expected)
        {
            throw new InvalidOperationException($"Expected NAV-001 manual state {expected}, but found {State}.");
        }
        State = next;
    }
}
