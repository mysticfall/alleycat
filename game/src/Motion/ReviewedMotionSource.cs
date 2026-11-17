using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;

namespace AlleyCat.Motion;

/// <summary>Immutable, reviewed SOMA source content. Selection/time belong to the caller, not this reader.</summary>
public sealed class ReviewedMotionSource
{
    /// <summary>Explicit raw trial only; never admission of cyclic suitability or mutation of the original role.</summary>
    public sealed record ExperimentalCycle
    {
        internal ExperimentalCycle(Role role, int first, int end, Transform3D transform)
        {
            Role = role;
            First = first;
            EndExclusive = end;
            Travel = transform;
        }
        /// <inheritdoc/>
        public Role Role
        {
            get;
        }
        /// <inheritdoc/>
        public int First
        {
            get;
        }
        /// <inheritdoc/>
        public int EndExclusive
        {
            get;
        }
        /// <inheritdoc/>
        public int PeriodFrames => EndExclusive - First;
        /// <inheritdoc/>
        public Transform3D Travel
        {
            get;
        }
        /// <inheritdoc/>
        public CycleSample Locate(int virtualFrame) => LocateCycleSample(First, PeriodFrames, virtualFrame);
    }

    /// <summary>Original sample identity and accumulated repetition count; no inferred displayed foot state.</summary>
    public readonly record struct CycleSample(int VirtualFrame, int OriginalFrame, long CycleIndex);

    /// <summary>Map each consecutive integer sample separately, including windows crossing multiple short periods.</summary>
    public static CycleSample LocateCycleSample(int first, int period, int virtualFrame)
    {
        if (period <= 0 || virtualFrame < first)
        {
            throw new ArgumentOutOfRangeException(nameof(virtualFrame));
        }
        long offset = (long)virtualFrame - first;
        return new CycleSample(virtualFrame, first + (int)(offset % period), offset / period);
    }

