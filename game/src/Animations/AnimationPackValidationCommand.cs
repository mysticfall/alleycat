using System.Text.Json;
using AlleyCat.Core.Content;
using Godot;

namespace AlleyCat.Animations;

/// <summary>
/// Godot entry for the headless animation pack validator command
/// (<c>tools/animation-pipeline/godot/commands/validate_animation_pack.gd</c>). It parses the command arguments, loads
/// character templates, runs <see cref="AnimationPackValidation" /> with the production resource checks and prints the
/// report. It never modifies authored files.
/// </summary>
/// <remarks>
/// Arguments: one or more <c>--pack=&lt;res:// manifest&gt;</c>; optional <c>--content-root=&lt;res:// directory&gt;</c>
/// to resolve every <c>--pack</c> against a content-pack context rooted there instead of the production location
/// rule; repeatable <c>--template=&lt;res:// character template scene&gt;</c>; <c>--priority=&lt;int&gt;</c> for packs
/// that a template does not reference; and <c>--require-compatible</c> to fail on incompatible entries.
/// </remarks>
[GlobalClass]
public partial class AnimationPackValidationCommand : RefCounted
{
    /// <summary>The prefix of the machine-readable summary line.</summary>
    public const string SummaryPrefix = "ANIMATION_PACK_VALIDATION_JSON=";

    /// <summary>Runs the command and returns its process exit code.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "The external GDScript command calls this on a script instance.")]
    public int Run(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        (AnimationPackValidationReport? report, string? usage) = Execute(arguments);
        if (report is null)
        {
            GD.PrintErr($"ANIMATION_PACK_VALIDATION_ERROR: {usage}");
            return 2;
        }

        foreach (string finding in report.Findings)
        {
            GD.Print(finding);
        }

        foreach (string error in report.Errors)
        {
            GD.PrintErr($"ANIMATION_PACK_VALIDATION_ERROR: {error}");
        }

        bool requireCompatible = arguments.Contains("--require-compatible");
        bool passed = report.IsValid && (!requireCompatible || report.IncompatibleEntries.Count == 0);
        GD.Print(SummaryPrefix + JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["valid"] = report.IsValid,
            ["passed"] = passed,
            ["errors"] = report.Errors,
            ["incompatible"] = report.IncompatibleEntries
        }));
        return passed ? 0 : 1;
    }

    /// <summary>Parses arguments and validates; returns a usage message instead of a report for bad arguments.</summary>
    public static (AnimationPackValidationReport? Report, string? Usage) Execute(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        List<string> manifests = [];
        List<string> templateScenes = [];
        string? contentRoot = null;
        int? priority = null;
        foreach (string argument in arguments)
        {
            if (TryValue(argument, "--pack=", out string pack))
            {
                manifests.Add(pack);
            }
            else if (TryValue(argument, "--template=", out string template))
            {
                templateScenes.Add(template);
            }
            else if (TryValue(argument, "--content-root=", out string root))
            {
                contentRoot = root.EndsWith('/') ? root : root + "/";
            }
            else if (TryValue(argument, "--priority=", out string value))
            {
                if (!int.TryParse(value, out int parsed))
                {
                    return (null, $"--priority must be an integer, found '{value}'.");
                }

                priority = parsed;
            }
            else if (argument != "--require-compatible")
            {
                return (null, $"unknown argument '{argument}'.");
            }
        }

        if (manifests.Count == 0)
        {
            return (null, "at least one --pack=<res:// pack.json> is required.");
        }

        AnimationPackSource[] packs = [.. manifests.Select(manifest => new AnimationPackSource(
            manifest,
            contentRoot is null
                ? AnimationPackLoader.DeriveContext(manifest)
                : new ContentContext(contentRoot.TrimEnd('/').Split('/')[^1], contentRoot),
            priority))];

        List<AnimationTemplateTarget> templates = [];
        List<string> templateErrors = [];
        foreach (string scene in templateScenes)
        {
            if (LoadTemplate(scene, out string? error) is { } target)
            {
                templates.Add(target);
            }
            else
            {
                templateErrors.Add(error!);
            }
        }

        AnimationPackValidationReport report = AnimationPackValidation.Validate(packs, GodotAnimationPackResources.Instance, templates);
        return templateErrors.Count == 0
            ? (report, null)
            : (report with
            {
                Errors = [.. templateErrors, .. report.Errors]
            }, null);
    }

    private static AnimationTemplateTarget? LoadTemplate(string scenePath, out string? error)
    {
        error = null;
        if (ResourceLoader.Load<PackedScene>(scenePath) is not { } scene)
        {
            error = $"Template '{scenePath}' is not a loadable scene.";
            return null;
        }

        Node root = scene.Instantiate();
        try
        {
            AnimationCatalogue catalogue;
            try
            {
                catalogue = AnimationCatalogue.RequireFor(root);
            }
            catch (InvalidOperationException failure)
            {
                error = $"Template '{scenePath}': {failure.Message}";
                return null;
            }

            Skeleton3D[] skeletons = [.. root.FindChildren("*", nameof(Skeleton3D), true, false).OfType<Skeleton3D>()];
            if (skeletons.Length != 1)
            {
                error = $"Template '{scenePath}' must contain exactly one native Skeleton3D before installation, found {skeletons.Length}.";
                return null;
            }

            HashSet<string> bones = [.. Enumerable.Range(0, skeletons[0].GetBoneCount()).Select(skeletons[0].GetBoneName)];
            AnimationPackSource[] packs = [.. catalogue.GetChildren().OfType<AnimationPack>()
                .Where(pack => pack.Enabled)
                .Select(pack => new AnimationPackSource(pack.Manifest, pack.ResolveContext(), pack.Priority))];
            return new AnimationTemplateTarget(scenePath, packs, skeletons[0].Name, bones.Contains, ReadClipTracks);
        }
        finally
        {
            root.Free();
        }
    }

    private static IEnumerable<(string SkeletonNode, string Bone)> ReadClipTracks(string clipPath)
        => ResourceLoader.Load<Animation>(clipPath) is { } clip ? AnimationCatalogue.ReadBoneTracks(clip) : [];

    private static bool TryValue(string argument, string prefix, out string value)
    {
        bool matches = argument.StartsWith(prefix, StringComparison.Ordinal);
        value = matches ? argument[prefix.Length..] : string.Empty;
        return matches;
    }
}
