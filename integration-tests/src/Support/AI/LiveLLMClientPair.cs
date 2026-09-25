using Microsoft.Extensions.AI;

namespace AlleyCat.IntegrationTests.Support.AI;

/// <summary>
/// Target and judge chat clients for live LLM integration tests.
/// </summary>
/// <remarks>
/// The pair owns both clients: disposing it disposes the target and the judge. The public constructor keeps
/// the two clients separately injectable so later work can substitute either one without redesign.
/// </remarks>
public sealed class LiveLLMClientPair(IChatClient target, IChatClient judge) : IDisposable
{
    /// <summary>
    /// Client the behaviour under test sends its live requests to.
    /// </summary>
    public IChatClient Target => target;

    /// <summary>
    /// Client used to judge target responses.
    /// </summary>
    public IChatClient Judge => judge;

    /// <summary>
    /// Disposes the target and judge clients owned by the pair.
    /// </summary>
    public void Dispose()
    {
        target.Dispose();
        judge.Dispose();
    }
}
