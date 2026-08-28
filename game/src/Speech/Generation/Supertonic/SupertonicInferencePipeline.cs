// Adapted from the Supertonic C# reference implementation (csharp/ExampleONNX.cs and csharp/Helper.cs),
// Copyright (c) 2025 Supertone Inc. Licensed under the MIT License.
// https://github.com/supertone-inc/supertonic
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AlleyCat.Speech.Generation.Supertonic;

/// <summary>
/// A single whole-utterance synthesis request for <see cref="SupertonicInferencePipeline" />.
/// </summary>
/// <param name="Text">Utterance text to synthesise.</param>
/// <param name="Language">Language tag, for example <c>en</c>.</param>
/// <param name="VoiceStyle">Preset voice style tensors.</param>
/// <param name="QualitySteps">Flow-matching denoising iterations.</param>
/// <param name="SpeedRatio">Speaking speed ratio; values above 1 speak faster.</param>
public sealed record SupertonicSynthesisRequest(
    string Text,
    string Language,
    SupertonicVoiceStyle VoiceStyle,
    int QualitySteps,
    float SpeedRatio);

/// <summary>
/// The narrow Supertonic pipeline boundary required by <see cref="SupertonicSpeechGenerator" />.
/// </summary>
internal interface ISupertonicInferencePipeline : IDisposable
{
    int SampleRate
    {
        get;
    }

    SupertonicExecutionBackend ActiveBackend
    {
        get;
    }

    string? BackendFallbackReason
    {
        get;
    }

    float[] Synthesise(SupertonicSynthesisRequest request);
}

