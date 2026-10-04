using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AlleyCat.Control.Locomotion;

/// <summary>
/// One contiguous planted or swing segment of one foot, in source-clip seconds. Segments are half-open
/// <c>[StartSeconds, EndSeconds)</c>, except that the last segment of a clip also contains the clip end.
/// </summary>
public readonly record struct PlantedIntervalSegment(double StartSeconds, double EndSeconds, bool IsPlanted);

/// <summary>
/// Both feet's planted/swing segments of one clip. Each foot's segments are contiguous and cover the whole clip.
/// </summary>
public sealed class PlantedClipIntervals
{
    internal PlantedClipIntervals(
        string identity,
        string role,
        string capability,
        bool isLooping,
        double durationSeconds,
        ImmutableArray<PlantedIntervalSegment> left,
        ImmutableArray<PlantedIntervalSegment> right)
    {
        Identity = identity;
        Role = role;
        Capability = capability;
        IsLooping = isLooping;
        DurationSeconds = durationSeconds;
        Left = left;
        Right = right;
    }

    /// <summary>Animation identity of the clip in its package library.</summary>
    public string Identity
    {
        get;
    }

    /// <summary>Package role of the clip.</summary>
    public string Role
    {
        get;
    }

    /// <summary>Canonical ANIM-002 capability identifier declared by the package.</summary>
    public string Capability
    {
        get;
    }

    /// <summary>Whether the clip loops; looping lookups wrap the source time.</summary>
    public bool IsLooping
    {
        get;
    }

    /// <summary>Clip duration in source seconds.</summary>
    public double DurationSeconds
    {
        get;
    }

    /// <summary>Left-foot segments in source order.</summary>
    public ImmutableArray<PlantedIntervalSegment> Left
    {
        get;
    }

    /// <summary>Right-foot segments in source order.</summary>
    public ImmutableArray<PlantedIntervalSegment> Right
    {
        get;
    }

    /// <summary>Segments of the given anatomical foot.</summary>
    public ImmutableArray<PlantedIntervalSegment> SegmentsFor(StandingLocomotionAnatomicalSide foot) =>
        foot == StandingLocomotionAnatomicalSide.Left ? Left : Right;

    /// <summary>
    /// Maps a playback position to source time: looping clips wrap into <c>[0, DurationSeconds)</c>, finite clips
    /// clamp to <c>[0, DurationSeconds]</c>.
    /// </summary>
    public double NormaliseSourceTime(double sourceTimeSeconds)
    {
        if (!double.IsFinite(sourceTimeSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(sourceTimeSeconds), "Source time must be finite.");
        }

        if (!IsLooping)
        {
            return Math.Clamp(sourceTimeSeconds, 0d, DurationSeconds);
        }

        double wrapped = sourceTimeSeconds % DurationSeconds;
        return wrapped < 0d ? wrapped + DurationSeconds : wrapped;
    }

    /// <summary>The segment of <paramref name="foot" /> that contains the playback position.</summary>
    public PlantedIntervalSegment SegmentAt(StandingLocomotionAnatomicalSide foot, double sourceTimeSeconds)
    {
        double time = NormaliseSourceTime(sourceTimeSeconds);
        ImmutableArray<PlantedIntervalSegment> segments = SegmentsFor(foot);
        for (int index = 0; index < segments.Length - 1; index++)
        {
            if (time < segments[index].EndSeconds)
            {
                return segments[index];
            }
        }

        return segments[^1];
    }

    /// <summary>Whether <paramref name="foot" /> is planted at the playback position.</summary>
    public bool IsPlanted(StandingLocomotionAnatomicalSide foot, double sourceTimeSeconds) =>
        SegmentAt(foot, sourceTimeSeconds).IsPlanted;
}

/// <summary>
/// What a package declares about one clip, for binding planted intervals to that package.
/// </summary>
internal readonly record struct PlantedClipExpectation(
    string Identity,
    string Role,
    string Capability,
    bool IsLooping,
    double DurationSeconds);

