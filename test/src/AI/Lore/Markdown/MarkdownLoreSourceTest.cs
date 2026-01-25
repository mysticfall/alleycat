using AlleyCat.Ai.Lore;
using AlleyCat.Ai.Lore.Markdown;
using AlleyCat.Common;
using AlleyCat.Io;
using LanguageExt.UnsafeValueAccess;
using static LanguageExt.Prelude;

namespace AlleyCat.Tests.AI.Lore.Markdown;

[TestFixture]
public class MarkdownLoreSourceTest
{
    private static MarkdownLoreSource CreateSource(
        string path,
        int order = int.MaxValue,
        bool essential = false
    )
    {
        var resourcePath = ResourcePath.Create(path).ValueUnsafe();
        var name = resourcePath.Path.EndsWith("/index.md")
            ? Path.GetFileNameWithoutExtension(
                resourcePath.Parent
                    .Map(x => x.Path)
                    .Map(Path.GetFileNameWithoutExtension)
                    .IfNone("index")
            )
            : Path.GetFileNameWithoutExtension(resourcePath.Path);

        return new MarkdownLoreSource(
            resourcePath,
            LoreId.Create(name).ValueUnsafe(),
            LoreTitle.Create(name?.ToTitleCase()).ValueUnsafe(),
            order,
            essential,
            []
        );
    }

