using Godot;

namespace AlleyCat.Async;

[GlobalClass]
public partial class AsyncQueueNode : QueueNode
{
    private bool _busy;

    public override void _Process(double delta)
    {
        base._Process(delta);

        if (_busy) return;

        Dequeue(task =>
        {
            _busy = true;

            Task.Run(async () =>
            {
                try
                {
                    var result = await task.Task();

                    result.Match(
                        task.Completion.SetResult,
                        e => task.Completion.SetException(e)
                    );
                }
                catch (Exception ex)
                {
                    task.Completion.SetException(ex);
                }
                finally
                {
                    _busy = false;
                }
            });
        });
    }
}