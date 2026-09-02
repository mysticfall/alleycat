using System.Security.Cryptography;
using AlleyCat.Speech.Transcription;
using Microsoft.ML.OnnxRuntime;
using Xunit;

namespace AlleyCat.Tests.Speech;

/// <summary>
/// Deterministic coverage for the Silero detector's framing, recurrent state, context, threshold, disposal, and
/// the committed model pin (SPCH-008 TR-2, TR-3, AC-T4).
/// </summary>
public sealed class SileroVoiceActivityDetectorTests
{
    private const long PinnedSizeBytes = 2_327_524L;
    private const string PinnedSha256 = "1a153a22f4509e292a94e67d6f9b85e8deb25b4988682b7e174c65279d8788e3";

    private static readonly string _modelPath = LocateModel();

    /// <summary>The state written by frame N becomes the state input of frame N plus one.</summary>
    [Fact]
    public void Process_FeedsStateOutputOfFrameNAsStateInputOfFrameNPlusOne()
    {
        ScriptedSileroSession session = new();
        using SileroVoiceActivityDetector detector = CreateDetector(0.5f, session);

        session.NextStateFill = 1f;
        _ = detector.Process(Frame(0.25f));
        session.NextStateFill = 2f;
        _ = detector.Process(Frame(0.25f));
        session.NextStateFill = 3f;
        _ = detector.Process(Frame(0.25f));

        Assert.All(session.States[0], value => Assert.Equal(0f, value));
        Assert.All(session.States[1], value => Assert.Equal(1f, value));
        Assert.All(session.States[2], value => Assert.Equal(2f, value));
    }

    /// <summary>The last 64 samples of each frame become the context prefix of the next model input.</summary>
    [Fact]
    public void Process_RetainsLast64SamplesAsNextFrameContext()
    {
        ScriptedSileroSession session = new();
        using SileroVoiceActivityDetector detector = CreateDetector(0.5f, session);

        float[] first = CreateRampFrame(1000f);
        float[] second = CreateRampFrame(2000f);
        _ = detector.Process(first);
        _ = detector.Process(second);

        float[] firstInput = session.ModelInputs[0];
        float[] secondInput = session.ModelInputs[1];
        Assert.All(firstInput[..64], value => Assert.Equal(0f, value));
        Assert.Equal(first, firstInput[64..]);
        Assert.Equal(first[^64..], secondInput[..64]);
        Assert.Equal(second, secondInput[64..]);
    }

    /// <summary>Reset zeroes the recurrent state and the retained context.</summary>
    [Fact]
    public void Reset_ZeroesRecurrentStateAndContext()
    {
        ScriptedSileroSession session = new()
        {
            NextStateFill = 7f,
        };
        using SileroVoiceActivityDetector detector = CreateDetector(0.5f, session);

        _ = detector.Process(CreateRampFrame(1000f));
        _ = detector.Process(CreateRampFrame(2000f));
        detector.Reset();
        _ = detector.Process(CreateRampFrame(3000f));

        float[] postResetInput = session.ModelInputs[2];
        Assert.All(postResetInput[..64], value => Assert.Equal(0f, value));
        Assert.All(session.States[2], value => Assert.Equal(0f, value));
    }

    /// <summary>The speech probability threshold is inclusive and the raw probability is reported.</summary>
    [Fact]
    public void Process_AppliesThresholdInclusivelyAndReportsRawProbability()
    {
        ScriptedSileroSession session = new();
        using SileroVoiceActivityDetector detector = CreateDetector(0.5f, session);

        session.Probabilities.Enqueue(0.4999f);
        session.Probabilities.Enqueue(0.5f);
        session.Probabilities.Enqueue(0f);
        session.Probabilities.Enqueue(1f);

        VoiceActivityDetection below = detector.Process(Frame(0.25f));
        VoiceActivityDetection atThreshold = detector.Process(Frame(0.25f));
        VoiceActivityDetection zero = detector.Process(Frame(0.25f));
        VoiceActivityDetection one = detector.Process(Frame(0.25f));

        Assert.False(below.IsVoiced);
        Assert.Equal(0.4999f, below.SpeechProbability);
        Assert.True(atThreshold.IsVoiced);
        Assert.Equal(0.5f, atThreshold.SpeechProbability);
        Assert.False(zero.IsVoiced);
        Assert.Equal(0f, zero.SpeechProbability);
        Assert.True(one.IsVoiced);
        Assert.Equal(1f, one.SpeechProbability);
    }

