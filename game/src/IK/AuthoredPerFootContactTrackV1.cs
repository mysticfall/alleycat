using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Text.Json;
using AlleyCat.Control.Locomotion;
using AlleyCat.Rigging;

#pragma warning disable CS1591

namespace AlleyCat.IK;

/// <summary>Immutable ANIM-002 authored evidence for one role-foot contact declaration.</summary>
public sealed record AuthoredPerFootContactSampleV1
{
    public AuthoredPerFootContactSampleV1(
        int schemaVersion,
        string role,
        LimbSide foot,
        string lifecycle,
        IEnumerable<double>? timing,
        string validity,
        float confidence,
        string provenance,
        string clip,
        string variant,
        string reference,
        string identity,
        string generation)
    {
        SchemaVersion = schemaVersion;
        Role = role;
        Foot = foot;
        Lifecycle = lifecycle;
        Timing = new ReadOnlyCollection<double>(timing?.ToArray() ?? []);
        Validity = validity;
        Confidence = confidence;
        Provenance = provenance;
        Clip = clip;
        Variant = variant;
        Reference = reference;
        Identity = identity;
        Generation = generation;
    }

    public int SchemaVersion
    {
        get;
        init;
    }

    public string Role
    {
        get;
        init;
    }

    public LimbSide Foot
    {
        get;
        init;
    }

    public string Lifecycle
    {
        get;
        init;
    }

    public IReadOnlyList<double> Timing
    {
        get;
    }

    public string Validity
    {
        get;
        init;
    }

    public float Confidence
    {
        get;
        init;
    }

    public string Provenance
    {
        get;
        init;
    }

    /// <summary>Library animation identity the sample's evidence belongs to (embedded-library packages have no clip files).</summary>
    public string Clip
    {
        get;
        init;
    }

    public string Variant
    {
        get;
        init;
    }

    public string Reference
    {
        get;
        init;
    }

    public string Identity
    {
        get;
        init;
    }

    public string Generation
    {
        get;
        init;
    }
}

/// <summary>Immutable per-physics observation which retains its authored ANIM sample unchanged.</summary>
public readonly record struct DerivedPerPhysicsContactSampleV1(
    AuthoredPerFootContactSampleV1? Authored,
    ulong PhysicsTick,
    double ClipTime,
    string ObservedLifecycle,
    string Freshness,
    string RuntimeValidation);

/// <summary>Loads the small, explicit ANIM-002 authored contact-track package.</summary>
public sealed class AuthoredPerFootContactTrackV1
{
    private const double PhaseEpsilon = 0.000001d;
    private readonly FrozenDictionary<(string Role, LimbSide Foot), IReadOnlyList<AuthoredPerFootContactSampleV1>> _samples;
    private readonly FrozenDictionary<string, bool> _loopingByClip;

    private AuthoredPerFootContactTrackV1(
        IEnumerable<AuthoredPerFootContactSampleV1> samples,
        IEnumerable<KeyValuePair<string, bool>> loopingByClip,
        string packageIdentity)
    {
        _samples = samples
            .GroupBy(sample => (sample.Role, sample.Foot))
            .ToFrozenDictionary(
                group => group.Key,
                group => (IReadOnlyList<AuthoredPerFootContactSampleV1>)new ReadOnlyCollection<AuthoredPerFootContactSampleV1>([.. group]));
        _loopingByClip = loopingByClip.ToFrozenDictionary(StringComparer.Ordinal);
        PackageIdentity = packageIdentity;
    }

    public string PackageIdentity
    {
        get;
    }

    public static AuthoredPerFootContactTrackV1 Load(string json)
        => Load(json, null);

