using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using Godot;

#pragma warning disable CA1822, CS1591, IDE0008, IDE0046, IDE0055, IDE0058, IDE0072, IDE1006

namespace AlleyCat.Control.Locomotion;

/// <summary>
/// Immutable, scheduling-only projection of the standing locomotion catalogue. Schema version 3 resolves every clip
/// through its package's co-located <see cref="AnimationLibrary" /> by animation identity; no standalone clip
/// resources participate in the runtime contract.
/// </summary>
public sealed class StandingLocomotionCatalogue
{
    private static readonly Regex IdentifierPattern = new("^[a-z][a-z0-9]*(?:_[a-z0-9]+)*$", RegexOptions.CultureInvariant);

    private StandingLocomotionCatalogue(
        string package,
        string packageVariant,
        string animationLibraryPath,
        ImmutableDictionary<string, StandingLocomotionClip> clips,
        ImmutableDictionary<StandingLocomotionCapability, StandingLocomotionClip> capabilities,
        ImmutableHashSet<StandingLocomotionStitch> mappings,
        ImmutableDictionary<string, StandingLocomotionShortBoutPair> shortBoutPairs,
        StandingLocomotionContactEvidence? contactEvidence)
    {
        Package = package;
        PackageVariant = packageVariant;
        AnimationLibraryPath = animationLibraryPath;
        Clips = clips;
        ClipsByIdentity = clips.Values.ToImmutableDictionary(clip => clip.Identity, StringComparer.Ordinal);
        Capabilities = capabilities;
        Mappings = mappings;
        ShortBoutPairs = shortBoutPairs;
        ContactEvidence = contactEvidence;
    }

    public string Package { get; }
    public string PackageVariant { get; }
    /// <summary>Resolved resource path of the package library co-located with the parsed catalogue.</summary>
    public string AnimationLibraryPath { get; }

    public ImmutableDictionary<string, StandingLocomotionClip> Clips { get; }
    public ImmutableDictionary<StandingLocomotionCapability, StandingLocomotionClip> Capabilities { get; }

    private ImmutableDictionary<string, StandingLocomotionClip> ClipsByIdentity { get; }
    private ImmutableHashSet<StandingLocomotionStitch> Mappings { get; }

    /// <summary>Package-approved terminal-only, opposite-release short-bout pairs.</summary>
    public ImmutableDictionary<string, StandingLocomotionShortBoutPair> ShortBoutPairs { get; }

    public StandingLocomotionContactEvidence? ContactEvidence { get; }

    public StandingLocomotionClip this[string role] => Clips[role];
    public StandingLocomotionClip this[StandingLocomotionCapability capability] => Capabilities[capability];

    /// <summary>
    /// Parses one embedded-only runtime catalogue (schema version 3). The package library is resolved as the
    /// co-located file named by <c>animation_library</c>, and every entry is validated against that library's
    /// animation membership.
    /// </summary>
    public static StandingLocomotionCatalogue Parse(string json, string cataloguePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(cataloguePath);
        using JsonDocument document = ParseDocument(json);
        JsonElement root = document.RootElement;
        RequireProperties(
            root,
            "root",
            ["schema_version", "package", "package_variant", "animation_library", "clip_count", "clips"],
            ["godot_response_coordinates", "phase_event_evidence", "edge_graph", "short_bout_splices", "contact_evidence"]);
        int schemaVersion = GetInt(root, "schema_version");
        if (schemaVersion == 2)
        {
            throw new InvalidDataException(
                $"ANIM-002 scheduling catalogue '{cataloguePath}' uses stale schema version 2, which declares per-clip "
                + "resource paths; republish the character-owned package as an embedded-only schema version 3 catalogue.");
        }
        if (schemaVersion != 3)
        {
            throw new InvalidDataException($"ANIM-002 scheduling catalogue '{cataloguePath}' must use schema version 3.");
        }
        string package = GetIdentifier(root, "package", "package");
        string packageVariant = GetIdentifier(root, "package_variant", "package variant");
        string animationLibraryPath = ResolveCoLocatedLibraryPath(
            cataloguePath,
            GetLibraryFilename(root, "animation_library"));

        ImmutableDictionary<string, StandingLocomotionClip>.Builder clips = ImmutableDictionary.CreateBuilder<string, StandingLocomotionClip>(StringComparer.Ordinal);
        HashSet<string> identities = new(StringComparer.Ordinal);
        foreach (JsonElement item in GetArray(root, "clips"))
        {
            StandingLocomotionClip clip = ParseClip(item);
            if (!identities.Add(clip.Identity) || !clips.TryAdd(clip.Role, clip))
            {
                throw new InvalidDataException("Clip roles and identities must be unique.");
            }
        }

        if (clips.Count == 0 || clips.Count != GetInt(root, "clip_count"))
        {
            throw new InvalidDataException("Catalogue clip count must be positive and match the declared clips.");
        }

        ImmutableDictionary<string, StandingLocomotionClip> parsedClips = clips.ToImmutable();
        ImmutableDictionary<StandingLocomotionCapability, StandingLocomotionClip>.Builder capabilities =
            ImmutableDictionary.CreateBuilder<StandingLocomotionCapability, StandingLocomotionClip>();
        foreach (StandingLocomotionClip clip in parsedClips.Values)
        {
            if (!capabilities.TryAdd(clip.Capability, clip))
            {
                throw new InvalidDataException($"Catalogue capability '{clip.Capability}' is declared by multiple clips.");
            }
        }
        ValidateLibraryCorrespondence(animationLibraryPath, parsedClips);
        StandingLocomotionContactEvidence? parsedContactEvidence = null;
        if (root.TryGetProperty("contact_evidence", out JsonElement contactEvidence))
        {
            parsedContactEvidence = ParseContactEvidence(contactEvidence, parsedClips);
        }

        StandingLocomotionCatalogue catalogue = new(
            package,
            packageVariant,
            animationLibraryPath,
            parsedClips,
            capabilities.ToImmutable(),
            ProjectMappings(parsedClips),
            root.TryGetProperty("short_bout_splices", out JsonElement shortBoutSplices)
                ? ParseShortBoutPairs(shortBoutSplices, parsedClips)
                : [],
            parsedContactEvidence);
        catalogue.Validate();
        return catalogue;
    }

    public bool HasApprovedMapping(string fromRole, string phase, string toRole) =>
        Mappings.Contains(new StandingLocomotionStitch(fromRole, phase, toRole, phase));

    public StandingLocomotionPhaseEvent ResolvePhaseEvent(
        string role,
        StandingLocomotionAnatomicalSide side,
        StandingLocomotionBoundaryContext boundaryContext)
    {
        int count = 0;
        StandingLocomotionPhaseEvent result = default;
        foreach (StandingLocomotionPhaseEvent candidate in this[role].PhaseEvents.Values)
        {
            if (candidate.Side == side && candidate.BoundaryContext == boundaryContext)
            {
                result = candidate;
                count++;
            }
        }

        return count switch
        {
            1 => result,
            0 => throw new InvalidOperationException(
                $"ANIM-002 role '{role}' declares no {side} phase event owned by '{boundaryContext}'."),
            _ => throw new InvalidOperationException(
                $"ANIM-002 role '{role}' ambiguously declares {count} {side} phase events owned by '{boundaryContext}'."),
        };
    }

