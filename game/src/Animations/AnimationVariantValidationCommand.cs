using System.Text.Json;
using AlleyCat.Core.Content;
using Godot;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlleyCat.Animations;

/// <summary>Read-only ANIM-003 descriptor and template validation, exposed to the pipeline command.</summary>
[GlobalClass]
public partial class AnimationVariantValidationCommand : RefCounted
{
    /// <summary>Runs the validator protocol and returns the process exit code.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "The external GDScript command calls this on a script instance.")]
    public int Run(string[] arguments)
    {
        (AnimationVariantValidationReport? report, string? usage) = Execute(arguments);
        if (report is null)
        {
            GD.PrintErr($"ANIMATION_VARIANT_VALIDATION_ERROR: {usage}");
            return 2;
        }

        foreach (string line in report.Findings)
        {
            GD.Print(line);
        }

        foreach (string error in report.Errors)
        {
            GD.PrintErr($"ANIMATION_VARIANT_VALIDATION_ERROR: {error}");
        }

        bool passed = report.IsValid && (!arguments.Contains("--require-compatible")
            || (report.IncompatibleEntries.Count == 0 && report.InvalidDefaults.Count == 0));
        GD.Print("ANIMATION_VARIANT_VALIDATION_JSON=" + JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["valid"] = report.IsValid,
            ["passed"] = passed,
            ["errors"] = report.Errors,
            ["incompatible"] = report.IncompatibleEntries,
            ["invalid_defaults"] = report.InvalidDefaults
        }));
        return passed ? 0 : 1;
    }

    internal static (AnimationVariantValidationReport? Report, string? Usage) Execute(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        List<string> descriptors = [];
        List<string> templates = [];
        foreach (string argument in arguments)
        {
            if (argument.StartsWith("--variant=", StringComparison.Ordinal) && argument.Length > 10)
            {
                descriptors.Add(argument[10..]);
            }
            else if (argument.StartsWith("--template=", StringComparison.Ordinal) && argument.Length > 11)
            {
                templates.Add(argument[11..]);
            }
            else if (argument != "--require-compatible")
            {
                return (null, $"unknown or blank argument '{argument}'");
            }
        }

        if (descriptors.Count == 0 && templates.Count == 0)
        {
            return (null, "at least one --variant=res://... or --template=res://... is required");
        }

        List<string> errors = [];
        List<string> findings = [];
        List<string> incompatible = [];
        List<string> defaults = [];
        var resolver = new ContentResolver();
        ContentContext active;
        try
        {
            active = resolver.GetCurrentContentContext();
        }
        catch (InvalidOperationException error)
        {
            return (new AnimationVariantValidationReport([error.Message], [], [], []), null);
        }

        foreach (string path in descriptors)
        {
            try
            {
                AnimationEntry entry = AnimationVariantLoader.Load(path, active, GodotAnimationVariantResources.Instance);
                findings.Add($"{entry}: '{path}' binds '{entry.ResourcePath}'.");
            }
            catch (Exception error) when (IsValidationError(error))
            {
                errors.Add($"Descriptor '{path}': {error.Message}");
            }
        }

        foreach (string path in templates)
        {
            try
            {
                ValidateTemplate(path, active, findings, incompatible, defaults);
            }
            catch (Exception error) when (IsValidationError(error))
            {
                errors.Add($"Template '{path}': {error.Message}");
            }
        }

        return (new AnimationVariantValidationReport(errors.AsReadOnly(), findings.AsReadOnly(), incompatible.AsReadOnly(), defaults.AsReadOnly()), null);
    }

    private static bool IsValidationError(Exception error)
        => error is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException;

    private static void ValidateTemplate(string path, ContentContext active, List<string> findings, List<string> incompatible, List<string> defaults)
    {
        _ = AnimationVariantLoader.RequireOwner(path, active);
        PackedScene template = ResourceLoader.Load<PackedScene>(path)
            ?? throw new InvalidOperationException("not a loadable character template");
        AnimationVariantLoader.RequireTemplateOwners(template, active);
        Node root = template.Instantiate();
        try
        {
            var catalogue = AnimationCatalogue.RequireFor(root);
            Skeleton3D[] skeletons = [.. root.FindChildren("*", nameof(Skeleton3D), true, false).OfType<Skeleton3D>()];
            if (skeletons.Length != 1)
            {
                throw new InvalidOperationException($"expected one native template skeleton, found {skeletons.Length}");
            }

            Skeleton3D skeleton = skeletons[0];
            if (root is Character.Character { MotionModel.Length: > 0 } actor)
            {
                // Use the same presentation mount as installed characters, without entering the tree or starting models.
                using GodotObject mount = GD.Load<GDScript>("res://src/Motion/native_soma_mount.gd").New().AsGodotObject();
                if (!mount.Call("install", actor, skeleton, actor.MotionModel, actor is Character.PlayerCharacter).AsBool())
                {
                    throw new InvalidOperationException("could not mount the template's MotionBricks presentation rig");
                }
            }

            catalogue.Build(skeleton, active, NullLogger.Instance, template);
            foreach (AnimationEntry entry in catalogue.Entries)
            {
                AnimationCategoryView view = entry.Category == AnimationCategory.Idle ? catalogue.Idle : catalogue.Locomotion;
                if (view.Find(entry.ID) is null)
                {
                    string failure = $"Template '{path}': {entry} ('{entry.DescriptorPath}') is incompatible with '{skeleton.Name}'.";
                    incompatible.Add(failure);
                    findings.Add(failure);
                }
                else
                {
                    findings.Add($"Template '{path}': {entry} binds '{entry.ResourcePath}' on '{skeleton.Name}'.");
                }
            }

            CheckDefault(catalogue.Idle);
            CheckDefault(catalogue.Locomotion);

            void CheckDefault(AnimationCategoryView view)
            {
                if (view.TemplateDefault is { } id && view.Find(id) is null)
                {
                    string failure = $"Template '{path}': default {view.Category.ToName()}/{id} is unknown or incompatible; uses compatible fallback.";
                    defaults.Add(failure);
                    findings.Add(failure);
                }
            }
        }
        finally
        {
            root.Free();
        }
    }
}

internal sealed record AnimationVariantValidationReport(IReadOnlyList<string> Errors, IReadOnlyList<string> Findings,
    IReadOnlyList<string> IncompatibleEntries, IReadOnlyList<string> InvalidDefaults)
{
    public bool IsValid => Errors.Count == 0;
}