/// <summary>
/// Read-only ANIM-002 per-foot planted intervals of one package (schema version 1): the locomotion package's
/// <c>per_foot_planted_intervals_v1.json</c> or the idle catalogue's <c>per_foot_planted_intervals</c> field.
/// Parse once at load time; lookups by clip identity and source time do not allocate. Intervals are bound to their
/// package by character, package, variant, clip inventory, and the canonical SHA-256 of the runtime library they were
/// derived from (<see cref="ValidateAgainst(StandingLocomotionCatalogue)" />, <see cref="LoadValidatedIdleCatalogue" />).
/// </summary>
public sealed class PerFootPlantedIntervals
{
    /// <summary>The only supported planted-interval schema version.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Contract identifier every planted-interval document declares.</summary>
    public const string Contract = "anim002_per_foot_planted_intervals_v1";

    /// <summary>File name of the locomotion package's planted intervals, co-located with its catalogue.</summary>
    public const string FileName = "per_foot_planted_intervals_v1.json";

    /// <summary>Idle catalogue field that embeds the idle package's planted intervals.</summary>
    public const string CatalogueField = "per_foot_planted_intervals";

    /// <summary>Pipeline tool that derives planted intervals.</summary>
    public const string DerivationTool = "derive_planted_intervals";

    /// <summary>The only supported derivation tool version.</summary>
    public const int DerivationToolVersion = 3;

    private const double TimeTolerance = 0.000001d;

    private const string RuntimePackageSegment = "/animations/runtime/";

    private static readonly string[] _rootProperties =
        ["schema_version", "contract", "character", "package", "variant", "derivation", "clips"];

    private static readonly string[] _derivationProperties = ["tool", "tool_version", "runtime_library_sha256"];

    private static readonly string[] _clipProperties =
        ["identity", "role", "capability", "looping", "duration_seconds", "feet"];

    private static readonly string[] _footProperties = ["baseline_ball_height_m", "segments"];

    private static readonly string[] _segmentProperties = ["state", "start_seconds", "end_seconds"];

    private static readonly string[] _idleCatalogueProperties =
        ["package", "package_variant", "animation_library", "clips", CatalogueField];

    private static readonly string[] _idleClipProperties =
        ["identity", "role", "capability", "loop_intent", "duration_seconds"];

    private static readonly Regex _libraryHeaderPattern = new(
        "^\\[gd_resource(?: [A-Za-z_][A-Za-z0-9_]*=(?:\"[^\"\\r\\n]*\"|[^ \"\\[\\]\\r\\n=]+))*\\]$",
        RegexOptions.CultureInvariant);

    private static readonly Regex _libraryUIDPattern = new(" uid=\"uid://[^\"\\r\\n]+\"", RegexOptions.CultureInvariant);

    private static readonly Regex _sha256Pattern = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    private readonly FrozenDictionary<string, PlantedClipIntervals> _clips;

    private PerFootPlantedIntervals(
        string character,
        string package,
        string variant,
        string runtimeLibrarySha256,
        FrozenDictionary<string, PlantedClipIntervals> clips)
    {
        Character = character;
        Package = package;
        Variant = variant;
        RuntimeLibrarySha256 = runtimeLibrarySha256;
        _clips = clips;
    }

    /// <summary>Character that owns the package.</summary>
    public string Character
    {
        get;
    }

    /// <summary>Package (animation set) the intervals belong to.</summary>
    public string Package
    {
        get;
    }

    /// <summary>Package variant the intervals belong to.</summary>
    public string Variant
    {
        get;
    }

    /// <summary>
    /// Canonical SHA-256 of the runtime library the intervals were derived from: the library's text bytes without
    /// the path-owned top-level resource UID annotation.
    /// </summary>
    public string RuntimeLibrarySha256
    {
        get;
    }

    /// <summary>Clips keyed by animation identity.</summary>
    public IReadOnlyDictionary<string, PlantedClipIntervals> Clips => _clips;

    /// <summary>The intervals of one clip by animation identity.</summary>
    public PlantedClipIntervals this[string clip] =>
        _clips.TryGetValue(clip, out PlantedClipIntervals? intervals)
            ? intervals
            : throw new KeyNotFoundException($"Planted intervals declare no clip '{clip}'.");

    /// <summary>Looks up one clip's intervals by animation identity.</summary>
    public bool TryGetClip(string clip, out PlantedClipIntervals intervals) =>
        _clips.TryGetValue(clip, out intervals!);