    public StandingLocomotionBoundaryMapping ResolveBoundaryMapping(
        string fromRole,
        string toRole,
        StandingLocomotionAnatomicalSide side)
    {
        int count = 0;
        StandingLocomotionBoundaryMapping result = default;
        foreach (StandingLocomotionStitch mapping in Mappings)
        {
            if (mapping.FromRole != fromRole || mapping.ToRole != toRole)
            {
                continue;
            }

            StandingLocomotionBoundaryMapping? candidate = CreateBoundaryMapping(mapping, side);
            if (candidate.HasValue)
            {
                result = candidate.Value;
                count++;
            }
        }

        return count switch
        {
            1 => result,
            0 => throw new InvalidOperationException(
                $"ANIM-002 declares no compatible {side} phase-event mapping from role '{fromRole}' to role '{toRole}'."),
            _ => throw new InvalidOperationException(
                $"ANIM-002 ambiguously declares {count} compatible {side} phase-event mappings from role '{fromRole}' to role '{toRole}'."),
        };
    }

    public double GetEventTime(string role, string eventID) => Clips[role].Events[eventID];

    public double GetNormalisedEventPhase(string role, string eventID)
        => GetEventTime(role, eventID) / Clips[role].DurationSeconds;

    public bool TryGetShortBoutPair(string entryRole, out StandingLocomotionShortBoutPair pair)
        => ShortBoutPairs.TryGetValue(entryRole, out pair!);

