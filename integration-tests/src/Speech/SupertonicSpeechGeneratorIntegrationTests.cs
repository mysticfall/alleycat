using System.Reflection;
using AlleyCat.Core.Logging;
using AlleyCat.Speech.Generation;
using AlleyCat.Speech.Generation.Supertonic;
using AlleyCat.TestFramework;
using Godot;
using Microsoft.Extensions.Logging;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Speech;

/// <summary>
/// Runtime coverage for the offline Supertonic speech generator at its inference-pipeline boundary (SPCH-007).
/// </summary>
public sealed partial class SupertonicSpeechGeneratorIntegrationTests
{
    private const int FakeSampleRate = 16000;
    private const string FixtureModelDirectoryPath = "res://assets/testing/speech/supertonic/model";
    private const string FixtureVoiceStylesDirectoryPath = "res://assets/testing/speech/supertonic/voice_styles";

    private static readonly MethodInfo _invokeGenerationAsyncMethod = typeof(SpeechGenerator)
        .GetMethod("InvokeGenerationAsync", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Expected SpeechGenerator.InvokeGenerationAsync for runtime speech tests.");

    /// <summary>
    /// Exported defaults must match the SPCH-007 contract without any configuration binding.
    /// </summary>
    [Fact]
    [Headless]
    public void SupertonicSpeechGenerator_Defaults_MatchSpecifiedExportedValues()
    {
        SupertonicSpeechGenerator generator = new();

        Assert.Equal("res://models/supertonic-3/onnx", generator.ModelDirectoryPath);
        Assert.Equal("res://models/supertonic-3/voice_styles", generator.VoiceStylesDirectoryPath);
        Assert.Equal("M1", generator.Voice);
        Assert.Equal(string.Empty, generator.VoiceOverride);
        Assert.Equal("en", generator.Language);
        Assert.Equal(1.0f, generator.SpeedRatio);
        Assert.Equal(8, generator.QualitySteps);
        Assert.Equal(SupertonicExecutionBackend.Cuda, generator.ExecutionBackend);
        Assert.True(generator.Enabled);
    }

    /// <summary>
    /// The production generator must globalise and forward its model directory, load the configured voice style,
    /// encode pipeline samples as PCM16, reuse its pipeline, and record its backend and latency traces.
    /// </summary>
    [Fact]
    [Headless]
    public async Task Generate_WithInjectedPipeline_ProducesPcm16MonoWave_AndReusesPipeline()
    {
        SceneTree sceneTree = GetSceneTree();
        using CapturingPipelineLogFixture pipelineLog = new();
        pipelineLog.Install();

        FakeInferencePipeline pipeline = new(SupertonicExecutionBackend.Cuda);
        SupertonicSpeechGenerator generator = new()
        {
            Name = "SupertonicSpeechGenerator",
            ModelDirectoryPath = FixtureModelDirectoryPath,
            VoiceStylesDirectoryPath = FixtureVoiceStylesDirectoryPath,
        };
        using IDisposable pipelineOverride = generator.OverridePipelineFactoryForTesting(
            (modelDirectory, requestedBackend) =>
            {
                pipeline.RecordInitialisation(modelDirectory, requestedBackend);
                return pipeline;
            });

        sceneTree.Root.AddChild(generator);
        await WaitForFramesAsync(sceneTree, 2);

        try
        {
            byte[] audio = await generator.Generate("Hello there, friend.");

            AssertPcm16MonoWave(audio, FakeSampleRate);
            Assert.Equal(new short[] { -32767, -16383, 0, 8191, 32767 }, ReadPcm16Samples(audio));
            AssertPipelineInitialised(pipeline, SupertonicExecutionBackend.Cuda);
            _ = Assert.Single(pipeline.SynthesisRequests);
            Assert.Equal("Hello there, friend.", pipeline.SynthesisRequests[0].Text);
            AssertFixtureVoiceStyle(pipeline.SynthesisRequests[0].VoiceStyle);

            CapturedLogEntry backendEntry = Assert.Single(
                pipelineLog.Entries,
                entry => entry.Message.Contains("TTS backend engaged", StringComparison.Ordinal));
            Assert.Equal(LogLevel.Trace, backendEntry.Level);
            Assert.Contains("supertonic-3 backend cuda", backendEntry.Message, StringComparison.Ordinal);

            CapturedLogEntry latencyEntry = Assert.Single(
                pipelineLog.Entries,
                entry => entry.Message.Contains("TTS backend returned in", StringComparison.Ordinal));
            Assert.Equal(LogLevel.Trace, latencyEntry.Level);
            Assert.Equal("AlleyCat.Pipeline", latencyEntry.CategoryName);
            Assert.Contains("TTS backend returned in", latencyEntry.Message, StringComparison.Ordinal);
            Assert.Contains("supertonic-3", latencyEntry.Message, StringComparison.Ordinal);

            byte[] secondAudio = await generator.Generate("Hello there, friend.");
            AssertPcm16MonoWave(secondAudio, FakeSampleRate);
            Assert.Equal(1, pipeline.InitialisationCount);
            Assert.Equal(2, pipeline.SynthesisRequests.Count);
        }
        finally
        {
            generator.GetParent()?.RemoveChild(generator);
            generator._ExitTree();
            generator.Free();
            await WaitForFramesAsync(sceneTree, 2);
        }

        Assert.True(pipeline.IsDisposed);
    }

    /// <summary>
    /// A CUDA request with a CUDA pipeline must record CUDA as active without a fallback warning.
    /// </summary>
    [Fact]
    [Headless]
    public async Task Generate_WithCudaRequest_RecordsCudaBackendWithoutFallbackWarning()
    {
        SceneTree sceneTree = GetSceneTree();
        using CapturingPipelineLogFixture pipelineLog = new();
        using RecordingLoggerProvider loggerProvider = new();
        pipelineLog.Install();

        FakeInferencePipeline pipeline = new(SupertonicExecutionBackend.Cuda);
        SupertonicSpeechGenerator generator = new()
        {
            Name = "SupertonicSpeechGenerator",
            ExecutionBackend = SupertonicExecutionBackend.Cuda,
            ModelDirectoryPath = FixtureModelDirectoryPath,
            VoiceStylesDirectoryPath = FixtureVoiceStylesDirectoryPath,
        };
        using IDisposable pipelineOverride = generator.OverridePipelineFactoryForTesting(
            (modelDirectory, requestedBackend) =>
            {
                pipeline.RecordInitialisation(modelDirectory, requestedBackend);
                return pipeline;
            });

        sceneTree.Root.AddChild(generator);
        await WaitForFramesAsync(sceneTree, 2);
        using IDisposable loggerOverride = generator.OverrideLoggerForTesting(
            loggerProvider.CreateLogger<SupertonicSpeechGenerator>());

        try
        {
            byte[] audio = await generator.Generate("Hello there, friend.");

            AssertPcm16MonoWave(audio, FakeSampleRate);
            AssertPipelineInitialised(pipeline, SupertonicExecutionBackend.Cuda);
            CapturedLogEntry backendEntry = Assert.Single(
                pipelineLog.Entries,
                entry => entry.Message.Contains("supertonic-3 backend", StringComparison.Ordinal));
            Assert.Equal(LogLevel.Trace, backendEntry.Level);
            Assert.Equal("AlleyCat.Pipeline", backendEntry.CategoryName);
            Assert.Contains("supertonic-3 backend cuda", backendEntry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(loggerProvider.Entries, entry => entry.Level == LogLevel.Warning);
        }
        finally
        {
            generator.GetParent()?.RemoveChild(generator);
            generator._ExitTree();
            generator.Free();
            await WaitForFramesAsync(sceneTree, 2);
        }

        Assert.True(pipeline.IsDisposed);
    }

    /// <summary>
    /// A pipeline reporting CUDA fallback must produce the generator's fallback warning and CPU backend trace.
    /// </summary>
    [Fact]
    [Headless]
    public async Task Generate_WithCudaFallbackPipeline_EmitsSingleFallbackWarning_AndCpuTrace()
    {
        const string failureReason = "Deterministic pipeline fallback.";

        SceneTree sceneTree = GetSceneTree();
        using CapturingPipelineLogFixture pipelineLog = new();
        using RecordingLoggerProvider loggerProvider = new();
        pipelineLog.Install();

        FakeInferencePipeline pipeline = new(SupertonicExecutionBackend.Cpu, failureReason);
        SupertonicSpeechGenerator generator = new()
        {
            Name = "SupertonicSpeechGenerator",
            ExecutionBackend = SupertonicExecutionBackend.Cuda,
            ModelDirectoryPath = FixtureModelDirectoryPath,
            VoiceStylesDirectoryPath = FixtureVoiceStylesDirectoryPath,
        };
        using IDisposable pipelineOverride = generator.OverridePipelineFactoryForTesting(
            (modelDirectory, requestedBackend) =>
            {
                pipeline.RecordInitialisation(modelDirectory, requestedBackend);
                return pipeline;
            });

        sceneTree.Root.AddChild(generator);
        await WaitForFramesAsync(sceneTree, 2);
        using IDisposable loggerOverride = generator.OverrideLoggerForTesting(
            loggerProvider.CreateLogger<SupertonicSpeechGenerator>());

        try
        {
            byte[] audio = await generator.Generate("Hello there, friend.");

            AssertPcm16MonoWave(audio, FakeSampleRate);
            AssertPipelineInitialised(pipeline, SupertonicExecutionBackend.Cuda);

            CapturedLogEntry fallbackWarning = Assert.Single(loggerProvider.Entries);
            Assert.Equal(typeof(SupertonicSpeechGenerator).FullName, fallbackWarning.CategoryName);
            Assert.Equal(LogLevel.Warning, fallbackWarning.Level);
            Assert.Contains("Cuda", fallbackWarning.Message, StringComparison.Ordinal);
            Assert.Contains(failureReason, fallbackWarning.Message, StringComparison.Ordinal);

            CapturedLogEntry activeBackendEntry = Assert.Single(
                pipelineLog.Entries,
                entry => entry.CategoryName == "AlleyCat.Pipeline"
                    && entry.Level == LogLevel.Trace
                    && entry.Message.Contains("TTS backend engaged", StringComparison.Ordinal));
            Assert.Contains("supertonic-3 backend cpu", activeBackendEntry.Message, StringComparison.Ordinal);
        }
        finally
        {
            generator.GetParent()?.RemoveChild(generator);
            generator._ExitTree();
            generator.Free();
            await WaitForFramesAsync(sceneTree, 2);
        }

        Assert.True(pipeline.IsDisposed);
    }

    /// <summary>
    /// A CPU pin must synthesise successfully without a CUDA fallback warning and trace CPU as the
    /// active backend (SPCH-007 AC 10 and AC 12).
    /// </summary>
    [Fact]
    [Headless]
    public async Task Generate_WithCpuBackend_SynthesisesOnCpuWithoutFallbackWarning()
    {
        SceneTree sceneTree = GetSceneTree();
        using CapturingPipelineLogFixture pipelineLog = new();
        using RecordingLoggerProvider loggerProvider = new();
        pipelineLog.Install();

        FakeInferencePipeline pipeline = new(SupertonicExecutionBackend.Cpu);
        SupertonicSpeechGenerator generator = new()
        {
            Name = "SupertonicSpeechGenerator",
            ExecutionBackend = SupertonicExecutionBackend.Cpu,
            ModelDirectoryPath = FixtureModelDirectoryPath,
            VoiceStylesDirectoryPath = FixtureVoiceStylesDirectoryPath,
        };
        using IDisposable pipelineOverride = generator.OverridePipelineFactoryForTesting(
            (modelDirectory, requestedBackend) =>
            {
                pipeline.RecordInitialisation(modelDirectory, requestedBackend);
                return pipeline;
            });

        sceneTree.Root.AddChild(generator);
        await WaitForFramesAsync(sceneTree, 2);
        using IDisposable loggerOverride = generator.OverrideLoggerForTesting(
            loggerProvider.CreateLogger<SupertonicSpeechGenerator>());

        try
        {
            byte[] audio = await generator.Generate("Hello there, friend.");

            AssertPcm16MonoWave(audio, FakeSampleRate);
            AssertPipelineInitialised(pipeline, SupertonicExecutionBackend.Cpu);
            CapturedLogEntry backendEntry = Assert.Single(
                pipelineLog.Entries,
                entry => entry.Message.Contains("supertonic-3 backend", StringComparison.Ordinal));
            Assert.Equal(LogLevel.Trace, backendEntry.Level);
            Assert.Equal("AlleyCat.Pipeline", backendEntry.CategoryName);
            Assert.Contains("supertonic-3 backend cpu", backendEntry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(
                loggerProvider.Entries,
                entry => entry.Level == LogLevel.Warning
                    && entry.Message.Contains("execution backend was requested but could not be initialised", StringComparison.Ordinal));
        }
        finally
        {
            generator.GetParent()?.RemoveChild(generator);
            generator._ExitTree();
            generator.Free();
            await WaitForFramesAsync(sceneTree, 2);
        }

        Assert.True(pipeline.IsDisposed);
    }

    /// <summary>
    /// Requesting an unknown voice must still complete generation through the warn-and-fallback default style.
    /// </summary>
    [Fact]
    [Headless]
    public async Task GenerateSpeech_WithUnknownVoice_FallsBackToDefaultStyle_AndEmitsCompletion()
    {
        SceneTree sceneTree = GetSceneTree();
        using CapturingPipelineLogFixture pipelineLog = new();
        using RecordingLoggerProvider loggerProvider = new();
        pipelineLog.Install();
        FakeInferencePipeline pipeline = new(SupertonicExecutionBackend.Cuda);
        SupertonicSpeechGenerator generator = new()
        {
            Name = "SupertonicSpeechGenerator",
            Voice = "Z9",
            ModelDirectoryPath = FixtureModelDirectoryPath,
            VoiceStylesDirectoryPath = FixtureVoiceStylesDirectoryPath,
        };
        using IDisposable pipelineOverride = generator.OverridePipelineFactoryForTesting(
            (modelDirectory, requestedBackend) =>
            {
                pipeline.RecordInitialisation(modelDirectory, requestedBackend);
                return pipeline;
            });

        sceneTree.Root.AddChild(generator);
        await WaitForFramesAsync(sceneTree, 2);
        using IDisposable loggerOverride = generator.OverrideLoggerForTesting(
            loggerProvider.CreateLogger<SupertonicSpeechGenerator>());

        byte[]? generatedAudio = null;
        int completedCount = 0;
        int failedCount = 0;
        _ = generator.Connect(
            SpeechGenerator.SignalName.SpeechGenerationCompleted,
            Callable.From<byte[]>(audio =>
            {
                completedCount++;
                generatedAudio = audio;
            }));
        _ = generator.Connect(
            SpeechGenerator.SignalName.SpeechGenerationFailed,
            Callable.From<string>(_ => failedCount++));

        try
        {
            await InvokeGenerationAsync(generator, "Hello there, friend.");
            await WaitForNextFrameAsync(sceneTree);

            Assert.Equal(1, completedCount);
            Assert.Equal(0, failedCount);
            Assert.False(generator.IsGenerating);
            byte[] audio = Assert.IsType<byte[]>(generatedAudio);
            AssertPcm16MonoWave(audio, FakeSampleRate);
            Assert.Equal(new short[] { -32767, -16383, 0, 8191, 32767 }, ReadPcm16Samples(audio));
            AssertPipelineInitialised(pipeline, SupertonicExecutionBackend.Cuda);
            SupertonicSynthesisRequest request = Assert.Single(pipeline.SynthesisRequests);
            Assert.Equal("Hello there, friend.", request.Text);
            AssertFixtureVoiceStyle(request.VoiceStyle);

            CapturedLogEntry fallbackWarning = Assert.Single(
                loggerProvider.Entries,
                entry => entry.Level == LogLevel.Warning
                    && entry.Message.Contains("voice style", StringComparison.Ordinal));
            Assert.Equal(LogLevel.Warning, fallbackWarning.Level);
            Assert.Contains("Z9", fallbackWarning.Message, StringComparison.Ordinal);
            Assert.Contains("M1", fallbackWarning.Message, StringComparison.Ordinal);
        }
        finally
        {
            generator.GetParent()?.RemoveChild(generator);
            generator._ExitTree();
            generator.Free();
            await WaitForFramesAsync(sceneTree, 2);
        }

        Assert.True(pipeline.IsDisposed);
    }

    /// <summary>
    /// Disabled generators must not dispatch signal-driven requests, per the SPCH-004 Enabled gating contract.
    /// </summary>
    [Fact]
    [Headless]
    public async Task GenerateSpeech_WhenDisabled_EmitsNoSignals()
    {
        SceneTree sceneTree = GetSceneTree();
        SupertonicSpeechGenerator generator = new()
        {
            Name = "SupertonicSpeechGenerator",
            Enabled = false,
            // A missing model directory would fail within a few frames if dispatch occurred, making gating
            // violations observable without loading the real model assets.
            ModelDirectoryPath = "res://models/supertonic-3/missing",
        };

        sceneTree.Root.AddChild(generator);
        await WaitForFramesAsync(sceneTree, 2);

        int completedCount = 0;
        int failedCount = 0;
        _ = generator.Connect(
            SpeechGenerator.SignalName.SpeechGenerationCompleted,
            Callable.From<byte[]>(_ => completedCount++));
        _ = generator.Connect(
            SpeechGenerator.SignalName.SpeechGenerationFailed,
            Callable.From<string>(_ => failedCount++));

        try
        {
            generator.GenerateSpeech("Hello there, friend.");
            await WaitForFramesAsync(sceneTree, 60);

            Assert.Equal(0, completedCount);
            Assert.Equal(0, failedCount);
            Assert.False(generator.IsGenerating);
        }
        finally
        {
            generator.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>
    /// A missing model directory must fail through the failure signal without crashing, and the generator must
    /// stay usable for further dispatch afterwards.
    /// </summary>
    [Fact]
    [Headless]
    public async Task GenerateSpeech_WithInvalidModelDirectory_EmitsFailureSignal_AndStaysUsable()
    {
        SceneTree sceneTree = GetSceneTree();
        SupertonicSpeechGenerator generator = new()
        {
            Name = "SupertonicSpeechGenerator",
            ModelDirectoryPath = "res://models/supertonic-3/missing",
        };

        sceneTree.Root.AddChild(generator);
        await WaitForFramesAsync(sceneTree, 2);

        string? failureText = null;
        int completedCount = 0;
        int failedCount = 0;
        _ = generator.Connect(
            SpeechGenerator.SignalName.SpeechGenerationCompleted,
            Callable.From<byte[]>(_ => completedCount++));
        _ = generator.Connect(
            SpeechGenerator.SignalName.SpeechGenerationFailed,
            Callable.From<string>(error =>
            {
                failedCount++;
                failureText = error;
            }));

        try
        {
            await InvokeGenerationAsync(generator, "Hello there, friend.");
            await WaitForNextFrameAsync(sceneTree);

            Assert.Equal(0, completedCount);
            Assert.Equal(1, failedCount);
            Assert.False(generator.IsGenerating);
            Assert.Contains("tts.json", failureText, StringComparison.Ordinal);
            Assert.Contains("was not found", failureText, StringComparison.Ordinal);

            // A second dispatch on the same node must keep failing gracefully rather than dead-locking or crashing.
            await InvokeGenerationAsync(generator, "Second request.");
            await WaitForNextFrameAsync(sceneTree);

            Assert.Equal(0, completedCount);
            Assert.Equal(2, failedCount);
            Assert.False(generator.IsGenerating);
        }
        finally
        {
            generator.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);
        }
    }

    private static async Task InvokeGenerationAsync(SpeechGenerator speechGenerator, string text, string? instruction = null)
    {
        Task invocation = (Task?)_invokeGenerationAsyncMethod.Invoke(speechGenerator, [text, instruction])
            ?? throw new InvalidOperationException("Expected speech-generation invocation task.");

        await invocation;
    }

    private static void AssertPcm16MonoWave(byte[] audio, int expectedSampleRate)
    {
        Assert.NotEmpty(audio);
        Assert.True(audio.Length > 44, "Expected a complete WAV header.");
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(audio, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(audio, 8, 4));
        Assert.Equal((short)1, BitConverter.ToInt16(audio, 20));
        Assert.Equal((short)1, BitConverter.ToInt16(audio, 22));
        Assert.Equal(expectedSampleRate, BitConverter.ToInt32(audio, 24));
        Assert.Equal((short)16, BitConverter.ToInt16(audio, 34));
        Assert.Equal("data", System.Text.Encoding.ASCII.GetString(audio, 36, 4));
        Assert.Equal(audio.Length - 44, BitConverter.ToInt32(audio, 40));
    }

    private static short[] ReadPcm16Samples(byte[] audio)
    {
        int sampleCount = BitConverter.ToInt32(audio, 40) / sizeof(short);
        short[] samples = new short[sampleCount];
        for (int index = 0; index < samples.Length; index++)
        {
            samples[index] = BitConverter.ToInt16(audio, 44 + (index * sizeof(short)));
        }

        return samples;
    }

    private static void AssertPipelineInitialised(
        FakeInferencePipeline pipeline,
        SupertonicExecutionBackend requestedBackend)
    {
        Assert.Equal(1, pipeline.InitialisationCount);
        Assert.Equal(ProjectSettings.GlobalizePath(FixtureModelDirectoryPath), pipeline.ModelDirectory);
        Assert.Equal(requestedBackend, pipeline.RequestedBackend);
    }

    private static void AssertFixtureVoiceStyle(SupertonicVoiceStyle voiceStyle)
    {
        Assert.Equal([0.125f], voiceStyle.TtlData);
        Assert.Equal([1, 1, 1], voiceStyle.TtlShape);
        Assert.Equal([0.75f], voiceStyle.DpData);
        Assert.Equal([1, 1, 1], voiceStyle.DpShape);
        Assert.Equal("M1", voiceStyle.Metadata["fixture"]);
        Assert.Equal("integration-test", voiceStyle.Metadata["speaker"]);
    }

    private sealed class FakeInferencePipeline(
        SupertonicExecutionBackend activeBackend,
        string? backendFallbackReason = null) : ISupertonicInferencePipeline
    {
        private static readonly float[] _samples = [-1f, -0.5f, 0f, 0.25f, 1f];

        public int SampleRate => FakeSampleRate;

        public SupertonicExecutionBackend ActiveBackend => activeBackend;

        public string? BackendFallbackReason => backendFallbackReason;

        public string? ModelDirectory
        {
            get; private set;
        }

        public SupertonicExecutionBackend? RequestedBackend
        {
            get; private set;
        }

        public int InitialisationCount
        {
            get; private set;
        }

        public List<SupertonicSynthesisRequest> SynthesisRequests { get; } = [];

        public bool IsDisposed
        {
            get; private set;
        }

        public void RecordInitialisation(string modelDirectory, SupertonicExecutionBackend requestedBackend)
        {
            ModelDirectory = modelDirectory;
            RequestedBackend = requestedBackend;
            InitialisationCount++;
        }

        public float[] Synthesise(SupertonicSynthesisRequest request)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            SynthesisRequests.Add(request);
            return [.. _samples];
        }

        public void Dispose() => IsDisposed = true;
    }

    /// <summary>
    /// Installs a trace-enabled capturing logger for <see cref="PipelineDebugLog" /> while isolating the
    /// process-wide pipeline category from the game logger factory.
    /// </summary>
    private sealed class CapturingPipelineLogFixture : IDisposable
    {
        private readonly List<CapturedLogEntry> _entries = [];
        private readonly Lock _entriesLock = new();

        public IReadOnlyList<CapturedLogEntry> Entries
        {
            get
            {
                lock (_entriesLock)
                {
                    return [.. _entries];
                }
            }
        }

        public void Install() => PipelineDebugLog.SetLoggerFactoryForTesting(new CapturingLoggerFactory(this));

        public void Dispose() => PipelineDebugLog.SetLoggerFactoryForTesting(null);

        private void Add(CapturedLogEntry entry)
        {
            lock (_entriesLock)
            {
                _entries.Add(entry);
            }
        }

        private sealed class CapturingLoggerFactory(CapturingPipelineLogFixture owner) : ILoggerFactory
        {
            public void AddProvider(ILoggerProvider provider)
            {
            }

            public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, owner);

            public void Dispose()
            {
            }
        }

        private sealed class CapturingLogger(string categoryName, CapturingPipelineLogFixture owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
                => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel is not LogLevel.None;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
                => owner.Add(new CapturedLogEntry(categoryName, logLevel, formatter(state, exception)));
        }
    }

    private sealed record CapturedLogEntry(string CategoryName, LogLevel Level, string Message);

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly List<CapturedLogEntry> _entries = [];
        private readonly Lock _entriesLock = new();
        private bool _disposed;

        public IReadOnlyList<CapturedLogEntry> Entries
        {
            get
            {
                lock (_entriesLock)
                {
                    return [.. _entries];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, this);

        public ILogger<T> CreateLogger<T>() => new RecordingLogger<T>(this);

        public void Dispose() => _disposed = true;

        private void Add(CapturedLogEntry entry)
        {
            if (_disposed)
            {
                return;
            }

            lock (_entriesLock)
            {
                _entries.Add(entry);
            }
        }

        private sealed class RecordingLogger(string categoryName, RecordingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
                => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel is not LogLevel.None;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                _ = eventId;
                owner.Add(new CapturedLogEntry(categoryName, logLevel, formatter(state, exception)));
            }
        }

        private sealed class RecordingLogger<T>(RecordingLoggerProvider owner) : ILogger<T>
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
                => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel is not LogLevel.None;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                _ = eventId;
                owner.Add(new CapturedLogEntry(typeof(T).FullName ?? typeof(T).Name, logLevel, formatter(state, exception)));
            }
        }
    }
}
