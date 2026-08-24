using System.Reflection;
using AlleyCat.Character;
using AlleyCat.Mind.AI;
using Xunit;

namespace AlleyCat.Tests.Context;

/// <summary>
/// Unit retirement guard for the removed context indirection API.
/// </summary>
public sealed class ContextualInformationApiRetirementTests
{
    /// <summary>The retired context indirection types stay absent and ICharacter does not extend IContextual.</summary>
    [Fact]
    public void RetiredContextContracts_StayAbsentFromTheGameAssembly()
    {
        Assembly gameAssembly = typeof(AgenticMind).Assembly;

        Assert.Null(gameAssembly.GetType("AlleyCat.Context.IContextual"));
        Assert.Null(gameAssembly.GetType("AlleyCat.Context.IContextSource"));
        Assert.Null(gameAssembly.GetType("AlleyCat.Context.IContextSource`1"));
        Assert.Null(gameAssembly.GetType("AlleyCat.Context.ContextSource"));
        Assert.Null(gameAssembly.GetType("AlleyCat.Character.CharacterCardContextSource"));
        Assert.DoesNotContain(typeof(ICharacter).GetInterfaces(), implemented => implemented.Name == "IContextual");
    }
}