    /// <summary>Loads evidence and verifies its complete package binding against a parsed catalogue.</summary>
    public static AuthoredPerFootContactTrackV1 Load(string json, StandingLocomotionCatalogue? catalogue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using JsonDocument document = ParseDocument(json);
        JsonElement root = document.RootElement;
        RequireProperties(root, "contact evidence", ["schema_version", "package_identity", "samples"], ["source_derived"]);
        if (root.GetProperty("schema_version").GetInt32() != 1)
        {
            throw new InvalidDataException("ANIM-002 authored contact tracks must use schema version 1.");
        }

        string packageIdentity = GetRequiredString(root, "package_identity");
        StandingLocomotionContactEvidence? declaration = catalogue?.ContactEvidence;
        if (catalogue is not null && declaration is null)
        {
            throw new InvalidDataException("Catalogue does not declare contact evidence.");
        }
        if (declaration is not null && packageIdentity != declaration.PackageIdentity)
        {
            throw new InvalidDataException("Contact evidence package identity is stale or mismatched.");
        }

        var samples = new List<AuthoredPerFootContactSampleV1>();
        foreach (JsonElement item in root.GetProperty("samples").EnumerateArray())
        {
            RequireProperties(
                item,
                "contact sample",
                ["schema_version", "role", "foot", "lifecycle", "timing", "validity", "confidence", "provenance", "clip", "variant", "reference", "identity", "generation"],
                []);
            var sample = new AuthoredPerFootContactSampleV1(
                item.GetProperty("schema_version").GetInt32(),
                GetRequiredString(item, "role"),
                ParseFoot(GetRequiredString(item, "foot")),
                GetRequiredString(item, "lifecycle"),
                GetTimingOrEmpty(item),
                GetRequiredString(item, "validity"),
                item.GetProperty("confidence").GetSingle(),
                GetRequiredString(item, "provenance"),
                GetClipIdentityReference(item),
                GetRequiredString(item, "variant"),
                GetRequiredString(item, "reference"),
                GetRequiredString(item, "identity"),
                GetRequiredString(item, "generation"));
            ValidateSample(sample, catalogue);
            samples.Add(sample);
        }

        ValidateUnambiguous(samples);
        if (catalogue is not null)
        {
            ValidatePackageBinding(root, samples, catalogue, declaration!);
        }
        Dictionary<string, bool> loopingByClip = catalogue?.Clips.Values.ToDictionary(clip => clip.Identity, clip => clip.IsLooping, StringComparer.Ordinal)
            ?? new Dictionary<string, bool>(StringComparer.Ordinal);
        return new AuthoredPerFootContactTrackV1(samples, loopingByClip, packageIdentity);
    }

    public bool TryGet(string role, LimbSide foot, out AuthoredPerFootContactSampleV1? sample)
    {
        sample = _samples.TryGetValue((role, foot), out IReadOnlyList<AuthoredPerFootContactSampleV1>? samples)
            ? samples.FirstOrDefault()
            : null;
        return sample is not null;
    }

    /// <summary>Gets the explicit lifecycle declaration selected only by the live clip and half-open phase window.</summary>
    public bool TryGetLifecycle(
        string role,
        LimbSide foot,
        string clip,
        float phase,
        out AuthoredContactLifecycleWindow window)
    {
        window = default;
        if (!_samples.TryGetValue((role, foot), out IReadOnlyList<AuthoredPerFootContactSampleV1>? samples))
        {
            return false;
        }

        foreach (string lifecycle in new[] { "acquire", "support", "release" })
        {
            foreach (AuthoredPerFootContactSampleV1 sample in samples)
            {
                if (sample.Lifecycle != lifecycle
                    || !string.Equals(sample.Clip, clip, StringComparison.Ordinal)
                    || !ContainsPhase(sample, phase, _loopingByClip.GetValueOrDefault(clip)))
                {
                    continue;
                }
                window = new(sample, lifecycle switch
                {
                    "acquire" => ContactLockLifecycle.Acquire,
                    "support" => ContactLockLifecycle.Support,
                    _ => ContactLockLifecycle.Release,
                });
                return true;
            }
        }

        return false;
    }

