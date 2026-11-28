using AlleyCat.Core.Content;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Animations;

/// <summary>
/// One pack submitted to <see cref="AnimationPackValidation" />.
/// </summary>
/// <param name="ManifestPath">The resource path of the <c>pack.json</c> manifest.</param>
/// <param name="Context">The content context its relative paths resolve against.</param>
/// <param name="Priority">
/// Application priority when composed with a template, or <see langword="null" /> to take the priority of the template
/// pack node referencing the same manifest (or to apply above every template pack when none does).
/// </param>
public sealed record AnimationPackSource(string ManifestPath, ContentContext Context, int? Priority = null);

/// <summary>
/// A character template to report compatibility and layering against (pack-format.md Validator item 3).
/// </summary>
/// <param name="Name">The template's display name, normally its scene path.</param>
/// <param name="Packs">The template's enabled pack nodes as sources with explicit priorities.</param>
/// <param name="SkeletonName">The node name of the template's native skeleton.</param>
/// <param name="HasBone">Whether the native skeleton has a bone with the given name.</param>
/// <param name="ReadBoneTracks">Reads a clip's bone tracks as (skeleton node name, bone name) pairs.</param>
public sealed record AnimationTemplateTarget(
    string Name,
    IReadOnlyList<AnimationPackSource> Packs,
    string SkeletonName,
    Func<string, bool> HasBone,
    Func<string, IEnumerable<(string SkeletonNode, string Bone)>> ReadBoneTracks);

/// <summary>
/// The outcome of validating packs, optionally against character templates.
/// </summary>
/// <param name="Errors">Validation failures; any error makes the packs invalid.</param>
/// <param name="Findings">Layering and compatibility findings, one line each.</param>
/// <param name="IncompatibleEntries">Entries a template's skeleton cannot bind, as <c>template: category/id</c>.</param>
public sealed record AnimationPackValidationReport(
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Findings,
    IReadOnlyList<string> IncompatibleEntries)
{
    /// <summary>Whether every pack passed validation.</summary>
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// The pack validation behind the headless validator command. Manifest validation is the single loader validation
/// (<see cref="AnimationPackLoader.Validate(string, ContentContext, IAnimationPackResources)" />), layering is the
/// catalogue composer, and skeleton binding is <see cref="IdleClipCompatibility" />, so the command reports exactly
/// what catalogue loading would do. Nothing here writes authored files.
/// </summary>
public static class AnimationPackValidation
{
    /// <summary>
    /// Validates every pack and, for each template, reports layering collisions, duplicate pack ids and per-entry
    /// skeleton-binding compatibility.
    /// </summary>
    public static AnimationPackValidationReport Validate(
        IReadOnlyList<AnimationPackSource> packs,
        IAnimationPackResources resources,
        IReadOnlyList<AnimationTemplateTarget> templates)
    {
        ArgumentNullException.ThrowIfNull(packs);
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(templates);

        List<string> errors = [];
        List<string> findings = [];
        List<string> incompatible = [];
        Dictionary<string, AnimationPackManifest?> manifests = new(StringComparer.Ordinal);

        AnimationPackManifest? Load(AnimationPackSource source)
        {
            if (manifests.TryGetValue(source.ManifestPath, out AnimationPackManifest? cached))
            {
                return cached;
            }

            (AnimationPackManifest? manifest, IReadOnlyList<AnimationPackDiagnostic> diagnostics) =
                AnimationPackLoader.Validate(source.ManifestPath, source.Context, resources);
            errors.AddRange(diagnostics.Select(diagnostic => diagnostic.ToString()));
            manifests[source.ManifestPath] = manifest;
            return manifest;
        }

        foreach (AnimationPackSource pack in packs)
        {
            if (Load(pack) is { } manifest)
            {
                findings.Add($"Animation pack '{manifest.ID}' ({manifest.ManifestPath}) is valid: {manifest.Entries.Count} entr{(manifest.Entries.Count == 1 ? "y" : "ies")}.");
            }
        }

        foreach (AnimationTemplateTarget template in templates)
        {
            List<AnimationPackLayer> layers = [];
            foreach (AnimationPackSource source in template.Packs)
            {
                if (Load(source) is { } manifest)
                {
                    layers.Add(new AnimationPackLayer(manifest, source.Priority ?? 0));
                }
            }

            int top = template.Packs.Count == 0 ? 0 : template.Packs.Max(source => source.Priority ?? 0) + 1;
            foreach (AnimationPackSource pack in packs)
            {
                if (template.Packs.Any(source => source.ManifestPath == pack.ManifestPath) || Load(pack) is not { } manifest)
                {
                    continue;
                }

                layers.Add(new AnimationPackLayer(manifest, pack.Priority ?? top));
            }

            var logger = new FindingLogger(template.Name, findings);
            IReadOnlyList<AnimationEntry> entries;
            try
            {
                entries = AnimationCatalogueComposer.Compose(layers, logger);
            }
            catch (InvalidOperationException error)
            {
                errors.Add($"Template '{template.Name}': {error.Message}");
                continue;
            }

            foreach (AnimationEntry entry in entries)
            {
                if (entry.Category == AnimationCategory.Locomotion)
                {
                    findings.Add($"Template '{template.Name}': {entry} is accepted by the ANIM-002 set loader; presentation is checked when the character installs its MotionBricks rig.");
                    continue;
                }

                IReadOnlyList<string> unresolved = IdleClipCompatibility.FindUnresolvedMappedTracks(
                    template.ReadBoneTracks(entry.ResourcePath), template.SkeletonName, template.HasBone);
                if (unresolved.Count == 0)
                {
                    findings.Add($"Template '{template.Name}': {entry} binds to skeleton '{template.SkeletonName}'.");
                }
                else
                {
                    incompatible.Add($"{template.Name}: {entry}");
                    findings.Add($"Template '{template.Name}': {entry} is incompatible with skeleton '{template.SkeletonName}'; unresolved SOMA-mapped tracks: {string.Join(", ", unresolved)}.");
                }
            }
        }

        return new AnimationPackValidationReport(errors.AsReadOnly(), findings.AsReadOnly(), incompatible.AsReadOnly());
    }

    /// <summary>Records the composer's override messages as template findings.</summary>
    private sealed class FindingLogger(string template, List<string> findings) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                findings.Add($"Template '{template}': {formatter(state, exception)}.");
            }
        }
    }
}