    /// <summary>Fails explicitly when a consumer's own semantic contract is not supplied by this generic package.</summary>
    public void RequireCapabilities(
        string consumer,
        IEnumerable<StandingLocomotionCapability> requiredCapabilities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumer);
        ArgumentNullException.ThrowIfNull(requiredCapabilities);
        StandingLocomotionCapability[] missing =
        [
            .. requiredCapabilities.Distinct().Where(capability => !Capabilities.ContainsKey(capability)),
        ];
        if (missing.Length > 0)
        {
            throw new InvalidDataException(
                $"{consumer} requires semantic capabilities which this package does not declare: {string.Join(", ", missing)}.");
        }
    }

    public StandingLocomotionCapability GetCapability(string role) => this[role].Capability;

    public bool TryGetClipByIdentity(string identity, out StandingLocomotionClip? clip)
        => ClipsByIdentity.TryGetValue(identity, out clip);

    public Vector2 GetCommonDurationResponse(string role, double commonDuration)
    {
        if (!double.IsFinite(commonDuration) || commonDuration <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(commonDuration));
        }

        Vector3 endpoint = Clips[role].EndpointRootDisplacement;
        return new Vector2(endpoint.X / (float)commonDuration, -endpoint.Z / (float)commonDuration);
    }

    private void Validate()
    {
        foreach (StandingLocomotionClip clip in Clips.Values)
        {
            if (!double.IsFinite(clip.DurationSeconds) || clip.DurationSeconds <= 0)
            {
                throw new InvalidDataException("Clip duration must be finite and positive.");
            }
            if (!clip.EndpointRootDisplacement.IsFinite() || !float.IsFinite(clip.EndpointYaw))
            {
                throw new InvalidDataException("Clip endpoint response must be finite.");
            }

            ValidateApplicability(clip);
            foreach ((string eventID, double time) in clip.Events)
            {
                if (!IdentifierPattern.IsMatch(eventID) || !double.IsFinite(time) || time < 0 || time > clip.DurationSeconds)
                {
                    throw new InvalidDataException("Phase events must be finite acquire or release events within the clip duration.");
                }
            }
            double previousResponseTime = double.NegativeInfinity;
            foreach (StandingLocomotionResponseSample sample in clip.ResponseSamples)
            {
                if (!double.IsFinite(sample.TimeSeconds)
                    || sample.TimeSeconds < 0d
                    || sample.TimeSeconds > clip.DurationSeconds
                    || sample.TimeSeconds <= previousResponseTime
                    || !sample.CumulativeLocalPlanar.IsFinite()
                    || !float.IsFinite(sample.Yaw)
                    || !sample.PlanarVelocity.IsFinite()
                    || !float.IsFinite(sample.YawRate))
                {
                    throw new InvalidDataException("Clip response samples must be finite, ordered, and within the clip duration.");
                }
                previousResponseTime = sample.TimeSeconds;
            }
        }

        foreach (StandingLocomotionShortBoutPair pair in ShortBoutPairs.Values)
        {
            if (!Clips.TryGetValue(pair.EntryRole, out StandingLocomotionClip? entry)
                || !Clips.TryGetValue(pair.ExitRole, out StandingLocomotionClip? exit)
                || !entry.Events.ContainsKey(pair.EntryReleaseEvent)
                || !exit.Events.ContainsKey(pair.ExitReleaseEvent)
                || entry.BoundaryKind != StandingLocomotionBoundaryKind.Transition
                || exit.BoundaryKind != StandingLocomotionBoundaryKind.Transition
                || pair.EntryRole == pair.ExitRole
                || pair.EntryReleaseEvent == pair.ExitReleaseEvent)
            {
                throw new InvalidDataException("Short-bout pairs must be package-approved, opposite-release transition pairs.");
            }
        }
    }

    private StandingLocomotionBoundaryMapping? CreateBoundaryMapping(
        StandingLocomotionStitch mapping,
        StandingLocomotionAnatomicalSide side)
    {
        StandingLocomotionClip from = this[mapping.FromRole];
        StandingLocomotionClip to = this[mapping.ToRole];
        if (!from.PhaseEvents.TryGetValue(mapping.FromEvent, out StandingLocomotionPhaseEvent source)
            || !to.PhaseEvents.TryGetValue(mapping.ToEvent, out StandingLocomotionPhaseEvent target))
        {
            throw new InvalidOperationException(
                $"ANIM-002 mapping '{Describe(mapping)}' references a missing phase event.");
        }

        StandingLocomotionBoundaryContext expectedSource = from.BoundaryKind == StandingLocomotionBoundaryKind.Transition
            ? StandingLocomotionBoundaryContext.End
            : StandingLocomotionBoundaryContext.Cyclic;
        StandingLocomotionBoundaryContext expectedTarget = to.BoundaryKind == StandingLocomotionBoundaryKind.Transition
            ? StandingLocomotionBoundaryContext.Start
            : StandingLocomotionBoundaryContext.Cyclic;
        return source.Side == side
            && target.Side == side
            && source.Kind == target.Kind
            && source.BoundaryContext == expectedSource
            && target.BoundaryContext == expectedTarget
                ? new(mapping, source, target)
                : null;
    }

    private static ImmutableDictionary<string, StandingLocomotionShortBoutPair> ParseShortBoutPairs(
        JsonElement declaration,
        ImmutableDictionary<string, StandingLocomotionClip> clips)
    {
        RequireProperties(declaration, "short_bout_splices", ["pairs"], []);
        ImmutableDictionary<string, StandingLocomotionShortBoutPair>.Builder pairs =
            ImmutableDictionary.CreateBuilder<string, StandingLocomotionShortBoutPair>(StringComparer.Ordinal);
        foreach (JsonElement item in GetArray(declaration, "pairs"))
        {
            RequireProperties(item, "short-bout pair", ["entry_role", "entry_release_event", "exit_role", "exit_release_event"], []);
            StandingLocomotionShortBoutPair pair = new(
                GetString(item, "entry_role"),
                GetString(item, "entry_release_event"),
                GetString(item, "exit_role"),
                GetString(item, "exit_release_event"));
            if (!clips.ContainsKey(pair.EntryRole) || !pairs.TryAdd(pair.EntryRole, pair))
            {
                throw new InvalidDataException("Short-bout pair entries must be unique known transition roles.");
            }
        }

        return pairs.ToImmutable();
    }

    private static ImmutableHashSet<StandingLocomotionStitch> ProjectMappings(ImmutableDictionary<string, StandingLocomotionClip> clips)
    {
        Dictionary<StandingLocomotionStitch, List<string>> occurrences = [];
        foreach (StandingLocomotionClip container in clips.Values)
        {
            foreach (StandingLocomotionStitch mapping in container.Mappings)
            {
                if (!clips.TryGetValue(mapping.FromRole, out StandingLocomotionClip? from)
                    || !clips.TryGetValue(mapping.ToRole, out StandingLocomotionClip? to)
                    || !from.Events.ContainsKey(mapping.FromEvent)
                    || !to.Events.ContainsKey(mapping.ToEvent))
                {
                    throw new InvalidDataException($"Stitch mapping '{Describe(mapping)}' refers to an unknown role or event.");
                }

                StandingLocomotionPhaseEvent source = from.PhaseEvents[mapping.FromEvent];
                StandingLocomotionPhaseEvent target = to.PhaseEvents[mapping.ToEvent];
                if (source.Side != target.Side
                    || source.Kind != target.Kind
                    || source.BoundaryContext != BoundaryContextFor(from, sourceEndpoint: true)
                    || target.BoundaryContext != BoundaryContextFor(to, sourceEndpoint: false))
                {
                    throw new InvalidDataException(
                        $"Stitch mapping '{Describe(mapping)}' declares incompatible phase-event semantics or boundary ownership.");
                }

                if (container.Role != mapping.FromRole && container.Role != mapping.ToRole)
                {
                    throw new InvalidDataException($"Stitch mapping '{Describe(mapping)}' is declared by third-party role '{container.Role}'.");
                }

                if (!occurrences.TryGetValue(mapping, out List<string>? containers))
                {
                    containers = [];
                    occurrences.Add(mapping, containers);
                }

                containers.Add(container.Role);
            }
        }

        ImmutableHashSet<StandingLocomotionStitch> projected = [.. occurrences.Keys];

        foreach ((StandingLocomotionStitch mapping, List<string> containers) in occurrences)
        {
            bool transition = clips[mapping.FromRole].BoundaryKind == StandingLocomotionBoundaryKind.Transition
                || clips[mapping.ToRole].BoundaryKind == StandingLocomotionBoundaryKind.Transition;
            if (!transition && (containers.Count != 1 || containers[0] != mapping.FromRole))
            {
                throw new InvalidDataException($"Cyclic mapping '{Describe(mapping)}' must be declared exactly once by its source role.");
            }

            if (transition && (containers.Count != 2
                || containers.Count(role => role == mapping.FromRole) != 1
                || containers.Count(role => role == mapping.ToRole) != 1))
            {
                throw new InvalidDataException($"Transition mapping '{Describe(mapping)}' must be declared exactly once by each endpoint role.");
            }
        }

        return projected;
    }

    private static StandingLocomotionBoundaryContext BoundaryContextFor(
        StandingLocomotionClip clip,
        bool sourceEndpoint) => clip.BoundaryKind == StandingLocomotionBoundaryKind.Transition
            ? sourceEndpoint ? StandingLocomotionBoundaryContext.End : StandingLocomotionBoundaryContext.Start
            : StandingLocomotionBoundaryContext.Cyclic;

    private static void ValidateApplicability(StandingLocomotionClip clip)
    {
        bool transition = clip.BoundaryKind == StandingLocomotionBoundaryKind.Transition;
        bool confined = clip.BoundaryKind == StandingLocomotionBoundaryKind.ConfinedManoeuvre;
        bool cyclicTravel = clip.BoundaryKind == StandingLocomotionBoundaryKind.CyclicTravel;
        if (clip.IsLooping == transition || (!cyclicTravel && !confined && !transition))
        {
            throw new InvalidDataException("Boundary kind and loop intent disagree.");
        }

        if (confined && (clip.Events.Count != 0 || clip.Mappings.Length != 0))
        {
            throw new InvalidDataException("Confined manoeuvres are phase and stitch inapplicable.");
        }

        if (cyclicTravel && clip.Events.Count == 0)
        {
            throw new InvalidDataException("Cyclic travel requires phase events.");
        }

        if (transition && clip.Events.Count != 1)
        {
            throw new InvalidDataException("Transitions require one locomotion-facing event.");
        }

        if (cyclicTravel && (clip.Events.Count is < 1 or > 2
            || (clip.Events.Count == 2 && (clip.PhaseEvents.Values.Count(value => value.Side == StandingLocomotionAnatomicalSide.Left) != 1
                || clip.PhaseEvents.Values.Count(value => value.Side == StandingLocomotionAnatomicalSide.Right) != 1
                || clip.Events.Values.Distinct().Count() != 2))))
        {
            throw new InvalidDataException("Cyclic travel requires one phase event or distinct bilateral phase events.");
        }
    }

    private static StandingLocomotionClip ParseClip(JsonElement item)
    {
        RequireProperties(
            item,
            "clip",
            ["identity", "role", "capability", "boundary_kind", "loop_intent", "duration_seconds", "phase_events", "stitch_mappings"],
            ["transport", "diagnostic_provenance_non_runtime_authoritative", "endpoint_root_displacement_godot", "endpoint_local_y_yaw", "response", "boundary"]);
        JsonElement phaseEvents = item.GetProperty("phase_events");
        JsonElement stitchMappings = item.GetProperty("stitch_mappings");
        string role = GetString(item, "role");
        string identity = GetString(item, "identity");
        double durationSeconds = GetDouble(item, "duration_seconds");
        ValidateIdentity(identity, role);
        StandingLocomotionCapability capability = ParseCapability(GetString(item, "capability"));
        StandingLocomotionBoundaryKind boundaryKind = ParseBoundary(GetString(item, "boundary_kind"));
        string phaseApplicability = GetApplicability(phaseEvents, "phase_events");
        string mappingApplicability = GetApplicability(stitchMappings, "stitch_mappings");
        RequireProperties(
            phaseEvents,
            "phase_events",
            phaseApplicability == "applicable"
                ? ["applicability", "contract", "duration_seconds", "evaluation_stage", "events", "sample_semantics", "timeline"]
                : ["applicability"],
            []);
        RequireProperties(
            stitchMappings,
            "stitch_mappings",
            mappingApplicability == "applicable" ? ["applicability", "mappings"] : ["applicability"],
            []);
        bool confined = boundaryKind == StandingLocomotionBoundaryKind.ConfinedManoeuvre;
        if (!confined && phaseApplicability != "applicable")
        {
            throw new InvalidDataException("Phase applicability must match the role boundary contract.");
        }

        ImmutableDictionary<string, double>.Builder events = ImmutableDictionary.CreateBuilder<string, double>(StringComparer.Ordinal);
        ImmutableDictionary<string, StandingLocomotionPhaseEvent>.Builder phaseEventMetadata =
            ImmutableDictionary.CreateBuilder<string, StandingLocomotionPhaseEvent>(StringComparer.Ordinal);
        if (phaseApplicability == "applicable")
        {
            ValidatePhaseEventHeader(phaseEvents, role, durationSeconds, item.GetProperty("loop_intent").GetBoolean());
            foreach (JsonElement phase in GetArray(phaseEvents, "events"))
            {
                RequireProperties(
                    phase,
                    "phase event",
                    ["accepted", "anatomical_side", "boundary_context", "canonicalised_from_duplicate_endpoint", "confidence", "derivation_event_id", "derived_output_frame", "event_kind", "id", "measurement", "output_frame", "reason", "repair_frame_delta", "seed_displacement_provenance", "time_seconds"],
                    []);
                string eventID = GetString(phase, "id");
                double timeSeconds = GetDouble(phase, "time_seconds");
                StandingLocomotionPhaseEvent metadata = ValidatePhaseEvent(
                    phase,
                    boundaryKind,
                    eventID,
                    timeSeconds,
                    phaseEvents);
                if (!events.TryAdd(eventID, timeSeconds))
                {
                    throw new InvalidDataException("Phase event identities must be unique.");
                }
                phaseEventMetadata.Add(eventID, metadata);
            }
        }

        ImmutableArray<StandingLocomotionStitch>.Builder mappings = ImmutableArray.CreateBuilder<StandingLocomotionStitch>();
        if (mappingApplicability == "applicable")
        {
            foreach (JsonElement mapping in GetArray(stitchMappings, "mappings"))
            {
                mappings.Add(ParseStitch(mapping));
            }
        }

        JsonElement endpoint = item.GetProperty("endpoint_root_displacement_godot");
        Vector3 endpointRoot = new(
            (float)endpoint[0].GetDouble(),
            (float)endpoint[1].GetDouble(),
            (float)endpoint[2].GetDouble());
        ImmutableArray<StandingLocomotionResponseSample>.Builder responseSamples = ImmutableArray.CreateBuilder<StandingLocomotionResponseSample>();
        if (item.TryGetProperty("response", out JsonElement response))
        {
            RequireProperties(response, "response", ["sample_count", "samples"], []);
            foreach (JsonElement sample in GetArray(response, "samples"))
            {
                RequireProperties(
                    sample,
                    "response sample",
                    ["time_seconds", "cumulative_local_planar", "yaw", "planar_velocity", "yaw_rate"],
                    []);
                JsonElement cumulative = sample.GetProperty("cumulative_local_planar");
                JsonElement velocity = sample.GetProperty("planar_velocity");
                responseSamples.Add(new(
                    GetDouble(sample, "time_seconds"),
                    new Vector2((float)cumulative[0].GetDouble(), (float)cumulative[1].GetDouble()),
                    (float)GetDouble(sample, "yaw"),
                    new Vector2((float)velocity[0].GetDouble(), (float)velocity[1].GetDouble()),
                    (float)GetDouble(sample, "yaw_rate")));
            }

            if (responseSamples.Count != GetInt(response, "sample_count"))
            {
                throw new InvalidDataException("Response sample count does not match its declaration.");
            }
        }
        StandingLocomotionClip clip = new(
            identity,
            role,
            boundaryKind,
            item.GetProperty("loop_intent").GetBoolean(),
            durationSeconds,
            events.ToImmutable(),
            mappings.ToImmutable(),
            endpointRoot,
            (float)GetDouble(item, "endpoint_local_y_yaw"),
            responseSamples.ToImmutable())
        {
            Capability = capability,
            PhaseEvents = phaseEventMetadata.ToImmutable(),
        };
        ValidateCapabilityContract(clip);
        return clip;
    }

    private static void ValidateCapabilityContract(StandingLocomotionClip clip)
    {
        bool compatibleBoundary = clip.Capability switch
        {
            StandingLocomotionCapability.CyclicForwardPrimary
                or StandingLocomotionCapability.CyclicBackwardPrimary
                or StandingLocomotionCapability.CyclicLateralLeftLeftLane
                or StandingLocomotionCapability.CyclicLateralLeftRightLane
                or StandingLocomotionCapability.CyclicLateralRightLeftLane
                or StandingLocomotionCapability.CyclicLateralRightRightLane
                or StandingLocomotionCapability.CyclicArcLeftLeftLane
                or StandingLocomotionCapability.CyclicArcLeftRightLane
                or StandingLocomotionCapability.CyclicArcRightLeftLane
                or StandingLocomotionCapability.CyclicArcRightRightLane =>
                clip.BoundaryKind == StandingLocomotionBoundaryKind.CyclicTravel,
            StandingLocomotionCapability.ConfinedSideStepLeft
                or StandingLocomotionCapability.ConfinedSideStepRight
                or StandingLocomotionCapability.ConfinedPivotLeft
                or StandingLocomotionCapability.ConfinedPivotRight =>
                clip.BoundaryKind == StandingLocomotionBoundaryKind.ConfinedManoeuvre,
            _ => clip.BoundaryKind == StandingLocomotionBoundaryKind.Transition,
        };
        if (!compatibleBoundary)
        {
            throw new InvalidDataException(
                $"Clip '{clip.Role}' capability '{clip.Capability}' is incompatible with boundary kind '{clip.BoundaryKind}'.");
        }

        if (clip.Capability is StandingLocomotionCapability.EntryForwardLeft
            or StandingLocomotionCapability.EntryForwardRight
            or StandingLocomotionCapability.EntryBackwardLeft
            or StandingLocomotionCapability.EntryBackwardRight
            or StandingLocomotionCapability.ExitForwardLeft
            or StandingLocomotionCapability.ExitForwardRight
            or StandingLocomotionCapability.ExitBackwardLeft
            or StandingLocomotionCapability.ExitBackwardRight)
        {
            StandingLocomotionPhaseEvent phase = clip.PhaseEvents.Values.Single();
            StandingLocomotionBoundaryContext expectedContext = clip.Capability is
                StandingLocomotionCapability.EntryForwardLeft
                or StandingLocomotionCapability.EntryForwardRight
                or StandingLocomotionCapability.EntryBackwardLeft
                or StandingLocomotionCapability.EntryBackwardRight
                ? StandingLocomotionBoundaryContext.End
                : StandingLocomotionBoundaryContext.Start;
            if (phase.Side != clip.Capability.Side() || phase.BoundaryContext != expectedContext)
            {
                throw new InvalidDataException(
                    $"Clip '{clip.Role}' capability '{clip.Capability}' is incompatible with its declared phase semantics.");
            }
        }
    }

    private static StandingLocomotionContactEvidence ParseContactEvidence(
        JsonElement declaration,
        ImmutableDictionary<string, StandingLocomotionClip> clips)
    {
        RequireProperties(
            declaration,
            "contact_evidence",
            ["schema_version", "path", "package_identity", "generation", "identities", "diagnostic_provenance_non_runtime_authoritative"],
            []);
        if (GetInt(declaration, "schema_version") != 1)
        {
            throw new InvalidDataException("Contact evidence declaration is invalid.");
        }
        string path = GetDeclaredResourcePath(declaration, "path", ".json");
        string packageIdentity = GetNonEmptyString(declaration, "package_identity");
        string generation = GetNonEmptyString(declaration, "generation");

        RequireProperties(
            declaration.GetProperty("diagnostic_provenance_non_runtime_authoritative"),
            "contact evidence diagnostic provenance",
            ["processed_index", "input"],
            []);
        if (string.IsNullOrWhiteSpace(GetString(declaration.GetProperty("diagnostic_provenance_non_runtime_authoritative"), "processed_index"))
            || string.IsNullOrWhiteSpace(GetString(declaration.GetProperty("diagnostic_provenance_non_runtime_authoritative"), "input")))
        {
            throw new InvalidDataException("Contact evidence diagnostic provenance is invalid.");
        }

        var identities = new HashSet<string>(StringComparer.Ordinal);
        var clipIdentities = clips.Values.Select(clip => clip.Identity).ToHashSet(StringComparer.Ordinal);
        foreach (JsonElement identity in GetArray(declaration, "identities"))
        {
            if (identity.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(identity.GetString())
                || !identities.Add(identity.GetString()!)
                || !clipIdentities.Contains(identity.GetString()!))
            {
                throw new InvalidDataException("Contact evidence identities must be unique declared clip identities.");
            }
        }

        return new(path, packageIdentity, generation, identities.ToImmutableHashSet(StringComparer.Ordinal));
    }

    /// <summary>
    /// Validates catalogue entries against the embedded animations of the package's co-located library: identity
    /// bijection, duration agreement (tolerance 1e-6), and loop intent. Standalone clip resources are not part of
    /// the contract.
    /// </summary>
    private static void ValidateLibraryCorrespondence(
        string animationLibraryPath,
        ImmutableDictionary<string, StandingLocomotionClip> clips)
    {
        AnimationLibrary library = ResourceLoader.Load<AnimationLibrary>(animationLibraryPath)
            ?? throw new InvalidDataException(
                $"Catalogue animation library '{animationLibraryPath}' is unavailable; the package library must be co-located with its catalogue.");
        var libraryIdentities = library.GetAnimationList().Select(name => name.ToString()).ToHashSet(StringComparer.Ordinal);
        if (!libraryIdentities.SetEquals(clips.Values.Select(clip => clip.Identity)))
        {
            throw new InvalidDataException(
                $"Catalogue clips and embedded library animations in '{animationLibraryPath}' must form an identity bijection.");
        }
        foreach (StandingLocomotionClip clip in clips.Values)
        {
            Animation? libraryAnimation = library.GetAnimation(clip.Identity);
            Animation.LoopModeEnum expectedLoop = clip.IsLooping ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None;
            if (libraryAnimation is null
                || !NearlyEqual(libraryAnimation.Length, clip.DurationSeconds)
                || libraryAnimation.LoopMode != expectedLoop)
            {
                throw new InvalidDataException(
                    $"Catalogue clip '{clip.Identity}' duration or loop intent differs from its embedded library animation in '{animationLibraryPath}'.");
            }
        }
    }

    private static void ValidatePhaseEventHeader(
        JsonElement phaseEvents,
        string role,
        double clipDurationSeconds,
        bool isLooping)
    {
        if (GetString(phaseEvents, "contract") != "anim002_post_repair_directional_phase_events_v2"
            || GetString(phaseEvents, "evaluation_stage") != "final_repaired_evaluated_hierarchy")
        {
            throw new InvalidDataException("Phase event evidence contract or evaluation stage is invalid.");
        }

        JsonElement timeline = phaseEvents.GetProperty("timeline");
        RequireProperties(timeline, "phase event timeline", ["fps", "frame_start", "frame_end"], []);
        JsonElement sampleSemantics = phaseEvents.GetProperty("sample_semantics");
        RequireProperties(
            sampleSemantics,
            "phase event sample semantics",
            ["conceptual_sample_count", "duplicate_endpoint", "is_loop", "unique_runtime_sample_count"],
            []);
        double fps = GetDouble(timeline, "fps");
        double frameStart = GetDouble(timeline, "frame_start");
        double frameEnd = GetDouble(timeline, "frame_end");
        double phaseDurationSeconds = GetDouble(phaseEvents, "duration_seconds");
        int conceptualSampleCount = GetInt(sampleSemantics, "conceptual_sample_count");
        int uniqueRuntimeSampleCount = GetInt(sampleSemantics, "unique_runtime_sample_count");
        bool duplicateEndpoint = sampleSemantics.GetProperty("duplicate_endpoint").GetBoolean();
        bool declaredLoop = sampleSemantics.GetProperty("is_loop").GetBoolean();
        if (!double.IsFinite(fps) || fps <= 0d
            || !IsWholeFrame(frameStart) || !IsWholeFrame(frameEnd) || frameStart < 0d || frameEnd <= frameStart
            || !double.IsFinite(phaseDurationSeconds) || phaseDurationSeconds <= 0d
            || !NearlyEqual(phaseDurationSeconds, clipDurationSeconds)
            || !NearlyEqual(phaseDurationSeconds, (frameEnd - frameStart) / fps)
            || conceptualSampleCount != (int)(frameEnd - frameStart + 1d)
            || uniqueRuntimeSampleCount != conceptualSampleCount - (duplicateEndpoint ? 1 : 0)
            || declaredLoop != isLooping
            || duplicateEndpoint != isLooping)
        {
            throw new InvalidDataException($"Phase event timeline is invalid for '{role}'.");
        }
    }

    private static StandingLocomotionPhaseEvent ValidatePhaseEvent(
        JsonElement phase,
        StandingLocomotionBoundaryKind boundaryKind,
        string eventID,
        double timeSeconds,
        JsonElement phaseEvents)
    {
        string anatomicalSide = GetString(phase, "anatomical_side");
        string eventKind = GetString(phase, "event_kind");
        _ = eventKind switch
        {
            "Acquire" => "acquire",
            "Release" => "release",
            _ => throw new InvalidDataException("Phase event kind is invalid."),
        };
        if (anatomicalSide is not ("Left" or "Right")
            || !IdentifierPattern.IsMatch(eventID))
        {
            throw new InvalidDataException("Phase event ID or anatomical side is invalid.");
        }

        JsonElement timeline = phaseEvents.GetProperty("timeline");
        double frameStart = GetDouble(timeline, "frame_start");
        double frameEnd = GetDouble(timeline, "frame_end");
        double fps = GetDouble(timeline, "fps");
        double outputFrame = GetDouble(phase, "output_frame");
        double derivedOutputFrame = GetDouble(phase, "derived_output_frame");
        double repairFrameDelta = GetDouble(phase, "repair_frame_delta");
        double confidence = GetDouble(phase, "confidence");
        bool duplicateEndpoint = phaseEvents.GetProperty("sample_semantics").GetProperty("duplicate_endpoint").GetBoolean();
        JsonElement boundaryContext = phase.GetProperty("boundary_context");
        JsonElement canonicalisedEndpoint = phase.GetProperty("canonicalised_from_duplicate_endpoint");
        string? expectedBoundary = boundaryContext.ValueKind == JsonValueKind.String ? boundaryContext.GetString() : null;
        bool transition = boundaryKind == StandingLocomotionBoundaryKind.Transition;
        if (!IsWholeFrame(outputFrame) || outputFrame < frameStart || outputFrame > frameEnd
            || !IsWholeFrame(derivedOutputFrame) || derivedOutputFrame < frameStart || derivedOutputFrame > frameEnd
            || !IsWholeFrame(repairFrameDelta)
            || repairFrameDelta != CanonicalFrameDelta(outputFrame, derivedOutputFrame, frameStart, frameEnd, duplicateEndpoint)
            || !double.IsFinite(confidence) || confidence < 0d || confidence > 1d
            || !double.IsFinite(timeSeconds) || !NearlyEqual(timeSeconds, (outputFrame - frameStart) / fps)
            || !phase.GetProperty("accepted").GetBoolean()
            || string.IsNullOrWhiteSpace(GetString(phase, "reason"))
            || GetString(phase, "derivation_event_id") != eventID
            || phase.GetProperty("measurement").ValueKind != JsonValueKind.Object
            || phase.GetProperty("seed_displacement_provenance").ValueKind != JsonValueKind.Object
            || (canonicalisedEndpoint.ValueKind != JsonValueKind.Null
                && (canonicalisedEndpoint.ValueKind != JsonValueKind.Number
                    || !IsWholeFrame(canonicalisedEndpoint.GetDouble())))
            || (transition && expectedBoundary is not ("start" or "end"))
            || (!transition && boundaryContext.ValueKind != JsonValueKind.Null)
            || (expectedBoundary == "start" && outputFrame != frameStart)
            || (expectedBoundary == "end" && outputFrame != frameEnd))
        {
            throw new InvalidDataException("Phase event evidence is invalid.");
        }

        return new(
            eventID,
            timeSeconds,
            eventKind == "Acquire" ? StandingLocomotionEventKind.Acquire : StandingLocomotionEventKind.Release,
            anatomicalSide == "Left" ? StandingLocomotionAnatomicalSide.Left : StandingLocomotionAnatomicalSide.Right,
            expectedBoundary switch
            {
                "start" => StandingLocomotionBoundaryContext.Start,
                "end" => StandingLocomotionBoundaryContext.End,
                _ => StandingLocomotionBoundaryContext.Cyclic,
            });
    }

    private static bool IsWholeFrame(double value) => double.IsFinite(value) && value == Math.Truncate(value);
    private static bool NearlyEqual(double first, double second) => double.IsFinite(first) && double.IsFinite(second) && Math.Abs(first - second) <= 0.000001d;

    private static double CanonicalFrameDelta(
        double outputFrame,
        double derivedOutputFrame,
        double frameStart,
        double frameEnd,
        bool duplicateEndpoint)
    {
        double direct = outputFrame - derivedOutputFrame;
        if (!duplicateEndpoint)
        {
            return direct;
        }

        double period = frameEnd - frameStart;
        return new[] { direct, direct - period, direct + period }
            .MinBy(value => (Math.Abs(value), value));
    }

    private static string Describe(StandingLocomotionStitch mapping) => $"{mapping.FromRole}:{mapping.FromEvent}>{mapping.ToRole}:{mapping.ToEvent}";
    private static StandingLocomotionStitch ParseStitch(JsonElement item)
    {
        RequireProperties(item, "mapping", ["from", "to"], []);
        JsonElement from = item.GetProperty("from");
        JsonElement to = item.GetProperty("to");
        RequireProperties(from, "mapping source", ["role", "event"], []);
        RequireProperties(to, "mapping destination", ["role", "event"], []);
        return new(GetString(from, "role"), GetString(from, "event"), GetString(to, "role"), GetString(to, "event"));
    }
    private static StandingLocomotionBoundaryKind ParseBoundary(string value) => value switch { "cyclic_travel" => StandingLocomotionBoundaryKind.CyclicTravel, "confined_manoeuvre" => StandingLocomotionBoundaryKind.ConfinedManoeuvre, "transition" => StandingLocomotionBoundaryKind.Transition, _ => throw new InvalidDataException("Unknown boundary kind.") };
    private static JsonElement.ArrayEnumerator GetArray(JsonElement element, string property) => element.GetProperty(property).EnumerateArray();
    private static string GetString(JsonElement element, string property) => element.GetProperty(property).GetString() ?? throw new InvalidDataException($"Missing {property}.");
    private static int GetInt(JsonElement element, string property)
    {
        double value = element.GetProperty(property).GetDouble();
        return double.IsFinite(value) && value == Math.Truncate(value) && value is >= int.MinValue and <= int.MaxValue
            ? (int)value
            : throw new InvalidDataException($"Catalogue {property} must be a finite integer.");
    }
    private static double GetDouble(JsonElement element, string property) => element.GetProperty(property).GetDouble();

    private static JsonDocument ParseDocument(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("ANIM-002 scheduling catalogue is not valid JSON.", exception);
        }
    }

    private static string GetApplicability(JsonElement element, string context)
    {
        string value = GetString(element, "applicability");
        return value is "applicable" or "inapplicable" ? value : throw new InvalidDataException($"{context} applicability is invalid.");
    }

    private static void ValidateIdentity(string identity, string role)
    {
        if (!IdentifierPattern.IsMatch(role)
            || !IdentifierPattern.IsMatch(identity))
        {
            throw new InvalidDataException("Role and opaque identity must be lower-snake-case identifiers.");
        }
    }

    private static StandingLocomotionCapability ParseCapability(string value) => value switch
    {
        "cyclic_forward_primary" => StandingLocomotionCapability.CyclicForwardPrimary,
        "cyclic_backward_primary" => StandingLocomotionCapability.CyclicBackwardPrimary,
        "cyclic_lateral_left_left_lane" => StandingLocomotionCapability.CyclicLateralLeftLeftLane,
        "cyclic_lateral_left_right_lane" => StandingLocomotionCapability.CyclicLateralLeftRightLane,
        "cyclic_lateral_right_left_lane" => StandingLocomotionCapability.CyclicLateralRightLeftLane,
        "cyclic_lateral_right_right_lane" => StandingLocomotionCapability.CyclicLateralRightRightLane,
        "cyclic_arc_left_left_lane" => StandingLocomotionCapability.CyclicArcLeftLeftLane,
        "cyclic_arc_left_right_lane" => StandingLocomotionCapability.CyclicArcLeftRightLane,
        "cyclic_arc_right_left_lane" => StandingLocomotionCapability.CyclicArcRightLeftLane,
        "cyclic_arc_right_right_lane" => StandingLocomotionCapability.CyclicArcRightRightLane,
        "confined_side_step_left" => StandingLocomotionCapability.ConfinedSideStepLeft,
        "confined_side_step_right" => StandingLocomotionCapability.ConfinedSideStepRight,
        "confined_pivot_left" => StandingLocomotionCapability.ConfinedPivotLeft,
        "confined_pivot_right" => StandingLocomotionCapability.ConfinedPivotRight,
        "entry_forward_left" => StandingLocomotionCapability.EntryForwardLeft,
        "entry_forward_right" => StandingLocomotionCapability.EntryForwardRight,
        "exit_forward_left" => StandingLocomotionCapability.ExitForwardLeft,
        "exit_forward_right" => StandingLocomotionCapability.ExitForwardRight,
        "entry_backward_left" => StandingLocomotionCapability.EntryBackwardLeft,
        "entry_backward_right" => StandingLocomotionCapability.EntryBackwardRight,
        "exit_backward_left" => StandingLocomotionCapability.ExitBackwardLeft,
        "exit_backward_right" => StandingLocomotionCapability.ExitBackwardRight,
        _ => throw new InvalidDataException($"Unknown standing locomotion capability '{value}'."),
    };

    private static string GetIdentifier(JsonElement element, string property, string context)
    {
        string value = GetNonEmptyString(element, property);
        return IdentifierPattern.IsMatch(value)
            ? value
            : throw new InvalidDataException($"Catalogue {context} must be a lower-snake-case identifier.");
    }

    private static string GetNonEmptyString(JsonElement element, string property)
    {
        string value = GetString(element, property);
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidDataException($"Catalogue {property} must not be empty.");
    }

    private static string GetLibraryFilename(JsonElement element, string property)
    {
        string value = GetNonEmptyString(element, property);
        return value.EndsWith(".tres", StringComparison.Ordinal)
            && !value.Contains('/', StringComparison.Ordinal)
            && !value.Contains('\\', StringComparison.Ordinal)
            && !value.Contains("..", StringComparison.Ordinal)
            && !value.Contains("://", StringComparison.Ordinal)
            ? value
            : throw new InvalidDataException(
                $"Catalogue {property} '{value}' must be the package library filename co-located with the catalogue "
                + "(for example 'my_library.tres'), not a resource path.");
    }

    private static string ResolveCoLocatedLibraryPath(string cataloguePath, string libraryFilename)
    {
        int separator = cataloguePath.LastIndexOf('/');
        return separator > 0
            ? $"{cataloguePath[..(separator + 1)]}{libraryFilename}"
            : throw new InvalidDataException(
                $"Catalogue path '{cataloguePath}' must be a directory-qualified Godot resource path.");
    }

    private static string GetDeclaredResourcePath(JsonElement element, string property, string extension)
    {
        string value = GetNonEmptyString(element, property);
        bool absolute = value.StartsWith("res://", StringComparison.Ordinal) || value.StartsWith("user://", StringComparison.Ordinal);
        bool relative = !value.Contains("://", StringComparison.Ordinal) && !value.StartsWith("/", StringComparison.Ordinal);
        return (absolute || relative) && value.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            && !value.Split('/').Contains("..", StringComparer.Ordinal)
            ? value
            : throw new InvalidDataException($"Catalogue {property} must be a safe Godot resource path ending in '{extension}'.");
    }

    private static void RequireProperties(
        JsonElement element,
        string context,
        IReadOnlyCollection<string> required,
        IReadOnlyCollection<string> optional)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{context} must be an object.");
        }

        HashSet<string> allowed = new(required, StringComparer.Ordinal);
        allowed.UnionWith(optional);
        HashSet<string> actual = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name) || !actual.Add(property.Name))
            {
                throw new InvalidDataException($"{context} contains an unknown or duplicate property.");
            }
        }

        if (!actual.IsSupersetOf(required))
        {
            throw new InvalidDataException($"{context} does not contain the required properties.");
        }
    }
}

