using AlleyCat.Async;
using LanguageExt;

namespace AlleyCat.Tests.Async;

public class MockTaskQueue : ITaskQueue
{
    public Eff<T> Enqueue<T>(Eff<T> task) => task;
}