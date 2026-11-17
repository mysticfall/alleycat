using System.Security.Cryptography;
using System.Text.Json;
using Godot;

namespace AlleyCat.Motion;

/// <summary>Shared multi-take role/elapsed-phase sampling. Controllers retain intent, route and worker ownership.</summary>
public sealed class ReviewedRoleSampler
{
    private sealed record Content(ReviewedMotionSource Source, ReviewedMotionSource.Role Role, ReviewedMotionSource.ExperimentalCycle Cycle, bool AngularEpisode,
        string Bundle, string SHA256, Transform3D GuideRoot);
    private readonly Dictionary<string, Content> _roles = [];
    private readonly SourcePhaseClock _clock = new();
    private readonly Transform3D[] _window;
    private Content? _active;
    private readonly string _directory;
    private Dictionary<string, StationaryTurnReference>? _turnReferences;
    private bool _explicitTurnSelection;
    /// <summary>Selected source identity; never a claim about displayed gait/contact.</summary>
    public SourcePhaseClock.Selection Selection
    {
        get; private set;
    }
    /// <summary>Current authored role or empty idle.</summary>
    public string Role => _active?.Role.Name ?? "";
    /// <summary>Actual measured translation speed, including the small observed drift of turn sources.</summary>
    public float ReferenceSpeed => _active?.Role.ReferenceSpeed ?? 0;
    /// <summary>Same-stage forward reference for the player's backward-speed ratio; source measurements stay unchanged.</summary>
    public float ForwardReferenceSpeed => _roles["forward"].Role.ReferenceSpeed;
    /// <summary>Stable authored root reference for guide diagnostics; never feeds generated sway into held intent.</summary>
    internal Transform3D GuideRoot => _active?.GuideRoot ?? _roles["forward"].GuideRoot;

    /// <summary>Representative signed rate; reading it does not activate or advance a second source clock.</summary>
    public float TurnReferenceAngularVelocity(bool left) => _roles[left ? "left_turn" : "right_turn"].Role.ReferenceAngularVelocity;