    /// <summary>SE2 group power: translation rotates on subsequent cycles, not repeated world-axis addition.</summary>
    public static Transform3D AccumulatedTravel(Transform3D travel, long cycles)
    {
        if (cycles < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cycles));
        }
        Transform3D result = Transform3D.Identity;
        while (cycles > 0)
        {
            if ((cycles & 1) != 0)
            {
                result *= travel;
            }
            travel *= travel;
            cycles >>= 1;
        }
        return result;
    }

    private readonly string _bundleSHA256;
    /// <summary>Non-cyclic inclusive source-frame bounds and independently declared physical/phase metadata.</summary>
    public sealed record Role(string Variant, string Stage, string Name, int First, int Last,
        int ReferenceFirst, int ReferenceLast, Vector3 ReferenceVelocity, float ReferenceSpeed,
        float ReferencePathSpeed, float ReferenceAngularVelocity, Vector3 TravelDirection, JsonElement Phase)
    {
        /// <summary>Last start that retains four consecutive samples inside this role.</summary>
        public int LastWindowStart => Last - 3;
    }

    private static readonly string[] _roleNames = ["forward", "backward", "left_noncross", "right_noncross",
        "left_cross", "right_cross", "left_turn", "right_turn"];
    private readonly Transform3D[][] _poses;
    private readonly Vector3[] _velocities;
    private readonly float[] _angularVelocities;
    private readonly Role[] _roles;

    /// <summary>Original saved source hash, action and slot, review and exporter provenance.</summary>
    public JsonElement Manifest
    {
        get;
    }
    /// <summary>Supported ranges; missing roles remain missing rather than relabelled.</summary>
    public IReadOnlyList<Role> Roles
    {
        get;
    }
    /// <summary>First original source frame, not a zero-based sample offset.</summary>
    public int FirstFrame
    {
        get;
    }
    /// <summary>All chronological samples, including acquisition, transition and settling.</summary>
    public int FrameCount => _poses.Length;
    /// <summary>Canonical joint count.</summary>
    public int JointCount
    {
        get;
    }

    /// <summary>Load once off the per-frame path; bind to the existing calibrated SOMA joint order.</summary>
    public ReviewedMotionSource(string path, SomaMotionData calibration)
    {
        _bundleSHA256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Manifest = document.RootElement.Clone();
        JsonElement b = Manifest;
        Require(b.GetProperty("schema").GetString() == "anim002_reviewed_source_v1"
            && b.GetProperty("disposition").GetString() == "HUMAN_ACCEPTED_SOURCE_ONLY", "Unsupported/unreviewed source.");
        JsonElement coordinates = b.GetProperty("coordinates");
        foreach ((string key, string value) in new (string, string)[] {
            ("units", "metres"), ("up", "+Y"), ("forward", "+Z"), ("anatomical_left", "+X"),
            ("root", "evaluated_Hips_not_ground_Root"), ("pose", "canonical_identity_rest_parent_local"),
            ("heading", "atan2_forward_x_forward_z_radians"), ("foot", "joint_not_sole") })
        {
            Require(coordinates.GetProperty(key).GetString() == value, "Unsupported source coordinates.");
        }
        string[] names = b.GetProperty("names").Deserialize<string[]>()!;
        int[] parents = b.GetProperty("parents").Deserialize<int[]>()!;
        Require(names.Length == 77 && names.Distinct().Count() == 77 && names[0] == "Hips"
            && names.SequenceEqual(calibration.Names) && parents.SequenceEqual(calibration.Parents), "SOMA skeleton mismatch.");
        JointCount = names.Length;
        JsonElement descriptor = b.GetProperty("descriptor"), source = descriptor.GetProperty("source"), review = b.GetProperty("review");
        string descriptorText = b.GetProperty("descriptor_text").GetString()!;
        JsonNode authored = JsonNode.Parse(descriptor.GetRawText())!;
        foreach (JsonNode? variant in authored["variants"]!.AsArray())
        {
            foreach (JsonNode? stage in variant!["stages"]!.AsArray())
            {
                foreach (JsonNode? role in stage!["roles"]!.AsArray())
                {
                    _ = role!.AsObject().Remove("physical_reference");
                }
            }
        }
        Require(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(descriptorText)))
                == b.GetProperty("descriptor_sha256").GetString()
            && JsonNode.DeepEquals(authored, JsonNode.Parse(descriptorText)), "Descriptor hash/content mismatch.");
        Require(descriptor.GetProperty("schema").GetString() == "anim002_reviewed_descriptor_v1"
            && source.GetProperty("fps").GetInt32() == 30, "Unsupported descriptor/rate.");
        Require(review.GetProperty("schema").GetString() == "anim002_reviewed_approval_v1"
            && review.GetProperty("decision").GetString() == "accepted"
            && !string.IsNullOrWhiteSpace(review.GetProperty("reviewer").GetString())
            && !string.IsNullOrWhiteSpace(review.GetProperty("approval_origin").GetString())
            && review.GetProperty("preview_rig").GetString() == "Female"
            && review.GetProperty("calibration_preserved").GetBoolean()
            && JsonElement.DeepEquals(source, review.GetProperty("source"))
            && b.GetProperty("descriptor_sha256").GetString() == review.GetProperty("descriptor_sha256").GetString(), "Review identity mismatch.");
        JsonElement range = source.GetProperty("frames"), samples = b.GetProperty("samples");
        FirstFrame = range[0].GetInt32();
        int last = range[1].GetInt32();
        Require(last - FirstFrame + 1 == samples.GetArrayLength() && last >= FirstFrame + 3
            && JsonElement.DeepEquals(range, review.GetProperty("full_window")), "Full ordered source required.");
        _poses = new Transform3D[samples.GetArrayLength()][];
        _velocities = new Vector3[_poses.Length - 1];
        _angularVelocities = new float[_poses.Length - 1];
        var roots = new Vector3[_poses.Length];
        double[] headings = new double[_poses.Length];
        for (int i = 0; i < _poses.Length; i++)
        {
            JsonElement sample = samples[i], pose = sample.GetProperty("pose");
            Require(sample.GetProperty("frame").GetInt32() == FirstFrame + i
                && Math.Abs(sample.GetProperty("time_seconds").GetDouble() - (i / 30.0)) < 1e-10
                && pose.GetArrayLength() == JointCount, "Source ordering/timing mismatch.");
            _poses[i] = new Transform3D[JointCount];
            for (int j = 0; j < JointCount; j++)
            {
                JsonElement p = pose[j];
                Require(p.GetArrayLength() == 7, "Invalid pose row.");
                var q = new Quaternion(p[3].GetSingle(), p[4].GetSingle(), p[5].GetSingle(), p[6].GetSingle());
                Vector3 position = Vector(p);
                Require(position.IsFinite() && q.IsFinite() && Math.Abs(q.LengthSquared() - 1) < 1e-5, "Non-finite/non-unit pose.");
                _poses[i][j] = new Transform3D(new Basis(q.Normalized()), position);
            }
            roots[i] = Vector(sample.GetProperty("root_m"));
            headings[i] = sample.GetProperty("heading_radians").GetDouble();
            Require(roots[i].IsFinite() && roots[i] == _poses[i][0].Origin && double.IsFinite(headings[i]), "Invalid root/heading.");
            if (i + 1 < _poses.Length)
            {
                _velocities[i] = Vector(sample.GetProperty("velocity_to_next_mps"));
                _angularVelocities[i] = sample.GetProperty("angular_velocity_to_next_rps").GetSingle();
                Require(_velocities[i].IsFinite() && float.IsFinite(_angularVelocities[i]), "Invalid local derivatives.");
            }
            else
            {
                Require(sample.GetProperty("velocity_to_next_mps").ValueKind == JsonValueKind.Null
                    && sample.GetProperty("angular_velocity_to_next_rps").ValueKind == JsonValueKind.Null, "Invented final derivative.");
            }
        }
        for (int i = 0; i < _velocities.Length; i++)
        {
            // Float storage/engine math follows JSON double arithmetic, allowing only float conversion roundoff.
            Require(_velocities[i].DistanceTo((roots[i + 1] - roots[i]) * 30) < 0.0001f
                && Math.Abs(_angularVelocities[i] - ((headings[i + 1] - headings[i]) * 30)) < 0.0001, "Local derivative mismatch.");
        }
        var roles = new List<Role>();
        var variantIDs = new HashSet<string>();
        foreach (JsonElement variant in descriptor.GetProperty("variants").EnumerateArray())
        {
            string variantID = variant.GetProperty("id").GetString()!;
            Require(!string.IsNullOrWhiteSpace(variantID) && !variantID.Contains('/') && variantIDs.Add(variantID)
                && !string.IsNullOrWhiteSpace(variant.GetProperty("posture").GetString()), "Invalid variant/posture.");
            var stageIDs = new HashSet<string>();
            foreach (JsonElement stage in variant.GetProperty("stages").EnumerateArray())
            {
                string stageID = stage.GetProperty("id").GetString()!;
                Require(!string.IsNullOrWhiteSpace(stageID) && !stageID.Contains('/') && stageIDs.Add(stageID), "Invalid stage.");
                var stageRoles = new List<Role>();
                foreach (JsonElement r in stage.GetProperty("roles").EnumerateArray())
                {
                    string name = r.GetProperty("role").GetString()!;
                    JsonElement usable = r.GetProperty("usable"), representative = r.GetProperty("representative"), physical = r.GetProperty("physical_reference"), phase = r.GetProperty("phase");
                    var role = new Role(variantID, stageID, name, usable[0].GetInt32(), usable[1].GetInt32(),
                        representative[0].GetInt32(), representative[1].GetInt32(), Vector(physical.GetProperty("velocity_mps")),
                        physical.GetProperty("planar_net_speed_mps").GetSingle(), physical.GetProperty("planar_path_speed_mps").GetSingle(),
                        physical.GetProperty("angular_velocity_rps").GetSingle(), Vector(r.GetProperty("travel_direction")), phase);
                    Require(_roleNames.Contains(name) && stageRoles.All(x => x.Name != name)
                        && role.First >= FirstFrame && role.Last <= last && role.LastWindowStart >= role.First
                        && role.ReferenceFirst >= role.First && role.ReferenceLast <= role.Last && role.ReferenceLast > role.ReferenceFirst
                        && r.GetProperty("loopability").GetString() == "non_cyclic"
                        && JsonElement.DeepEquals(usable, review.GetProperty("accepted_ranges").GetProperty($"{variantID}/{stageID}/{name}"))
                        && JsonElement.DeepEquals(representative, physical.GetProperty("interval"))
                        && role.TravelDirection.IsFinite() && role.ReferenceVelocity.IsFinite()
                        && float.IsFinite(role.ReferenceSpeed) && role.ReferenceSpeed >= 0
                        && float.IsFinite(role.ReferencePathSpeed) && role.ReferencePathSpeed >= 0
                        && float.IsFinite(role.ReferenceAngularVelocity)
                        && phase.GetProperty("observed_support").GetString() is "unknown" or "supported" or "mismatched"
                        && phase.GetProperty("transfer_compatibility").GetString() == "unverified"
                        && phase.TryGetProperty("intended", out _), "Invalid reviewed role metadata.");
                    float seconds = (role.ReferenceLast - role.ReferenceFirst) / 30f;
                    Vector3 velocity = (roots[role.ReferenceLast - FirstFrame] - roots[role.ReferenceFirst - FirstFrame]) / seconds;
                    float pathLength = 0;
                    for (int i = role.ReferenceFirst - FirstFrame; i < role.ReferenceLast - FirstFrame; i++)
                    {
                        Vector3 delta = roots[i + 1] - roots[i];
                        pathLength += new Vector2(delta.X, delta.Z).Length();
                    }
                    Require(velocity.DistanceTo(role.ReferenceVelocity) < 0.0001f
                        && Math.Abs(new Vector2(velocity.X, velocity.Z).Length() - role.ReferenceSpeed) < 0.0001f
                        && Math.Abs((pathLength / seconds) - role.ReferencePathSpeed) < 0.0001f
                        && Math.Abs(((headings[role.ReferenceLast - FirstFrame] - headings[role.ReferenceFirst - FirstFrame]) / seconds)
                            - role.ReferenceAngularVelocity) < 0.0001, "Physical reference mismatch.");
                    Require(stageRoles.All(x => role.Last < x.First || x.Last < role.First), "Overlapping role ranges.");
                    stageRoles.Add(role);
                }
                string coverage = stage.GetProperty("coverage").GetString()!;
                Require(stageRoles.Count > 0 && coverage is "partial" or "complete"
                    && (coverage != "complete" || stageRoles.Count == _roleNames.Length), "Invalid completeness claim.");
                roles.AddRange(stageRoles);
            }
            Require(stageIDs.Count > 0, "Missing stages.");
        }
        Require(roles.Count > 0 && review.GetProperty("accepted_ranges").EnumerateObject().Count() == roles.Count, "Missing/unreviewed role coverage.");
        _roles = [.. roles];
        Roles = Array.AsReadOnly(_roles);
    }

    /// <summary>Explicit role lookup; no fallback to another role, stage or variant.</summary>
    public Role GetRole(string variant, string stage, string role) => _roles.Single(r => r.Variant == variant && r.Stage == stage && r.Name == role);

    /// <summary>Load separately bound experimental metadata; original receipt/range/loopability remains unchanged.</summary>
    public ExperimentalCycle LoadExperimentalCycle(string path, Role role)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return LoadExperimentalCycle(document.RootElement, role);
    }

    /// <summary>Inline set metadata has the same identity and measured SE2 checks as a standalone receipt.</summary>
    public ExperimentalCycle LoadExperimentalCycle(JsonElement c, Role role)
    {
        Require(_roles.Any(r => ReferenceEquals(r, role)) && c.GetProperty("schema").GetString() == "anim002_experimental_raw_cycle_v1"
            && c.GetProperty("status").GetString() == "EXPERIMENTAL_NOT_ADMITTED"
            && c.GetProperty("bundle_sha256").GetString() == _bundleSHA256
            && c.GetProperty("variant").GetString() == role.Variant && c.GetProperty("stage").GetString() == role.Stage
            && c.GetProperty("role").GetString() == role.Name && c.GetProperty("fps").GetInt32() == 30
            && c.GetProperty("endpoint_policy").GetString() == "next_boundary_reference_not_duplicated"
            && c.GetProperty("source_processing").GetString() == "none", "Unbound/unsupported experimental raw cycle.");
        int first = c.GetProperty("first").GetInt32(), end = c.GetProperty("end_exclusive").GetInt32();
        Require(first >= role.First && end <= role.Last && end > first
            && c.GetProperty("period_frames").GetInt32() == end - first, "Cycle/reference exceeds approved role or period mismatch.");
        JsonElement samples = Manifest.GetProperty("samples");
        Transform3D Frame(int frame)
        {
            JsonElement s = samples[frame - FirstFrame];
            Vector3 p = Vector(s.GetProperty("root_m"));
            p.Y = 0;
            return new Transform3D(new Basis(Vector3.Up, s.GetProperty("heading_radians").GetSingle()), p);
        }
        Transform3D measured = Frame(end) * Frame(first).Inverse();
        JsonElement declared = c.GetProperty("cycle_transform");
        Vector3 translation = Vector(declared.GetProperty("translation_m"));
        float yaw = declared.GetProperty("yaw_radians").GetSingle();
        Require(translation.IsFinite() && float.IsFinite(yaw) && translation.DistanceTo(measured.Origin) < 1e-5f
            && new Basis(Vector3.Up, yaw).Z.DistanceTo(measured.Basis.Z) < 1e-5f, "Cycle travel/facing does not match original boundary references.");
        return new ExperimentalCycle(role, first, end, new Transform3D(new Basis(Vector3.Up, yaw), translation));
    }

    /// <summary>Raw repeated sampling; only root travel/facing is accumulated. Never modify local articulation.</summary>
    public void CopyCycleWindow(ExperimentalCycle cycle, int virtualFrame, Span<Transform3D> destination)
    {
        bool owned = false;
        foreach (Role role in _roles)
        {
            owned |= ReferenceEquals(role, cycle.Role);
        }
        Require(owned, "Cycle belongs to another source.");
        if (destination.Length != JointCount * 4)
        {
            throw new ArgumentException("Expected four poses.", nameof(destination));
        }
        for (int i = 0; i < 4; i++)
        {
            CycleSample sample = cycle.Locate(checked(virtualFrame + i));
            Span<Transform3D> pose = destination.Slice(i * JointCount, JointCount);
            _poses[sample.OriginalFrame - FirstFrame].CopyTo(pose);
            pose[0] = AccumulatedTravel(cycle.Travel, sample.CycleIndex) * pose[0];
        }
    }

    /// <summary>Copy four consecutive original frames, frame-major, into caller-owned storage; never wrap/clamp.</summary>
    public void CopyWindow(Role role, int sourceFrame, Span<Transform3D> destination)
    {
        bool owned = false;
        foreach (Role candidate in _roles)
        {
            owned |= ReferenceEquals(candidate, role);
        }
        Require(owned, "Role belongs to another source.");
        if (sourceFrame < role.First || sourceFrame > role.LastWindowStart)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceFrame), "Four-frame window exceeds non-cyclic role coverage.");
        }
        if (destination.Length != JointCount * 4)
        {
            throw new ArgumentException("Expected four frame-major poses.", nameof(destination));
        }
        for (int i = 0; i < 4; i++)
        {
            _poses[sourceFrame - FirstFrame + i].CopyTo(destination.Slice(i * JointCount, JointCount));
        }
    }

    /// <summary>Floor seconds since FirstFrame at 30 Hz, allowing one product ULP for boundary roundoff.
    /// No clock policy or interpolation.</summary>
    public int FrameAtTime(double sourceSeconds)
    {
        return !double.IsFinite(sourceSeconds) || sourceSeconds < 0 || sourceSeconds > (FrameCount - 1) / 30.0
            ? throw new ArgumentOutOfRangeException(nameof(sourceSeconds))
            : FirstFrame + (int)Math.Floor(Math.BitIncrement(sourceSeconds * 30));
    }

    /// <summary>Observed finite-difference source velocity, not representative speed or displayed feedback.</summary>
    public Vector3 VelocityToNext(int sourceFrame) => _velocities[sourceFrame - FirstFrame];
    /// <summary>Observed Hips-facing progression about source +Y.</summary>
    public float AngularVelocityToNext(int sourceFrame) => _angularVelocities[sourceFrame - FirstFrame];

    private static Vector3 Vector(JsonElement row) => new(row[0].GetSingle(), row[1].GetSingle(), row[2].GetSingle());
    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }
}
