using System.Diagnostics;
using AlleyCat.Core.Logging;
using AlleyCat.Models;
using AlleyCat.Speech.Generation.Supertonic;
using Godot;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Speech.Generation;

/// <summary>
/// Fully offline speech generator backed by local Supertonic ONNX inference.
/// </summary>
/// <remarks>
/// The inference pipeline is a shared backend under CORE-011 (SPCH-007 TR-16): generators with the same globalised
/// model directory and requested <see cref="ExecutionBackend" /> share one loaded pipeline through per-generator
/// handles, while voice, language, speed and quality steps travel with each request.
/// </remarks>
[GlobalClass]
public partial class SupertonicSpeechGenerator : SpeechGenerator
{
    private const string BackendDescription = "supertonic-3";

    private readonly Lock _pipelineLock = new();

    private ILogger<SupertonicSpeechGenerator>? _logger;
    private SupertonicPipelineHandle? _pipeline;
    private Func<string, SupertonicExecutionBackend, ISupertonicInferencePipeline> _pipelineFactory = CreatePipeline;
    private bool _pipelineFactoryOverridden;
    private ISharedModelRegistry? _sharedModels;
    private SharedModelRegistry? _ownedSharedModels;
    private string? _suppliedContentLanguage;
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
    /// Supplies the spoken language resolved from the current content context (CORE-008) to this generator.
    /// </summary>
    /// <remarks>
    /// The supplied language takes precedence over the exported <see cref="Language" /> property and stays fixed for
    /// the generator's lifetime: a second supply must repeat the same value. The wiring that prepares synthesis calls
    /// this once on the Godot thread before the first generation; standalone instances, fixtures, and direct tests
    /// that supply no language keep the exported property authoritative. Internal so the supply stays a wiring hook
    /// rather than a public runtime-switching API.
    /// </remarks>
    /// <param name="language">Canonical spoken-language code, for example <c>en</c> or <c>ko</c>.</param>
    internal void SupplyContentLanguage(string language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        string canonical = language.Trim();

        lock (_pipelineLock)
        {
            if (_suppliedContentLanguage is { } existing)
            {
                if (!string.Equals(existing, canonical, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Supertonic speech language is fixed for the generator's lifetime; cannot change "
                        + $"'{existing}' to '{canonical}'.");
                }

                return;
            }

            _suppliedContentLanguage = canonical;
        }
    }