public enum StandingLocomotionBoundaryKind { CyclicTravel, ConfinedManoeuvre, Transition }
public enum StandingLocomotionEventKind { Acquire, Release }
public enum StandingLocomotionAnatomicalSide { Left, Right }
public enum StandingLocomotionBoundaryContext { Cyclic, Start, End }
public enum StandingLocomotionCapability
{
    CyclicForwardPrimary,
    CyclicBackwardPrimary,
    CyclicLateralLeftLeftLane,
    CyclicLateralLeftRightLane,
    CyclicLateralRightLeftLane,
    CyclicLateralRightRightLane,
    CyclicArcLeftLeftLane,
    CyclicArcLeftRightLane,
    CyclicArcRightLeftLane,
    CyclicArcRightRightLane,
    ConfinedSideStepLeft,
    ConfinedSideStepRight,
    ConfinedPivotLeft,
    ConfinedPivotRight,
    EntryForwardLeft,
    EntryForwardRight,
    ExitForwardLeft,
    ExitForwardRight,
    EntryBackwardLeft,
    EntryBackwardRight,
    ExitBackwardLeft,
    ExitBackwardRight,
}

public static class StandingLocomotionCapabilities
{
    public static StandingLocomotionCapability Travel(bool backward) => backward
        ? StandingLocomotionCapability.CyclicBackwardPrimary
        : StandingLocomotionCapability.CyclicForwardPrimary;

