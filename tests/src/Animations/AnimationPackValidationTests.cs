using AlleyCat.Animations;
using AlleyCat.Core.Content;
using Xunit;

namespace AlleyCat.Tests.Animations;

/// <summary>
/// The validator command's report (pack-format.md Validator items 1–4): manifest findings come from the single loader
/// validation, and a template adds layering collisions, duplicate pack ids and per-entry skeleton binding.
/// </summary>
public sealed class AnimationPackValidationTests
{
    private const string BasePack = "res://data/animations/base/pack.json";
    private const string AddonPack = "res://data/animations/addon/pack.json";

    private static readonly HashSet<string> _femaleBones = ["Root", "Hips", "LeftFoot", "breast_l"];
    private static readonly HashSet<string> _maleBones = ["Root", "Hips", "LeftFoot"];

    private static FakePackResources Resources(string addonID = "addon")
    {
        var resources = new FakePackResources();
        foreach (string file in new[] { "res://clips/calm.tres", "res://clips/slump.tres", "res://clips/odd.tres", "res://sets/walk.json" })
        {
            _ = resources.Files.Add(file);
        }

        resources.Texts[BasePack] = Pack("base",
            Idle("calm", "clips/calm.tres"),
            /*lang=json,strict*/ "{ \"id\": \"walk\", \"category\": \"locomotion\", \"tags\": [\"easy\"], \"description\": \"walks\", \"set\": \"sets/walk.json\" }");
        resources.Texts[AddonPack] = Pack(addonID, Idle("calm", "clips/slump.tres"), Idle("odd", "clips/odd.tres"));
        return resources;
    }

    private static string Idle(string id, string clip)
        => $"{{ \"id\": \"{id}\", \"category\": \"idle\", \"tags\": [\"t\"], \"description\": \"stands\", \"clip\": \"{clip}\" }}";

    private static string Pack(string id, params string[] entries)
        => $"{{ \"schema\": \"anim003_pack_v1\", \"id\": \"{id}\", \"entries\": [ {string.Join(", ", entries)} ] }}";

    // calm and slump bind on any reference skeleton; odd carries a SOMA-mapped LeftFoot track bound to another node.
    private static IEnumerable<(string, string)> Tracks(string clip) => clip switch
    {
        "res://clips/odd.tres" => [("%GeneralSkeleton", "Hips"), ("Armature", "LeftFoot")],
        _ => [("%GeneralSkeleton", "Hips"), ("%GeneralSkeleton", "LeftFoot"), ("%GeneralSkeleton", "breast_l")]
    };

    private static AnimationTemplateTarget Template(string name, HashSet<string> bones, params AnimationPackSource[] packs)
        => new(name, packs, "GeneralSkeleton", bones.Contains, Tracks);

    /// <summary>Packs alone report validity, and invalid packs report the loader's located diagnostics.</summary>
    [Fact]
    public void Validate_PacksWithoutTemplate_ReportLoaderFindings()
    {
        FakePackResources resources = Resources();
        resources.ClipErrors["res://clips/odd.tres"] = "clip must be a loadable native Animation resource, found Curve";

        AnimationPackValidationReport report = AnimationPackValidation.Validate(
            [new AnimationPackSource(BasePack, ContentContext.Default), new AnimationPackSource(AddonPack, ContentContext.Default)],
            resources, []);

        Assert.False(report.IsValid);
        Assert.Equal("Animation pack 'base' (res://data/animations/base/pack.json) is valid: 2 entries.", Assert.Single(report.Findings));
        Assert.Equal(
            "Animation pack 'addon', entry 'odd', field 'clip', path 'clips/odd.tres': clip must be a loadable native Animation resource, found Curve",
            Assert.Single(report.Errors));
    }

    /// <summary>
    /// A pack the template does not reference applies above the template's packs, so its entry overrides with the
    /// composer's log line, and per-entry binding is reported for the template skeleton.
    /// </summary>
    [Fact]
    public void Validate_AgainstTemplate_ReportsOverridesAndBinding()
    {
        AnimationPackValidationReport report = AnimationPackValidation.Validate(
            [new AnimationPackSource(AddonPack, ContentContext.Default)],
            Resources(),
            [Template("male", _maleBones, new AnimationPackSource(BasePack, ContentContext.Default, 10))]);

        Assert.True(report.IsValid);
        Assert.Contains("Template 'male': Animation catalogue: idle entry 'calm' from pack 'addon' overrides pack 'base'.", report.Findings);
        Assert.Contains("Template 'male': idle/calm (pack 'addon') binds to skeleton 'GeneralSkeleton'.", report.Findings);
        Assert.Contains(report.Findings, finding => finding.StartsWith("Template 'male': locomotion/walk (pack 'base') is accepted", StringComparison.Ordinal));
        Assert.Equal("male: idle/odd (pack 'addon')", Assert.Single(report.IncompatibleEntries));
        Assert.Contains("Template 'male': idle/odd (pack 'addon') is incompatible with skeleton 'GeneralSkeleton'; unresolved SOMA-mapped tracks: Armature:LeftFoot.",
            report.Findings);
    }

    /// <summary>Equal priorities apply in ordinal pack-id order, so the later id overrides with the same log line.</summary>
    [Fact]
    public void Validate_EqualPriorities_OrderByPackID()
    {
        AnimationPackValidationReport report = AnimationPackValidation.Validate(
            [new AnimationPackSource(AddonPack, ContentContext.Default, 0)],
            Resources(addonID: "aardvark"),
            [Template("female", _femaleBones, new AnimationPackSource(BasePack, ContentContext.Default, 0))]);

        Assert.Contains("Template 'female': Animation catalogue: idle entry 'calm' from pack 'base' overrides pack 'aardvark'.", report.Findings);
    }

    /// <summary>A submitted pack that the template already references keeps the template node's priority.</summary>
    [Fact]
    public void Validate_TemplateReferencedPack_KeepsTemplatePriority()
    {
        AnimationPackValidationReport report = AnimationPackValidation.Validate(
            [new AnimationPackSource(AddonPack, ContentContext.Default, 99)],
            Resources(),
            [Template("female", _femaleBones,
                new AnimationPackSource(AddonPack, ContentContext.Default, 0),
                new AnimationPackSource(BasePack, ContentContext.Default, 5))]);

        Assert.Contains("Template 'female': Animation catalogue: idle entry 'calm' from pack 'base' overrides pack 'addon'.", report.Findings);
        Assert.DoesNotContain(report.IncompatibleEntries, entry => entry.Contains("calm", StringComparison.Ordinal));
    }

    /// <summary>A pack id already used by a template pack fails as a duplicate, as catalogue construction does.</summary>
    [Fact]
    public void Validate_DuplicatePackID_IsAnError()
    {
        AnimationPackValidationReport report = AnimationPackValidation.Validate(
            [new AnimationPackSource(AddonPack, ContentContext.Default)],
            Resources(addonID: "base"),
            [Template("female", _femaleBones, new AnimationPackSource(BasePack, ContentContext.Default, 0))]);

        Assert.False(report.IsValid);
        string error = Assert.Single(report.Errors);
        Assert.StartsWith("Template 'female': Animation catalogue has more than one enabled pack with id 'base'", error, StringComparison.Ordinal);
    }
}
