using AlleyCat.Ai.Lore;
using AlleyCat.Ai.Lore.Markdown;
using AlleyCat.Common;
using AlleyCat.Tests.Env;
using AlleyCat.Io;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Tests.AI.Lore.Markdown;

[TestFixture]
public class MarkdownLoreBookTest
{
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(c => c.AddConsole());

    [Test]
    public async Task Create_CreatesValidLoreBook()
    {
        var env = new MockEnv();
        var book = await (
            from contentRoot in ResourcePath
                .Create(
                    "res://fixtures/AI/Lore/Markdown/source")
                .ToEff(identity)
            from result in MarkdownLoreBook.Create(contentRoot, _loggerFactory)
            select result
        ).As().RunUnsafeAsync(env);

        Assert.That(book, Is.Not.Null, "The lore book should not be null.");

        var toc = book.TableOfContents;

        Assert.That(
            toc,
            Has.Length.EqualTo(1),
            "The lore book should have a single top-level entry."
        );

        var world = toc[0];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                world.Id.Value,
                Is.EqualTo("world"),
                "The 'world' entry should have the correct ID."
            );

            Assert.That(
                world.Title.Value,
                Is.EqualTo("The World"),
                "The 'world' entry should have the correct title."
            );

            Assert.That(
                world.Essential,
                Is.True,
                "The 'world' entry should be marked as essential."
            );

            Assert.That(
                world.Parent,
                Is.Default,
                "The 'world' entry should have no parent."
            );

            Assert.That(
                world.Children,
                Has.Length.EqualTo(2),
                "The 'world' entry should have two children."
            );
        }

        var overview = world.Children[0];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                overview.Id.Value,
                Is.EqualTo("world_overview"),
                "The 'world_overview' entry should have the correct ID."
            );

            Assert.That(
                overview.Title.Value,
                Is.EqualTo("World Overview"),
                "The 'world_overview' entry should have the correct title."
            );

            Assert.That(
                overview.Essential,
                Is.False,
                "The 'world_overview' entry should be marked as essential."
            );

            Assert.That(
                overview.Parent,
                Is.EqualTo(Optional(world.Id)),
                "The 'world_overview' entry should have the correct parent."
            );

            Assert.That(
                overview.Children.IsEmpty,
                Is.True,
                "The 'world_overview' entry should have no children."
            );
        }
    }

    [Test]
    public async Task GetContents_ReturnsAllContents()
    {
        var env = new MockEnv();

        var expected = await (
            from path in ResourcePath.Create("res://fixtures/AI/Lore/Markdown/source.md").ToEff(identity)
            from text in env.FileProvider.ReadAllText(path)
            select text
        ).As().RunUnsafeAsync();

        var actual = await (
            from root in ResourcePath.Create("res://fixtures/AI/Lore/Markdown/source").ToEff(identity)
            from book in MarkdownLoreBook.Create(root, _loggerFactory)
            from text in book.GetContents()
            select text
        ).As().RunUnsafeAsync(env);

        Assert.That(
            actual.Value,
            Is.EqualTo(expected),
            "The combined document should match the expected content."
        );
    }

    [Test]
    public async Task GetContents_ReturnsSelectedContents()
    {
        var env = new MockEnv();

        var expected = await (
            from path in ResourcePath.Create("res://fixtures/AI/Lore/Markdown/selected.md").ToEff(identity)
            from text in env.FileProvider.ReadAllText(path)
            select text
        ).As().RunUnsafeAsync();

        var actual = await (
            from root in ResourcePath.Create("res://fixtures/AI/Lore/Markdown/source").ToEff(identity)
            from book in MarkdownLoreBook.Create(root, _loggerFactory)
            from selection in Seq("succession").Traverse(LoreId.Create).As().ToEff(identity)
            from text in book.GetContents(selection)
            select text
        ).As().RunUnsafeAsync(env);

        Assert.That(
            actual.Value,
            Is.EqualTo(expected),
            "The combined document should match the expected content."
        );
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        _loggerFactory.Dispose();
    }
}