    /// <summary>Whether <paramref name="foot" /> is planted in <paramref name="clip" /> at the source time.</summary>
    public bool IsPlanted(string clip, StandingLocomotionAnatomicalSide foot, double sourceTimeSeconds) =>
        this[clip].IsPlanted(foot, sourceTimeSeconds);

    /// <summary>Parses a planted-interval document (the root of <see cref="FileName" />).</summary>
    public static PerFootPlantedIntervals Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using JsonDocument document = ParseDocument(json);
        return Parse(document.RootElement);
    }

    /// <summary>Parses the planted intervals embedded in an idle catalogue's <see cref="CatalogueField" />.</summary>
    public static PerFootPlantedIntervals ParseIdleCatalogue(string catalogueJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogueJson);
        using JsonDocument document = ParseDocument(catalogueJson);
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty(CatalogueField, out JsonElement field)
                ? Parse(field)
                : throw new InvalidDataException($"Catalogue does not declare '{CatalogueField}'.");
    }

    /// <summary>Loads and parses the planted-interval file at a Godot resource path.</summary>
    public static PerFootPlantedIntervals Load(string resourcePath) => Parse(ReadResource(resourcePath));

    /// <summary>Loads an idle catalogue at a Godot resource path and parses its embedded planted intervals.</summary>
    public static PerFootPlantedIntervals LoadIdleCatalogue(string cataloguePath) =>
        ParseIdleCatalogue(ReadResource(cataloguePath));

    /// <summary>
    /// Loads an idle catalogue's embedded planted intervals and fails closed unless they belong to that catalogue's
    /// character, package, variant, and clips, and were derived from its co-located runtime library.
    /// </summary>
    public static PerFootPlantedIntervals LoadValidatedIdleCatalogue(string cataloguePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cataloguePath);
        PerFootPlantedIntervals intervals = ParseValidatedIdleCatalogue(
            ReadResource(cataloguePath),
            CharacterOf(cataloguePath),
            out string libraryFile);
        intervals.ValidateLibraryBinding(ReadResourceBytes($"{cataloguePath[..(cataloguePath.LastIndexOf('/') + 1)]}{libraryFile}"));
        return intervals;
    }

    /// <summary>
    /// Fails closed unless these intervals cover exactly the catalogue's clips with the same character, package,
    /// variant, roles, capabilities, loop intent, and durations, and were derived from the catalogue's library.
    /// </summary>
    public void ValidateAgainst(StandingLocomotionCatalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ValidateAgainst(
            CharacterOf(catalogue.AnimationLibraryPath),
            catalogue.Package,
            catalogue.PackageVariant,
            [.. catalogue.Clips.Values.Select(clip => new PlantedClipExpectation(
                clip.Identity,
                clip.Role,
                StandingLocomotionCatalogue.CapabilityIdentifier(clip.Capability),
                clip.IsLooping,
                clip.DurationSeconds))]);
        ValidateLibraryBinding(ReadResourceBytes(catalogue.AnimationLibraryPath));
    }

    /// <summary>
    /// Fails closed unless these intervals belong to the given character, package, and variant and cover exactly the
    /// declared clips with matching roles, capabilities, loop intent, and durations.
    /// </summary>
    internal void ValidateAgainst(
        string character,
        string package,
        string variant,
        IReadOnlyCollection<PlantedClipExpectation> clips)
    {
        if (character != Character || package != Package || variant != Variant)
        {
            throw new InvalidDataException(
                $"Planted intervals for '{Character}/{Package}/{Variant}' do not belong to package '{character}/{package}/{variant}'.");
        }

        if (clips.Count != _clips.Count)
        {
            throw new InvalidDataException("Planted intervals and package clips must form an identity bijection.");
        }

        foreach (PlantedClipExpectation clip in clips)
        {
            if (!_clips.TryGetValue(clip.Identity, out PlantedClipIntervals? intervals)
                || intervals.Role != clip.Role
                || intervals.Capability != clip.Capability
                || intervals.IsLooping != clip.IsLooping
                || Math.Abs(intervals.DurationSeconds - clip.DurationSeconds) > TimeTolerance)
            {
                throw new InvalidDataException(
                    $"Planted intervals disagree with package clip '{clip.Identity}' identity, role, capability, loop intent, or duration.");
            }
        }
    }

    /// <summary>Fails closed unless the intervals were derived from the runtime library with these bytes.</summary>
    internal void ValidateLibraryBinding(ReadOnlySpan<byte> libraryContent)
    {
        string actual = ComputeRuntimeLibrarySha256(libraryContent);
        if (actual != RuntimeLibrarySha256)
        {
            throw new InvalidDataException(
                $"Planted intervals were derived from runtime library {RuntimeLibrarySha256}, not the bound library {actual}.");
        }
    }

    /// <summary>
    /// Canonical runtime-library SHA-256 shared with the pipeline: the library's bytes with only the path-owned
    /// top-level <c>uid="uid://..."</c> annotation of a well-formed <c>[gd_resource ...]</c> header removed.
    /// </summary>
    internal static string ComputeRuntimeLibrarySha256(ReadOnlySpan<byte> content) =>
        Convert.ToHexStringLower(SHA256.HashData(CanonicalRuntimeLibraryBytes(content)));

    /// <summary>
    /// Character that owns a package resource: the directory above <c>animations/runtime/</c> in its path.
    /// </summary>
    internal static string CharacterOf(string packageResourcePath)
    {
        int segment = packageResourcePath.IndexOf(RuntimePackageSegment, StringComparison.Ordinal);
        int start = segment > 0 ? packageResourcePath.LastIndexOf('/', segment - 1) + 1 : -1;
        return start > 0 && start < segment
            ? packageResourcePath[start..segment]
            : throw new InvalidDataException(
                $"Package resource '{packageResourcePath}' is not under a character's animations/runtime directory.");
    }

    /// <summary>
    /// Parses an idle catalogue's embedded planted intervals and validates them against that catalogue's package
    /// identity and clips. Returns the catalogue's library file name for binding.
    /// </summary>
    internal static PerFootPlantedIntervals ParseValidatedIdleCatalogue(
        string catalogueJson,
        string character,
        out string libraryFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogueJson);
        using JsonDocument document = ParseDocument(catalogueJson);
        JsonElement root = document.RootElement;
        RequireProperties(root, "idle catalogue", _idleCatalogueProperties);
        PerFootPlantedIntervals intervals = Parse(Property(root, CatalogueField));
        libraryFile = GetString(root, "animation_library");
        if (libraryFile.Contains('/', StringComparison.Ordinal) || !libraryFile.EndsWith(".tres", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Idle catalogue library '{libraryFile}' must be a co-located .tres file.");
        }

        JsonElement clipsElement = Property(root, "clips");
        if (clipsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Idle catalogue clips must be an array.");
        }

        List<PlantedClipExpectation> clips = [];
        foreach (JsonElement clip in clipsElement.EnumerateArray())
        {
            RequireProperties(clip, "idle catalogue clip", _idleClipProperties);
            JsonElement looping = Property(clip, "loop_intent");
            if (looping.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new InvalidDataException("Idle catalogue clip loop_intent must be a boolean.");
            }

            clips.Add(new(
                GetString(clip, "identity"),
                GetString(clip, "role"),
                GetString(clip, "capability"),
                looping.GetBoolean(),
                GetDouble(clip, "duration_seconds")));
        }

        if (clips.Select(clip => clip.Identity).Distinct(StringComparer.Ordinal).Count() != clips.Count)
        {
            throw new InvalidDataException("Idle catalogue clip identities must be unique.");
        }

        intervals.ValidateAgainst(
            character,
            GetString(root, "package"),
            GetString(root, "package_variant"),
            clips);
        return intervals;
    }

    /// <summary>
    /// Removes only a valid, non-empty UID attribute from a valid top-level resource header. The header (the first
    /// line, without its line ending) must be strict UTF-8 without NUL bytes; any other header leaves the content
    /// unchanged. The pipeline's Python and Godot implementations apply the same rule, checked against the shared
    /// <c>tools/kimodo-pipeline/tests/fixtures/runtime_library_canonicalisation_vectors.json</c> vectors.
    /// </summary>
    internal static byte[] CanonicalRuntimeLibraryBytes(ReadOnlySpan<byte> content)
    {
        int headerEnd = content.IndexOf((byte)'\n');
        if (headerEnd < 0)
        {
            headerEnd = content.Length;
        }

        int syntaxEnd = headerEnd > 0 && content[headerEnd - 1] == (byte)'\r' ? headerEnd - 1 : headerEnd;
        ReadOnlySpan<byte> headerBytes = content[..syntaxEnd];
        if (headerBytes.Contains((byte)0))
        {
            return content.ToArray();
        }

        string header;
        try
        {
            header = new UTF8Encoding(false, true).GetString(headerBytes);
        }
        catch (DecoderFallbackException)
        {
            return content.ToArray();
        }

        MatchCollection annotations = _libraryUIDPattern.Matches(header);
        if (!_libraryHeaderPattern.IsMatch(header) || annotations.Count != 1)
        {
            return content.ToArray();
        }

        int annotationStart = Encoding.UTF8.GetByteCount(header.AsSpan(0, annotations[0].Index));
        int annotationEnd = Encoding.UTF8.GetByteCount(header.AsSpan(0, annotations[0].Index + annotations[0].Length));
        byte[] canonical = new byte[content.Length - (annotationEnd - annotationStart)];
        content[..annotationStart].CopyTo(canonical);
        content[annotationEnd..].CopyTo(canonical.AsSpan(annotationStart));
        return canonical;
    }

    private static PerFootPlantedIntervals Parse(JsonElement root)
    {
        RequireExactProperties(root, "planted-interval document", _rootProperties);
        if (GetInt(root, "schema_version") != SchemaVersion)
        {
            throw new InvalidDataException(
                $"Planted-interval schema version {GetDouble(root, "schema_version")} is unknown; expected {SchemaVersion}.");
        }

        if (GetString(root, "contract") != Contract)
        {
            throw new InvalidDataException($"Planted-interval contract must be '{Contract}'.");
        }

        JsonElement derivation = Property(root, "derivation");
        RequireProperties(derivation, "planted-interval derivation", _derivationProperties);
        if (GetString(derivation, "tool") != DerivationTool || GetInt(derivation, "tool_version") != DerivationToolVersion)
        {
            throw new InvalidDataException(
                $"Planted-interval derivation must come from {DerivationTool} version {DerivationToolVersion}.");
        }

        string librarySha256 = GetString(derivation, "runtime_library_sha256");
        if (!_sha256Pattern.IsMatch(librarySha256))
        {
            throw new InvalidDataException("Planted-interval runtime_library_sha256 must be a lowercase SHA-256 digest.");
        }

        JsonElement clipsElement = Property(root, "clips");
        if (clipsElement.ValueKind != JsonValueKind.Array || clipsElement.GetArrayLength() == 0)
        {
            throw new InvalidDataException("Planted-interval clips must be a non-empty array.");
        }

        Dictionary<string, PlantedClipIntervals> clips = new(StringComparer.Ordinal);
        foreach (JsonElement item in clipsElement.EnumerateArray())
        {
            PlantedClipIntervals clip = ParseClip(item);
            if (!clips.TryAdd(clip.Identity, clip))
            {
                throw new InvalidDataException($"Planted-interval clip '{clip.Identity}' is declared more than once.");
            }
        }

        return new(
            GetString(root, "character"),
            GetString(root, "package"),
            GetString(root, "variant"),
            librarySha256,
            clips.ToFrozenDictionary(StringComparer.Ordinal));
    }

    private static PlantedClipIntervals ParseClip(JsonElement element)
    {
        RequireExactProperties(element, "planted-interval clip", _clipProperties);
        string identity = GetString(element, "identity");
        JsonElement looping = Property(element, "looping");
        if (looping.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException($"Planted-interval clip '{identity}' looping must be a boolean.");
        }

        double duration = GetDouble(element, "duration_seconds");
        if (duration <= 0d)
        {
            throw new InvalidDataException($"Planted-interval clip '{identity}' duration must be positive.");
        }

        JsonElement feet = Property(element, "feet");
        RequireExactProperties(feet, $"planted-interval feet of '{identity}'", ["left", "right"]);
        return new(
            identity,
            GetString(element, "role"),
            GetString(element, "capability"),
            looping.GetBoolean(),
            duration,
            ParseFoot(Property(feet, "left"), duration, identity, "left"),
            ParseFoot(Property(feet, "right"), duration, identity, "right"));
    }

    private static ImmutableArray<PlantedIntervalSegment> ParseFoot(
        JsonElement element,
        double duration,
        string identity,
        string foot)
    {
        string context = $"planted-interval {foot} foot of '{identity}'";
        RequireExactProperties(element, context, _footProperties);
        _ = GetDouble(element, "baseline_ball_height_m");
        JsonElement segments = Property(element, "segments");
        if (segments.ValueKind != JsonValueKind.Array || segments.GetArrayLength() == 0)
        {
            throw new InvalidDataException($"The {context} declares no segments.");
        }

        ImmutableArray<PlantedIntervalSegment>.Builder result =
            ImmutableArray.CreateBuilder<PlantedIntervalSegment>(segments.GetArrayLength());
        double cursor = 0d;
        bool? previous = null;
        foreach (JsonElement item in segments.EnumerateArray())
        {
            RequireExactProperties(item, $"segment of the {context}", _segmentProperties);
            bool planted = GetString(item, "state") switch
            {
                "planted" => true,
                "swing" => false,
                _ => throw new InvalidDataException($"The {context} declares an unknown segment state."),
            };
            double start = GetDouble(item, "start_seconds");
            double end = GetDouble(item, "end_seconds");
            if (planted == previous || Math.Abs(start - cursor) > TimeTolerance || end <= start)
            {
                throw new InvalidDataException(
                    $"The {context} must declare contiguous, non-empty, alternating planted and swing segments.");
            }

            result.Add(new(start, end, planted));
            cursor = end;
            previous = planted;
        }

        return Math.Abs(cursor - duration) <= TimeTolerance
            ? result.MoveToImmutable()
            : throw new InvalidDataException($"The {context} must cover the whole clip.");
    }

    private static string ReadResource(string resourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePath);
        using var file = Godot.FileAccess.Open(resourcePath, Godot.FileAccess.ModeFlags.Read);
        return file?.GetAsText()
            ?? throw new InvalidDataException($"Planted-interval source '{resourcePath}' is unavailable.");
    }

    private static byte[] ReadResourceBytes(string resourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePath);
        return Godot.FileAccess.FileExists(resourcePath)
            ? Godot.FileAccess.GetFileAsBytes(resourcePath)
            : throw new InvalidDataException($"Runtime library '{resourcePath}' is unavailable for planted-interval binding.");
    }

    private static JsonDocument ParseDocument(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Planted-interval data is not valid JSON.", exception);
        }
    }

    // Requires an object that declares every expected property exactly once and nothing else.
    private static void RequireExactProperties(JsonElement element, string context, IReadOnlyCollection<string> expected)
    {
        HashSet<string> seen = RequireUniqueProperties(element, context);
        foreach (string name in seen)
        {
            if (!expected.Contains(name))
            {
                throw new InvalidDataException($"The {context} declares unsupported field '{name}'.");
            }
        }

        if (seen.Count != expected.Count)
        {
            throw new InvalidDataException($"The {context} must declare exactly: {string.Join(", ", expected)}.");
        }
    }

    // Requires an object that declares every required property and no property more than once.
    private static void RequireProperties(JsonElement element, string context, IReadOnlyCollection<string> required)
    {
        HashSet<string> seen = RequireUniqueProperties(element, context);
        foreach (string name in required)
        {
            if (!seen.Contains(name))
            {
                throw new InvalidDataException($"The {context} is missing '{name}'.");
            }
        }
    }

    private static HashSet<string> RequireUniqueProperties(JsonElement element, string context)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"The {context} must be an object.");
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw new InvalidDataException($"The {context} declares '{property.Name}' more than once.");
            }
        }

        return seen;
    }

    private static JsonElement Property(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement value)
            ? value
            : throw new InvalidDataException($"Planted-interval data is missing {property}.");

    private static string GetString(JsonElement element, string property)
    {
        JsonElement value = Property(element, property);
        return value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException($"Planted-interval {property} must be a non-empty string.");
    }

    private static double GetDouble(JsonElement element, string property)
    {
        JsonElement value = Property(element, property);
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && double.IsFinite(number)
            ? number
            : throw new InvalidDataException($"Planted-interval {property} must be a finite number.");
    }

    private static int GetInt(JsonElement element, string property)
    {
        double value = GetDouble(element, property);
        return value == Math.Truncate(value) && value is >= int.MinValue and <= int.MaxValue
            ? (int)value
            : throw new InvalidDataException($"Planted-interval {property} must be an integer.");
    }
}
