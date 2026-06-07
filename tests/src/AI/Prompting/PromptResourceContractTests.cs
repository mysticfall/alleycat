using AlleyCat.AI.Prompting;
using Godot;
using Xunit;

namespace AlleyCat.Tests.AI.Prompting;

/// <summary>
/// Unit coverage for AI-003 prompt resource authoring contracts that can be verified without Godot runtime execution.
/// </summary>
public sealed class PromptResourceContractTests
{
    /// <summary>
    /// Prompt resources should be editor-authorable Godot custom resources without editor-time execution.
    /// </summary>
    [Fact]
    public void PromptResourcesAreGodotAuthorableResources()
    {
        AssertResourceContract<PromptStack>();
        AssertResourceContract<PromptSection>();
        AssertResourceContract<TextPromptSection>();
        AssertResourceContract<FilePromptSection>();
    }

    private static void AssertResourceContract<T>()
        where T : Resource
    {
        Assert.True(typeof(Resource).IsAssignableFrom(typeof(T)));
        Assert.NotNull(typeof(T).GetCustomAttributes(typeof(GlobalClassAttribute), inherit: true).SingleOrDefault());
        Assert.Null(typeof(T).GetCustomAttributes(typeof(ToolAttribute), inherit: true).SingleOrDefault());
        if (!typeof(T).IsAbstract)
        {
            Assert.NotNull(typeof(T).GetConstructor(Type.EmptyTypes));
        }
    }
}
