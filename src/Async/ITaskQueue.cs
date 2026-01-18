using LanguageExt;

namespace AlleyCat.Async;

public interface ITaskQueue
{
    Eff<T> Enqueue<T>(Eff<T> task);
}