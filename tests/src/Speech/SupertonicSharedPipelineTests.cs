using System.Collections.Concurrent;
using AlleyCat.Models;
using AlleyCat.Speech.Generation.Supertonic;
using Xunit;

namespace AlleyCat.Tests.Speech;

/// <summary>
/// Unit coverage for the shared Supertonic pipeline (SPCH-007 TR-16, AC18; CORE-011-TAC-002/003/007): one load per
/// model directory and requested backend, per-call voice and language isolation, once-per-backend fallback
/// diagnostics, and a cached load failure delivered to every generator without retry.
/// </summary>
public sealed class SupertonicSharedPipelineTests
{
    private static readonly TimeSpan _bound = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The key must treat equivalent directory spellings as one artefact and keep requested backends apart.
    /// </summary>
    [Fact]
    public void Key_EqualDirectoryAndBackend_AreEqual_AndDifferentBackendIsNot()
    {
        string directory = Path.Combine(Path.GetTempPath(), "supertonic-3", "onnx");

        Assert.Equal(
            SupertonicPipelineKey.Create(directory, SupertonicExecutionBackend.Cuda),
            SupertonicPipelineKey.Create(directory + Path.DirectorySeparatorChar, SupertonicExecutionBackend.Cuda));
        Assert.NotEqual(
            SupertonicPipelineKey.Create(directory, SupertonicExecutionBackend.Cuda),
            SupertonicPipelineKey.Create(directory, SupertonicExecutionBackend.Cpu));
    }

    /// <summary>
    /// Three generators with the same key load one pipeline, report the CUDA fallback once, and each request reaches
    /// the pipeline with its own voice and language and returns to its own caller.
    /// </summary>
    [Fact]
    public async Task Handles_WithEqualKeys_ShareOnePipeline_IsolatePerCallInputs_AndReportFallbackOnce()
    {
        var registry = new SharedModelRegistry();
        var pipeline = new RecordingPipeline(SupertonicExecutionBackend.Cpu, "no CUDA");
        int factoryCalls = 0;
        int diagnostics = 0;
        string directory = Path.Combine(Path.GetTempPath(), "supertonic-3", "onnx");

        SupertonicPipelineHandle[] handles = [.. new[] { "F1", "M2", "F3" }.Select(voice =>
        {
            var definition = new SupertonicPipelineDefinition(
                SupertonicPipelineKey.Create(directory, SupertonicExecutionBackend.Cuda),
                (_, _) =>
                {
                    _ = Interlocked.Increment(ref factoryCalls);
                    return pipeline;
                },
                (_, _) => Interlocked.Increment(ref diagnostics));
            return new SupertonicPipelineHandle(registry.Resolve(definition), voice);
        })];

        string[] languages = ["en", "ko", "en"];
        SupertonicSynthesisResult[] results = await Task.WhenAll(handles.Select((handle, i) =>
            handle.SynthesiseAsync(CreateRequest(handle.Consumer.Name, languages[i])))).WaitAsync(_bound);

        Assert.Equal(1, factoryCalls);
        Assert.Equal(1, diagnostics);
        Assert.Equal(3, pipeline.Requests.Count);
        for (int i = 0; i < handles.Length; i++)
        {
            // The fake echoes each request's voice marker, so a crossed result would carry another voice's marker.
            Assert.Equal(RecordingPipeline.Marker(handles[i].Consumer.Name, languages[i]), results[i].Samples[0]);
            Assert.Equal(RecordingPipeline.FakeSampleRate, results[i].SampleRate);
            Assert.Equal(SupertonicExecutionBackend.Cpu, handles[i].ActiveBackend);
            Assert.Equal("no CUDA", handles[i].BackendFallbackReason);
        }

        foreach (SupertonicPipelineHandle handle in handles)
        {
            handle.Dispose();
        }

        Assert.False(pipeline.IsDisposed);
        await registry.Stop().WaitAsync(_bound);
        Assert.True(pipeline.IsDisposed);
    }