    /// <summary>Counts only explicit half-open support windows; release metadata is never treated as support.</summary>
    public int CountSupportingFeet(string role, string clip, float phase)
    {
        int count = 0;
        foreach (LimbSide foot in new[] { LimbSide.Left, LimbSide.Right })
        {
            if (!_samples.TryGetValue((role, foot), out IReadOnlyList<AuthoredPerFootContactSampleV1>? samples))
            {
                continue;
            }

            if (samples.Any(sample => string.Equals(sample.Clip, clip, StringComparison.Ordinal)
                && sample.Lifecycle == "support"
                && ContainsPhase(sample, phase, _loopingByClip.GetValueOrDefault(clip))))
            {
                count++;
            }
        }

        return count;
    }

    private static LimbSide ParseFoot(string? foot) => foot switch
    {
        "left" => LimbSide.Left,
        "right" => LimbSide.Right,
        _ => throw new InvalidDataException("ANIM-002 contact sample foot must be 'left' or 'right'."),
    };

    private static string GetRequiredString(JsonElement item, string name)
    {
        return !item.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString())
            ? throw new InvalidDataException($"Contact evidence '{name}' must be a non-empty string.")
            : value.GetString()!;
    }

    /// <summary>Contact evidence binds clips by library animation identity; resource-path references fail closed.</summary>
    private static string GetClipIdentityReference(JsonElement item)
    {
        string value = GetRequiredString(item, "clip");
        return !value.Contains('/', StringComparison.Ordinal)
            && !value.Contains('\\', StringComparison.Ordinal)
            && !value.Contains("://", StringComparison.Ordinal)
            ? value
            : throw new InvalidDataException(
                $"Contact evidence 'clip' value '{value}' must be the library animation identity, not a resource path.");
    }

    private static double[] GetTimingOrEmpty(JsonElement item)
        => item.TryGetProperty("timing", out JsonElement timing)
            ? [.. timing.EnumerateArray().Select(value => value.GetDouble())]
            : [];

    private static bool ContainsPhase(AuthoredPerFootContactSampleV1 sample, double phase, bool looping)
    {
        if (!double.IsFinite(phase) || phase < 0d || phase > 1d)
        {
            return false;
        }
        if (sample.Lifecycle == "acquire")
        {
            return Math.Abs(phase - sample.Timing[0]) <= PhaseEpsilon;
        }
        if (sample.Lifecycle == "release")
        {
            return looping
                ? Math.Abs(phase - sample.Timing[0]) <= PhaseEpsilon
                : phase + PhaseEpsilon >= sample.Timing[0];
        }

        double start = sample.Timing[0];
        double end = sample.Timing[1];
        return start < end ? phase >= start && phase < end : phase >= start || phase < end;
    }

    private static void ValidateSample(AuthoredPerFootContactSampleV1 sample, StandingLocomotionCatalogue? catalogue)
    {
        AuthoredContactRefusalReason refusal = AuthoredPerFootContactProvider.Validate(
            sample,
            sample.Role,
            sample.Foot,
            sample.Variant,
            sample.Identity,
            sample.Generation);
        if (refusal != AuthoredContactRefusalReason.None)
        {
            throw new InvalidDataException($"Contact sample is not admissible: {refusal}.");
        }
        if (sample.Timing.Count != (sample.Lifecycle == "support" ? 2 : 1))
        {
            throw new InvalidDataException("Contact lifecycle timing has the wrong arity.");
        }
        if (sample.Lifecycle == "support" && sample.Timing[0] == sample.Timing[1])
        {
            throw new InvalidDataException("Contact support interval must not be empty.");
        }
        if (catalogue is null)
        {
            return;
        }
        if (!catalogue.Clips.TryGetValue(sample.Role, out StandingLocomotionClip? clip)
            || clip.Identity != sample.Identity
            || clip.Identity != sample.Clip
            || catalogue.PackageVariant != sample.Variant
            || catalogue.ContactEvidence is null
            || catalogue.ContactEvidence.Generation != sample.Generation
            || !catalogue.ContactEvidence.Identities.Contains(sample.Identity))
        {
            throw new InvalidDataException("Contact sample role, clip, variant, identity, or generation differs from its catalogue declaration.");
        }
    }

    private static void ValidateUnambiguous(IReadOnlyList<AuthoredPerFootContactSampleV1> samples)
    {
        foreach (IGrouping<(string Role, LimbSide Foot, string Clip), AuthoredPerFootContactSampleV1> path in
                 samples.GroupBy(sample => (sample.Role, sample.Foot, sample.Clip)))
        {
            if (path.Count(sample => sample.Lifecycle == "acquire") > 1
                || path.Count(sample => sample.Lifecycle == "release") > 1)
            {
                throw new InvalidDataException("Contact evidence contains duplicate or ambiguous lifecycle declarations.");
            }
            AuthoredPerFootContactSampleV1[] supports = [.. path.Where(sample => sample.Lifecycle == "support")];
            for (int first = 0; first < supports.Length; first++)
            {
                for (int second = first + 1; second < supports.Length; second++)
                {
                    if (SupportIntervalsOverlap(supports[first], supports[second]))
                    {
                        throw new InvalidDataException("Contact evidence contains overlapping support declarations.");
                    }
                }
            }
        }
    }

    private static bool SupportIntervalsOverlap(AuthoredPerFootContactSampleV1 first, AuthoredPerFootContactSampleV1 second)
    {
        foreach ((double Start, double End) in Expand(first.Timing[0], first.Timing[1]))
        {
            foreach ((double Start, double End) right in Expand(second.Timing[0], second.Timing[1]))
            {
                if (Math.Max(Start, right.Start) < Math.Min(End, right.End))
                {
                    return true;
                }
            }
        }
        return false;

        static IEnumerable<(double Start, double End)> Expand(double start, double end)
        {
            if (start < end)
            {
                yield return (start, end);
            }
            else
            {
                yield return (start, 1d);
                yield return (0d, end);
            }
        }
    }

    private static void ValidatePackageBinding(
        JsonElement root,
        IReadOnlyList<AuthoredPerFootContactSampleV1> samples,
        StandingLocomotionCatalogue catalogue,
        StandingLocomotionContactEvidence declaration)
    {
        var sampledIdentities = samples.Select(sample => sample.Identity).ToHashSet(StringComparer.Ordinal);
        if (!sampledIdentities.SetEquals(declaration.Identities))
        {
            throw new InvalidDataException("Contact sample identities do not match the catalogue declaration.");
        }
        if (!root.TryGetProperty("source_derived", out JsonElement source))
        {
            return;
        }
        RequireProperties(
            source,
            "source-derived contact evidence",
            [
                "schema_version", "method", "generation", "publication_eligible", "required_refusals", "source_package",
                "paths_relative_to_processed_package", "input_digests", "phase_policy", "bone_mapping", "input_contracts",
                "phase_seed_policy", "sampling", "configuration", "configuration_digest", "confidence_weights", "clips",
                "role_contact_matrix",
            ],
            []);
        int sourceVersion = source.GetProperty("schema_version").GetInt32();
        if (sourceVersion != 5
            || GetRequiredString(source, "method") != "phase_seeded_relative_motion_segmentation_v2"
            || GetRequiredString(source, "generation") != declaration.Generation
            || GetRequiredString(source, "sampling") != "native_fps"
            || string.IsNullOrWhiteSpace(GetRequiredString(source, "configuration_digest"))
            || !source.GetProperty("publication_eligible").GetBoolean()
            || source.GetProperty("required_refusals").GetArrayLength() != 0)
        {
            throw new InvalidDataException("Contact evidence source provenance is stale, refused, or unsupported.");
        }
        JsonElement sourcePackage = source.GetProperty("source_package");
        RequireProperties(sourcePackage, "contact source package", ["package", "variant", "action_count"], []);
        if (GetRequiredString(sourcePackage, "package") != catalogue.Package
            || GetRequiredString(sourcePackage, "variant") != catalogue.PackageVariant
            || sourcePackage.GetProperty("action_count").GetInt32() != catalogue.Clips.Count)
        {
            throw new InvalidDataException("Contact evidence source package differs from the catalogue.");
        }
        foreach (string objectName in new[]
                 {
                     "paths_relative_to_processed_package", "input_digests", "phase_policy", "bone_mapping",
                     "phase_seed_policy", "configuration", "confidence_weights",
                 })
        {
            JsonElement value = source.GetProperty(objectName);
            if (value.ValueKind != JsonValueKind.Object || !value.EnumerateObject().Any())
            {
                throw new InvalidDataException($"Contact evidence {objectName} provenance is empty or malformed.");
            }
        }
        if (source.GetProperty("input_contracts").ValueKind != JsonValueKind.Array
            || source.GetProperty("input_contracts").GetArrayLength() == 0)
        {
            throw new InvalidDataException("Contact evidence input contracts are empty or malformed.");
        }

        HashSet<string> clipBindings = [];
        foreach (JsonElement clip in source.GetProperty("clips").EnumerateArray())
        {
            string identity = GetRequiredString(clip, "identity");
            string role = GetRequiredString(clip, "role");
            if (!catalogue.Clips.TryGetValue(role, out StandingLocomotionClip? declaredClip)
                || declaredClip.Identity != identity
                || !clipBindings.Add(identity))
            {
                throw new InvalidDataException("Contact evidence clip bindings are duplicate or differ from the catalogue.");
            }
        }
        if (!clipBindings.SetEquals(catalogue.Clips.Values.Select(clip => clip.Identity)))
        {
            throw new InvalidDataException("Contact evidence does not bind every catalogue clip exactly once.");
        }

        HashSet<(string Role, LimbSide Foot)> coverage = [];
        HashSet<(string Role, LimbSide Foot)> emitted = [];
        foreach (JsonElement row in source.GetProperty("role_contact_matrix").EnumerateArray())
        {
            string role = GetRequiredString(row, "role");
            LimbSide foot = ParseFoot(GetRequiredString(row, "foot"));
            string identity = GetRequiredString(row, "identity");
            string outcome = GetRequiredString(row, "outcome");
            if (!catalogue.Clips.TryGetValue(role, out StandingLocomotionClip? clip)
                || clip.Identity != identity
                || outcome is not ("emitted" or "fallback")
                || !coverage.Add((role, foot)))
            {
                throw new InvalidDataException("Contact role-foot coverage is unknown, refused, duplicate, or mismatched.");
            }
            if (outcome == "emitted")
            {
                _ = emitted.Add((role, foot));
            }
        }
        HashSet<(string Role, LimbSide Foot)> expectedCoverage = [.. catalogue.Clips.Keys.SelectMany(role => new[] { (role, LimbSide.Left), (role, LimbSide.Right) })];
        HashSet<(string Role, LimbSide Foot)> sampledPaths = [.. samples.Select(sample => (sample.Role, sample.Foot))];
        if (!coverage.SetEquals(expectedCoverage) || !emitted.SetEquals(sampledPaths))
        {
            throw new InvalidDataException("Contact evidence role-foot coverage is incomplete or inconsistent with samples.");
        }
    }

    private static JsonDocument ParseDocument(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Contact evidence is not valid JSON.", exception);
        }
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
        HashSet<string> actual = [];
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

/// <summary>Immutable authored lifecycle selection returned from the ANIM-002 track.</summary>
public readonly record struct AuthoredContactLifecycleWindow(AuthoredPerFootContactSampleV1 Sample, ContactLockLifecycle Lifecycle);