    public static StandingLocomotionCapability Entry(bool backward, StandingLocomotionAnatomicalSide side) =>
        (backward, side) switch
        {
            (false, StandingLocomotionAnatomicalSide.Left) => StandingLocomotionCapability.EntryForwardLeft,
            (false, StandingLocomotionAnatomicalSide.Right) => StandingLocomotionCapability.EntryForwardRight,
            (true, StandingLocomotionAnatomicalSide.Left) => StandingLocomotionCapability.EntryBackwardLeft,
            (true, StandingLocomotionAnatomicalSide.Right) => StandingLocomotionCapability.EntryBackwardRight,
            _ => throw new ArgumentOutOfRangeException(nameof(side)),
        };

    public static StandingLocomotionCapability Exit(bool backward, StandingLocomotionAnatomicalSide side) =>
        (backward, side) switch
        {
            (false, StandingLocomotionAnatomicalSide.Left) => StandingLocomotionCapability.ExitForwardLeft,
            (false, StandingLocomotionAnatomicalSide.Right) => StandingLocomotionCapability.ExitForwardRight,
            (true, StandingLocomotionAnatomicalSide.Left) => StandingLocomotionCapability.ExitBackwardLeft,
            (true, StandingLocomotionAnatomicalSide.Right) => StandingLocomotionCapability.ExitBackwardRight,
            _ => throw new ArgumentOutOfRangeException(nameof(side)),
        };