    /// <summary>
    /// Language reaching the inference pipeline: the supplied content-pack language when gameplay wiring supplied
    /// one, otherwise the exported <see cref="Language" /> property. Narrow internal seam for lifecycle coverage.
    /// </summary>
    internal string EffectiveLanguage => (_suppliedContentLanguage ?? Language).Trim();

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
    /// replaced. Unless a registry was injected through <see cref="OverrideSharedModelsForTesting" />, an overridden
    /// factory loads into a registry owned by this generator, so test pipelines never enter the session-wide registry;
    /// that owned registry is shut down, disposing its pipeline, when the generator leaves the tree. It is internal to
    /// keep test doubles out of production scene configuration.
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
            bool previousOverridden = _pipelineFactoryOverridden;
            _pipelineFactory = pipelineFactory;
            _pipelineFactoryOverridden = true;
            return new PipelineFactoryOverride(this, previousFactory, previousOverridden, pipelineFactory);
        }
    }

    /// <summary>
    /// Supplies the shared model registry this generator resolves its pipeline from, instead of the
    /// <see cref="Game" />-owned registry (CORE-011 TR5). Must be called before the first generation request; the
    /// caller owns the registry's lifetime.
    /// </summary>
    /// <param name="sharedModels">Registry to resolve the shared pipeline from.</param>
    internal void OverrideSharedModelsForTesting(ISharedModelRegistry sharedModels)
    {
        ArgumentNullException.ThrowIfNull(sharedModels);

        lock (_pipelineLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_pipeline is not null)
            {
                throw new InvalidOperationException("The Supertonic inference pipeline has already been created.");
            }

            _sharedModels = sharedModels;
        }
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        base._ExitTree();

        SharedModelRegistry? ownedSharedModels;
        lock (_pipelineLock)
        {
            _disposed = true;
            // Releasing the handle never unloads the shared pipeline (CORE-011 TR10, TR22).
            _pipeline?.Dispose();
            _pipeline = null;
            ownedSharedModels = _ownedSharedModels;
            _ownedSharedModels = null;
        }

        // Only a test-owned private registry is shut down here; Stop never blocks the scene thread.
        _ = ownedSharedModels?.Stop();
    }

    /// <inheritdoc />
    protected override async Task<byte[]> GenerateCore(string text, string? instruction = null)
    {
        // SPCH-004 permits backends to ignore instructions; Supertonic exposes no expression channel.
        _ = instruction;

        string modelDirectory = _globalisedModelDirectory ??= ProjectSettings.GlobalizePath(ModelDirectoryPath);
        string voiceStylesDirectory =
            _globalisedVoiceStylesDirectory ??= ProjectSettings.GlobalizePath(VoiceStylesDirectoryPath);
        string effectiveLanguage = EffectiveLanguage;

        Stopwatch backendStopwatch = PipelineDebugLog.StartTimer();

        // Resolving the handle never blocks: the shared pipeline loads off the calling thread on first request.
        SupertonicPipelineHandle pipeline = GetOrCreatePipeline(modelDirectory);
        int qualitySteps = QualitySteps;
        float speedRatio = SpeedRatio;

        // A pipeline load failure, including a cached one, surfaces before any per-request work and faults this call;
        // the inherited failure path reports it.
        SupertonicSynthesisResult synthesis = await pipeline.SynthesiseWhenLoadedAsync(() => new SupertonicSynthesisRequest(
            text,
            effectiveLanguage,
            LoadVoiceStyle(voiceStylesDirectory),
            qualitySteps,
            speedRatio));

        byte[] wavAudio = await Task.Run(() => SupertonicWaveWriter.Write(synthesis.Samples, synthesis.SampleRate));

        if (PipelineDebugLog.IsEnabled)
        {
            PipelineDebugLog.LogOnlyLatency(
                "TTS backend returned in",
                backendStopwatch,
                $"model {BackendDescription} ({QualitySteps} flow-matching steps)");
        }

        return wavAudio;
    }

    private SupertonicPipelineHandle GetOrCreatePipeline(string modelDirectory)
    {
        lock (_pipelineLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_pipeline is not null)
            {
                return _pipeline;
            }

            ISharedModelRegistry registry = ResolveSharedModels();
            var definition = new SupertonicPipelineDefinition(
                SupertonicPipelineKey.Create(modelDirectory, ExecutionBackend),
                _pipelineFactory,
                CreateLoadDiagnostics(_logger));
            ISharedModel<ISupertonicInferencePipeline> model = registry.Resolve(definition);
            // The registry logs load and execution events per key; the handle adds no per-generator load diagnostics.
            return _pipeline = new SupertonicPipelineHandle(model, Name);
        }
    }

    private ISharedModelRegistry ResolveSharedModels()
        => _sharedModels
            ?? (_pipelineFactoryOverridden
                ? _ownedSharedModels ??= new SharedModelRegistry(ResolveRegistryLogger)
                : _sharedModels = Game.Instance.GetRequiredService<ISharedModelRegistry>());

    private static ILogger? ResolveRegistryLogger()
        => GameLoggerResolver.TryResolveFactory(out ILoggerFactory? factory)
            ? factory!.CreateLogger<SharedModelRegistry>()
            : null;

    /// <summary>
    /// Creates the once-per-backend load diagnostics. Only the definition that actually loads the shared pipeline runs
    /// it, so the CUDA fallback warning is logged once per shared backend rather than once per generator.
    /// </summary>
    private static Action<SupertonicPipelineKey, ISupertonicInferencePipeline> CreateLoadDiagnostics(ILogger? logger)
        => (key, pipeline) =>
        {
            if (key.RequestedBackend != pipeline.ActiveBackend)
            {
                logger?.LogWarning(
                    "Supertonic speech pipeline initialised from '{ModelDirectory}'; the {RequestedBackend} " +
                    "execution backend was requested but could not be initialised, falling back to " +
                    "{ActiveBackend}. Reason: {FallbackReason}",
                    key.ModelDirectory,
                    key.RequestedBackend,
                    pipeline.ActiveBackend,
                    pipeline.BackendFallbackReason);
            }
            else
            {
                logger?.LogInformation(
                    "Supertonic speech pipeline initialised from '{ModelDirectory}' with the {ActiveBackend} execution backend.",
                    key.ModelDirectory,
                    pipeline.ActiveBackend);
            }

            if (PipelineDebugLog.IsEnabled)
            {
                PipelineDebugLog.Stage(
                    "TTS backend engaged",
                    $"{BackendDescription} backend {pipeline.ActiveBackend.ToString().ToLowerInvariant()}");
            }
        };

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
        bool previousOverridden,
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
                    generator._pipelineFactoryOverridden = previousOverridden;
                }
            }

            _generator = null;
        }
    }
}