    /// <summary>
    /// A failed shared load reaches every generator's request, including a later one, without a second load attempt.
    /// </summary>
    [Fact]
    public async Task Handles_WhenLoadFails_ReportCachedFailureToEveryRequest_WithoutRetry()
    {
        var registry = new SharedModelRegistry();
        int factoryCalls = 0;
        var failure = new InvalidOperationException("Supertonic asset 'tts.json' was not found.");
        string directory = Path.Combine(Path.GetTempPath(), "supertonic-3", "missing");

        SupertonicPipelineHandle CreateHandle(string name)
        {
            return new(
            registry.Resolve(new SupertonicPipelineDefinition(
                SupertonicPipelineKey.Create(directory, SupertonicExecutionBackend.Cuda),
                (_, _) =>
                {
                    _ = Interlocked.Increment(ref factoryCalls);
                    throw failure;
                })),
            name);
        }

        SupertonicPipelineHandle first = CreateHandle("first");
        SupertonicPipelineHandle second = CreateHandle("second");

        InvalidOperationException firstError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => first.SynthesiseAsync(CreateRequest("M1", "en")).WaitAsync(_bound));
        InvalidOperationException secondError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => second.SynthesiseAsync(CreateRequest("M1", "en")).WaitAsync(_bound));
        InvalidOperationException laterError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateHandle("later").SynthesiseAsync(CreateRequest("M1", "en")).WaitAsync(_bound));
        InvalidOperationException repeatError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => first.SynthesiseAsync(CreateRequest("M1", "en")).WaitAsync(_bound));

        Assert.All(
            [firstError, secondError, laterError, repeatError],
            error => Assert.Contains("tts.json", error.Message, StringComparison.Ordinal));
        Assert.Equal(1, factoryCalls);
        await registry.Stop().WaitAsync(_bound);
    }

    /// <summary>
    /// A generator request started before its generator releases the handle (scene exit) settles as cancelled, never
    /// as <see cref="ObjectDisposedException" /> or a stale result, whichever stage it is waiting in when the handle is
    /// released: the shared load, the per-request preparation (voice style) or the in-flight synthesis
    /// (CORE-011 UR3, TR22).
    /// </summary>
    /// <param name="stage">Stage the request is held in when the handle is released.</param>
    [Theory]
    [InlineData("load")]
    [InlineData("prepare")]
    [InlineData("synthesis")]
    public async Task Request_WhenHandleReleasedMidRequest_SettlesAsCancelled(string stage)
    {
        var registry = new SharedModelRegistry();
        using var loadGate = new ManualResetEventSlim(stage != "load");
        using var prepareGate = new ManualResetEventSlim(stage != "prepare");
        using var synthesisGate = new ManualResetEventSlim(stage != "synthesis");
        using var reached = new ManualResetEventSlim(false);
        var pipeline = new GatedPipeline(synthesisGate, stage == "synthesis" ? reached : null);
        SupertonicPipelineHandle handle = new(
            registry.Resolve(new SupertonicPipelineDefinition(
                SupertonicPipelineKey.Create(Path.Combine(Path.GetTempPath(), "supertonic-3", stage), SupertonicExecutionBackend.Cuda),
                (_, _) =>
                {
                    if (stage == "load")
                    {
                        reached.Set();
                    }

                    Assert.True(loadGate.Wait(_bound), "The test never released the load.");
                    return pipeline;
                })),
            "Npc");

        Task<SupertonicSynthesisResult> request = handle.SynthesiseWhenLoadedAsync(() =>
        {
            if (stage == "prepare")
            {
                reached.Set();
            }

            Assert.True(prepareGate.Wait(_bound), "The test never released the preparation.");
            return CreateRequest("F1", "en");
        });

        Assert.True(reached.Wait(_bound), $"The request never reached the {stage} stage.");
        handle.Dispose();
        loadGate.Set();
        prepareGate.Set();
        synthesisGate.Set();

        Exception? error = await Record.ExceptionAsync(() => request.WaitAsync(_bound));
        Assert.True(request.IsCanceled, $"The request ended {request.Status}: {error}");
        _ = Assert.IsAssignableFrom<OperationCanceledException>(error);
        await registry.Stop().WaitAsync(_bound);
    }

    private static SupertonicSynthesisRequest CreateRequest(string voice, string language) => new(
        "Hello there.",
        language,
        new SupertonicVoiceStyle([0f], [1, 1, 1], [0f], [1, 1, 1], new Dictionary<string, string> { ["voice"] = voice }),
        8,
        1f);

    private sealed class GatedPipeline(ManualResetEventSlim gate, ManualResetEventSlim? reached) : ISupertonicInferencePipeline
    {
        public int SampleRate => 44100;

        public SupertonicExecutionBackend ActiveBackend => SupertonicExecutionBackend.Cuda;

        public string? BackendFallbackReason => null;

        public float[] Synthesise(SupertonicSynthesisRequest request)
        {
            reached?.Set();
            Assert.True(gate.Wait(_bound), "The test never released the synthesis.");
            return [0f];
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingPipeline(SupertonicExecutionBackend activeBackend, string? fallbackReason)
        : ISupertonicInferencePipeline
    {
        public const int FakeSampleRate = 44100;

        public ConcurrentQueue<SupertonicSynthesisRequest> Requests { get; } = new();

        public bool IsDisposed
        {
            get; private set;
        }

        public int SampleRate => FakeSampleRate;

        public SupertonicExecutionBackend ActiveBackend => activeBackend;

        public string? BackendFallbackReason => fallbackReason;

        public static float Marker(string voice, string language) => (voice + "|" + language).GetHashCode();

        public float[] Synthesise(SupertonicSynthesisRequest request)
        {
            Requests.Enqueue(request);
            return [Marker(request.VoiceStyle.Metadata["voice"], request.Language)];
        }

        public void Dispose() => IsDisposed = true;
    }
}
