using System.Text.Json;
using AlleyCat.Animations;
using Godot;

namespace AlleyCat.IntegrationTests.Animations;

internal static class AnimationFixtureVariants
{
    public const string Sad = "res://assets/animations/idle/sad/variant.json";
    public const string Feminine = "res://assets/animations/locomotion/feminine/variant.json";
    public const string Slump = "res://tests/animation/catalogue/fixture/idle/variant.json";

    // The alternate selection uses identical reviewed data. Materialise its owner-adjacent files in scratch rather
    // than duplicating a protected export in the repository or permitting a cross-directory descriptor shortcut.
    private static readonly Lazy<string> _stroll = new(CreateAlternateGait);

    public static string Stroll => _stroll.Value;

    public static string GaitWithIdleID()
    {
        string descriptor = Stroll;
        string original = ProjectSettings.GlobalizePath(descriptor);
        string alternate = Path.Combine(Path.GetDirectoryName(original)!, "sad.json");
        File.WriteAllText(alternate, File.ReadAllText(original).Replace("\"id\":\"stroll\"", "\"id\":\"sad\"", StringComparison.Ordinal));
        return descriptor[..descriptor.LastIndexOf('/')] + "/sad.json";
    }

    public static AnimationVariantLayer Layer(string name, params AnimationVariantOperation[] operations)
        => new()
        {
            Name = name,
            Operations = operations
        };

    public static AnimationVariantOperation Add(string descriptor)
        => Operation(AnimationVariantOperation.Action.Add, descriptor: descriptor);

    public static AnimationVariantOperation Operation(AnimationVariantOperation.Action kind,
        string category = "", string id = "", string descriptor = "")
        => new()
        {
            Kind = kind,
            Category = category,
            EntryID = id,
            Descriptor = descriptor,
            ResourcePath = $"res://tests/animation/catalogue/chain/chain_template.tscn::Fixture_{Guid.NewGuid():N}"
        };

    public static void AddAlternates(AnimationCatalogue catalogue)
        => catalogue.AddChild(Layer("TestVariants", Add(Slump), Add(Stroll)));

    private static string CreateAlternateGait()
    {
        string relative = $"temp/category-first-animation-variants/test-gaits/{Guid.NewGuid():N}";
        string directory = ProjectSettings.GlobalizePath("res://" + relative);
        _ = Directory.CreateDirectory(directory);
        string source = ProjectSettings.GlobalizePath("res://assets/animations/locomotion/feminine");
        foreach (string file in new[] { "set.json", "landing-ablation-01.json", "turn-angular-reference.json" })
        {
            File.Copy(Path.Combine(source, file), Path.Combine(directory, file));
        }

        File.WriteAllText(Path.Combine(directory, "variant.json"), JsonSerializer.Serialize(new
        {
            schema = "anim003_variant_v1",
            category = "locomotion",
            id = "stroll",
            tags = new[] { "leisurely" },
            description = "strolls along at an unhurried pace",
            set = relative + "/set.json"
        }));
        return "res://" + relative + "/variant.json";
    }
}
