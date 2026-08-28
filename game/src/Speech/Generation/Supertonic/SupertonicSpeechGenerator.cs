using System.Diagnostics;
using AlleyCat.Core.Logging;
using AlleyCat.Speech.Generation.Supertonic;
using Godot;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Speech.Generation;

/// <summary>
/// Fully offline speech generator backed by local Supertonic ONNX inference.
/// </summary>
[GlobalClass]
public partial class SupertonicSpeechGenerator : SpeechGenerator
{
    private const string BackendDescription = "supertonic-3";

    private readonly Lock _pipelineLock = new();

    private ILogger<SupertonicSpeechGenerator>? _logger;
    private ISupertonicInferencePipeline? _pipeline;
    private Func<string, SupertonicExecutionBackend, ISupertonicInferencePipeline> _pipelineFactory = CreatePipeline;
    private string? _globalisedModelDirectory;
    private string? _globalisedVoiceStylesDirectory;
    private bool _disposed;

    /// <summary>
    /// Directory containing the Supertonic ONNX graphs, <c>tts.json</c>, and <c>unicode_indexer.json</c>.
    /// </summary>
    [ExportGroup("Assets")]
    [Export(PropertyHint.Dir)]
    public string ModelDirectoryPath
    {
        get;
        set;
    } = "res://models/supertonic-3/onnx";

    /// <summary>
    /// Directory containing the preset voice-style JSON files (F1-F5/M1-M5).
    /// </summary>
    [Export(PropertyHint.Dir)]
    public string VoiceStylesDirectoryPath
    {
        get;
        set;
    } = "res://models/supertonic-3/voice_styles";

    /// <summary>
    /// Preset voice style used when no override is supplied.
    /// </summary>
    [ExportGroup("Voice")]
    [Export]
    public string Voice
    {
        get;
        set;
    } = "M1";

    /// <summary>
    /// Optional per-node voice override taking precedence over <see cref="Voice" />.
    /// </summary>
    [Export]
    public string VoiceOverride
    {
        get;
        set;
    } = string.Empty;

    /// <summary>
    /// Language tag passed to synthesis, for example <c>en</c>.
    /// </summary>
    [Export]
    public string Language
    {
        get;
        set;
    } = "en";

    /// <summary>
    /// Speaking speed ratio; values above 1 speak faster.
    /// </summary>
    [Export]
    public float SpeedRatio
    {
        get;
        set;
    } = 1.0f;

    /// <summary>
    /// Flow-matching denoising iterations; higher values trade latency for quality.
    /// </summary>
    [ExportGroup("Quality")]
    [Export]
    public int QualitySteps
    {
        get;
        set;
    } = 8;

    /// <summary>
    /// ONNX execution backend to attempt; CUDA requests fall back to CPU together when the CUDA
    /// execution provider cannot be initialised on the host.
    /// </summary>
    [ExportGroup("Execution")]
    [Export]
    public SupertonicExecutionBackend ExecutionBackend
    {
        get;
        set;
    } = SupertonicExecutionBackend.Cuda;

    /// <inheritdoc />
    public override void _Ready()
    {
        base._Ready();
        _logger = GameLoggerResolver.ResolveRequired<SupertonicSpeechGenerator>();
    }

    /// <summary>
    /// Temporarily replaces the resolved logger for an isolated test instance.
    /// </summary>
    /// <remarks>
    /// This narrow hook is internal so scenes always resolve their logger from the active game service provider.
    /// </remarks>
    internal IDisposable OverrideLoggerForTesting(ILogger<SupertonicSpeechGenerator> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ILogger<SupertonicSpeechGenerator>? previousLogger = _logger;
        _logger = logger;
        return new LoggerOverride(this, previousLogger);
    }

    /// <summary>
    /// Temporarily replaces the inference-pipeline factory for this generator instance.
    /// </summary>
    /// <remarks>
    /// This hook must be installed before the first generation request, so cached production pipelines cannot be
    /// replaced. It is internal to keep test doubles out of production scene configuration.
    /// </remarks>
    internal IDisposable OverridePipelineFactoryForTesting(
        Func<string, SupertonicExecutionBackend, ISupertonicInferencePipeline> pipelineFactory)
    {
        ArgumentNullException.ThrowIfNull(pipelineFactory);

        lock (_pipelineLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_pipeline is not null)
            {
                throw new InvalidOperationException("The Supertonic inference pipeline has already been created.");
            }

            Func<string, SupertonicExecutionBackend, ISupertonicInferencePipeline> previousFactory = _pipelineFactory;
            _pipelineFactory = pipelineFactory;
            return new PipelineFactoryOverride(this, previousFactory, pipelineFactory);
        }
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        base._ExitTree();