    public static bool IsBackward(this StandingLocomotionCapability capability) => capability is
        StandingLocomotionCapability.CyclicBackwardPrimary
        or StandingLocomotionCapability.EntryBackwardLeft
        or StandingLocomotionCapability.EntryBackwardRight
        or StandingLocomotionCapability.ExitBackwardLeft
        or StandingLocomotionCapability.ExitBackwardRight;

    public static StandingLocomotionAnatomicalSide Side(this StandingLocomotionCapability capability) => capability switch
    {
        StandingLocomotionCapability.EntryForwardLeft
            or StandingLocomotionCapability.CyclicLateralLeftLeftLane
            or StandingLocomotionCapability.CyclicLateralRightLeftLane
            or StandingLocomotionCapability.CyclicArcLeftLeftLane
            or StandingLocomotionCapability.CyclicArcRightLeftLane
            or StandingLocomotionCapability.ExitForwardLeft
            or StandingLocomotionCapability.EntryBackwardLeft
            or StandingLocomotionCapability.ExitBackwardLeft
            or StandingLocomotionCapability.ConfinedSideStepLeft
            or StandingLocomotionCapability.ConfinedPivotLeft => StandingLocomotionAnatomicalSide.Left,
        StandingLocomotionCapability.EntryForwardRight
            or StandingLocomotionCapability.CyclicLateralLeftRightLane
            or StandingLocomotionCapability.CyclicLateralRightRightLane
            or StandingLocomotionCapability.CyclicArcLeftRightLane
            or StandingLocomotionCapability.CyclicArcRightRightLane
            or StandingLocomotionCapability.ExitForwardRight
            or StandingLocomotionCapability.EntryBackwardRight
            or StandingLocomotionCapability.ExitBackwardRight
            or StandingLocomotionCapability.ConfinedSideStepRight
            or StandingLocomotionCapability.ConfinedPivotRight => StandingLocomotionAnatomicalSide.Right,
        _ => throw new InvalidDataException($"Capability '{capability}' has no single anatomical side."),
    };
}
public sealed record StandingLocomotionClip(
    string Identity,
    string Role,
    StandingLocomotionBoundaryKind BoundaryKind,
    bool IsLooping,
    double DurationSeconds,
    ImmutableDictionary<string, double> Events,
    ImmutableArray<StandingLocomotionStitch> Mappings,
    Vector3 EndpointRootDisplacement,
    float EndpointYaw,
    ImmutableArray<StandingLocomotionResponseSample> ResponseSamples)
{
    /// <summary>Opaque-role semantic capability explicitly declared by the package.</summary>
    public StandingLocomotionCapability Capability { get; init; }

    /// <summary>Semantic event declarations retained from the authoritative package.</summary>
    public ImmutableDictionary<string, StandingLocomotionPhaseEvent> PhaseEvents { get; init; }
        = [];

    public Vector2 AveragePlanarVelocity => new(
        EndpointRootDisplacement.X / (float)DurationSeconds,
        -EndpointRootDisplacement.Z / (float)DurationSeconds);

    public float AverageYawRate => EndpointYaw / (float)DurationSeconds;

    public StandingLocomotionResponseSample SampleResponse(double timeSeconds)
    {
        if (ResponseSamples.IsDefaultOrEmpty)
        {
            return new(0d, Vector2.Zero, 0f, AveragePlanarVelocity, AverageYawRate);
        }

        double time = Math.Clamp(timeSeconds, 0d, DurationSeconds);
        for (int index = 1; index < ResponseSamples.Length; index++)
        {
            StandingLocomotionResponseSample next = ResponseSamples[index];
            if (time > next.TimeSeconds)
            {
                continue;
            }

            StandingLocomotionResponseSample previous = ResponseSamples[index - 1];
            double range = Math.Max(next.TimeSeconds - previous.TimeSeconds, double.Epsilon);
            float weight = (float)Math.Clamp((time - previous.TimeSeconds) / range, 0d, 1d);
            return new(
                time,
                previous.CumulativeLocalPlanar.Lerp(next.CumulativeLocalPlanar, weight),
                Mathf.Lerp(previous.Yaw, next.Yaw, weight),
                previous.PlanarVelocity.Lerp(next.PlanarVelocity, weight),
                Mathf.Lerp(previous.YawRate, next.YawRate, weight));
        }

        return ResponseSamples[^1];
    }
}
public readonly record struct StandingLocomotionResponseSample(
    double TimeSeconds,
    Vector2 CumulativeLocalPlanar,
    float Yaw,
    Vector2 PlanarVelocity,
    float YawRate);
public readonly record struct StandingLocomotionPhaseEvent(
    string ID,
    double TimeSeconds,
    StandingLocomotionEventKind Kind,
    StandingLocomotionAnatomicalSide Side,
    StandingLocomotionBoundaryContext BoundaryContext);
public sealed record StandingLocomotionStitch(string FromRole, string FromEvent, string ToRole, string ToEvent);
public readonly record struct StandingLocomotionBoundaryMapping(
    StandingLocomotionStitch Stitch,
    StandingLocomotionPhaseEvent Source,
    StandingLocomotionPhaseEvent Target);
public sealed record StandingLocomotionShortBoutPair(
    string EntryRole,
    string EntryReleaseEvent,
    string ExitRole,
    string ExitReleaseEvent);
public sealed record StandingLocomotionContactEvidence(
    string Path,
    string PackageIdentity,
    string Generation,
    ImmutableHashSet<string> Identities);