/// <summary>
/// Executes the five-stage Supertonic synthesis pipeline over local ONNX graphs:
/// unicode tokenisation → duration prediction → text encoding → flow-matching sampling → vocoding.
/// </summary>
/// <remarks>
/// Instances are initialised from pre-loaded ONNX sessions plus the synthesis constants parsed from
/// <c>tts.json</c> and a text processor bound to the model's unicode indexer. Instances own their ONNX
/// sessions and must be disposed deterministically. All methods are safe to invoke from background
/// threads provided no two calls overlap on a single instance.
/// </remarks>
/// <param name="durationPredictor">Duration-predictor graph session.</param>
/// <param name="textEncoder">Text-encoder graph session.</param>
/// <param name="vectorEstimator">Vector-estimator graph session.</param>
/// <param name="vocoder">Vocoder graph session.</param>
/// <param name="config">Synthesis constants parsed from <c>tts.json</c>.</param>
/// <param name="textProcessor">Text processor bound to the model's unicode indexer.</param>
public sealed class SupertonicInferencePipeline(
    InferenceSession durationPredictor,
    InferenceSession textEncoder,
    InferenceSession vectorEstimator,
    InferenceSession vocoder,
    SupertonicModelConfig config,
    SupertonicTextProcessor textProcessor) : ISupertonicInferencePipeline
{
    private const string DurationPredictorModelFile = "duration_predictor.onnx";
    private const string TextEncoderModelFile = "text_encoder.onnx";
    private const string VectorEstimatorModelFile = "vector_estimator.onnx";
    private const string VocoderModelFile = "vocoder.onnx";
    private const string ConfigFileName = "tts.json";
    private const string UnicodeIndexerFileName = "unicode_indexer.json";

    private const string TextIdsInput = "text_ids";
    private const string TextMaskInput = "text_mask";
    private const string StyleDpInput = "style_dp";
    private const string StyleTtlInput = "style_ttl";
    private const string NoisyLatentInput = "noisy_latent";
    private const string TextEmbInput = "text_emb";
    private const string LatentMaskInput = "latent_mask";
    private const string TotalStepInput = "total_step";
    private const string CurrentStepInput = "current_step";
    private const string LatentInput = "latent";

    private const string DurationOutput = "duration";
    private const string TextEmbOutput = "text_emb";
    private const string DenoisedLatentOutput = "denoised_latent";
    private const string WavTtsOutput = "wav_tts";

    /// <summary>
    /// Maximum character count per synthesis chunk for non-CJK languages, matching the reference limits.
    /// </summary>
    public const int MaximumChunkLength = 300;

    /// <summary>
    /// Maximum character count per synthesis chunk for Japanese or Korean text.
    /// </summary>
    public const int MaximumCjkChunkLength = 120;

    /// <summary>
    /// Silence inserted between synthesised text chunks, in seconds.
    /// </summary>
    public const float InterChunkSilenceSeconds = 0.3f;

    private static readonly Regex _paragraphSplitPattern = new(@"\n\s*\n+", RegexOptions.Compiled);

    private static readonly Regex _sentenceBoundaryPattern = new(
        @"(?<!Mr\.|Mrs\.|Ms\.|Dr\.|Prof\.|Sr\.|Jr\.|Ph\.D\.|etc\.|e\.g\.|i\.e\.|vs\.|Inc\.|Ltd\.|Co\.|Corp\.|St\.|Ave\.|Blvd\.)(?<!\b[A-Z]\.)(?<=[.!?])\s+",
        RegexOptions.Compiled);

    /// <summary>
    /// Gets the audio sample rate produced by the vocoder, in hertz.
    /// </summary>
    public int SampleRate => config.SampleRate;

    /// <summary>
    /// Gets the backend whose session initialisation completed successfully.
    /// </summary>
    public SupertonicExecutionBackend ActiveBackend
    {
        get;
        private set;
    } = SupertonicExecutionBackend.Cpu;

    /// <summary>
    /// Gets the CUDA initialisation failure that caused a CPU fallback, or <see langword="null" />
    /// when the requested backend was engaged.
    /// </summary>
    public string? BackendFallbackReason
    {
        get;
        private set;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        durationPredictor.Dispose();
        textEncoder.Dispose();
        vectorEstimator.Dispose();
        vocoder.Dispose();
    }

    /// <summary>
    /// Creates a pipeline from the Supertonic ONNX asset directory.
    /// </summary>
    /// <param name="modelDirectory">Globalised directory containing the ONNX graphs and metadata files.</param>
    /// <param name="executionBackend">
    /// Execution backend to attempt; defaults to <see cref="SupertonicExecutionBackend.Cuda" />. When CUDA
    /// cannot be initialised, every session is rebuilt together on CPU so providers are never mixed.
    /// </param>
    /// <returns>The initialised pipeline, including its active-backend metadata.</returns>
    /// <exception cref="InvalidOperationException">Thrown when a required model asset is missing.</exception>
    public static SupertonicInferencePipeline Create(
        string modelDirectory,
        SupertonicExecutionBackend executionBackend = SupertonicExecutionBackend.Cuda)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);

        var modelConfig = SupertonicModelConfig.Load(RequireAsset(modelDirectory, ConfigFileName));
        var indexer = SupertonicUnicodeIndexer.Load(RequireAsset(modelDirectory, UnicodeIndexerFileName));

        SupertonicExecutionBackendResolution<SupertonicInferencePipeline> resolution = SupertonicSessionFactory.Create(
            executionBackend,
            (sessionOptions, _) => CreateSessions(
                RequireAsset(modelDirectory, DurationPredictorModelFile),
                RequireAsset(modelDirectory, TextEncoderModelFile),
                RequireAsset(modelDirectory, VectorEstimatorModelFile),
                RequireAsset(modelDirectory, VocoderModelFile),
                modelConfig,
                indexer,
                sessionOptions));

        resolution.Value.ActiveBackend = resolution.ActiveBackend;
        resolution.Value.BackendFallbackReason = resolution.FallbackReason;
        return resolution.Value;
    }

    private static SupertonicInferencePipeline CreateSessions(
        string durationPredictorPath,
        string textEncoderPath,
        string vectorEstimatorPath,
        string vocoderPath,
        SupertonicModelConfig modelConfig,
        SupertonicUnicodeIndexer indexer,
        SessionOptions sessionOptions)
    {
        // Every session shares one options build so a backend attempt applies to the whole pipeline
        // atomically; partially created sessions are disposed before rethrowing so the CPU fallback
        // rebuild starts clean.
        InferenceSession? durationPredictor = null;
        InferenceSession? textEncoder = null;
        InferenceSession? vectorEstimator = null;
        InferenceSession? vocoder = null;
        try
        {
            durationPredictor = new InferenceSession(durationPredictorPath, sessionOptions);
            textEncoder = new InferenceSession(textEncoderPath, sessionOptions);
            vectorEstimator = new InferenceSession(vectorEstimatorPath, sessionOptions);
            vocoder = new InferenceSession(vocoderPath, sessionOptions);

            return new SupertonicInferencePipeline(
                durationPredictor,
                textEncoder,
                vectorEstimator,
                vocoder,
                modelConfig,
                new SupertonicTextProcessor(indexer));
        }
        catch
        {
            durationPredictor?.Dispose();
            textEncoder?.Dispose();
            vectorEstimator?.Dispose();
            vocoder?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Synthesises the requested utterance into mono float32 PCM samples at <see cref="SampleRate" />.
    /// </summary>
    /// <param name="request">The synthesis request.</param>
    /// <returns>Vocoder output samples covering every text chunk plus inter-chunk silence.</returns>
    public float[] Synthesise(SupertonicSynthesisRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.VoiceStyle);
        ValidateQualitySteps(request.QualitySteps);

        if (request.SpeedRatio <= 0f)
        {
            throw new InvalidOperationException(
                $"Supertonic speed ratio must be greater than zero. Got {request.SpeedRatio.ToString(CultureInfo.InvariantCulture)}.");
        }

        List<string> chunks = SplitIntoChunks(request.Text, ResolveMaximumChunkLength(request.Language));
        List<float> samples = [];

        foreach (string chunk in chunks)
        {
            if (samples.Count > 0)
            {
                samples.AddRange(new float[(int)(InterChunkSilenceSeconds * SampleRate)]);
            }

            samples.AddRange(InferChunk(
                chunk,
                request.Language,
                request.VoiceStyle,
                request.QualitySteps,
                request.SpeedRatio));
        }

        return [.. samples];
    }

    internal static void ValidateQualitySteps(int qualitySteps)
    {
        if (qualitySteps < 1)
        {
            throw new InvalidOperationException($"Supertonic quality steps must be at least 1. Got {qualitySteps}.");
        }
    }

    internal static List<string> SplitIntoChunks(string text, int maximumChunkLength)
    {
        List<string> chunks = [];
        IEnumerable<string> paragraphs = _paragraphSplitPattern
            .Split(text.Trim())
            .Select(paragraph => paragraph.Trim())
            .Where(paragraph => !string.IsNullOrEmpty(paragraph));

        foreach (string paragraph in paragraphs)
        {
            string currentChunk = string.Empty;

            foreach (string sentence in _sentenceBoundaryPattern.Split(paragraph))
            {
                if (string.IsNullOrEmpty(sentence))
                {
                    continue;
                }

                if (currentChunk.Length + sentence.Length + 1 <= maximumChunkLength)
                {
                    currentChunk = string.IsNullOrEmpty(currentChunk)
                        ? sentence
                        : $"{currentChunk} {sentence}";
                }
                else
                {
                    if (!string.IsNullOrEmpty(currentChunk))
                    {
                        chunks.Add(currentChunk.Trim());
                    }

                    currentChunk = sentence;
                }
            }

            if (!string.IsNullOrEmpty(currentChunk))
            {
                chunks.Add(currentChunk.Trim());
            }
        }

        if (chunks.Count == 0)
        {
            chunks.Add(text.Trim());
        }

        return chunks;
    }

    private static string RequireAsset(string modelDirectory, string fileName)
    {
        string assetPath = Path.Combine(modelDirectory, fileName);
        return File.Exists(assetPath)
            ? assetPath
            : throw new InvalidOperationException($"Supertonic model asset '{fileName}' was not found in '{modelDirectory}'.");
    }

    private static int ResolveMaximumChunkLength(string language)
        => language is "ko" or "ja" ? MaximumCjkChunkLength : MaximumChunkLength;

    private float[] InferChunk(
        string chunkText,
        string language,
        SupertonicVoiceStyle voiceStyle,
        int qualitySteps,
        float speedRatio)
    {
        SupertonicTextEncoding encoding = textProcessor.Encode(chunkText, language);
        var textIdsTensor = new DenseTensor<long>(encoding.TokenIds, [1, encoding.TokenIds.Length]);
        var textMaskTensor = new DenseTensor<float>(encoding.PaddingMask, [1, 1, encoding.PaddingMask.Length]);
        var styleTtlTensor = new DenseTensor<float>(voiceStyle.TtlData, ToDimensions(voiceStyle.TtlShape));
        var styleDpTensor = new DenseTensor<float>(voiceStyle.DpData, ToDimensions(voiceStyle.DpShape));

        float[] durations = PredictDurations(textIdsTensor, textMaskTensor, styleDpTensor, speedRatio);
        Tensor<float> textEmbedding = EncodeText(textIdsTensor, textMaskTensor, styleTtlTensor);

        float[] latentBuffer = SampleNoisyLatent(durations[^1] * SampleRate);

        RunFlowMatchingLoop(
            latentBuffer,
            textEmbedding,
            textMaskTensor,
            styleTtlTensor,
            qualitySteps);

        float[] waveform = DecodeWaveform(latentBuffer);
        return TrimToExpectedLength(waveform, durations[^1]);
    }

    private float[] PredictDurations(
        DenseTensor<long> textIdsTensor,
        DenseTensor<float> textMaskTensor,
        DenseTensor<float> styleDpTensor,
        float speedRatio)
    {
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = durationPredictor.Run(
        [
            NamedOnnxValue.CreateFromTensor(TextIdsInput, textIdsTensor),
            NamedOnnxValue.CreateFromTensor(StyleDpInput, styleDpTensor),
            NamedOnnxValue.CreateFromTensor(TextMaskInput, textMaskTensor),
        ]);

        float[] durations = [.. RequireTensor(outputs, DurationOutput)];

        if (durations.Length == 0)
        {
            throw new InvalidOperationException("Supertonic duration predictor produced no values.");
        }

        for (int index = 0; index < durations.Length; index++)
        {
            durations[index] /= speedRatio;
        }

        return durations;
    }

    private Tensor<float> EncodeText(
        DenseTensor<long> textIdsTensor,
        DenseTensor<float> textMaskTensor,
        DenseTensor<float> styleTtlTensor)
    {
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = textEncoder.Run(
        [
            NamedOnnxValue.CreateFromTensor(TextIdsInput, textIdsTensor),
            NamedOnnxValue.CreateFromTensor(StyleTtlInput, styleTtlTensor),
            NamedOnnxValue.CreateFromTensor(TextMaskInput, textMaskTensor),
        ]);

        Tensor<float> textEmb = RequireTensor(outputs, TextEmbOutput);

        // Copy into an owned tensor while preserving the graph's native [batch, embedding, time]
        // layout: the vector estimator validates this exact shape, and the source buffer's lifetime
        // is tied to the disposed results collection.
        float[] values = [.. textEmb];
        return new DenseTensor<float>(values, textEmb.Dimensions.ToArray());
    }

    private float[] SampleNoisyLatent(float maximumWaveformLength)
    {
        int chunkSize = config.BaseChunkSize * config.ChunkCompressFactor;
        int latentDim = config.LatentDim * config.ChunkCompressFactor;

        long expectedSampleCount = (long)maximumWaveformLength;
        if (expectedSampleCount <= 0)
        {
            throw new InvalidOperationException("Supertonic duration prediction produced an empty utterance.");
        }

        // Latent frames cover whole vocoder chunks; the trailing partial chunk is padding.
        int latentLength = (int)((expectedSampleCount + chunkSize - 1) / chunkSize);

        Random random = Random.Shared;
        float[] latentBuffer = new float[latentDim * latentLength];
        for (int channel = 0; channel < latentDim; channel++)
        {
            for (int frame = 0; frame < latentLength; frame++)
            {
                double u1 = 1.0 - random.NextDouble();
                double u2 = 1.0 - random.NextDouble();
                latentBuffer[(channel * latentLength) + frame] =
                    (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
            }
        }

        return latentBuffer;
    }

    private void RunFlowMatchingLoop(
        float[] latentBuffer,
        Tensor<float> textEmbeddingTensor,
        DenseTensor<float> textMaskTensor,
        DenseTensor<float> styleTtlTensor,
        int qualitySteps)
    {
        int latentDim = config.LatentDim * config.ChunkCompressFactor;
        int latentLength = latentBuffer.Length / latentDim;

        // Single-utterance batches carry no padding, so the latent mask is fully enabled.
        float[] latentMask = new float[latentLength];
        Array.Fill(latentMask, 1f);
        var latentMaskTensor = new DenseTensor<float>(latentMask, [1, 1, latentLength]);

        for (int step = 0; step < qualitySteps; step++)
        {
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = vectorEstimator.Run(
            [
                NamedOnnxValue.CreateFromTensor(NoisyLatentInput, WrapLatent(latentBuffer, latentDim, latentLength)),
                NamedOnnxValue.CreateFromTensor(TextEmbInput, textEmbeddingTensor),
                NamedOnnxValue.CreateFromTensor(StyleTtlInput, styleTtlTensor),
                NamedOnnxValue.CreateFromTensor(TextMaskInput, textMaskTensor),
                NamedOnnxValue.CreateFromTensor(LatentMaskInput, latentMaskTensor),
                NamedOnnxValue.CreateFromTensor(TotalStepInput, StepTensor(qualitySteps)),
                NamedOnnxValue.CreateFromTensor(CurrentStepInput, StepTensor(step)),
            ]);

            float[] denoisedLatent = [.. RequireTensor(outputs, DenoisedLatentOutput)];
            if (denoisedLatent.Length != latentBuffer.Length)
            {
                throw new InvalidOperationException(
                    $"Supertonic vector estimator returned {denoisedLatent.Length} values, expected {latentBuffer.Length}.");
            }

            Array.Copy(denoisedLatent, latentBuffer, latentBuffer.Length);
        }
    }

    private float[] DecodeWaveform(float[] latentBuffer)
    {
        int latentDim = config.LatentDim * config.ChunkCompressFactor;
        int latentLength = latentBuffer.Length / latentDim;

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = vocoder.Run(
        [
            NamedOnnxValue.CreateFromTensor(LatentInput, WrapLatent(latentBuffer, latentDim, latentLength)),
        ]);

        float[] waveform = [.. RequireTensor(outputs, WavTtsOutput)];
        return waveform.Length > 0
            ? waveform
            : throw new InvalidOperationException("Supertonic vocoder produced no audio samples.");
    }

    private float[] TrimToExpectedLength(float[] waveform, float durationSeconds)
    {
        int expectedSampleCount = (int)(durationSeconds * SampleRate);
        int trimmedSampleCount = Math.Clamp(expectedSampleCount, 1, waveform.Length);
        float[] trimmedWaveform = new float[trimmedSampleCount];
        Array.Copy(waveform, trimmedWaveform, trimmedSampleCount);
        return trimmedWaveform;
    }

    private static DenseTensor<float> WrapLatent(float[] latentBuffer, int latentDim, int latentLength)
        => new(latentBuffer, [1, latentDim, latentLength]);

    private static DenseTensor<float> StepTensor(int step)
    {
        float[] stepValue = [step];
        return new DenseTensor<float>(stepValue, [1]);
    }

    private static int[] ToDimensions(long[] shape)
    {
        int[] dimensions = new int[shape.Length];
        for (int index = 0; index < shape.Length; index++)
        {
            dimensions[index] = checked((int)shape[index]);
        }

        return dimensions;
    }

    private static Tensor<float> RequireTensor(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs,
        string outputName)
        => outputs.FirstOrDefault(output => string.Equals(output.Name, outputName, StringComparison.Ordinal))?.AsTensor<float>()
            ?? throw new InvalidOperationException($"Supertonic ONNX graph did not produce the '{outputName}' output.");
}
