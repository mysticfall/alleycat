using AlleyCat.Motion;
using AlleyCat.Navigation;
using Godot;

namespace AlleyCat.Tests.MotionBricks;

/// <summary>Task-only session wrapper: real first movement, then injected handling evidence, never a GPU stall.</summary>
public partial class MotionBricksFailureInjection : Node
{
    private readonly InjectionState _state = new();
    /// <summary>Select before the scene creates its navigation component.</summary>
    public bool Withhold
    {
        get; set;
    }
    /// <summary>Observed entry into the second session call.</summary>
    public bool Injected => Volatile.Read(ref _state.Injected);
    /// <summary>Session calls, including the injected second call.</summary>
    public int Calls => Volatile.Read(ref _state.Calls);
    /// <summary>Release from the scene thread after timeout and its safe-pose observation window.</summary>
    public void Release() => _state.Release.TrySetResult();

    /// <inheritdoc/>
    public override void _EnterTree() => GetTree().NodeAdded += Added;

    private void Added(Node node)
    {
        if (node is not MotionBricksNavigation navigation)
        {
            return;
        }
        InjectionState state = _state;
        bool withhold = Withhold;
        navigation.SessionDecorator = session => new InjectedSession(session, state, withhold);
    }

    /// <inheritdoc/>
    public override void _ExitTree()
    {
        GetTree().NodeAdded -= Added;
        Release();
    }

    private sealed class InjectionState
    {
        public int Calls;
        public bool Injected;
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class InjectedSession(SomaInferenceWorker.ISession inner, InjectionState state, bool withhold)
        : SomaInferenceWorker.ISession
    {
        public Transform3D[][] Generate(Transform3D[][] context, Transform3D[][] target)
        {
            int call = Interlocked.Increment(ref state.Calls);
            if (call != 2)
            {
                return inner.Generate(context, target);
            }
            if (!withhold)
            {
                Volatile.Write(ref state.Injected, true);
                throw new InvalidOperationException("Injected second-generation failure after real movement; not a native Run error.");
            }
            Transform3D[][] result = inner.Generate(context, target);
            Volatile.Write(ref state.Injected, true);
            // Only this test worker waits. Native Run has returned; scene processing and budget are unchanged.
            state.Release.Task.GetAwaiter().GetResult();
            return result;
        }

        public void Dispose() => inner.Dispose();
    }
}