        lock (_pipelineLock)
        {
            _disposed = true;
            _pipeline?.Dispose();
            _pipeline = null;
        }
    }

    /// <inheritdoc />
    protected override async Task<byte[]> GenerateCore(string text, string? instruction = null)
    {
        // SPCH-004 permits backends to ignore instructions; Supertonic exposes no expression channel.
        _ = instruction;

        string modelDirectory = _globalisedModelDirectory ??= ProjectSettings.GlobalizePath(ModelDirectoryPath);
        string voiceStylesDirectory =
            _globalisedVoiceStylesDirectory ??= ProjectSettings.GlobalizePath(VoiceStylesDirectoryPath);

        Stopwatch backendStopwatch = PipelineDebugLog.StartTimer();

        byte[] wavAudio = await Task.Run(() =>
        {
            ISupertonicInferencePipeline pipeline = GetOrCreatePipeline(modelDirectory);
            SupertonicVoiceStyle voiceStyle = LoadVoiceStyle(voiceStylesDirectory);

            float[] samples = pipeline.Synthesise(new SupertonicSynthesisRequest(
                text,
                Language.Trim(),
                voiceStyle,
                QualitySteps,
                SpeedRatio));

            return SupertonicWaveWriter.Write(samples, pipeline.SampleRate);
        });

        if (PipelineDebugLog.IsEnabled)
        {
            PipelineDebugLog.LogOnlyLatency(
                "TTS backend returned in",
                backendStopwatch,
                $"model {BackendDescription} ({QualitySteps} flow-matching steps)");
        }

        return wavAudio;
    }

    private ISupertonicInferencePipeline GetOrCreatePipeline(string modelDirectory)
    {
        lock (_pipelineLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_pipeline is null)
            {
                _pipeline = _pipelineFactory(modelDirectory, ExecutionBackend);
                LogExecutionBackend(modelDirectory, _pipeline);
            }

            return _pipeline;
        }
    }

    private void LogExecutionBackend(
        string modelDirectory,
        ISupertonicInferencePipeline pipeline)
    {
        if (ExecutionBackend != pipeline.ActiveBackend)
        {
            _logger?.LogWarning(
                "Supertonic speech pipeline initialised from '{ModelDirectory}'; the {RequestedBackend} " +
                "execution backend was requested but could not be initialised, falling back to " +
                "{ActiveBackend}. Reason: {FallbackReason}",
                modelDirectory,
                ExecutionBackend,
                pipeline.ActiveBackend,
                pipeline.BackendFallbackReason);
        }
        else
        {
            _logger?.LogInformation(
                "Supertonic speech pipeline initialised from '{ModelDirectory}' with the {ActiveBackend} execution backend.",
                modelDirectory,
                pipeline.ActiveBackend);
        }

        if (PipelineDebugLog.IsEnabled)
        {
            PipelineDebugLog.Stage(
                "TTS backend engaged",
                $"{BackendDescription} backend {pipeline.ActiveBackend.ToString().ToLowerInvariant()}");
        }
    }

    private SupertonicVoiceStyle LoadVoiceStyle(string voiceStylesDirectory)
    {
        SupertonicVoiceStyleResolution resolution = SupertonicVoiceStyleResolver.Resolve(
            voiceStylesDirectory,
            VoiceOverride,
            Voice);

        if (resolution.UsedFallback)
        {
            _logger?.LogWarning(
                "Supertonic voice style '{RequestedName}' was not found in '{VoiceStylesDirectory}'; falling back to '{ResolvedName}'.",
                resolution.RequestedName,
                VoiceStylesDirectoryPath,
                resolution.ResolvedName);
        }

        return !File.Exists(resolution.FilePath)
            ? throw new InvalidOperationException(
                $"Supertonic voice style '{resolution.ResolvedName}' was not found at '{resolution.FilePath}'.")
            : SupertonicVoiceStyle.Load(resolution.FilePath);
    }

    private static ISupertonicInferencePipeline CreatePipeline(
        string modelDirectory,
        SupertonicExecutionBackend executionBackend)
        => SupertonicInferencePipeline.Create(modelDirectory, executionBackend);

    private sealed class LoggerOverride(
        SupertonicSpeechGenerator generator,
        ILogger<SupertonicSpeechGenerator>? previousLogger) : IDisposable
    {
        private SupertonicSpeechGenerator? _generator = generator;

        public void Dispose()
        {
            if (_generator is not { } generator)
            {
                return;
            }

            generator._logger = previousLogger;
            _generator = null;
        }
    }

    private sealed class PipelineFactoryOverride(
        SupertonicSpeechGenerator generator,
        Func<string, SupertonicExecutionBackend, ISupertonicInferencePipeline> previousFactory,
        Func<string, SupertonicExecutionBackend, ISupertonicInferencePipeline> installedFactory) : IDisposable
    {
        private SupertonicSpeechGenerator? _generator = generator;

        public void Dispose()
        {
            if (_generator is not { } generator)
            {
                return;
            }

            lock (generator._pipelineLock)
            {
                if (ReferenceEquals(generator._pipelineFactory, installedFactory))
                {
                    generator._pipelineFactory = previousFactory;
                }
            }

            _generator = null;
        }
    }
}
