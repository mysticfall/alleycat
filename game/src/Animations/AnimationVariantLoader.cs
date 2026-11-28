using System.Text.Json;
using System.Text.RegularExpressions;
using AlleyCat.Core.Content;
using Godot;

namespace AlleyCat.Animations;

internal static partial class AnimationVariantLoader
{
    [GeneratedRegex(@"\A[a-z][a-z0-9_]*\z")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(@"\A[a-z][a-z0-9_-]*\z")]
    private static partial Regex TagPattern();

    internal static bool IsIdentifier(string id) => IdentifierPattern().IsMatch(id);

    internal static void RequireTemplateOwners(PackedScene template, ContentContext active)
    {
        _ = RequireOwner(template.ResourcePath, active);
        for (SceneState? state = template.GetState().GetBaseSceneState(); state is not null; state = state.GetBaseSceneState())
        {
            string inheritedPath = state.GetPath();
            if (inheritedPath.Length != 0)
            {
                _ = RequireOwner(inheritedPath, active);
            }
        }
    }

    internal static ContentContext RequireOwner(string path, ContentContext active)
    {
        if (!path.StartsWith("res://", StringComparison.Ordinal) || !IsRelativeFile(path[6..]))
        {
            throw new InvalidOperationException($"Animation variant declaration or descriptor requires a canonical res:// file path: '{path}'.");
        }

        ContentContext owner = ContentContext.Default;
        if (path.StartsWith(ContentPaths.ContentRoot, StringComparison.Ordinal))
        {
            string[] parts = path[ContentPaths.ContentRoot.Length..].Split('/');
            if (parts.Length < 2 || active == ContentContext.Default || !string.Equals(parts[0], active.ContentID, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Animation variant '{path}' is not owned by active content '{active.ContentID}'.");
            }

            owner = active;
        }

        RequirePhysicalOwner(ProjectSettings.GlobalizePath(path), owner, active);
        return owner;
    }

    internal static AnimationEntry Load(string path, ContentContext active, IAnimationVariantResources resources)
    {
        ContentContext owner = RequireOwner(path, active);
        string? id = null;
        string? categoryName = null;
        try
        {
            RequireRuntimeLocation(path, owner);
            using var document = JsonDocument.Parse(resources.ReadText(path));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("schema", "descriptor must be a JSON object");
            }

            if (ReadString(root, "schema") != "anim003_variant_v1")
            {
                throw Invalid("schema", "must be anim003_variant_v1");
            }

            categoryName = ReadString(root, "category");
            if (!AnimationCategoryNames.TryParse(categoryName, out AnimationCategory category))
            {
                throw Invalid("category", "must be idle or locomotion");
            }

            id = ReadString(root, "id");
            if (id is null || !IsIdentifier(id))
            {
                throw Invalid("id", "must be a lowercase identifier");
            }

            string payloadField = category == AnimationCategory.Idle ? "clip" : "set";
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty field in root.EnumerateObject())
            {
                if ((field.Name is not ("schema" or "category" or "id" or "tags" or "description") && field.Name != payloadField)
                    || !fields.Add(field.Name))
                {
                    throw Invalid(field.Name, "unknown, wrong-category or duplicate field");
                }
            }

            if (!root.TryGetProperty("tags", out JsonElement rawTags)
                || rawTags.ValueKind != JsonValueKind.Array || rawTags.GetArrayLength() == 0)
            {
                throw Invalid("tags", "must be a non-empty array of lowercase words");
            }

            List<string> tags = [];
            foreach (JsonElement tag in rawTags.EnumerateArray())
            {
                if (tag.ValueKind != JsonValueKind.String || tag.GetString() is not { } value || !TagPattern().IsMatch(value))
                {
                    throw Invalid("tags", "must be lowercase words");
                }

                tags.Add(value);
            }

            string? description = ReadString(root, "description");
            if (string.IsNullOrWhiteSpace(description))
            {
                throw Invalid("description", "must not be blank");
            }

            string? authored = ReadString(root, payloadField);
            if (authored is null || !IsRelativeFile(authored))
            {
                throw Invalid(payloadField, "must be an owner-root-relative file path without parent traversal");
            }

            string payload = owner.RootPath + authored;
            if (owner == ContentContext.Default && payload.StartsWith(ContentPaths.ContentRoot, StringComparison.Ordinal))
            {
                throw Invalid(payloadField, $"'{payload}' is outside the base owner's content namespace");
            }

            try
            {
                _ = RequireOwner(payload, active);
            }
            catch (InvalidOperationException error)
            {
                throw Invalid(payloadField, error.Message);
            }

            if (DirectoryOf(path) != DirectoryOf(payload))
            {
                throw Invalid(payloadField, "must be adjacent to its descriptor");
            }

            RequireRuntimeLocation(payload, owner);
            if (!resources.Exists(payload))
            {
                throw Invalid(payloadField, $"'{payload}' does not exist");
            }

            if (category == AnimationCategory.Locomotion)
            {
                try
                {
                    RequireReviewedCompanionOwners(payload, owner, active, resources);
                }
                catch (Exception error) when (error is InvalidOperationException or JsonException or IOException)
                {
                    throw Invalid(payloadField, error.Message);
                }
            }

            string? resourceError = category == AnimationCategory.Idle ? resources.CheckClip(payload) : resources.CheckReviewedSet(payload);
            return resourceError is not null
                ? throw Invalid(payloadField, resourceError)
                : new AnimationEntry(id, category, tags.AsReadOnly(), description, owner.ContentID, authored, payload)
                {
                    DescriptorPath = path
                };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw Invalid("descriptor/payload", exception.Message);
        }

