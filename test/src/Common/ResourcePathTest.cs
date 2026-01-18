using AlleyCat.Common;
using LanguageExt.UnsafeValueAccess;

namespace AlleyCat.Tests.Common;

[TestFixture]
public class ResourcePathTest
{
    [Test]
    [TestCase("res://icon.png", "icon.png", ResourcePath.ResourceScheme.Resource)]
    [TestCase("user://game/saves", "game/saves", ResourcePath.ResourceScheme.User)]
    [TestCase("res://assets/textures/player.png", "assets/textures/player.png", ResourcePath.ResourceScheme.Resource)]
    public void Create_ValidPath_ReturnsSuccess(string input, string expectedPath,
        ResourcePath.ResourceScheme expectedScheme)
    {
        var result = ResourcePath.Create(input);

        Assert.That(result.IsRight, Is.True);

        result.IfRight(rp =>
        {
            Assert.That(rp.Value, Is.EqualTo(input));
            Assert.That(rp.Path, Is.EqualTo(expectedPath));
            Assert.That(rp.Scheme, Is.EqualTo(expectedScheme));
        });
    }

    [Test]
    [TestCase(null, "Resource path cannot be null or empty.")]
    [TestCase("", "Resource path cannot be null or empty.")]
    [TestCase("   ", "Resource path cannot be null or empty.")]
    [TestCase("C:/Windows", "Resource path must start with 'res://' or 'user://'.")]
    [TestCase("res://", "Resource path must contain a relative path after the scheme.")]
    [TestCase("res://path/with\\backslash.txt",
        "Resource path must use forward slashes ('/'), not backslashes ('\\').")]
    [TestCase("res:///leading/slash", "Resource path must not start or end with '/'.")]
    [TestCase("res://trailing/slash/", "Resource path must not start or end with '/'.")]
    [TestCase("res://empty//segment", "Resource path must not contain empty path segments ('//').")]
    [TestCase("res://path/./current", "Resource path must not contain '.' or '..' segments.")]
    [TestCase("res://path/../parent", "Resource path must not contain '.' or '..' segments.")]
    [TestCase("res://invalid@char.png", "Resource path contains invalid characters.")]
    public void Create_InvalidPath_ReturnsFailure(string? input, string expectedErrorMessage)
    {
        var result = ResourcePath.Create(input);

        Assert.That(result.IsLeft, Is.True);
        result.IfLeft(err => Assert.That(err.Message, Does.Contain(expectedErrorMessage)));
    }

    [Test]
    public void Segments_ReturnsCorrectParts()
    {
        var rp = ResourcePath.Create("res://assets/ui/buttons/ok.png").Value();
        var segments = rp.Segments.ToList();

        Assert.That(segments, Is.EqualTo(["assets", "ui", "buttons", "ok.png"]));
    }

    [Test]
    public void Parent_RootFile_ReturnsNone()
    {
        var rp = ResourcePath.Create("res://icon.png").Value();

        Assert.That(rp.Parent.IsNone, Is.True);
    }

    [Test]
    public void Parent_NestedFile_ReturnsParentDirectory()
    {
        var rp = ResourcePath.Create("res://assets/textures/wood.tres").Value();
        var parent = rp.Parent;

        Assert.That(parent.IsSome, Is.True);
        parent.IfSome(p => Assert.That(p.Value, Is.EqualTo("res://assets/textures")));
    }

    [Test]
    public void OperatorPlus_AppendsChildPath()
    {
        var parent = ResourcePath.Create("user://data").Value();
        var child = parent + "settings.json";

        using (Assert.EnterMultipleScope())
        {
            Assert.That(child.Value, Is.EqualTo("user://data/settings.json"));
            Assert.That(child.Scheme, Is.EqualTo(ResourcePath.ResourceScheme.User));
        }
    }

    [Test]
    public void ImplicitConversion_ToString_ReturnsValue()
    {
        const string pathString = "res://scenes/main.tscn";

        var rp = ResourcePath.Create(pathString).Value();

        string converted = rp;

        Assert.That(converted, Is.EqualTo(pathString));
    }
}