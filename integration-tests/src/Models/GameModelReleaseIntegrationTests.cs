using AlleyCat.IntegrationTests.Support;
using AlleyCat.Models;
using AlleyCat.Motion;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Models;

/// <summary>
/// CORE-011-TAC-008 (TR24): the end of a <see cref="Game" />'s lifetime stops its shared model registry and motion
/// workers without blocking, even when the shutdown boundary was never called, and a stop after the shutdown boundary
/// is a no-op. A fake backend stands in for a real model, so no inference system is loaded.
/// </summary>
[Headless]
public sealed class GameModelReleaseIntegrationTests
{
    private static readonly TimeSpan _bound = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Freeing a <see cref="Game" /> that never reached the shutdown boundary stops its registry and workers, returns
    /// while a backend call is still in flight, and disposes the backend exactly once after that call returns.
    /// </summary>
    [Fact]
    public async Task FreeingGame_StopsModelsWithoutBlocking_AndDisposesBackendOnceAfterInFlightCall()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var game = new ProbeGame { Name = "GameModelRelease" };
        tree.Root.AddChild(game);
        SharedModelRegistry registry = game.SharedModels;
        MotionBricksWorkers workers = game.MotionWorkers;
        var backend = new CountingBackend();
        ISharedModel<CountingBackend> model = registry.Resolve(new CountingDefinition(backend));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> inFlight = model.SubmitAsync(new ModelWorkItem(new ModelConsumerId("probe")), async _ =>
        {
            started.SetResult();
            await gate.Task.ConfigureAwait(false);
            return 1;
        });
        await started.Task.WaitAsync(_bound);

        try
        {
            game.Free();

            Assert.True(registry.IsStopping);
            Assert.True(workers.IsStopping);
            Assert.False(inFlight.IsCompleted);
            Assert.Equal(0, backend.DisposeCount);
        }
        finally
        {
            _ = gate.TrySetResult();
        }

        await registry.Stop().WaitAsync(_bound);
        Assert.Equal(1, backend.DisposeCount);
    }

    /// <summary>
    /// Tree exit after the shutdown boundary already stopped the registry is a no-op: the stop completion stays the
    /// same and the backend is not disposed again.
    /// </summary>
    [Fact]
    public async Task TreeExitAfterShutdownBoundary_IsNoOp()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var game = new ProbeGame { Name = "GameModelReleaseAfterBoundary" };
        tree.Root.AddChild(game);
        SharedModelRegistry registry = game.SharedModels;
        var backend = new CountingBackend();
        ISharedModel<CountingBackend> model = registry.Resolve(new CountingDefinition(backend));
        await model.Loaded.WaitAsync(_bound);

        try
        {
            await game.DrainMotionAndDiagnosticsAsync().WaitAsync(_bound);
            Task boundaryStop = registry.Stop();
            Assert.Equal(1, backend.DisposeCount);

            game.Free();

            Assert.Same(boundaryStop, registry.Stop());
            Assert.Equal(1, backend.DisposeCount);
        }
        finally
        {
            if (GodotObject.IsInstanceValid(game))
            {
                game.Free();
            }
        }
    }

    private sealed record CountingKey(string Name) : ModelKey;

    private sealed class CountingBackend : IDisposable
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed class CountingDefinition(CountingBackend backend) : ModelDefinition<CountingBackend>
    {
        public override ModelKey Key { get; } = new CountingKey("game-model-release");

        public override Task<CountingBackend> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(backend);
    }

    private sealed partial class ProbeGame : Game
    {
        public override void _Ready()
        {
        }
    }
}
