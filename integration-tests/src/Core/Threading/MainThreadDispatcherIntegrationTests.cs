using AlleyCat.Core.Threading;
using AlleyCat.XR;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Core.Threading;

/// <summary>
/// Runtime integration coverage for the global main-thread dispatcher.
/// </summary>
public sealed partial class MainThreadDispatcherIntegrationTests
{
    /// <summary>
    /// Verifies worker submissions are deferred and start on Godot's main thread.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_FromWorkerThread_DefersInvocationToGodotMainThread()
    {
        SceneTree sceneTree = GetSceneTree();
        DispatcherFixture fixture = await CreateFixtureAsync(sceneTree);

        try
        {
            bool invoked = false;
            ulong invocationThreadID = 0;

            Task<Task> submission = Task.Factory.StartNew(
                () => fixture.Dispatcher.InvokeAsync(() =>
                {
                    invoked = true;
                    invocationThreadID = OS.GetThreadCallerId();
                }).AsTask(),
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default);

            Task invocation = await submission;
            await invocation;

            Assert.True(invoked);
            Assert.Equal(OS.GetMainThreadId(), invocationThreadID);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    /// <summary>
    /// Verifies an asynchronous delegate submitted by a worker begins on Godot's main thread.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_AsynchronousDelegateFromWorkerThread_BeginsOnGodotMainThread()
    {
        SceneTree sceneTree = GetSceneTree();
        DispatcherFixture fixture = await CreateFixtureAsync(sceneTree);

        try
        {
            ulong invocationThreadID = 0;
            Task<Task> submission = Task.Factory.StartNew(
                () => fixture.Dispatcher.InvokeAsync(cancellationToken =>
                {
                    invocationThreadID = OS.GetThreadCallerId();
                    return ValueTask.CompletedTask;
                }).AsTask(),
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default);

            await await submission;
            Assert.Equal(OS.GetMainThreadId(), invocationThreadID);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    /// <summary>
    /// Verifies main-thread submissions execute inline with their results available immediately, while queued
    /// worker submissions still start in FIFO order.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_OnMainThread_RunsInlineWhileQueuedSubmissionsKeepFIFOOrder()
    {
        SceneTree sceneTree = GetSceneTree();
        DispatcherFixture fixture = await CreateFixtureAsync(sceneTree);

        try
        {
            List<int> inlineOrder = [];
            ValueTask first = fixture.Dispatcher.InvokeAsync(() => inlineOrder.Add(1));
            ValueTask second = fixture.Dispatcher.InvokeAsync(() => inlineOrder.Add(2));
            ValueTask third = fixture.Dispatcher.InvokeAsync(() => inlineOrder.Add(3));

            // No frame has advanced while the submissions ran, so completed work here proves execution happened
            // inline rather than through Godot's deferred scheduling.
            Assert.Equal([1, 2, 3], inlineOrder);
            Assert.True(first.IsCompletedSuccessfully);
            Assert.True(second.IsCompletedSuccessfully);
            Assert.True(third.IsCompletedSuccessfully);

            await Task.WhenAll(first.AsTask(), second.AsTask(), third.AsTask());

            List<int> queuedOrder = [];
            Task queuedCompletion = await Task.Factory.StartNew(
                () =>
                {
                    Task one = fixture.Dispatcher.InvokeAsync(() => queuedOrder.Add(1)).AsTask();
                    Task two = fixture.Dispatcher.InvokeAsync(() => queuedOrder.Add(2)).AsTask();
                    Task three = fixture.Dispatcher.InvokeAsync(() => queuedOrder.Add(3)).AsTask();
                    return Task.WhenAll(one, two, three);
                },
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default);

            await queuedCompletion;
            Assert.Equal([1, 2, 3], queuedOrder);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    /// <summary>
    /// Verifies a main-thread submission during a running invocation executes inline within the submitting
    /// callback, while a worker submission made during the same invocation still waits behind an intervening
    /// deferred callback.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_DuringInvocation_RunsMainThreadNestedWorkInlineAndDefersWorkerWork()
    {
        SceneTree sceneTree = GetSceneTree();
        DispatcherFixture fixture = await CreateFixtureAsync(sceneTree);

        try
        {
            List<string> events = [];
            ValueTask first = fixture.Dispatcher.InvokeAsync(() =>
            {
                events.Add("first");
                Callable.From(() => events.Add("sentinel")).CallDeferred();
                ValueTask nested = fixture.Dispatcher.InvokeAsync(() => events.Add("nested"));
                Assert.True(nested.IsCompletedSuccessfully);
            });
            ValueTask second = fixture.Dispatcher.InvokeAsync(() => events.Add("second"));

            await Task.WhenAll(first.AsTask(), second.AsTask());
            Assert.Equal(["first", "nested", "second"], events);

            Task<Task> workerSubmission = Task.Factory.StartNew(
                () => fixture.Dispatcher.InvokeAsync(() => events.Add("worker")).AsTask(),
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default);

            await await workerSubmission;
            await WaitForNextFrameAsync(sceneTree);

            Assert.Equal(["first", "nested", "second", "sentinel", "worker"], events);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    /// <summary>
    /// Verifies both delegate forms complete and propagate failures to their awaiters.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_SynchronousAndAsynchronousDelegates_PropagateCompletionAndFailure()
    {
        SceneTree sceneTree = GetSceneTree();
        DispatcherFixture fixture = await CreateFixtureAsync(sceneTree);

        try
        {
            bool synchronousCompleted = false;
            bool asynchronousCompleted = false;

            await fixture.Dispatcher.InvokeAsync(() => synchronousCompleted = true);
            await fixture.Dispatcher.InvokeAsync(async _ =>
            {
                await Task.Yield();
                asynchronousCompleted = true;
            });

            InvalidOperationException synchronousException = await Assert.ThrowsAsync<InvalidOperationException>(
                () => fixture.Dispatcher.InvokeAsync(
                    () => throw new InvalidOperationException("synchronous failure")).AsTask());
            InvalidOperationException asynchronousException = await Assert.ThrowsAsync<InvalidOperationException>(
                () => fixture.Dispatcher.InvokeAsync(async _ =>
                {
                    await Task.Yield();
                    throw new InvalidOperationException("asynchronous failure");
                }).AsTask());

            Assert.True(synchronousCompleted);
            Assert.True(asynchronousCompleted);
            Assert.Equal("synchronous failure", synchronousException.Message);
            Assert.Equal("asynchronous failure", asynchronousException.Message);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    /// <summary>
    /// Verifies an inline-completed invocation continues an eligible awaiter synchronously on the calling thread,
    /// while a queued invocation completed by a flush still keeps its continuation off the main thread.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_Completion_InlineContinuesOnCallerWhileQueuedStaysOffMainThread()
    {
        SceneTree sceneTree = GetSceneTree();
        DispatcherFixture fixture = await CreateFixtureAsync(sceneTree);

        try
        {
            Task invocation = fixture.Dispatcher.InvokeAsync(() => { }).AsTask();
            Task<ulong> continuation = invocation.ContinueWith(
                _ => OS.GetThreadCallerId(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            ulong continuationThreadID = await continuation;

            Assert.Equal(OS.GetMainThreadId(), continuationThreadID);

            Task<ulong> queuedContinuation = await Task.Factory.StartNew(
                () => fixture.Dispatcher.InvokeAsync(() => { }).AsTask().ContinueWith(
                    _ => OS.GetThreadCallerId(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default),
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default);

            ulong queuedContinuationThreadID = await queuedContinuation;

            Assert.NotEqual(OS.GetMainThreadId(), queuedContinuationThreadID);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    /// <summary>
    /// Verifies pre-cancelled inline submissions and queued submissions cancelled before their flush never invoke
    /// their delegates.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_WhenCancelledBeforeStart_SettlesCancelledWithoutInvocation()
    {
        SceneTree sceneTree = GetSceneTree();
        DispatcherFixture fixture = await CreateFixtureAsync(sceneTree);

        try
        {
            bool inlineCancelledInvoked = false;
            bool queuedCancelledInvoked = false;
            using CancellationTokenSource inlineCancellationSource = new();
            using CancellationTokenSource queuedCancellationSource = new();
            inlineCancellationSource.Cancel();

            Task inlineCancelled = fixture.Dispatcher.InvokeAsync(
                () => inlineCancelledInvoked = true,
                inlineCancellationSource.Token).AsTask();

            // Blocks until the worker has submitted, so the cancellation that follows cannot race the deferred
            // flush that would otherwise start the queued item.
            using ManualResetEventSlim queuedSubmitted = new();
            Task? queuedCancelled = null;
            _ = Task.Factory.StartNew(
                () =>
                {
                    queuedCancelled = fixture.Dispatcher.InvokeAsync(
                        () => queuedCancelledInvoked = true,
                        queuedCancellationSource.Token).AsTask();
                    queuedSubmitted.Set();
                },
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default);
            Assert.True(queuedSubmitted.Wait(TimeSpan.FromSeconds(2)));
            queuedCancellationSource.Cancel();

            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inlineCancelled);
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queuedCancelled!);
            await WaitForNextFrameAsync(sceneTree);

            Assert.False(inlineCancelledInvoked);
            Assert.False(queuedCancelledInvoked);
            Assert.True(inlineCancelled.IsCanceled);
            Assert.True(queuedCancelled!.IsCanceled);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    /// <summary>
    /// Verifies a started asynchronous delegate observes caller cancellation through its linked token.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_AsynchronousDelegate_ReceivesCallerCancellation()
    {
        SceneTree sceneTree = GetSceneTree();
        DispatcherFixture fixture = await CreateFixtureAsync(sceneTree);

        try
        {
            using CancellationTokenSource callerCancellationSource = new();
            TaskCompletionSource<CancellationToken> receivedToken = new(
                TaskCreationOptions.RunContinuationsAsynchronously);

            Task invocation = fixture.Dispatcher.InvokeAsync(async cancellationToken =>
            {
                receivedToken.SetResult(cancellationToken);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }, callerCancellationSource.Token).AsTask();

            CancellationToken delegateToken = await receivedToken.Task;
            callerCancellationSource.Cancel();

            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation);
            Assert.True(delegateToken.IsCancellationRequested);
            Assert.True(invocation.IsCanceled);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    /// <summary>
    /// Verifies shutdown cancels inline-started and queued work and rejects later submissions on both paths.
    /// </summary>
    [Fact]
    public async Task ExitTree_CancelsAcceptedWorkAndRejectsSubsequentWorkWithoutEffects()
    {
        SceneTree sceneTree = GetSceneTree();
        DispatcherFixture fixture = await CreateFixtureAsync(sceneTree);
        TaskCompletionSource activeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource activeStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

        bool queuedInvoked = false;
        bool postShutdownInvoked = false;
        CancellationToken activeToken = default;
        Task active = fixture.Dispatcher.InvokeAsync(async cancellationToken =>
        {
            activeToken = cancellationToken;
            activeStarted.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                activeStopped.SetResult();
            }
        }).AsTask();

        await activeStarted.Task;

        // Blocks until the worker has submitted, so the queued item is still pending when shutdown runs and no
        // frame advance can flush it first.
        using ManualResetEventSlim queuedSubmitted = new();
        Task? queued = null;
        _ = Task.Factory.StartNew(
            () =>
            {
                queued = fixture.Dispatcher.InvokeAsync(() => queuedInvoked = true).AsTask();
                queuedSubmitted.Set();
            },
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
        Assert.True(queuedSubmitted.Wait(TimeSpan.FromSeconds(2)));

        fixture.Game._ExitTree();

        Task postShutdown = fixture.Dispatcher.InvokeAsync(() => postShutdownInvoked = true).AsTask();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued!);
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => postShutdown);
        await activeStopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForNextFrameAsync(sceneTree);

        Assert.True(active.IsCanceled);
        Assert.True(queued!.IsCanceled);
        Assert.True(postShutdown.IsCanceled);
        Assert.True(activeToken.IsCancellationRequested);
        Assert.False(queuedInvoked);
        Assert.False(postShutdownInvoked);

        await DestroyFixtureAsync(sceneTree, fixture);
    }

    /// <summary>
    /// Verifies a closure that wins after Godot starts a flush but before its first work-item start prevents all initial code.
    /// </summary>
    [Fact]
    public async Task ExitTree_BetweenGodotFlushAndWorkStart_CancelsWorkWithoutInvokingInitialCode()
    {
        SceneTree sceneTree = GetSceneTree();
        using ManualResetEventSlim flushReached = new();
        using ManualResetEventSlim closeCompleted = new();
        Action? closeGame = null;
        TestGame game = new(() =>
        {
            flushReached.Set();
            _ = Task.Run(() =>
            {
                closeGame!();
                closeCompleted.Set();
            });
            Assert.True(closeCompleted.Wait(TimeSpan.FromSeconds(2)));
        })
        {
            Name = "MainThreadDispatcherShutdownStartRaceFixture",
        };
        TestXRManager xrManager = new()
        {
            Name = "XR",
        };
        game.AddChild(xrManager);
        game._EnterTree();
        sceneTree.Root.AddChild(game);
        closeGame = game._ExitTree;

        try
        {
            bool synchronousInvoked = false;
            bool asynchronousInvoked = false;
            Task synchronous = game.MainThreadDispatcher.InvokeAsync(() => synchronousInvoked = true).AsTask();
            Task asynchronous = game.MainThreadDispatcher.InvokeAsync(_ =>
            {
                asynchronousInvoked = true;
                return ValueTask.CompletedTask;
            }).AsTask();

            await WaitForNextFrameAsync(sceneTree);
            Assert.True(flushReached.IsSet);
            Assert.True(closeCompleted.Wait(TimeSpan.FromSeconds(2)));

            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => synchronous);
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => asynchronous);
            Assert.False(synchronousInvoked);
            Assert.False(asynchronousInvoked);
            Assert.True(synchronous.IsCanceled);
            Assert.True(asynchronous.IsCanceled);
        }
        finally
        {
            if (GodotObject.IsInstanceValid(game) && game.IsInsideTree())
            {
                game.QueueFree();
                await WaitForNextFrameAsync(sceneTree);
            }
        }
    }

    private static async Task<DispatcherFixture> CreateFixtureAsync(SceneTree sceneTree)
    {
        TestGame game = new()
        {
            Name = "MainThreadDispatcherFixture",
        };
        TestXRManager xrManager = new()
        {
            Name = "XR",
        };

        game.AddChild(xrManager);
        game._EnterTree();
        sceneTree.Root.AddChild(game);
        await WaitForNextFrameAsync(sceneTree);

        return new DispatcherFixture(game, game.MainThreadDispatcher);
    }

    private static async Task DestroyFixtureAsync(SceneTree sceneTree, DispatcherFixture fixture)
    {
        if (GodotObject.IsInstanceValid(fixture.Game) && fixture.Game.IsInsideTree())
        {
            fixture.Game.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    private sealed record DispatcherFixture(TestGame Game, IMainThreadDispatcher Dispatcher);

    private sealed partial class TestGame : Game
    {
        public TestGame()
        {
        }

        public TestGame(Action beforeWorkItemStart)
            : base(beforeWorkItemStart)
        {
        }
    }

    private sealed partial class TestXRManager : XRManager
    {
        public override void _Ready()
        {
        }
    }
}