    [Test]
    public void CompareTo_ComparesResourcePaths()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                Compare("user://apple/banana.md", "user://apple/banana.md"),
                Is.Zero,
                "Comparing the same paths should result in zero."
            );

            Assert.That(
                Compare("user://fruit/apple.md", "user://fruit/banana.md"),
                Is.EqualTo(-1),
                "Comparing paths under the same parent should follow alphabetical order."
            );

            Assert.That(
                Compare("user://fruit/index.md", "user://fruit/apple.md"),
                Is.EqualTo(-1),
                "index.md should precede other files in the same directory."
            );

            Assert.That(
                Compare("user://fruit/banana.md", "user://fruit/index.md"),
                Is.EqualTo(1),
                "Files should follow index.md in the same directory."
            );

            Assert.That(
                Compare("user://fruit/index.md", "user://fruit/citrus/orange.md"),
                Is.EqualTo(-1),
                "A parent's index.md should precede files in its subdirectories."
            );

            Assert.That(
                Compare("user://fruit/apple.md", "user://vegetable/carrot.md"),
                Is.EqualTo(-1),
                "Comparing different top-level directories should follow alphabetical order."
            );
        }

        return;

        int Compare(string p1, string p2)
        {
            return CreateSource(p1).CompareTo(CreateSource(p2));
        }
    }

    [Test]
    public void CompareTo_ComparesSourcesWithExplicitOrders()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                Compare("user://fruit/apple.md", 2, "user://fruit/banana.md", 1),
                Is.EqualTo(1),
                "Comparing paths under the same parent should follow the specified order."
            );
            
            Assert.That(
                Compare("user://fruit/apple.md", 1, "user://fruit/banana.md", 2),
                Is.EqualTo(-1),
                "Comparing paths under the same parent should follow the specified order."
            );
            
            Assert.That(
                Compare("user://fruit/index.md", 1, "user://animal.md", 2),
                Is.EqualTo(-1),
                "Comparing paths of the same level should follow the specified order."
            );

            Assert.That(
                Compare("user://fruit/index.md", 2, "user://animal.md", 1),
                Is.EqualTo(1),
                "Comparing paths of the same level should follow the specified order."
            );

            Assert.That(
                Compare("user://animal/cats/housecat.md", 2, "user://vegetables.md", 1),
                Is.EqualTo(-1),
                "Comparing paths of different levels should ignore the specified order."
            );
        }

        return;

        int Compare(string p1, int o1, string p2, int o2)
        {
            return CreateSource(p1, o1).CompareTo(CreateSource(p2, o2));
        }
    }


    [Test]
    public void ToTableOfContents_BuildsTocFromSources()
    {
        var sources = Seq(
            CreateSource("res://lore/fruits/orange.md"),
            CreateSource("res://lore/index.md"),
            CreateSource("res://lore/fruits/apple.md"),
            CreateSource("res://lore/fruits/index.md"),
            CreateSource("res://lore/animals/index.md"),
            CreateSource("res://lore/animals/cats/tiger.md"),
            CreateSource("res://lore/animals/cats/housecat.md"),
            CreateSource("res://lore/animals/cats/index.md"),
            CreateSource("res://lore/animals/birds/eagle.md"),
            CreateSource("res://lore/animals/birds/index.md"),
            CreateSource("res://lore/animals/birds/sparrow.md")
        );

        var toc = sources.ToTableOfContents();

        Assert.That(toc, Is.Not.Empty, "The table of contents should not be empty.");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                toc,
                Has.Count.EqualTo(1),
                "Should have a single root ('lore')."
            );

            var root = toc[0];

            Assert.That(
                root.Id.Value,
                Is.EqualTo("lore"),
                "The root entry ID should be 'lore'."
            );

            Assert.That(
                root.Children,
                Has.Count.EqualTo(2),
                "Root should contain exactly two children: 'fruits' and 'animals'."
            );

            var fruits = root.Children.First(c => c.Id.Value == "fruits");
            var animals = root.Children.First(c => c.Id.Value == "animals");

            Assert.That(
                fruits.Title.Value,
                Is.EqualTo("Fruits"),
                "The 'fruits' entry should have the correct title."
            );

            Assert.That(
                fruits.Children,
                Has.Count.EqualTo(2),
                "The 'fruits' entry should contain two children."
            );

            Assert.That(
                fruits.Children.Map(c => c.Id.Value),
                Is.EquivalentTo(["apple", "orange"]),
                "The 'fruits' entry should contain 'apple' and 'orange' children."
            );

            var orange = fruits.Children.First(c => c.Id.Value == "orange");

            Assert.That(
                orange.Title.Value,
                Is.EqualTo("Orange"),
                "Source orange.md should have the title 'Orange'."
            );

            Assert.That(
                animals.Children.Any(c => c.Id.Value == "birds"),
                Is.True,
                "The 'animals' entry should contain a 'birds' child."
            );

            Assert.That(
                animals.Children.Any(c => c.Id.Value == "cats"),
                Is.True,
                "The 'animals' entry should contain a 'cats' child."
            );

            var cats = animals.Children.First(c => c.Id.Value == "cats");

            Assert.That(
                cats.Title.Value,
                Is.EqualTo("Cats"),
                "The 'cats' entry should have the correct title."
            );

            Assert.That(
                cats.Children.Map(c => c.Id.Value),
                Is.EquivalentTo(["housecat", "tiger"]),
                "The 'cats' entry should contain 'housecat' and 'tiger' children."
            );

            Assert.That(
                cats.Children.First(c => c.Id.Value == "tiger").Title.Value,
                Is.EqualTo("Tiger"),
                "The 'tiger' entry should have the title 'Tiger'."
            );

            var birds = animals.Children.First(c => c.Id.Value == "birds");

            Assert.That(
                birds.Title.Value,
                Is.EqualTo("Birds"),
                "The 'birds' entry should have the correct title."
            );

            Assert.That(
                birds.Children.Map(c => c.Id.Value),
                Contains.Item("eagle"),
                "The 'birds' entry should contain 'eagle'."
            );

            Assert.That(
                birds.Children.Map(c => c.Id.Value),
                Contains.Item("sparrow"),
                "The 'birds' entry should contain 'sparrow'."
            );

            Assert.That(
                birds.Children.First(c => c.Id.Value == "sparrow").Title.Value,
                Is.EqualTo("Sparrow"),
                "The 'sparrow' entry should have the title 'Sparrow'."
            );
        }
    }
}