    /// <summary>Load durable adjacent bundles once, with hashes and existing independent review/data checks.</summary>
    public ReviewedRoleSampler(string path, SomaMotionData calibration)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement set = document.RootElement;
        if (set.GetProperty("schema").GetString() != "anim002_reviewed_set_v1"
            || set.GetProperty("disposition").GetString() != "HUMAN_ACCEPTED_SOURCES_EXPERIMENTAL_REPETITION")
        {
            throw new InvalidDataException("Unreviewed source set.");
        }
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        _directory = directory;
        var sources = new Dictionary<string, ReviewedMotionSource>();
        string? identity = null;
        foreach (JsonElement entry in set.GetProperty("roles").EnumerateArray())
        {
            string name = entry.GetProperty("source").GetString()!;
            string file = Path.GetFullPath(Path.Combine(directory, name));
            if (Path.GetDirectoryName(file) != directory || Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))) != entry.GetProperty("sha256").GetString())
            {
                throw new InvalidDataException("Source set identity/path mismatch.");
            }
            if (!sources.TryGetValue(file, out ReviewedMotionSource? source))
            {
                source = new ReviewedMotionSource(file, calibration);
                sources.Add(file, source);
            }
            JsonElement c = entry.GetProperty("cycle");
            string variant = c.GetProperty("variant").GetString()!, stage = c.GetProperty("stage").GetString()!;
            identity ??= variant + "/" + stage;
            if (identity != variant + "/" + stage)
            {
                throw new InvalidDataException("Mixed set variant/stage.");
            }
            ReviewedMotionSource.Role role = source.GetRole(variant, stage, c.GetProperty("role").GetString()!);
            ReviewedMotionSource.ExperimentalCycle cycle = source.LoadExperimentalCycle(c, role);
            var guide = new Transform3D[calibration.Names.Length * 4];
            source.CopyWindow(role, cycle.First, guide);
            _roles.Add(role.Name, new Content(source, role, cycle,
                c.TryGetProperty("repetition_kind", out JsonElement kind) && kind.GetString() == "angular_episode",
                name, entry.GetProperty("sha256").GetString()!, guide[0]));
        }
        string[] required = ["forward", "backward", "left_noncross", "right_noncross", "left_cross", "right_cross", "left_turn", "right_turn"];
        if (_roles.Count != 8 || required.Any(r => !_roles.ContainsKey(r)))
        {
            throw new InvalidDataException("Incomplete source set.");
        }
        _window = new Transform3D[calibration.Names.Length * 4];
    }

    /// <summary>Forward-axis component tolerance, not stick magnitude: either signed fore/aft component selects crossing.</summary>
    public static string Classify(Vector2 movement, float turn, float nearZero)
    {
        return !movement.IsFinite() || !float.IsFinite(turn) || !float.IsFinite(nearZero) || nearZero < 0
            ? throw new ArgumentOutOfRangeException(nameof(movement))
            : movement != Vector2.Zero
            ? Math.Abs(movement.X) <= nearZero
                ? movement.Y < 0 ? "backward" : "forward"
                : (movement.X < 0 ? "left" : "right") + (Math.Abs(movement.Y) <= nearZero ? "_noncross" : "_cross")
            : turn == 0 ? "" : turn < 0 ? "left_turn" : "right_turn";
    }

    /// <summary>Role change initialises phase once; optional horizon aligns repeatable angular episodes' first future to entry.</summary>
    public bool SetIntent(string role, double now, double targetHorizon = 0)
    {
        if (role == Role)
        {
            return false;
        }
        if (!double.IsFinite(targetHorizon) || targetHorizon < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetHorizon));
        }
        _clock.Stop(now);
        _explicitTurnSelection = false;
        _active = role.Length == 0 ? null : _roles[role];
        if (_active is { } c)
        {
            int period = c.Cycle.PeriodFrames;
            // Explicit intended initial phase, not an activation-time/range rewrite or a per-request clock reset.
            double offset = c.AngularEpisode && role.EndsWith("_turn", StringComparison.Ordinal)
                ? (period - (targetHorizon * SourcePhaseClock.FramesPerSecond % period)) % period : 0;
            _clock.Activate(c.Role.Variant + "/" + c.Role.Stage + "/" + role, c.Cycle.First, c.Cycle.EndExclusive - 1, now, period, offset);
        }
        Selection = _clock.Select(now, 0);
        return true;
    }

    /// <summary>Four ordered raw samples including SE2 wrap. Only target placement/scaling, no articulation repair.</summary>
    public Transform3D[][] Targets(double now, double horizon, Basis facing, Vector3 ground, float translationScale = 1)
    {
        Content c = _active ?? throw new InvalidOperationException("No selected source role.");
        _explicitTurnSelection = false;
        Selection = _clock.Select(now, horizon);
        if (!Selection.Available)
        {
            throw new InvalidOperationException("Source phase unavailable.");
        }
        c.Source.CopyCycleWindow(c.Cycle, Selection.WindowStart, _window);
        Transform3D root = _window[0];
        Vector3 forward = root.Basis.Z;
        Basis placement = facing * new Basis(Vector3.Up, -Mathf.Atan2(forward.X, forward.Z));
        var target = new Transform3D[4][];
        for (int i = 0; i < 4; i++)
        {
            target[i] = _window.AsSpan(i * c.Source.JointCount, c.Source.JointCount).ToArray();
            Vector3 delta = target[i][0].Origin - root.Origin;
            delta.X *= translationScale;
            delta.Z *= translationScale;
            delta.Y = target[i][0].Origin.Y;
            target[i][0] = new Transform3D(placement * target[i][0].Basis, ground + (placement * delta));
        }
        return target;
    }

    // Loaded only by the explicitly opted-in CTRL trial; NAV/default sampling never reads this metadata.
    internal string RoleSourceSHA256(string role) => _roles[role].SHA256;

    internal Transform3D[][] FiniteTurnTargets(string role, double now, int virtualFrame, int localPhase, double cycleStart, double scale, Vector3 ground)
    {
        _ = SetIntent(role, now);
        Content c = _active!;
        StationaryTurnReference reference = TurnReference(role);
        c.Source.CopyCycleWindow(c.Cycle, virtualFrame, _window);
        Transform3D root = _window[0];
        float rawYaw = Mathf.Atan2(root.Basis.Z.X, root.Basis.Z.Z);
        double firstYaw = cycleStart + (reference.Sign * scale * reference.Angle(localPhase));
        Basis placement = new(Vector3.Up, (float)firstYaw - rawYaw);
        var target = new Transform3D[4][];
        for (int i = 0; i < 4; i++)
        {
            target[i] = _window.AsSpan(i * c.Source.JointCount, c.Source.JointCount).ToArray();
            Transform3D sample = target[i][0];
            Vector3 delta = sample.Origin - root.Origin;
            delta.Y = sample.Origin.Y;
            double yaw = cycleStart + (reference.Sign * scale * reference.Angle(localPhase + i));
            float sampleYaw = Mathf.Atan2(sample.Basis.Z.X, sample.Basis.Z.Z);
            target[i][0] = new(new Basis(Vector3.Up, (float)yaw - sampleYaw) * sample.Basis, ground + (placement * delta));
        }
        Selection = new SourcePhaseClock.Selection(Selection.Revision, c.Role.Variant + "/" + c.Role.Stage + "/" + role,
            virtualFrame, virtualFrame, virtualFrame, true)
        {
            CycleIndex = (virtualFrame - reference.First) / reference.Period,
            InCycleFrame = reference.First + localPhase
        };
        return target;
    }

    internal StationaryTurnReference TurnReference(string role)
    {
        if (_turnReferences is null)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(_directory, "turn-angular-reference.json")));
            var references = new Dictionary<string, StationaryTurnReference>();
            foreach (string name in new[] { "left_turn", "right_turn" })
            {
                Content c = _roles[name];
                if (!c.AngularEpisode)
                {
                    throw new InvalidDataException("Turn trial requires an angular episode.");
                }
                references.Add(name, new StationaryTurnReference(name, document.RootElement.GetProperty(name),
                    c.Source.Manifest, c.SHA256, c.Cycle.First, c.Cycle.EndExclusive));
            }
            _turnReferences = references;
        }
        return _turnReferences[role];
    }

    /// <summary>CTRL trial explicit consecutive window; does not change the shared elapsed clock policy.</summary>
    internal Transform3D[][] TurnTargets(Control.MetadataStationaryTurn.Snapshot snapshot, Vector3 ground)
    {
        Content c = _active ?? throw new InvalidOperationException("No selected turn role.");
        StationaryTurnReference reference = TurnReference(snapshot.Role);
        if (c.Role.Name != snapshot.Role)
        {
            throw new InvalidOperationException("Turn snapshot role is stale.");
        }
        c.Source.CopyCycleWindow(c.Cycle, snapshot.WindowStart, _window);
        _explicitTurnSelection = true;
        Transform3D root = _window[0];
        float rootYaw = Mathf.Atan2(root.Basis.Z.X, root.Basis.Z.Z);
        Basis placement = new(Vector3.Up, (float)snapshot.RootYaw - rootYaw);
        var target = new Transform3D[4][];
        for (int i = 0; i < 4; i++)
        {
            target[i] = _window.AsSpan(i * c.Source.JointCount, c.Source.JointCount).ToArray();
            Transform3D sample = target[i][0];
            Vector3 delta = sample.Origin - root.Origin;
            delta.Y = sample.Origin.Y;
            double yaw = reference.Sign * ((snapshot.Scale * reference.Angle(snapshot.SelectedPhase + i)) + snapshot.Offset);
            float rawYaw = Mathf.Atan2(sample.Basis.Z.X, sample.Basis.Z.Z);
            target[i][0] = new Transform3D(new Basis(Vector3.Up, (float)yaw - rawYaw) * sample.Basis,
                ground + (placement * delta));
        }
        Selection = new SourcePhaseClock.Selection(snapshot.Epoch, c.Role.Variant + "/" + c.Role.Stage + "/" + c.Role.Name,
            reference.First + snapshot.ClockPhase, snapshot.WindowStart, snapshot.WindowStart, true)
        {
            CycleIndex = (long)Math.Floor(snapshot.ClockPhase / reference.Period),
            InCycleFrame = reference.First + (snapshot.ClockPhase % reference.Period)
        };
        return target;
    }

    // Snapshot only when the opt-in recorder observes a real submission; no mutable sampler is handed to IO.
    internal object RecordingSnapshot() => new
    {
        role = Role,
        selection = Selection,
        fps = SourcePhaseClock.FramesPerSecond,
        initialPhaseFrames = _explicitTurnSelection ? 0 : _clock.InitialPhaseFrames,
        policy = _explicitTurnSelection ? "CTRL-metadata-cue-debt; selected snapshot can lag elapsed intent" : "default-elapsed-clock",
        bundle = _active?.Bundle,
        bundleSHA256 = _active?.SHA256,
        metadata = _active is { } c ? new
        {
            c.Role.Variant,
            c.Role.Stage,
            c.Role.First,
            c.Role.Last,
            c.Role.ReferenceSpeed,
            c.Role.ReferenceAngularVelocity,
            c.Role.Phase,
            repeatFirst = c.Cycle.First,
            endExclusive = c.Cycle.EndExclusive,
            c.Cycle.PeriodFrames,
            c.AngularEpisode,
            accumulatedEpisodeTransform = Navigation.Recording.NavigationRecordingSink.Pack([c.Cycle.Travel]),
            samples = Selection.Available ? Enumerable.Range(Selection.WindowStart, 4).Select(c.Cycle.Locate).ToArray() : []
        } : null
    };
}