        InvalidOperationException Invalid(string field, string reason)
        {
            return new($"Animation variant '{path}' (owner '{owner.ContentID}', {categoryName ?? "?"}/{id ?? "?"}), field '{field}': {reason}.");
        }
    }

    private static void RequireReviewedCompanionOwners(
        string payload, ContentContext owner, ContentContext active, IAnimationVariantResources resources)
    {
        string directory = Path.GetDirectoryName(ProjectSettings.GlobalizePath(payload))!;
        using var set = JsonDocument.Parse(resources.ReadText(payload));
        if (set.RootElement.ValueKind == JsonValueKind.Object && set.RootElement.TryGetProperty("roles", out JsonElement roles)
            && roles.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement role in roles.EnumerateArray())
            {
                if (role.ValueKind == JsonValueKind.Object && ReadString(role, "source") is { } source)
                {
                    // Ownership only. ANIM-002 still checks its own adjacency, hashes and role schema.
                    RequirePhysicalOwner(Path.GetFullPath(Path.Combine(directory, source)), owner, active);
                }
            }
        }

        RequirePhysicalOwner(Path.Combine(directory, "turn-angular-reference.json"), owner, active);
    }

    private static void RequireRuntimeLocation(string path, ContentContext owner)
    {
        if (path[owner.RootPath.Length..].Split('/').SkipLast(1).Any(part => part is "source" or "authoring"))
        {
            throw new InvalidOperationException($"Animation variant '{path}' (owner '{owner.ContentID}') is inside an authoring directory.");
        }
    }

    private static void RequirePhysicalOwner(string path, ContentContext owner, ContentContext active)
    {
        string target = ResolvePhysicalPath(path);
        string root = ResolvePhysicalPath(ProjectSettings.GlobalizePath(owner.RootPath));
        bool outside = !IsWithin(root, target);
        if (owner == ContentContext.Default)
        {
            outside |= IsWithin(ResolvePhysicalPath(ProjectSettings.GlobalizePath(ContentPaths.ContentRoot)), target);
            if (active != ContentContext.Default)
            {
                outside |= IsWithin(ResolvePhysicalPath(ProjectSettings.GlobalizePath(active.RootPath)), target);
            }
        }

        if (outside)
        {
            throw new InvalidOperationException($"Animation variant file '{path}' resolves outside owner '{owner.ContentID}' ({owner.RootPath}).");
        }
    }

    private static bool IsWithin(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string ResolvePhysicalPath(string path, int remainingLinks = 40)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full)!;
        string current = root;
        foreach (string segment in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: false) is { } link)
            {
                current = remainingLinks > 0
                    ? ResolvePhysicalPath(link.FullName, remainingLinks - 1)
                    : throw new InvalidOperationException($"Animation variant path '{path}' contains too many symbolic links.");
            }
        }

        return current;
    }

    private static bool IsRelativeFile(string value)
        => !string.IsNullOrWhiteSpace(value) && !value.Contains('\\') && !value.Contains(':') && !value.Any(char.IsControl)
            && value.Split('/').All(part => part is not ("" or "." or ".."));

    private static string DirectoryOf(string path) => path[..path.LastIndexOf('/')];

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
