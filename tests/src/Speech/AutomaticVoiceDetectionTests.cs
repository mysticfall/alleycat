using AlleyCat.Core.Logging;
using AlleyCat.Speech.Transcription;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AlleyCat.Tests.Speech;

/// <summary>Deterministic coverage for the pure automatic voice detection primitives.</summary>
public sealed class AutomaticVoiceDetectionTests
{
    /// <summary>Options reject invalid tuning and retain defaults.</summary>
    /// <summary>Out-of-order automatic results publish only once all lower indexes have settled.</summary>
    [Fact]
    public void Options_ValidateFiniteRangesDeadlinesAndDefaults()
    {
        Assert.Equal([VoiceInputMode.ButtonOnly, VoiceInputMode.AutomaticOnly, VoiceInputMode.ButtonAndAutomatic], Enum.GetValues<VoiceInputMode>());
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => CreateOptions(threshold: float.NaN));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => CreateOptions(threshold: float.PositiveInfinity));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => CreateOptions(threshold: -0.01f));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => CreateOptions(threshold: 1.01f));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => CreateOptions(preRoll: TimeSpan.FromSeconds(-1)));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => CreateOptions(endpoint: TimeSpan.FromMilliseconds(500), continuation: TimeSpan.FromMilliseconds(300)));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => CreateOptions(maximum: TimeSpan.FromMinutes(3)));

        var defaults = new AutomaticVoiceInputOptions();
        Assert.Equal(0.5f, defaults.SpeechProbabilityThreshold);
        Assert.Null(defaults.RearmSilence);

        AutomaticVoiceInputOptions accepted = CreateOptions(threshold: 0f);
        Assert.Equal(0f, accepted.SpeechProbabilityThreshold);
        Assert.Equal(1f, CreateOptions(threshold: 1f).SpeechProbabilityThreshold);
    }

    /// <summary>Minimum voiced duration qualifies onset on the coordinator's own sample clock.</summary>
    /// <summary>Blank and failed results unblock later successes without manufacturing transcript text.</summary>
    [Fact]
    public void Coordinator_QualifiesOnsetFromMinimumVoicedDurationAboveFrameDecisions()
    {
        var coordinator = new AutomaticUtteranceCoordinator(
            StreamingMonoResampler.TargetSampleRate,
            CreateOptions(minimumVoiced: TimeSpan.FromMilliseconds(64)));

        // Two 512-sample frames at 16 kHz are 32 ms each: the first voiced frame only opens a candidate and the
        // second — reaching 64 ms of continuous voice — qualifies the onset.
        Assert.Equal(AutomaticUtteranceAction.None, coordinator.ProcessFrame(0, 512, Voiced()).Action);
        Assert.Equal(AutomaticUtteranceState.Candidate, coordinator.State);
        AutomaticUtteranceTransitions started = coordinator.ProcessFrame(512, 512, Voiced());
        Assert.Equal(AutomaticUtteranceAction.Started, started.Action);
        Assert.Equal(0, started.UtteranceStartSample);

        // A silent frame between voiced frames resets the candidate so qualification restarts from scratch.
        var reset = new AutomaticUtteranceCoordinator(
            StreamingMonoResampler.TargetSampleRate,
            CreateOptions(minimumVoiced: TimeSpan.FromMilliseconds(64)));
        Assert.Equal(AutomaticUtteranceAction.None, reset.ProcessFrame(0, 512, Voiced()).Action);
        Assert.Equal(AutomaticUtteranceState.Monitoring, reset.ProcessFrame(512, 512, Silent()).State);
        Assert.Equal(AutomaticUtteranceAction.None, reset.ProcessFrame(1024, 512, Voiced()).Action);
        Assert.Equal(AutomaticUtteranceState.Candidate, reset.State);
    }

    /// <summary>Coordinator uses exact 16 kHz sample positions for every lifecycle deadline.</summary>
    /// <summary>Groups drain independently and an abandoned group rejects late backend completions.</summary>
    [Fact]
    public void Coordinator_UsesExact16KSampleClockForAllDeadlines()
    {
        var coordinator = new AutomaticUtteranceCoordinator(
            StreamingMonoResampler.TargetSampleRate,
            CreateOptions(
                endpoint: TimeSpan.FromMilliseconds(32),
                continuation: TimeSpan.FromMilliseconds(96),
                maximum: TimeSpan.FromMilliseconds(128),
                rearm: TimeSpan.FromMilliseconds(16)));

        Assert.Equal(AutomaticUtteranceAction.Started, coordinator.ProcessFrame(0, 512, Voiced()).Action);
        // The silent frame ending at sample 1024 crosses the endpoint deadline: 512 voiced end + 512 samples.
        Assert.Equal(AutomaticUtteranceAction.Endpointed, coordinator.ProcessFrame(512, 512, Silent()).Action);
        Assert.Equal(AutomaticUtteranceAction.None, coordinator.ProcessFrame(1024, 256, Silent()).Action);
        // Voice resuming before the continuation deadline — 512 + 1536 samples — extends the same utterance.
        Assert.Equal(AutomaticUtteranceAction.Continued, coordinator.ProcessFrame(1280, 256, Voiced()).Action);
        // Maximum duration: the frame ending at sample 2048 crosses onset 0 + 2048 samples (128 ms at 16 kHz).
        AutomaticUtteranceTransitions forced = coordinator.ProcessFrame(1536, 512, Voiced());
        Assert.Equal(AutomaticUtteranceAction.ForceClosed, forced.Action);
        Assert.Equal(2048, forced.UtteranceEndSample);
        // Rearm silence: 256 samples (16 ms at 16 kHz) measured from the force-closing frame's end.
        Assert.Equal(AutomaticUtteranceAction.Rearmed, coordinator.ProcessFrame(2048, 256, Silent()).Action);
    }

    /// <summary>A silent predecessor endpoints at its exact deadline before the following voiced frame continues.</summary>
    [Fact]
    public void Coordinator_SilentPredecessor_EndpointsAtExactDeadlineThenFollowingFrameContinuesGroup()
    {
        var coordinator = new AutomaticUtteranceCoordinator(
            StreamingMonoResampler.TargetSampleRate,
            CreateOptions(endpoint: TimeSpan.FromMilliseconds(32), continuation: TimeSpan.FromMilliseconds(96)));
        AutomaticUtteranceTransitions started = coordinator.ProcessFrame(0, 512, Voiced());
        AutomaticUtteranceTransitions endpoint = coordinator.ProcessFrame(512, 512, Silent());

        AutomaticUtteranceTransitions continued = coordinator.ProcessFrame(1024, 512, Voiced());

        Assert.Equal([AutomaticUtteranceAction.Endpointed], endpoint.Select(transition => transition.Action));
        Assert.Equal(1024, endpoint[0].SegmentEndSample);
        Assert.Equal(AutomaticUtteranceState.AwaitingContinuation, endpoint.State);
        Assert.Equal([AutomaticUtteranceAction.Continued], continued.Select(transition => transition.Action));
        Assert.Equal(started[0].SpeechGroupID, continued[0].SpeechGroupID);
        Assert.Equal(1, continued[0].SegmentIndex);
        Assert.Equal(1024, continued[0].SegmentStartSample);
        Assert.Equal(AutomaticUtteranceState.CapturingSegment, coordinator.State);
    }

    /// <summary>Voiced audio in the first frame after endpoint extends the existing utterance.</summary>
    [Fact]
    public void Coordinator_ContinuesWhenFirstPostEndpointFrameIsVoiced()
    {
        var coordinator = new AutomaticUtteranceCoordinator(StreamingMonoResampler.TargetSampleRate, CreateOptions());
        _ = coordinator.ProcessFrame(0, 512, Voiced());
        Assert.Equal(AutomaticUtteranceAction.Endpointed, coordinator.ProcessFrame(512, 512, Silent()).Action);

        AutomaticUtteranceTransitions continued = coordinator.ProcessFrame(1024, 512, Voiced());

        Assert.Equal(AutomaticUtteranceAction.Continued, continued.Action);
        Assert.Equal(AutomaticUtteranceState.CapturingSegment, coordinator.State);
    }

    /// <summary>Coordinator frames are positive-length contiguous intervals.</summary>
    [Fact]
    public void Coordinator_RejectsZeroLengthOverlappingAndGappedFrames()
    {
        var coordinator = new AutomaticUtteranceCoordinator(StreamingMonoResampler.TargetSampleRate, CreateOptions());
        _ = coordinator.ProcessFrame(100, 512, Voiced());

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => coordinator.ProcessFrame(612, 0, Silent()));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => coordinator.ProcessFrame(611, 1, Silent()));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => coordinator.ProcessFrame(613, 1, Silent()));
    }

    /// <summary>Rearming starts after the complete force-closing frame, never inside post-boundary voice.</summary>
    [Fact]
    public void Coordinator_ForceCloseCrossingFrameRequiresSubsequentCleanSilenceToRearm()
    {
        var coordinator = new AutomaticUtteranceCoordinator(
            StreamingMonoResampler.TargetSampleRate,
            CreateOptions(maximum: TimeSpan.FromMilliseconds(96), rearm: TimeSpan.FromMilliseconds(16)));
        _ = coordinator.ProcessFrame(0, 512, Voiced());
        _ = coordinator.ProcessFrame(512, 512, Voiced());

        // The voiced frame ending at sample 1536 crosses the maximum-duration deadline.
        AutomaticUtteranceTransitions forced = coordinator.ProcessFrame(1024, 512, Voiced());

        Assert.Equal(AutomaticUtteranceAction.ForceClosed, forced.Action);
        Assert.Equal(1536, forced.UtteranceEndSample);
        // Post-boundary voice keeps rearming suppressed and pushes the silence deadline to the frame end.
        Assert.Equal(AutomaticUtteranceAction.None, coordinator.ProcessFrame(1536, 512, Voiced()).Action);
        // Rearm silence: 256 samples (16 ms at 16 kHz) of clean silence after the last voiced frame.
        Assert.Equal(AutomaticUtteranceAction.Rearmed, coordinator.ProcessFrame(2048, 256, Silent()).Action);
    }

    /// <summary>Ordered silent boundaries leave no old-group ownership for the following voiced frame.</summary>
    [Fact]
    public void Coordinator_ClosedGroupDoesNotOwnFollowingVoicedFrame()
    {
        var coordinator = new AutomaticUtteranceCoordinator(
            StreamingMonoResampler.TargetSampleRate,
            CreateOptions(endpoint: TimeSpan.FromMilliseconds(32), continuation: TimeSpan.FromMilliseconds(96)));

        AutomaticUtteranceTransitions started = coordinator.ProcessFrame(0, 512, Voiced());
        AutomaticUtteranceTransitions boundaries = coordinator.ProcessFrame(512, 2048, Silent());
        AutomaticUtteranceTransitions following = coordinator.ProcessFrame(2560, 512, Voiced());

        Assert.Equal([AutomaticUtteranceAction.Endpointed, AutomaticUtteranceAction.Closed], boundaries.Select(transition => transition.Action));
        Assert.All(boundaries, transition => Assert.Equal(started[0].SpeechGroupID, transition.SpeechGroupID));
        Assert.Equal([1024, 2048], boundaries.Select(transition => transition.SegmentEndSample));
        Assert.Equal(AutomaticUtteranceState.Monitoring, boundaries.State);
        Assert.Equal([AutomaticUtteranceAction.Started], following.Select(transition => transition.Action));
        Assert.NotEqual(started[0].SpeechGroupID, following[0].SpeechGroupID);
        Assert.Equal(0, following[0].SegmentIndex);
        Assert.False(following[0].Continued);
        Assert.Equal(2560, following[0].SegmentStartSample);
        Assert.Equal(AutomaticUtteranceState.CapturingSegment, following.State);
    }

    /// <summary>Successive pause-delimited segments retain one group identity and advance indexes without gaps.</summary>
    [Fact]
    public void Coordinator_MultipleResumes_RetainOneGroupAndConsecutiveSegmentIndexes()
    {
        var coordinator = new AutomaticUtteranceCoordinator(
            StreamingMonoResampler.TargetSampleRate,
            CreateOptions(endpoint: TimeSpan.FromMilliseconds(32), continuation: TimeSpan.FromMilliseconds(128)));

        AutomaticUtteranceTransitions started = coordinator.ProcessFrame(0, 512, Voiced());
        _ = coordinator.ProcessFrame(512, 512, Silent());
        AutomaticUtteranceTransitions firstResume = coordinator.ProcessFrame(1024, 512, Voiced());
        _ = coordinator.ProcessFrame(1536, 512, Silent());
        AutomaticUtteranceTransitions secondResume = coordinator.ProcessFrame(2048, 512, Voiced());

        Guid groupID = started[0].SpeechGroupID!.Value;
        Assert.Equal(groupID, firstResume[0].SpeechGroupID);
        Assert.Equal(groupID, secondResume[0].SpeechGroupID);
        Assert.Equal(1, firstResume[0].SegmentIndex);
        Assert.Equal(2, secondResume[0].SegmentIndex);
        Assert.True(firstResume[0].Continued);
        Assert.True(secondResume[0].Continued);
    }

    /// <summary>A silent predecessor closes at the continuation deadline before the next voiced frame starts a new group.</summary>
    [Fact]
    public void Coordinator_SilentPredecessor_ClosesAtContinuationDeadlineThenFollowingFrameStartsNewGroup()
    {
        var coordinator = new AutomaticUtteranceCoordinator(
            StreamingMonoResampler.TargetSampleRate,
            CreateOptions(endpoint: TimeSpan.FromMilliseconds(32), continuation: TimeSpan.FromMilliseconds(96)));

        AutomaticUtteranceTransitions first = coordinator.ProcessFrame(0, 512, Voiced());
        _ = coordinator.ProcessFrame(512, 512, Silent());
        AutomaticUtteranceTransitions closure = coordinator.ProcessFrame(1024, 1024, Silent());
        AutomaticUtteranceTransitions following = coordinator.ProcessFrame(2048, 512, Voiced());

        Assert.Equal([AutomaticUtteranceAction.Closed], closure.Select(transition => transition.Action));
        Assert.Equal(first[0].SpeechGroupID, closure[0].SpeechGroupID);
        Assert.Equal(2048, closure[0].SegmentEndSample);
        Assert.Equal(AutomaticUtteranceState.Monitoring, closure.State);
        Assert.Equal([AutomaticUtteranceAction.Started], following.Select(transition => transition.Action));
        Assert.NotEqual(first[0].SpeechGroupID, following[0].SpeechGroupID);
        Assert.Equal(0, following[0].SegmentIndex);
        Assert.False(following[0].Continued);
    }

    /// <summary>Out-of-order automatic results publish only once all lower indexes have settled.</summary>
    [Fact]
    public void SettlementGate_OutOfOrderResults_DrainsOnlyConsecutiveIndexes()
    {
        var gate = new AutomaticSegmentSettlementGate();
        gate.RegisterDispatch();
        gate.RegisterDispatch();
        gate.RegisterDispatch();

        Assert.Empty(gate.Settle(2, new("two", null)));
        Assert.Equal([0], gate.Settle(0, new("zero", null)).Select(entry => entry.SegmentIndex));
        Assert.Equal([1, 2], gate.Settle(1, new("one", null)).Select(entry => entry.SegmentIndex));
    }

    /// <summary>Blank and failed results unblock later successes without manufacturing transcript text.</summary>
    [Fact]
    public void SettlementGate_BlankOrFailure_AdvancesOrderingWithoutInventingText()
    {
        var gate = new AutomaticSegmentSettlementGate();
        gate.RegisterDispatch();
        gate.RegisterDispatch();

        Assert.Empty(gate.Settle(1, new("later", null)));
        IReadOnlyList<AutomaticSegmentSettlement> settled = gate.Settle(0, new(null, new InvalidOperationException("failed")));

        Assert.Equal([0, 1], settled.Select(entry => entry.SegmentIndex));
        Assert.Null(settled[0].Outcome.Text);
        Assert.NotNull(settled[0].Outcome.Exception);
        Assert.Equal("later", settled[1].Outcome.Text);
    }

    /// <summary>Groups drain independently and an abandoned group rejects late backend completions.</summary>
    [Fact]
    public void SettlementGate_GroupsAreIndependent_AndAbandonmentSuppressesLateResults()
    {
        var first = new AutomaticSegmentSettlementGate();
        var second = new AutomaticSegmentSettlementGate();
        first.RegisterDispatch();
        second.RegisterDispatch();

        Assert.Equal([0], second.Settle(0, new("second", null)).Select(entry => entry.SegmentIndex));
        first.Abandon();
        Assert.Empty(first.Settle(0, new("late", null)));
        Assert.Equal(0, first.NextExpectedSegmentIndex);
    }

    /// <summary>Maximum closure retains only its prefix and cannot create a trailing segment.</summary>
    [Fact]
    public void Coordinator_ForceClose_TrimsAndClosesWithoutTrailingSegment()
    {
        var coordinator = new AutomaticUtteranceCoordinator(
            StreamingMonoResampler.TargetSampleRate,
            CreateOptions(maximum: TimeSpan.FromMilliseconds(64), rearm: TimeSpan.FromMilliseconds(16)));
        _ = coordinator.ProcessFrame(0, 512, Voiced());

        AutomaticUtteranceTransitions transitions = coordinator.ProcessFrame(512, 1024, Voiced());

        Assert.Equal([AutomaticUtteranceAction.ForceClosed, AutomaticUtteranceAction.Closed], transitions.Select(transition => transition.Action));
        Assert.Equal(1024, transitions[0].SegmentEndSample);
        Assert.DoesNotContain(transitions, transition => transition.Action == AutomaticUtteranceAction.Continued);
        Assert.Equal(AutomaticUtteranceState.RearmSuppressed, coordinator.State);
    }

    /// <summary>Pre-roll ring ordering survives wrap-around.</summary>
    [Fact]
    public void PreRollBuffer_WrapsAndDrainsOldestFirst()
    {
        var buffer = new MonoFloatPreRollBuffer(3);
        buffer.Append([1f, 2f, 3f, 4f]);
        float[] destination = new float[3];

        Assert.Equal(3, buffer.DrainTo(destination));
        Assert.Equal([2f, 3f, 4f], destination);
        Assert.Equal(0, buffer.Count);
    }

    /// <summary>Arbitrary source rates produce the exact target sample clock.</summary>
    [Theory]
    [InlineData(48000, 4800, 1600)]
    [InlineData(48000, 4803, 1601)]
    [InlineData(44100, 4410, 1600)]
    public void Resampler_ConvertsToExact16KSampleClock(int sourceRate, int sourceSamples, int expectedOutputSamples)
    {
        float[] output = Resample(sourceRate, Enumerable.Repeat(0.25f, sourceSamples).ToArray());
        var resampler = new StreamingMonoResampler(sourceRate);

        Assert.Equal(expectedOutputSamples, output.Length);
        Assert.Equal((double)sourceRate / StreamingMonoResampler.TargetSampleRate, resampler.GetSourceSamplePosition(1));
    }

    /// <summary>Filtered output retains speech-band energy, rejects above-Nyquist energy, and is chunk independent.</summary>
    [Fact]
    public void Resampler_PreservesSpeechBandChunkContinuityAndAttenuates10KHz()
    {
        float[] speechBandSource = CreateTone(6500f);
        float[] aboveNyquistSource = CreateTone(10000f);
        float[] whole = Resample(48000, speechBandSource);
        float[] split = Resample(48000, speechBandSource[..1234], speechBandSource[1234..]);
        float[] aboveNyquist = Resample(48000, aboveNyquistSource);
        double speechBandMeanAbsolute = whole.Skip(100).Average(MathF.Abs);
        double inputMeanAbsolute = speechBandSource.Average(MathF.Abs);
        double aboveNyquistMeanAbsolute = aboveNyquist.Skip(100).Average(MathF.Abs);

        Assert.Equal(whole, split);
        Assert.True(speechBandMeanAbsolute > inputMeanAbsolute * 0.7d);
        Assert.True(aboveNyquistMeanAbsolute < speechBandMeanAbsolute * 0.25d);
    }

    /// <summary>Notification text is concise and mode-specific; AutomaticOnly never promises push-to-talk.</summary>
    [Theory]
    [InlineData(VoiceInputMode.ButtonAndAutomatic, true, "Automatic voice detection is unavailable. Use push-to-talk.")]
    [InlineData(VoiceInputMode.AutomaticOnly, false, "Automatic voice detection is unavailable.")]
    public void UnavailableEntry_RendersConciseModeSpecificNotificationText(
        VoiceInputMode inputMode,
        bool manualInputAvailable,
        string expected)
    {
        AutomaticVoiceInputUnavailableEntry entry = CreateEntry(inputMode, manualInputAvailable);

        Assert.Equal(expected, entry.ToNotificationText());
    }

    /// <summary>The full-log text carries the mode, fixed model path, exception details, and manual availability.</summary>
    [Fact]
    public void UnavailableEntry_DetailedLogCarriesModeModelPathExceptionAndManualAvailability()
    {
        AutomaticVoiceInputUnavailableEntry entry = CreateEntry(VoiceInputMode.ButtonAndAutomatic, manualInputAvailable: true);

        string detailed = entry.ToDetailedLogText();
        Assert.Contains("ButtonAndAutomatic", detailed, StringComparison.Ordinal);
        Assert.Contains(SileroVoiceActivityDetector.ModelPath, detailed, StringComparison.Ordinal);
        Assert.Contains("FileNotFoundException", detailed, StringComparison.Ordinal);
        Assert.Contains("model file went missing", detailed, StringComparison.Ordinal);
        Assert.Contains("push-to-talk", detailed, StringComparison.Ordinal);

        AutomaticVoiceInputUnavailableEntry automaticOnly = CreateEntry(VoiceInputMode.AutomaticOnly, manualInputAvailable: false);
        Assert.DoesNotContain("Use push-to-talk", automaticOnly.ToNotificationText(), StringComparison.Ordinal);
        Assert.Contains("not available in AutomaticOnly mode", automaticOnly.ToDetailedLogText(), StringComparison.Ordinal);
    }

    /// <summary>One warning entry routes exactly one concise UI notification past the Error floor while the detailed text reaches ordinary providers.</summary>
    [Fact]
    public void UnavailableEntry_LogWarningRoutesOneConciseNotificationAndPreservesDetailedLog()
    {
        CapturingNotificationSink sink = new();
        CapturingLoggerProvider ordinaryProvider = new();
        using NotificationLoggerProvider notificationProvider = new(sink);
        using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
        {
            _ = builder.AddProvider(ordinaryProvider);
            _ = builder.AddProvider(notificationProvider);
        });
        ILogger logger = loggerFactory.CreateLogger("AlleyCat.Speech.Transcription.Transcriber");
        AutomaticVoiceInputUnavailableEntry entry = CreateEntry(VoiceInputMode.ButtonAndAutomatic, manualInputAvailable: true);

        logger.Log(
            LogLevel.Warning,
            default,
            entry,
            entry.Exception,
            static (state, _) => state.ToDetailedLogText());

        string notification = Assert.Single(sink.Messages);
        Assert.Equal("Automatic voice detection is unavailable. Use push-to-talk.", notification);
        Assert.Equal(LogLevel.Warning, Assert.Single(ordinaryProvider.Levels));
        string detailed = Assert.Single(ordinaryProvider.Texts);
        Assert.Contains(SileroVoiceActivityDetector.ModelPath, detailed, StringComparison.Ordinal);
        Assert.Contains("FileNotFoundException", detailed, StringComparison.Ordinal);
    }

    /// <summary>
    /// The failure-signal decision is mode- and utterance-specific: only <c>AutomaticOnly</c> — which has no manual
    /// alternative — or an open automatic utterance whose speaking window needs exactly one terminal emits the
    /// failure.
    /// </summary>
    [Theory]
    [InlineData(VoiceInputMode.ButtonAndAutomatic, false, false)]
    [InlineData(VoiceInputMode.ButtonAndAutomatic, true, true)]
    [InlineData(VoiceInputMode.AutomaticOnly, false, true)]
    [InlineData(VoiceInputMode.AutomaticOnly, true, true)]
    public void UnavailableEntry_RequiresFailureSignalOnlyForAutomaticOnlyOrOpenUtterance(
        VoiceInputMode inputMode,
        bool hasOpenAutomaticUtterance,
        bool expected)
    {
        bool manualInputAvailable = inputMode != VoiceInputMode.AutomaticOnly;

        Assert.Equal(
            expected,
            AutomaticVoiceInputUnavailableEntry.RequiresFailureSignal(manualInputAvailable, hasOpenAutomaticUtterance));
    }

    private static AutomaticVoiceInputOptions CreateOptions(
        float threshold = 0.5f,
        TimeSpan? preRoll = null,
        TimeSpan? minimumVoiced = null,
        TimeSpan? endpoint = null,
        TimeSpan? continuation = null,
        TimeSpan? maximum = null,
        TimeSpan? rearm = null) => new(
        threshold,
        preRoll ?? TimeSpan.Zero,
        minimumVoiced ?? TimeSpan.Zero,
        endpoint ?? TimeSpan.FromMilliseconds(32),
        continuation ?? TimeSpan.FromMilliseconds(96),
        maximum ?? TimeSpan.FromSeconds(2),
        rearm);

    private static VoiceActivityDetection Voiced() => new(true, 0.9f);

    private static VoiceActivityDetection Silent() => new(false, 0.1f);

    private static AutomaticVoiceInputUnavailableEntry CreateEntry(VoiceInputMode inputMode, bool manualInputAvailable)
        => new(
            inputMode,
            SileroVoiceActivityDetector.ModelPath,
            new FileNotFoundException("The model file went missing."),
            manualInputAvailable);

    private static float[] Resample(int sourceRate, params float[][] chunks)
    {
        var resampler = new StreamingMonoResampler(sourceRate);
        List<float> output = [];
        foreach (float[] chunk in chunks)
        {
            float[] scratch = new float[(chunk.Length * StreamingMonoResampler.TargetSampleRate / sourceRate) + 64];
            int count = resampler.Process(chunk, scratch);
            output.AddRange(scratch[..count]);
        }

        float[] tail = new float[64];
        int tailCount = resampler.Flush(tail);
        output.AddRange(tail[..tailCount]);
        return [.. output];
    }

    private static float[] CreateTone(float frequencyHz)
        => [.. Enumerable.Range(0, 48000).Select(index => 0.8f * MathF.Sin(2f * MathF.PI * frequencyHz * index / 48000f))];

    private sealed class CapturingNotificationSink : ILogNotificationSink
    {
        public List<string> Messages { get; } = [];

        public bool TryPostNotification(string? message, double timeoutSeconds = 3.0)
        {
            if (message is not null)
            {
                Messages.Add(message);
            }

            return true;
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<LogLevel> Levels { get; } = [];

        public List<string> Texts { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
                => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                owner.Levels.Add(logLevel);
                owner.Texts.Add(formatter(state, exception));
            }
        }
    }
}
