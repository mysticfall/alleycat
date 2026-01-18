using Godot;
using LanguageExt;

namespace AlleyCat.Async;

[GlobalClass]
public partial class DeferredQueueNode : QueueNode
{
    public override void _Process(double delta)
    {
        base._Process(delta);

        DequeueAll(tasks => tasks.Iter(x =>
            x.Task().Result.Match(
                x.Completion.SetResult,
                e => { x.Completion.SetException(e); }
            )
        ));
    }

    public override Eff<T> Enqueue<T>(Eff<T> task)
    {
        return GodotThread.IsMainThread() ? task : base.Enqueue(task);
    }
}