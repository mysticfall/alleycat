using System.Text.Json;
using System.Text.RegularExpressions;
using AlleyCat.Core.Content;

namespace AlleyCat.Animations;

/// <summary>
/// Loads and validates <c>anim003_pack_v1</c> animation pack manifests. This is the single validation implementation
/// shared by catalogue loading and pack validation.
/// </summary>
public static partial class AnimationPackLoader
{
    private const string SchemaField = "schema";
    private const string IDField = "id";
    private const string EntriesField = "entries";
    private const string CategoryField = "category";
    private const string TagsField = "tags";
    private const string DescriptionField = "description";
    private const string ClipField = "clip";
    private const string SetField = "set";

    private static readonly HashSet<string> _topLevelFields = new(StringComparer.Ordinal) { SchemaField, IDField, EntriesField };

    private static readonly HashSet<string> _idleFields = new(StringComparer.Ordinal)
    {
        IDField, CategoryField, TagsField, DescriptionField, ClipField
    };

    private static readonly HashSet<string> _locomotionFields = new(StringComparer.Ordinal)
    {
        IDField, CategoryField, TagsField, DescriptionField, SetField
    };

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex("^[a-z][a-z0-9_-]*$")]
    private static partial Regex TagPattern();

    /// <summary>
    /// Derives the production content context from a manifest location: a manifest under <c>res://content/&lt;id&gt;/</c>
    /// gets that content pack's context; any other location gets <see cref="ContentContext.Default" />.
    /// </summary>
    public static ContentContext DeriveContext(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);

        if (manifestPath.StartsWith(ContentPaths.ContentRoot, StringComparison.Ordinal))
        {
            string remainder = manifestPath[ContentPaths.ContentRoot.Length..];
            int separator = remainder.IndexOf('/', StringComparison.Ordinal);
            if (separator > 0)
            {
                return ContentContext.ForPack(remainder[..separator]);
            }
        }

        return ContentContext.Default;
    }

    /// <summary>
    /// Resolves a content-root-relative pack path against a context root.
    /// </summary>
    /// <returns><see langword="null" /> when valid; otherwise the reason the path is rejected.</returns>
    public static string? TryResolvePath(string authored, ContentContext context, out string resolved)
    {
        ArgumentNullException.ThrowIfNull(context);
        resolved = string.Empty;

        if (string.IsNullOrWhiteSpace(authored))
        {
            return "path is blank";
        }

        if (authored.Contains("://", StringComparison.Ordinal))
        {
            return "path must be relative to the content root, not a 'res://' or other scheme path";
        }

        if (authored[0] is '/' or '\\' || (authored.Length > 1 && authored[1] == ':' && char.IsAsciiLetter(authored[0])))
        {
            return "path must be relative, not absolute";
        }

        if (authored.Split('/', '\\').Any(segment => segment == ".."))
        {
            return "path must not escape the content root with '..'";
        }

        string root = context.RootPath;
        resolved = root.EndsWith('/') ? root + authored : root + "/" + authored;
        return null;
    }

    /// <summary>
    /// Loads and validates a manifest, throwing with every finding when invalid.
    /// </summary>
    /// <exception cref="AnimationPackValidationException">Thrown when validation fails.</exception>
    public static AnimationPackManifest Load(string manifestPath, ContentContext context, IAnimationPackResources resources)
    {
        (AnimationPackManifest? manifest, IReadOnlyList<AnimationPackDiagnostic> diagnostics) = Validate(manifestPath, context, resources);
        return manifest ?? throw new AnimationPackValidationException(diagnostics);
    }

    /// <summary>
    /// Loads and validates a manifest file, returning the manifest when valid and every finding otherwise.
    /// </summary>
    public static (AnimationPackManifest? Manifest, IReadOnlyList<AnimationPackDiagnostic> Diagnostics) Validate(
        string manifestPath,
        ContentContext context,
        IAnimationPackResources resources)
    {
        ArgumentNullException.ThrowIfNull(resources);

        string json;
        try
        {
            json = resources.ReadText(manifestPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return (null, [new AnimationPackDiagnostic(manifestPath, null, null, manifestPath, $"manifest cannot be read: {error.Message}")]);
        }

        return Validate(json, manifestPath, context, resources);
    }

    /// <summary>
    /// Validates manifest JSON text, returning the manifest when valid and every finding otherwise.
    /// </summary>
    public static (AnimationPackManifest? Manifest, IReadOnlyList<AnimationPackDiagnostic> Diagnostics) Validate(
        string json,
        string manifestPath,
        ContentContext context,
        IAnimationPackResources resources)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(resources);

        List<AnimationPackDiagnostic> diagnostics = [];
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException error)
        {
            return (null, [new AnimationPackDiagnostic(manifestPath, null, null, manifestPath, $"malformed JSON: {error.Message}")]);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, [new AnimationPackDiagnostic(manifestPath, null, null, manifestPath, "manifest must be a JSON object")]);
            }

            string? packID = ReadString(root, IDField);
            string pack = string.IsNullOrWhiteSpace(packID) ? manifestPath : packID;

            void Report(string? entry, string? field, string? path, string message)
            {
                diagnostics.Add(new AnimationPackDiagnostic(pack, entry, field, path ?? manifestPath, message));
            }

            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!_topLevelFields.Contains(property.Name))
                {
                    Report(null, property.Name, null, "unknown top-level field");
                }
            }

            string? schema = ReadString(root, SchemaField);
            if (schema != AnimationPackManifest.Schema)
            {
                Report(null, SchemaField, null, $"schema must be '{AnimationPackManifest.Schema}', found '{schema ?? "<missing>"}'");
            }

            if (string.IsNullOrWhiteSpace(packID))
            {
                Report(null, IDField, null, "pack id is missing or blank");
            }
            else if (!IdentifierPattern().IsMatch(packID))
            {
                Report(null, IDField, null, $"pack id '{packID}' must be a lowercase identifier");
            }

            List<AnimationEntry> entries = [];
            if (!root.TryGetProperty(EntriesField, out JsonElement entriesElement) || entriesElement.ValueKind != JsonValueKind.Array)
            {
                Report(null, EntriesField, null, "entries must be an array");
            }
            else
            {
                HashSet<(AnimationCategory, string)> seen = [];
                int index = 0;
                foreach (JsonElement element in entriesElement.EnumerateArray())
                {
                    AnimationEntry? entry = ReadEntry(element, index, pack, context, resources, Report);
                    if (entry is not null)
                    {
                        if (!seen.Add((entry.Category, entry.ID)))
                        {
                            Report(entry.ID, IDField, null, $"duplicate {entry.Category.ToName()} entry id '{entry.ID}' within the pack");
                        }
                        else
                        {
                            entries.Add(entry);
                        }
                    }

                    index++;
                }
            }

            return diagnostics.Count == 0
                ? (new AnimationPackManifest(packID!, manifestPath, context, entries.AsReadOnly()), diagnostics)
                : (null, diagnostics.AsReadOnly());
        }
    }

    private static AnimationEntry? ReadEntry(
        JsonElement element,
        int index,
        string pack,
        ContentContext context,
        IAnimationPackResources resources,
        Action<string?, string?, string?, string> report)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            report($"#{index}", null, null, "entry must be a JSON object");
            return null;
        }

        string? id = ReadString(element, IDField);
        string label = string.IsNullOrWhiteSpace(id) ? $"#{index}" : id;
        int errors = 0;

        void Fail(string? field, string? path, string message)
        {
            errors++;
            report(label, field, path, message);
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            Fail(IDField, null, "entry id is missing or blank");
        }
        else if (!IdentifierPattern().IsMatch(id))
        {
            Fail(IDField, null, $"entry id '{id}' must be a lowercase identifier");
        }

        string? categoryName = ReadString(element, CategoryField);
        bool hasCategory = AnimationCategoryNames.TryParse(categoryName, out AnimationCategory category);
        if (!hasCategory)
        {
            Fail(CategoryField, null,
                $"category must be '{AnimationCategoryNames.Idle}' or '{AnimationCategoryNames.Locomotion}', found '{categoryName ?? "<missing>"}'");
        }

        HashSet<string>? allowed = hasCategory ? (category == AnimationCategory.Idle ? _idleFields : _locomotionFields) : null;
        foreach (JsonProperty property in element.EnumerateObject())
        {
            bool known = allowed?.Contains(property.Name)
                ?? (_idleFields.Contains(property.Name) || _locomotionFields.Contains(property.Name));
            if (!known)
            {
                Fail(property.Name, null, "unknown entry field");
            }
        }

        List<string> tags = [];
        if (!element.TryGetProperty(TagsField, out JsonElement tagsElement) || tagsElement.ValueKind != JsonValueKind.Array)
        {
            Fail(TagsField, null, "tags must be a non-empty array");
        }
        else
        {
            foreach (JsonElement tag in tagsElement.EnumerateArray())
            {
                string? value = tag.ValueKind == JsonValueKind.String ? tag.GetString() : null;
                if (string.IsNullOrWhiteSpace(value) || !TagPattern().IsMatch(value))
                {
                    Fail(TagsField, null, $"tag '{value ?? tag.ToString()}' must be a short lowercase word");
                }
                else
                {
                    tags.Add(value);
                }
            }

            if (tagsElement.GetArrayLength() == 0)
            {
                Fail(TagsField, null, "tags must be a non-empty array");
            }
        }

        string? description = ReadString(element, DescriptionField);
        if (string.IsNullOrWhiteSpace(description))
        {
            Fail(DescriptionField, null, "description is missing or blank");
        }

        string resolved = string.Empty;
        string? authored = null;
        if (hasCategory)
        {
            string field = category == AnimationCategory.Idle ? ClipField : SetField;
            authored = ReadString(element, field);
            if (string.IsNullOrWhiteSpace(authored))
            {
                Fail(field, null, $"{field} is missing or blank");
            }
            else if (TryResolvePath(authored, context, out resolved) is { } pathError)
            {
                Fail(field, authored, pathError);
            }
            else if (!resources.Exists(resolved))
            {
                Fail(field, authored, $"file '{resolved}' does not exist");
            }
            else if ((category == AnimationCategory.Idle ? resources.CheckClip(resolved) : resources.CheckReviewedSet(resolved)) is { } resourceError)
            {
                Fail(field, authored, resourceError);
            }
        }

        return errors > 0 || !hasCategory
            ? null
            : new AnimationEntry(id!, category, tags.AsReadOnly(), description!, pack, authored!, resolved);
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
