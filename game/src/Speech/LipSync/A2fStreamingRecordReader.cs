using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Godot;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Speech.LipSync;

/// <summary>
/// Snapshot of the eye-rotation translation configuration captured when a streaming inference response
/// starts, so the record reader can convert frames without referencing the player node.
/// </summary>
internal sealed record A2fEyeTranslationSettings(
    bool TranslateEyeRotations,
    float EyeRotationScale,
    float EyeSmoothingAlpha,
    bool InvertHorizontal,
    bool InvertVertical)
{
    /// <summary>
    /// Captures the eye-rotation translation settings from a configured player.
    /// </summary>
    public static A2fEyeTranslationSettings FromPlayer(A2FLipSyncPlayer player) => new(
        player.TranslateEyeRotationsToBlendshapes,
        player.EyeRotationToBlendshapeScale,
        player.EyeRotationSmoothingFactor,
        player.InvertEyeRotationHorizontal,
        player.InvertEyeRotationVertical);
}

/// <summary>
/// Reads newline-delimited Audio2Face streaming records from a response stream, converting frames into
/// the supplied buffer until the terminal complete record arrives.
/// </summary>
/// <remarks>
/// Extracted from the player so the NDJSON record handling — parsing, validation, conversion, and the
/// inter-record idle timeout — is unit-testable without a Godot runtime or live HTTP server. Runs on a
/// background thread and must stay free of Godot API calls.
/// </remarks>
internal sealed class A2fStreamingRecordReader(
    TimeSpan idleTimeout,
    A2fEyeTranslationSettings eyeTranslationSettings,
    ILogger logger)
{
    /// <summary>
    /// Reads records until the complete record arrives and the buffer is marked complete.
    /// </summary>
    public async Task ReadAsync(
        Stream responseStream,
        StreamingFrameBuffer frameBuffer,
        CancellationToken cancellationToken)
    {
        NdjsonLineReader lineReader = new(responseStream, idleTimeout);
        StreamingFrameConverter? converter = null;
        HashSet<string> unknownRecordTypes = [];

        while (true)
        {
            string? line = await lineReader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            converter = ProcessStreamingRecord(line, frameBuffer, converter, unknownRecordTypes);

            if (frameBuffer.IsCompleted)
            {
                // The complete record is terminal; stop reading even if the connection stays open.
                return;
            }
        }

        if (!frameBuffer.IsCompleted)
        {
            throw new InvalidOperationException("LipSyncPlayer: Audio2Face stream ended without a complete record.");
        }
    }

    private StreamingFrameConverter? ProcessStreamingRecord(
        string line,
        StreamingFrameBuffer frameBuffer,
        StreamingFrameConverter? converter,
        HashSet<string> unknownRecordTypes)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"LipSyncPlayer: failed to parse Audio2Face streaming record: {TruncateForLog(line)}", ex);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("LipSyncPlayer: Audio2Face streaming record was not a JSON object.");
            }

            if (!root.TryGetProperty("type", out JsonElement typeElement)
                || typeElement.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException(
                    $"LipSyncPlayer: Audio2Face streaming record is missing its type field: {TruncateForLog(line)}");
            }

            switch (typeElement.GetString())
            {
                case "metadata":
                    converter = CreateFrameConverter(root);
                    frameBuffer.SetMetadata(ReadStreamingFps(root), converter.RetainedBlendshapeNames);
                    break;
                case "frame":
                    if (converter is null)
                    {
                        throw new InvalidOperationException(
                            "LipSyncPlayer: Audio2Face streaming frame record arrived before metadata.");
                    }

                    HandleStreamingFrame(root, frameBuffer, converter);
                    break;
                case "complete":
                    HandleStreamingComplete(root, frameBuffer);
                    break;
                default:
                    if (typeElement.GetString() is { } recordType && unknownRecordTypes.Add(recordType))
                    {
                        logger.LogWarning(
                            "A2FLipSyncPlayer: skipping unknown Audio2Face streaming record type '{RecordType}'.",
                            recordType);
                    }

                    break;
            }
        }

        return converter;
    }

    private static float ReadStreamingFps(JsonElement root)
        => !root.TryGetProperty("fps", out JsonElement fpsElement)
            || fpsElement.ValueKind != JsonValueKind.Number
            || !fpsElement.TryGetDouble(out double fps)
            || fps <= 0d
            ? throw new InvalidOperationException(
                "LipSyncPlayer: Audio2Face streaming metadata record is missing a valid fps value.")
            : (float)fps;

    private static void HandleStreamingFrame(
        JsonElement root,
        StreamingFrameBuffer frameBuffer,
        StreamingFrameConverter converter)
    {
        if (!root.TryGetProperty("index", out JsonElement indexElement)
            || !indexElement.TryGetInt32(out int frameIndex))
        {
            throw new InvalidOperationException(
                "LipSyncPlayer: Audio2Face streaming frame record is missing a valid index.");
        }

        int expectedIndex = frameBuffer.FrameCount;
        if (frameIndex != expectedIndex)
        {
            throw new InvalidOperationException(
                $"LipSyncPlayer: Audio2Face streaming frame index {frameIndex} does not match the expected sequential index {expectedIndex}.");
        }

        float[] weights = ReadStreamingFloatArray(root, "weights")
            ?? throw new InvalidOperationException("LipSyncPlayer: Audio2Face streaming frame record is missing weights.");

        if (weights.Length != converter.FullChannelCount)
        {
            throw new InvalidOperationException(
                $"LipSyncPlayer: API frame {frameIndex} has {weights.Length} channels, expected {converter.FullChannelCount}.");
        }

        // The jaw array is parsed by the batch payload but unused there too; it is deliberately not read
        // here. Eye rotations are optional per record and only used when translation is enabled.
        float[]? eyeRotation = ReadStreamingFloatArray(root, "eye_rotation");

        frameBuffer.Append(converter.Convert(weights, eyeRotation));
    }

    private static void HandleStreamingComplete(JsonElement root, StreamingFrameBuffer frameBuffer)
    {
        if (!root.TryGetProperty("frame_count", out JsonElement countElement)
            || !countElement.TryGetInt32(out int declaredFrameCount))
        {
            throw new InvalidOperationException(
                "LipSyncPlayer: Audio2Face streaming complete record is missing a valid frame_count.");
        }

        int receivedFrameCount = frameBuffer.FrameCount;
        if (receivedFrameCount == 0)
        {
            throw new InvalidOperationException("LipSyncPlayer: streaming inference produced zero frames.");
        }

        if (receivedFrameCount != declaredFrameCount)
        {
            throw new InvalidOperationException(
                $"LipSyncPlayer: Audio2Face streaming complete record declared {declaredFrameCount} frames but {receivedFrameCount} were received.");
        }

        frameBuffer.MarkComplete(declaredFrameCount);
    }

    private static float[]? ReadStreamingFloatArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement element)
            || element.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        float[] values = new float[element.GetArrayLength()];
        int valueIndex = 0;
        foreach (JsonElement valueElement in element.EnumerateArray())
        {
            if (!valueElement.TryGetSingle(out float value))
            {
                throw new InvalidOperationException(
                    $"LipSyncPlayer: Audio2Face streaming frame '{propertyName}' contained a non-numeric value.");
            }

            values[valueIndex++] = value;
        }

        return values;
    }

    private static string TruncateForLog(string line)
        => line.Length <= 200 ? line : $"{line[..200]}...";

    /// <summary>
    /// Builds the per-stream frame converter from the metadata record's blendshape names and the
    /// captured eye-rotation translation settings.
    /// </summary>
    private StreamingFrameConverter CreateFrameConverter(JsonElement metadataRoot)
    {
        if (!metadataRoot.TryGetProperty("blendshape_names", out JsonElement namesElement)
            || namesElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "LipSyncPlayer: Audio2Face streaming metadata record is missing blendshape_names.");
        }

        List<string> blendshapeNames = new(namesElement.GetArrayLength());
        foreach (JsonElement nameElement in namesElement.EnumerateArray())
        {
            if (nameElement.ValueKind != JsonValueKind.String || nameElement.GetString() is not { } name)
            {
                throw new InvalidOperationException(
                    "LipSyncPlayer: Audio2Face streaming metadata contained a non-string blendshape name.");
            }

            blendshapeNames.Add(name);
        }

        return StreamingFrameConverter.Create(eyeTranslationSettings, blendshapeNames, logger);
    }

    /// <summary>
    /// Converts raw streaming frame records into retained playback frames, mirroring the batch
    /// pipeline's eye-rotation translation and eye-controlled channel removal per frame.
    /// </summary>
    internal sealed class StreamingFrameConverter(
        string[] fullBlendshapeNames,
        int[] retainedIndices,
        string[] retainedBlendshapeNames,
        A2fEyeBlendshapeMapping.EyeBlendshapeIndices? eyeIndices,
        float eyeRotationScale,
        float smoothingAlpha,
        bool invertHorizontal,
        bool invertVertical,
        ILogger logger)
    {
        private float[]? _eyeBaseline;
        private float _previousRightHorizontal;
        private float _previousRightVertical;
        private float _previousLeftHorizontal;
        private float _previousLeftVertical;
        private bool _hasWarnedMissingEyeRotation;

        public static StreamingFrameConverter Create(
            A2fEyeTranslationSettings settings,
            IReadOnlyList<string> blendshapeNames,
            ILogger logger)
        {
            if (blendshapeNames.Count == 0)
            {
                throw new InvalidOperationException("LipSyncPlayer: API returned no blendshape names.");
            }

            Dictionary<string, int> nameToIndex = new(StringComparer.Ordinal);
            for (int index = 0; index < blendshapeNames.Count; index++)
            {
                _ = nameToIndex.TryAdd(LipSyncPlayer.NormalizeBlendshapeName(blendshapeNames[index]), index);
            }

            bool hasEyeChannels = A2fEyeBlendshapeMapping.TryGetEyeBlendshapeIndices(
                nameToIndex,
                out A2fEyeBlendshapeMapping.EyeBlendshapeIndices eyeIndices);
            if (settings.TranslateEyeRotations && !hasEyeChannels)
            {
                logger.LogWarning(
                    "A2FLipSyncPlayer: required ARKit eyeLook blendshape channels are missing in blendshape_names; eye translation skipped.");
            }

            List<int> retainedIndices = new(blendshapeNames.Count);
            List<string> retainedNames = new(blendshapeNames.Count);
            for (int index = 0; index < blendshapeNames.Count; index++)
            {
                if (A2fEyeBlendshapeMapping.IsEyesControlledBlendshapeName(blendshapeNames[index]))
                {
                    continue;
                }

                retainedIndices.Add(index);
                retainedNames.Add(blendshapeNames[index]);
            }

            _ = retainedNames.Count == 0
                ? throw new InvalidOperationException("LipSyncPlayer: API returned no non-eye blendshape names.")
                : true;

            return new StreamingFrameConverter(
                [.. blendshapeNames],
                [.. retainedIndices],
                [.. retainedNames],
                settings.TranslateEyeRotations && hasEyeChannels ? eyeIndices : null,
                Mathf.Max(0f, settings.EyeRotationScale),
                Mathf.Clamp(settings.EyeSmoothingAlpha, 0.01f, 1f),
                settings.InvertHorizontal,
                settings.InvertVertical,
                logger);
        }

        public IReadOnlyList<string> RetainedBlendshapeNames => retainedBlendshapeNames;

        public int FullChannelCount => fullBlendshapeNames.Length;

        /// <summary>
        /// Converts one frame's weights (and optional eye rotation) into a retained playback frame.
        /// </summary>
        public float[] Convert(float[] weights, float[]? eyeRotation)
        {
            if (eyeIndices is { } indices)
            {
                TranslateEyeRotations(weights, eyeRotation, indices);
            }

            float[] retainedFrame = new float[retainedIndices.Length];
            for (int index = 0; index < retainedIndices.Length; index++)
            {
                retainedFrame[index] = weights[retainedIndices[index]];
            }

            return retainedFrame;
        }

        private void TranslateEyeRotations(
            float[] weights,
            float[]? eyeRotation,
            A2fEyeBlendshapeMapping.EyeBlendshapeIndices indices)
        {
            if (eyeRotation is null || eyeRotation.Length < 6)
            {
                if (!_hasWarnedMissingEyeRotation)
                {
                    _hasWarnedMissingEyeRotation = true;
                    logger.LogWarning(
                        "A2FLipSyncPlayer: eye_rotation missing from a streaming frame record; eye translation skipped for that frame. " +
                        "Set execution to include eyes.");
                }

                return;
            }

            // Batch inference baselines eye rotations against the mean over the whole clip
            // (ComputeEyeRotationBaseline); a streaming converter cannot see future frames, so the
            // baseline is seeded from the first valid eye-rotation record instead. For speech starting
            // at or near rest the first-frame rotation approximates the clip mean closely, while clips
            // that begin mid-gaze carry a small constant offset relative to the batch result. The
            // smoothing, inversion, and directional-pair writes below match the batch implementation
            // exactly, and (as in batch) the eyeLook channels they write are subsequently stripped by
            // the retained-index filter, so the approximation never reaches playback directly.
            _eyeBaseline ??= [eyeRotation[0], eyeRotation[1], eyeRotation[2], eyeRotation[3], eyeRotation[4], eyeRotation[5]];

            float rightHorizontal = eyeRotation[0] - _eyeBaseline[0];
            float rightVertical = eyeRotation[1] - _eyeBaseline[1];
            float leftHorizontal = eyeRotation[3] - _eyeBaseline[3];
            float leftVertical = eyeRotation[4] - _eyeBaseline[4];

            rightHorizontal = _previousRightHorizontal + ((rightHorizontal - _previousRightHorizontal) * smoothingAlpha);
            rightVertical = _previousRightVertical + ((rightVertical - _previousRightVertical) * smoothingAlpha);
            leftHorizontal = _previousLeftHorizontal + ((leftHorizontal - _previousLeftHorizontal) * smoothingAlpha);
            leftVertical = _previousLeftVertical + ((leftVertical - _previousLeftVertical) * smoothingAlpha);
            _previousRightHorizontal = rightHorizontal;
            _previousRightVertical = rightVertical;
            _previousLeftHorizontal = leftHorizontal;
            _previousLeftVertical = leftVertical;

            if (invertHorizontal)
            {
                rightHorizontal = -rightHorizontal;
                leftHorizontal = -leftHorizontal;
            }

            if (invertVertical)
            {
                rightVertical = -rightVertical;
                leftVertical = -leftVertical;
            }

            A2fEyeBlendshapeMapping.WriteDirectionalPair(weights, indices.RightOut, indices.RightIn, rightHorizontal, eyeRotationScale);
            A2fEyeBlendshapeMapping.WriteDirectionalPair(weights, indices.LeftIn, indices.LeftOut, leftHorizontal, eyeRotationScale);
            A2fEyeBlendshapeMapping.WriteDirectionalPair(weights, indices.RightUp, indices.RightDown, rightVertical, eyeRotationScale);
            A2fEyeBlendshapeMapping.WriteDirectionalPair(weights, indices.LeftUp, indices.LeftDown, leftVertical, eyeRotationScale);
        }
    }

    /// <summary>
    /// Reads newline-delimited JSON records from a response stream with an inter-record idle timeout,
    /// without buffering the whole body.
    /// </summary>
    internal sealed class NdjsonLineReader(Stream stream, TimeSpan idleTimeout)
    {
        private readonly byte[] _chunkBuffer = new byte[8192];
        private readonly List<byte> _lineOverflow = [];
        private int _chunkStart;
        private int _chunkEnd;

        public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            _lineOverflow.Clear();

            while (true)
            {
                if (_chunkStart < _chunkEnd)
                {
                    int newlineIndex = Array.IndexOf(_chunkBuffer, (byte)'\n', _chunkStart, _chunkEnd - _chunkStart);
                    if (newlineIndex >= 0)
                    {
                        ReadOnlySpan<byte> lineSuffix = _chunkBuffer.AsSpan(_chunkStart, newlineIndex - _chunkStart);
                        _chunkStart = newlineIndex + 1;

                        if (_lineOverflow.Count == 0)
                        {
                            return DecodeLine(lineSuffix);
                        }

                        // The line started in an earlier chunk; reassemble it from the carried prefix
                        // before returning, otherwise the prefix would be lost.
                        _lineOverflow.AddRange(lineSuffix);
                        string line = DecodeLine(CollectionsMarshal.AsSpan(_lineOverflow));
                        _lineOverflow.Clear();
                        return line;
                    }

                    // No newline in the buffered span; carry the partial line over and refill.
                    _lineOverflow.AddRange(_chunkBuffer.AsSpan(_chunkStart, _chunkEnd - _chunkStart));
                    _chunkStart = _chunkEnd;
                }

                int bytesRead = await ReadChunkAsync(cancellationToken);
                if (bytesRead == 0)
                {
                    return _lineOverflow.Count == 0
                        ? null
                        : DecodeLine(CollectionsMarshal.AsSpan(_lineOverflow));
                }

                _chunkStart = 0;
                _chunkEnd = bytesRead;
            }
        }

        private async Task<int> ReadChunkAsync(CancellationToken cancellationToken)
        {
            using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readCancellation.CancelAfter(idleTimeout);

            try
            {
                return await stream.ReadAsync(_chunkBuffer, readCancellation.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"LipSyncPlayer: Audio2Face stream received no data for {idleTimeout.TotalSeconds:0.##} s.");
            }
        }

        private static string DecodeLine(ReadOnlySpan<byte> lineBytes)
        {
            if (lineBytes.Length > 0 && lineBytes[^1] == (byte)'\r')
            {
                lineBytes = lineBytes[..^1];
            }

            return Encoding.UTF8.GetString(lineBytes);
        }
    }
}
