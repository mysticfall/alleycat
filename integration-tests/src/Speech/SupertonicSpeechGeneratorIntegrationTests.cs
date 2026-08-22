using System.Reflection;
using AlleyCat.Core;
using AlleyCat.Core.Logging;
using AlleyCat.Speech.Generation;
using AlleyCat.Speech.Generation.Supertonic;
using AlleyCat.TestFramework;
using Godot;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Speech;

/// <summary>
/// Runtime coverage for the offline Supertonic speech generator against the shipped model assets (SPCH-007).
/// </summary>
/// <remarks>
/// Synthesis facts reuse a single generator instance per process so the 245 MB vector-estimator graph is loaded
/// once per generator, and the suite as a whole performs at most five inference invocations.
/// </remarks>
public sealed partial class SupertonicSpeechGeneratorIntegrationTests
{
    private const int NativeSampleRate = 44100;

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
    /// Real local synthesis must produce whole PCM16 mono RIFF/WAVE utterances at the 44100 Hz vocoder rate and
    /// record the backend latency trace without generator-side sample-rate conversion.
    /// </summary>
    [Fact]
    [Headless]
    public async Task Generate_WithRealModel_ProducesPcm16MonoWave_AtNativeSampleRate()
    {
        SceneTree sceneTree = GetSceneTree();
        using CapturingPipelineLogFixture pipelineLog = new();
        pipelineLog.Install();

        SupertonicSpeechGenerator generator = new()
        {
            Name = "SupertonicSpeechGenerator",
        };

        sceneTree.Root.AddChild(generator);
        await WaitForFramesAsync(sceneTree, 2);

        try
        {
            byte[] audio = await generator.Generate("Hello there, friend.");

            AssertPcm16MonoWave(audio, NativeSampleRate);
            Assert.True(ReadWaveDataLength(audio) > NativeSampleRate / 2, "Expected at least half a second of synthesised audio.");

            CapturedLogEntry latencyEntry = Assert.Single(pipelineLog.Entries, entry => entry.Message.Contains("TTS backend returned in", StringComparison.Ordinal));
            Assert.Equal(LogLevel.Trace, latencyEntry.Level);
            Assert.Equal("AlleyCat.Pipeline", latencyEntry.CategoryName);
            Assert.Contains("TTS backend returned in", latencyEntry.Message, StringComparison.Ordinal);
            Assert.Contains("supertonic-3", latencyEntry.Message, StringComparison.Ordinal);

            byte[] secondAudio = await generator.Generate("Hello there, friend.");
            AssertPcm16MonoWave(secondAudio, NativeSampleRate);
            Assert.True(
                ReadWaveDataLength(secondAudio) > NativeSampleRate / 2,
                "Expected at least half a second of synthesised audio.");
        }
        finally
        {
            generator.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>
    /// A default CUDA request must either engage CUDA without a fallback warning or fully rebuild on CPU
    /// with exactly one warning, while recording the active backend as a pipeline trace (SPCH-007 AC 11-12).
    /// </summary>
    [Fact]
    [Headless]
    public async Task Generate_WithCudaRequest_ReportsConsistentActiveBackendAndFallbackDiagnostics()
    {
        SceneTree sceneTree = GetSceneTree();
        using CapturingPipelineLogFixture pipelineLog = new();
        using RecordingLoggerProvider loggerProvider = new();
        pipelineLog.Install();
        Game.Instance.GetRequiredService<ILoggerFactory>().AddProvider(loggerProvider);

        SupertonicSpeechGenerator generator = new()
        {
            Name = "SupertonicSpeechGenerator",
            ExecutionBackend = SupertonicExecutionBackend.Cuda,
        };

        sceneTree.Root.AddChild(generator);
        await WaitForFramesAsync(sceneTree, 2);

        try
        {
            byte[] audio = await generator.Generate("Hello there, friend.");

            AssertPcm16MonoWave(audio, NativeSampleRate);
            CapturedLogEntry backendEntry = Assert.Single(
                pipelineLog.Entries,
                entry => entry.Message.Contains("supertonic-3 backend", StringComparison.Ordinal));
            Assert.Equal(LogLevel.Trace, backendEntry.Level);
            Assert.Equal("AlleyCat.Pipeline", backendEntry.CategoryName);

            IReadOnlyList<CapturedLogEntry> fallbackWarnings =
            [
                .. loggerProvider.Entries.Where(entry => entry.CategoryName == typeof(SupertonicSpeechGenerator).FullName
                    && entry.Level == LogLevel.Warning
                    && entry.Message.Contains("execution backend was requested but could not be initialised", StringComparison.Ordinal)),
            ];

            if (backendEntry.Message.Contains("supertonic-3 backend cuda", StringComparison.Ordinal))
            {
                Assert.Empty(fallbackWarnings);
            }
            else
            {
                Assert.Contains("supertonic-3 backend cpu", backendEntry.Message, StringComparison.Ordinal);
                CapturedLogEntry fallbackWarning = Assert.Single(fallbackWarnings);
                Assert.Contains("Cuda", fallbackWarning.Message, StringComparison.Ordinal);
                Assert.Contains("Reason:", fallbackWarning.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            generator.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>
    /// A forced CUDA provider-append failure must rebuild the real generator pipeline on CPU, emit one
    /// fallback warning, and record CPU as the active backend (SPCH-007 AC 11-12).
    /// </summary>
    [Fact]
    [Headless]
    public async Task Generate_WithForcedCudaProviderAppendFailure_FallsBackToCpuWithSingleDiagnostics()
    {
        const string failureReason = "Deterministic CUDA provider append failure.";

        SceneTree sceneTree = GetSceneTree();
        using CapturingPipelineLogFixture pipelineLog = new();
        using RecordingLoggerProvider loggerProvider = new();
        using IDisposable cudaProviderOverride = SupertonicSessionFactory.OverrideCudaExecutionProviderForTesting(
            static _ => throw new DllNotFoundException(failureReason));
        pipelineLog.Install();

        SupertonicSpeechGenerator generator = new()
        {
            Name = "SupertonicSpeechGenerator",
            ExecutionBackend = SupertonicExecutionBackend.Cuda,
        };

        sceneTree.Root.AddChild(generator);
        await WaitForFramesAsync(sceneTree, 2);
        using IDisposable loggerOverride = generator.OverrideLoggerForTesting(
            loggerProvider.CreateLogger<SupertonicSpeechGenerator>());

        try
        {
            byte[] audio = await generator.Generate("Hello there, friend.");

            AssertPcm16MonoWave(audio, NativeSampleRate);

            CapturedLogEntry fallbackWarning = Assert.Single(loggerProvider.Entries);
            Assert.Equal(typeof(SupertonicSpeechGenerator).FullName, fallbackWarning.CategoryName);
            Assert.Equal(LogLevel.Warning, fallbackWarning.Level);
            Assert.Contains("Cuda", fallbackWarning.Message, StringComparison.Ordinal);
            Assert.Contains($"DllNotFoundException: {failureReason}", fallbackWarning.Message, StringComparison.Ordinal);

            CapturedLogEntry activeBackendEntry = Assert.Single(
                pipelineLog.Entries,
                entry => entry.CategoryName == "AlleyCat.Pipeline"
                    && entry.Level == LogLevel.Trace
                    && entry.Message.Contains("TTS backend engaged", StringComparison.Ordinal));
            Assert.Contains("supertonic-3 backend cpu", activeBackendEntry.Message, StringComparison.Ordinal);
        }
        finally
        {
            generator.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);
        }
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
        Game.Instance.GetRequiredService<ILoggerFactory>().AddProvider(loggerProvider);

        SupertonicSpeechGenerator generator = new()
        {
            Name = "SupertonicSpeechGenerator",
            ExecutionBackend = SupertonicExecutionBackend.Cpu,
        };

        sceneTree.Root.AddChild(generator);
        await WaitForFramesAsync(sceneTree, 2);

        try
        {
            byte[] audio = await generator.Generate("Hello there, friend.");

            AssertPcm16MonoWave(audio, NativeSampleRate);
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
            generator.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>
    /// Requesting an unknown voice must still complete generation through the warn-and-fallback default style.
    /// </summary>
    [Fact]
    [Headless]
    public async Task GenerateSpeech_WithUnknownVoice_FallsBackToDefaultStyle_AndEmitsCompletion()
    {
        SceneTree sceneTree = GetSceneTree();
        SupertonicSpeechGenerator generator = new()
        {
            Name = "SupertonicSpeechGenerator",
            Voice = "Z9",
        };

        sceneTree.Root.AddChild(generator);
        await WaitForFramesAsync(sceneTree, 2);

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
            AssertPcm16MonoWave(audio, NativeSampleRate);
        }
        finally
        {
            generator.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);
        }
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

    private static int ReadWaveDataLength(byte[] audio)
        => BitConverter.ToInt32(audio, 40);

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