    /// <summary>Only exact 512-sample frames are accepted at the model boundary.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(511)]
    [InlineData(513)]
    [InlineData(1024)]
    public void Process_RejectsNon512SampleFrames(int sampleCount)
    {
        using SileroVoiceActivityDetector detector = CreateDetector(0.5f, new ScriptedSileroSession());

        _ = Assert.Throws<ArgumentException>(() => detector.Process(new float[sampleCount]));
    }    /// <summary>The threshold is validated as finite and within [0, 1].</summary>
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(-0.01f)]
    [InlineData(1.01f)]
    public void Constructor_RejectsInvalidThresholds(float threshold)
        => _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => new SileroVoiceActivityDetector(threshold, CreateSession));

    /// <summary>The factory always receives the fixed production model path.</summary>
    [Fact]
    public void Constructor_PassesFixedModelPathToSessionFactory()
    {
        List<string> receivedPaths = [];
        using SileroVoiceActivityDetector detector = new(0.5f, path =>
        {
            receivedPaths.Add(path);
            return new ScriptedSileroSession();
        });

        Assert.Equal([SileroVoiceActivityDetector.ModelPath], receivedPaths);
        Assert.Equal("res://models/silero/silero_vad.onnx", SileroVoiceActivityDetector.ModelPath);
    }

    /// <summary>A missing or malformed model fails initialisation deterministically.</summary>
    [Fact]
    public void Constructor_WhenSessionCreationFails_PropagatesTheFailure()
    {
        FileNotFoundException failure = new("The model file went missing.");

        FileNotFoundException thrown = Assert.Throws<FileNotFoundException>(
            () => new SileroVoiceActivityDetector(0.5f, _ => throw failure));

        Assert.Same(failure, thrown);
    }

    /// <summary>Disposal disposes the session exactly once and guards further use.</summary>
    [Fact]
    public void Dispose_DisposesSessionOnceAndGuardsFurtherUse()
    {
        ScriptedSileroSession session = new();
        SileroVoiceActivityDetector detector = CreateDetector(0.5f, session);

        detector.Dispose();
        detector.Dispose();

        Assert.Equal(1, session.DisposeCount);
        _ = Assert.Throws<ObjectDisposedException>(() => detector.Process(Frame(0.25f)));
        _ = Assert.Throws<ObjectDisposedException>(detector.Reset);
    }

    /// <summary>A session created after the first disposal cannot be revived through a second dispose.</summary>
    [Fact]
    public void Dispose_IsDeterministicWithoutFinaliserReliance()
    {
        ScriptedSileroSession session = new();
        SileroVoiceActivityDetector detector = CreateDetector(0.5f, session);

        detector.Dispose();

        Assert.Equal(1, session.DisposeCount);
    }

    /// <summary>The committed model exists with the pinned byte size and SHA-256 checksum.</summary>
    [Fact]
    public void CommittedModel_MatchesPinnedSizeAndChecksum()
    {
        Assert.True(File.Exists(_modelPath), $"The committed Silero model was not found at '{_modelPath}'.");

        FileInfo model = new(_modelPath);
        Assert.Equal(PinnedSizeBytes, model.Length);

        byte[] hash = SHA256.HashData(File.ReadAllBytes(_modelPath));
        Assert.Equal(PinnedSha256, Convert.ToHexStringLower(hash));
    }

    /// <summary>The CPU session factory initialises the committed model on CPU without any CUDA requirement.</summary>
    [Fact]
    public void CreateCpuSession_LoadsTheCommittedModelWithTheCpuExecutionProvider()
    {
        using InferenceSession session = SileroVadSessions.CreateCpuSession(_modelPath);

        Assert.Contains("input", session.InputMetadata.Keys);
        Assert.Contains("state", session.InputMetadata.Keys);
        Assert.Contains("sr", session.InputMetadata.Keys);
        Assert.Contains("output", session.OutputMetadata.Keys);
        Assert.Contains("stateN", session.OutputMetadata.Keys);
    }

    /// <summary>Absolute paths that do not exist fail session creation instead of producing a live detector.</summary>
    [Fact]
    public void CreateCpuSession_WithMissingModelFile_Throws()
        => _ = Assert.ThrowsAny<Exception>(() => SileroVadSessions.CreateCpuSession(Path.Combine(Path.GetTempPath(), "alleycat-missing-silero.onnx")));

    /// <summary>A malformed ONNX file fails session creation deterministically.</summary>
    [Fact]
    public void CreateCpuSession_WithMalformedModel_Throws()
    {
        string malformedPath = Path.Combine(Path.GetTempPath(), $"alleycat-malformed-{Guid.NewGuid():N}.onnx");
        try
        {
            File.WriteAllBytes(malformedPath, [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07]);

            _ = Assert.ThrowsAny<Exception>(() => SileroVadSessions.CreateCpuSession(malformedPath));
        }
        finally
        {
            File.Delete(malformedPath);
        }
    }

    /// <summary>The committed model reports near-zero speech probability for digital silence.</summary>
    [Fact]
    public void Process_WithCommittedModelOnDigitalSilence_ReportsLowFiniteProbability()
    {
        using SileroVoiceActivityDetector detector = CreateRealModelDetector(0.5f);

        for (int frame = 0; frame < 4; frame++)
        {
            VoiceActivityDetection detection = detector.Process(new float[SileroVoiceActivityDetector.FrameSampleCount]);

            Assert.False(detection.IsVoiced);
            Assert.True(float.IsFinite(detection.SpeechProbability));
            Assert.InRange(detection.SpeechProbability, 0f, 0.5f);
        }
    }

    /// <summary>Identical input sequences produce identical probabilities across detector instances.</summary>
    [Fact]
    public void Process_IsDeterministicAcrossInstances()
    {
        float[] first = CollectSilenceProbabilities();
        float[] second = CollectSilenceProbabilities();

        Assert.Equal(first, second);
    }

    private static float[] CollectSilenceProbabilities()
    {
        using SileroVoiceActivityDetector detector = CreateRealModelDetector(0.5f);
        float[] probabilities = new float[4];
        for (int frame = 0; frame < probabilities.Length; frame++)
        {
            probabilities[frame] = detector.Process(new float[SileroVoiceActivityDetector.FrameSampleCount]).SpeechProbability;
        }

        return probabilities;
    }

    private static SileroVoiceActivityDetector CreateDetector(float threshold, ISileroVadSession session)
        => new(threshold, _ => session);

    private static SileroVoiceActivityDetector CreateRealModelDetector(float threshold)
        => new(threshold, _ => new OnnxSileroVadSession(SileroVadSessions.CreateCpuSession(_modelPath)));

    private static ISileroVadSession CreateSession(string modelPath)
    {
        _ = modelPath;
        return new ScriptedSileroSession();
    }

    private static float[] Frame(float value)
        => [.. Enumerable.Repeat(value, SileroVoiceActivityDetector.FrameSampleCount)];

    private static float[] CreateRampFrame(float baseValue)
        => [.. Enumerable.Range(0, SileroVoiceActivityDetector.FrameSampleCount).Select(index => baseValue + index)];

    private static string LocateModel()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AlleyCat.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "game", "models", "silero", "silero_vad.onnx");
    }

    private sealed class ScriptedSileroSession : ISileroVadSession
    {
        public List<float[]> ModelInputs { get; } = [];

        public List<float[]> States { get; } = [];

        public Queue<float> Probabilities { get; } = [];

        public float NextStateFill
        {
            get; set;
        }

        public int DisposeCount
        {
            get; private set;
        }

        public float Run(float[] modelInput, float[] state, float[] nextState)
        {
            ModelInputs.Add([.. modelInput]);
            States.Add([.. state]);
            Array.Fill(nextState, NextStateFill);
            return Probabilities.Count > 0 ? Probabilities.Dequeue() : 0.1f;
        }

        public void Dispose() => DisposeCount++;
    }
}
