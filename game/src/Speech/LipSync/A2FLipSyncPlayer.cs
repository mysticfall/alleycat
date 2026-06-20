using System.Buffers.Binary;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using AlleyCat.Body.Eyes;
using Godot;
using HttpClient = System.Net.Http.HttpClient;

namespace AlleyCat.Speech.LipSync;

/// <summary>
/// Audio2Face HTTP API-backed <see cref="LipSyncPlayer"/> implementation.
/// </summary>
[GlobalClass]
public partial class A2FLipSyncPlayer : LipSyncPlayer
{
    /// <summary>
    /// Model selection sent as the server `model` query parameter.
    /// </summary>
    public enum ModelIdOption
    {
        /// <summary>Use the Mark model.</summary>
        Mark,
        /// <summary>Use the Claire model.</summary>
        Claire,
        /// <summary>Use the James model.</summary>
        James,
        /// <summary>Use the v3 model.</summary>
        V3,
        /// <summary>Use <see cref="CustomModelId"/>.</summary>
        Custom
    }

    /// <summary>
    /// Inference mode selection sent as the server `mode` query parameter.
    /// </summary>
    public enum InferenceModeOption
    {
        /// <summary>Use regression mode.</summary>
        Regression,
        /// <summary>Use diffusion mode.</summary>
        Diffusion
    }

    /// <summary>
    /// Execution preset sent as the server `execution` query parameter.
    /// </summary>
    public enum ExecutionOption
    {
        /// <summary>Enable skin outputs only.</summary>
        Skin,
        /// <summary>Enable all outputs (`skin,tongue,jaw,eyes`).</summary>
        All,
        /// <summary>Enable skin and tongue outputs.</summary>
        SkinTongue,
        /// <summary>Enable skin, tongue, jaw, and eyes outputs.</summary>
        SkinTongueJawEyes,
        /// <summary>Use <see cref="CustomExecution"/>.</summary>
        Custom
    }

    private enum KnownModelFamily
    {
        Regression,
        Diffusion
    }

    /// <summary>
    /// Full endpoint URL for blendshape inference.
    /// </summary>
    [Export]
    public string EndpointUrl
    {
        get;
        set;
    } = "http://127.0.0.1:8765/blendshapes";

    /// <summary>
    /// Timeout used for HTTP requests to the inference server.
    /// </summary>
    [Export(PropertyHint.Range, "1,120,1")]
    public int RequestTimeoutSeconds
    {
        get;
        set;
    } = 30;

    /// <summary>
    /// When enabled, validates connectivity against GET /health during initialisation.
    /// </summary>
    [Export]
    public bool ProbeHealthOnInitialise
    {
        get;
        set;
    }

    /// <summary>
    /// Number of retry attempts when probing /health.
    /// </summary>
    [Export(PropertyHint.Range, "1,120,1")]
    public int ProbeHealthRetries
    {
        get;
        set;
    } = 20;

    /// <summary>
    /// Delay (milliseconds) between /health probe retries.
    /// </summary>
    [Export(PropertyHint.Range, "10,5000,10")]
    public int ProbeHealthRetryDelayMs
    {
        get;
        set;
    } = 250;

    /// <summary>
    /// Optional model id override (e.g. mark, claire, james, v3).
    /// </summary>
    [Export]
    public ModelIdOption ModelId
    {
        get;
        set;
    } = ModelIdOption.Mark;

    /// <summary>
    /// Custom model id when ModelId is set to Custom.
    /// </summary>
    [Export]
    public string CustomModelId
    {
        get;
        set;
    } = string.Empty;

    /// <summary>
    /// Optional inference mode override (regression or diffusion).
    /// </summary>
    [Export]
    public InferenceModeOption InferenceMode
    {
        get;
        set;
    } = InferenceModeOption.Regression;

    /// <summary>
    /// When enabled, automatically adjusts mode to match known model families.
    /// </summary>
    [Export]
    public bool AutoAdjustModeForKnownModel
    {
        get;
        set;
    } = true;

    /// <summary>
    /// Emits a warning when mode is auto-adjusted to match model compatibility.
    /// </summary>
    [Export]
    public bool WarnOnModeAutoAdjust
    {
        get;
        set;
    } = true;

    /// <summary>
    /// Optional execution flags (skin, all, skin,tongue, etc.).
    /// </summary>
    [Export]
    public ExecutionOption Execution
    {
        get;
        set;
    } = ExecutionOption.All;

    /// <summary>
    /// Custom execution query value when Execution is set to Custom.
    /// </summary>
    [Export]
    public string CustomExecution
    {
        get;
        set;
    } = string.Empty;

    /// <summary>
    /// Audio input strength sent as input_strength query parameter.
    /// </summary>
    [Export(PropertyHint.Range, "0,3,0.01")]
    public float InputStrength
    {
        get;
        set;
    } = 1f;

    /// <summary>
    /// Use GPU blendshape solver on the server.
    /// </summary>
    [Export]
    public bool UseGpuSolver
    {
        get;
        set;
    }

    /// <summary>
    /// Diffusion identity index.
    /// </summary>
    [Export(PropertyHint.Range, "0,32,1")]
    public int IdentityIndex
    {
        get;
        set;
    }

    /// <summary>
    /// Diffusion constant noise flag.
    /// </summary>
    [Export]
    public bool ConstantNoise
    {
        get;
        set;
    }

    /// <summary>
    /// Optional comma-separated emotion vector override.
    /// </summary>
    [Export]
    public string EmotionCsv
    {
        get;
        set;
    } = string.Empty;

    /// <summary>
    /// Blend factor between model default emotion and EmotionCsv.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float EmotionBlend
    {
        get;
        set;
    } = 1f;

    /// <summary>
    /// When enabled, replaces the solver's PCA-derived eye-look blendshape weights
    /// with values derived from the dedicated eyesRotation tensor output. This
    /// produces more accurate and symmetrical eye movements. Requires the server
    /// execution to include eyes.
    /// </summary>
    [Export]
    public bool TranslateEyeRotationsToBlendshapes
    {
        get;
        set;
    } = true;

    /// <summary>
    /// Scale applied to eye rotation values before converting to blendshape weights.
    /// The SDK outputs rotation in radians (typical range ±0.1–0.5 for direct gaze,
    /// up to ±6 with saccade). A scale of ~2.0 maps a 0.5 rad rotation to full
    /// blendshape weight. Increase for more pronounced eye movement, decrease for
    /// subtler motion.
    /// </summary>
    [Export(PropertyHint.Range, "0,10,0.01")]
    public float EyeRotationToBlendshapeScale
    {
        get;
        set;
    } = 2f;

    /// <summary>
    /// Invert horizontal eye rotation sign before conversion.
    /// </summary>
    [Export]
    public bool InvertEyeRotationHorizontal
    {
        get;
        set;
    }

    /// <summary>
    /// Invert vertical eye rotation sign before conversion.
    /// </summary>
    [Export]
    public bool InvertEyeRotationVertical
    {
        get;
        set;
    }

    /// <summary>
    /// Temporal smoothing factor for eye rotation blendshape translation.
    /// Each output value is blended with the previous frame:
    ///   smoothed = prev * (1 - factor) + current * factor.
    /// A value of 1.0 means no smoothing; lower values produce smoother motion
    /// at the cost of latency. Typical range: 0.2–0.6.
    /// </summary>
    [Export(PropertyHint.Range, "0.01,1,0.01")]
    public float EyeRotationSmoothingFactor
    {
        get;
        set;
    } = 0.4f;

    private HttpClient? _httpClient;
    private Uri? _blendshapeEndpointUri;
    private bool _hasWarnedModeAutoAdjust;
    private bool _hasWarnedMissingEyeRotationFrames;
    private bool _hasWarnedMissingEyeBlendshapeChannels;
    private static readonly IReadOnlySet<string> _eyesControlledBlendshapeNames = CreateEyesControlledBlendshapeNames();

    /// <inheritdoc />
    protected override void InitialiseBackend()
    {
        _blendshapeEndpointUri = BuildBlendshapeEndpointUri(EndpointUrl);
        _hasWarnedModeAutoAdjust = false;
        _hasWarnedMissingEyeRotationFrames = false;
        _hasWarnedMissingEyeBlendshapeChannels = false;

        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds)
        };
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (ProbeHealthOnInitialise)
        {
            ProbeHealthWithRetry(_httpClient, _blendshapeEndpointUri, ProbeHealthRetries, ProbeHealthRetryDelayMs);
        }
    }

    /// <inheritdoc />
    protected override LipSyncInferenceResult RunBackendInference(AudioStreamWav speech)
    {
        HttpClient httpClient = _httpClient
            ?? throw new InvalidOperationException("LipSyncPlayer: HTTP client was not initialised.");
        Uri endpointUri = _blendshapeEndpointUri
            ?? throw new InvalidOperationException("LipSyncPlayer: endpoint URI was not initialised.");
        Uri requestUri = BuildInferenceUri(endpointUri);
        float[] monoWaveform = LoadAudioWaveform(speech);

        using var requestContent = new ByteArrayContent(FloatsToBytesLittleEndian(monoWaveform));
        requestContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using HttpResponseMessage response = httpClient
            .PostAsync(requestUri, requestContent)
            .GetAwaiter()
            .GetResult();

        string jsonPayload = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            string errorMessage = TryParseApiError(jsonPayload) ?? jsonPayload;
            throw new InvalidOperationException(
                $"LipSyncPlayer: Audio2Face API request failed ({(int)response.StatusCode} {response.StatusCode}): {errorMessage}");
        }

        A2fInferenceResponse? payload = JsonSerializer.Deserialize<A2fInferenceResponse>(jsonPayload, _jsonOptions)
            ?? throw new InvalidOperationException("LipSyncPlayer: failed to parse Audio2Face API response.");

        List<string> blendshapeNames = payload.BlendshapeNames.Count == 0
            ? throw new InvalidOperationException("LipSyncPlayer: API returned no blendshape names.")
            : payload.BlendshapeNames;

        for (int frameIndex = 0; frameIndex < payload.Frames.Length; frameIndex++)
        {
            if (payload.Frames[frameIndex].Length != blendshapeNames.Count)
            {
                throw new InvalidOperationException(
                    $"LipSyncPlayer: API frame {frameIndex} has {payload.Frames[frameIndex].Length} channels, expected {blendshapeNames.Count}.");
            }
        }

        if (TranslateEyeRotationsToBlendshapes)
        {
            ApplyEyeRotationBlendshapeTranslation(payload.Frames, blendshapeNames, payload.EyeRotationFrames);
        }

        LipSyncInferenceResult filteredResult = RemoveEyesControlledBlendshapes(payload.Frames, blendshapeNames, payload.Fps);
        return filteredResult.BlendshapeNames.Count == 0
            ? throw new InvalidOperationException("LipSyncPlayer: API returned no non-eye blendshape names.")
            : filteredResult;
    }

    /// <inheritdoc />
    protected override void DisposeBackend()
    {
        _httpClient?.Dispose();
        _httpClient = null;
        _blendshapeEndpointUri = null;
        _hasWarnedModeAutoAdjust = false;
        _hasWarnedMissingEyeRotationFrames = false;
        _hasWarnedMissingEyeBlendshapeChannels = false;
    }

    private static Uri BuildBlendshapeEndpointUri(string endpointUrl)
    {
        if (!Uri.TryCreate(endpointUrl, UriKind.Absolute, out Uri? endpointUri))
        {
            throw new InvalidOperationException(
                $"LipSyncPlayer: EndpointUrl must be an absolute URL, got '{endpointUrl}'.");
        }

        bool isHttpScheme = endpointUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || endpointUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        _ = isHttpScheme
            ? true
            : throw new InvalidOperationException(
                $"LipSyncPlayer: EndpointUrl must use HTTP or HTTPS, got '{endpointUri.Scheme}'.");

        return endpointUri;
    }

    private static void ProbeHealthWithRetry(
        HttpClient httpClient,
        Uri blendshapeEndpointUri,
        int retries,
        int retryDelayMs
    )
    {
        Uri healthUri = BuildHealthUri(blendshapeEndpointUri);
        Exception? lastError = null;

        int attempts = Mathf.Max(1, retries);
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                using HttpResponseMessage response = httpClient
                    .GetAsync(healthUri)
                    .GetAwaiter()
                    .GetResult();
                string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(
                        $"health probe failed ({(int)response.StatusCode} {response.StatusCode}) at '{healthUri}'. Body: {body}");
                }

                HealthResponse? health = JsonSerializer.Deserialize<HealthResponse>(body, _jsonOptions);
                if (health is null || !string.Equals(health.Status, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"health probe returned unexpected payload at '{healthUri}': {body}");
                }

                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt == attempts)
                {
                    break;
                }

                Thread.Sleep(Mathf.Max(1, retryDelayMs));
            }
        }

        throw new InvalidOperationException(
            $"LipSyncPlayer: health probe failed after {attempts} attempt(s) at '{healthUri}'. Last error: {lastError}");
    }

    private static Uri BuildHealthUri(Uri blendshapeEndpointUri)
    {
        var builder = new UriBuilder(blendshapeEndpointUri)
        {
            Query = string.Empty,
            Fragment = string.Empty
        };

        string path = builder.Path.TrimEnd('/');
        if (path.EndsWith("/blendshapes", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^"/blendshapes".Length];
        }

        builder.Path = string.IsNullOrEmpty(path) ? "/health" : $"{path}/health";
        return builder.Uri;
    }

    private Uri BuildInferenceUri(Uri baseEndpointUri)
    {
        var queryParts = new List<string>();

        string existingQuery = baseEndpointUri.Query.TrimStart('?');
        if (!string.IsNullOrWhiteSpace(existingQuery))
        {
            queryParts.Add(existingQuery);
        }

        string resolvedModelId = ResolveModelId();
        AppendQuery(queryParts, "model", resolvedModelId);
        string resolvedMode = ResolveInferenceMode();
        resolvedMode = ResolveEffectiveMode(resolvedModelId, resolvedMode);
        AppendQuery(queryParts, "mode", resolvedMode);
        string execution = ResolveExecution();
        if (TranslateEyeRotationsToBlendshapes)
        {
            execution = EnsureExecutionIncludesEyes(execution);
        }
        AppendQuery(queryParts, "execution", execution);
        if (Mathf.Abs(InputStrength - 1f) > 1e-4f)
        {
            AppendQuery(queryParts, "input_strength", InputStrength.ToString(CultureInfo.InvariantCulture));
        }
        if (UseGpuSolver)
        {
            AppendQuery(queryParts, "use_gpu_solver", "true");
        }
        if (IdentityIndex > 0)
        {
            AppendQuery(queryParts, "identity_index", IdentityIndex.ToString(CultureInfo.InvariantCulture));
        }
        if (ConstantNoise)
        {
            AppendQuery(queryParts, "constant_noise", "true");
        }
        AppendQuery(queryParts, "emotion", EmotionCsv);
        if (!string.IsNullOrWhiteSpace(EmotionCsv))
        {
            AppendQuery(queryParts, "emotion_blend", Mathf.Clamp(EmotionBlend, 0f, 1f).ToString(CultureInfo.InvariantCulture));
        }

        if (queryParts.Count == 0)
        {
            return baseEndpointUri;
        }

        var builder = new UriBuilder(baseEndpointUri)
        {
            Query = string.Join("&", queryParts)
        };
        return builder.Uri;
    }

    private static void AppendQuery(List<string> queryParts, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        queryParts.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}");
    }

    private string ResolveModelId() => ModelId switch
    {
        ModelIdOption.Mark => "mark",
        ModelIdOption.Claire => "claire",
        ModelIdOption.James => "james",
        ModelIdOption.V3 => "v3",
        ModelIdOption.Custom => string.IsNullOrWhiteSpace(CustomModelId) ? "mark" : CustomModelId.Trim(),
        _ => "mark"
    };

    private string ResolveInferenceMode() => InferenceMode switch
    {
        InferenceModeOption.Regression => "regression",
        InferenceModeOption.Diffusion => "diffusion",
        _ => "regression"
    };

    private string ResolveExecution() => Execution switch
    {
        ExecutionOption.Skin => "skin",
        ExecutionOption.All => "all",
        ExecutionOption.SkinTongue => "skin,tongue",
        ExecutionOption.SkinTongueJawEyes => "skin,tongue,jaw,eyes",
        ExecutionOption.Custom => string.IsNullOrWhiteSpace(CustomExecution) ? "skin" : CustomExecution.Trim(),
        _ => "skin"
    };

    private string ResolveEffectiveMode(string modelId, string requestedMode)
    {
        if (!AutoAdjustModeForKnownModel)
        {
            return requestedMode;
        }

        KnownModelFamily? family = DetectKnownModelFamily(modelId);
        if (!family.HasValue)
        {
            return requestedMode;
        }

        string requiredMode = family.Value == KnownModelFamily.Diffusion
            ? "diffusion"
            : "regression";

        if (string.Equals(requestedMode, requiredMode, StringComparison.OrdinalIgnoreCase))
        {
            return requestedMode;
        }

        if (WarnOnModeAutoAdjust && !_hasWarnedModeAutoAdjust)
        {
            GD.PushWarning(
                $"A2FLipSyncPlayer: auto-adjusted mode from '{requestedMode}' to '{requiredMode}' for model '{modelId}'.");
            _hasWarnedModeAutoAdjust = true;
        }

        return requiredMode;
    }

    private static KnownModelFamily? DetectKnownModelFamily(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        string normalized = modelId.Trim().ToLowerInvariant();
        return normalized switch
        {
            "mark" => KnownModelFamily.Regression,
            "claire" => KnownModelFamily.Regression,
            "james" => KnownModelFamily.Regression,
            "v3" => KnownModelFamily.Diffusion,
            _ => null
        };
    }

    private static string EnsureExecutionIncludesEyes(string execution)
    {
        if (string.IsNullOrWhiteSpace(execution))
        {
            return "eyes";
        }

        if (string.Equals(execution, "all", StringComparison.OrdinalIgnoreCase))
        {
            return execution;
        }

        string[] parts = execution.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var tokens = new List<string>(parts.Length + 1);
        foreach (string part in parts)
        {
            string token = part.ToLowerInvariant();
            if (seen.Add(token))
            {
                tokens.Add(token);
            }
        }

        if (!tokens.Contains("eyes", StringComparer.Ordinal))
        {
            tokens.Add("eyes");
        }

        return string.Join(',', tokens);
    }

    private void ApplyEyeRotationBlendshapeTranslation(
        float[][] blendshapeFrames,
        IReadOnlyList<string> blendshapeNames,
        float[][]? eyeRotationFrames
    )
    {
        if (eyeRotationFrames is null || eyeRotationFrames.Length == 0)
        {
            if (!_hasWarnedMissingEyeRotationFrames)
            {
                GD.PushWarning(
                    "A2FLipSyncPlayer: eye_rotation_frames missing from server response; eye translation skipped. " +
                    "Set execution to include eyes.");
                _hasWarnedMissingEyeRotationFrames = true;
            }
            return;
        }

        Dictionary<string, int> nameToIndex = new(StringComparer.Ordinal);
        for (int i = 0; i < blendshapeNames.Count; i++)
        {
            string normalized = NormalizeBlendshapeName(blendshapeNames[i]);
            _ = nameToIndex.TryAdd(normalized, i);
        }

        if (!TryGetEyeBlendshapeIndices(nameToIndex, out EyeBlendshapeIndices indices))
        {
            if (!_hasWarnedMissingEyeBlendshapeChannels)
            {
                GD.PushWarning(
                    "A2FLipSyncPlayer: required ARKit eyeLook blendshape channels are missing in blendshape_names; eye translation skipped.");
                _hasWarnedMissingEyeBlendshapeChannels = true;
            }
            return;
        }

        float[] baseline = ComputeEyeRotationBaseline(eyeRotationFrames);

        float scale = Mathf.Max(0f, EyeRotationToBlendshapeScale);
        float alpha = Mathf.Clamp(EyeRotationSmoothingFactor, 0.01f, 1f);
        float prevRightH = 0f, prevRightV = 0f, prevLeftH = 0f, prevLeftV = 0f;
        int count = Math.Min(blendshapeFrames.Length, eyeRotationFrames.Length);
        for (int frameIndex = 0; frameIndex < count; frameIndex++)
        {
            float[] frame = blendshapeFrames[frameIndex];
            float[]? eyeFrame = eyeRotationFrames[frameIndex];
            if (eyeFrame is null || eyeFrame.Length < 6)
            {
                continue;
            }

            float rightH = eyeFrame[0] - baseline[0];
            float rightV = eyeFrame[1] - baseline[1];
            float leftH = eyeFrame[3] - baseline[3];
            float leftV = eyeFrame[4] - baseline[4];

            rightH = prevRightH + ((rightH - prevRightH) * alpha);
            rightV = prevRightV + ((rightV - prevRightV) * alpha);
            leftH = prevLeftH + ((leftH - prevLeftH) * alpha);
            leftV = prevLeftV + ((leftV - prevLeftV) * alpha);
            prevRightH = rightH;
            prevRightV = rightV;
            prevLeftH = leftH;
            prevLeftV = leftV;

            if (InvertEyeRotationHorizontal)
            {
                rightH = -rightH;
                leftH = -leftH;
            }
            if (InvertEyeRotationVertical)
            {
                rightV = -rightV;
                leftV = -leftV;
            }

            WriteDirectionalPair(frame, indices.RightOut, indices.RightIn, rightH, scale);
            WriteDirectionalPair(frame, indices.LeftIn, indices.LeftOut, leftH, scale);
            WriteDirectionalPair(frame, indices.RightUp, indices.RightDown, rightV, scale);
            WriteDirectionalPair(frame, indices.LeftUp, indices.LeftDown, leftV, scale);
        }
    }

    private static float[] ComputeEyeRotationBaseline(float[][]? eyeRotationFrames)
    {
        float[] sums = new float[6];
        int validCount = 0;

        if (eyeRotationFrames is null)
        {
            return sums;
        }

        foreach (float[]? eyeFrame in eyeRotationFrames)
        {
            if (eyeFrame is null || eyeFrame.Length < 6)
            {
                continue;
            }

            for (int i = 0; i < 6; i++)
            {
                sums[i] += eyeFrame[i];
            }

            validCount++;
        }

        if (validCount > 0)
        {
            for (int i = 0; i < 6; i++)
            {
                sums[i] /= validCount;
            }
        }

        return sums;
    }

    private static void WriteDirectionalPair(
        float[] frame,
        int positiveIndex,
        int negativeIndex,
        float value,
        float scale
    )
    {
        float scaled = value * scale;
        frame[positiveIndex] = Mathf.Clamp(scaled, 0f, 1f);
        frame[negativeIndex] = Mathf.Clamp(-scaled, 0f, 1f);
    }

    private static bool TryGetEyeBlendshapeIndices(
        Dictionary<string, int> nameToIndex,
        out EyeBlendshapeIndices indices
    )
    {
        if (!nameToIndex.TryGetValue(NormalizeBlendshapeName(EyesAnimationTreePaths.EyeLookInLeftBlendShapeName), out int leftIn)
            || !nameToIndex.TryGetValue(NormalizeBlendshapeName(EyesAnimationTreePaths.EyeLookOutLeftBlendShapeName), out int leftOut)
            || !nameToIndex.TryGetValue(NormalizeBlendshapeName(EyesAnimationTreePaths.EyeLookUpLeftBlendShapeName), out int leftUp)
            || !nameToIndex.TryGetValue(NormalizeBlendshapeName(EyesAnimationTreePaths.EyeLookDownLeftBlendShapeName), out int leftDown)
            || !nameToIndex.TryGetValue(NormalizeBlendshapeName(EyesAnimationTreePaths.EyeLookInRightBlendShapeName), out int rightIn)
            || !nameToIndex.TryGetValue(NormalizeBlendshapeName(EyesAnimationTreePaths.EyeLookOutRightBlendShapeName), out int rightOut)
            || !nameToIndex.TryGetValue(NormalizeBlendshapeName(EyesAnimationTreePaths.EyeLookUpRightBlendShapeName), out int rightUp)
            || !nameToIndex.TryGetValue(NormalizeBlendshapeName(EyesAnimationTreePaths.EyeLookDownRightBlendShapeName), out int rightDown))
        {
            indices = default;
            return false;
        }

        indices = new EyeBlendshapeIndices(
            leftIn,
            leftOut,
            leftUp,
            leftDown,
            rightIn,
            rightOut,
            rightUp,
            rightDown
        );
        return true;
    }

    private static LipSyncInferenceResult RemoveEyesControlledBlendshapes(
        float[][] frames,
        IReadOnlyList<string> blendshapeNames,
        float fps
    )
    {
        var retainedIndices = new List<int>(blendshapeNames.Count);
        var retainedNames = new List<string>(blendshapeNames.Count);
        for (int index = 0; index < blendshapeNames.Count; index++)
        {
            if (IsEyesControlledBlendshapeName(blendshapeNames[index]))
            {
                continue;
            }

            retainedIndices.Add(index);
            retainedNames.Add(blendshapeNames[index]);
        }

        if (retainedIndices.Count == blendshapeNames.Count)
        {
            return new LipSyncInferenceResult(frames, blendshapeNames, fps);
        }

        float[][] retainedFrames = new float[frames.Length][];
        for (int frameIndex = 0; frameIndex < frames.Length; frameIndex++)
        {
            float[] sourceFrame = frames[frameIndex];
            float[] retainedFrame = new float[retainedIndices.Count];
            for (int index = 0; index < retainedIndices.Count; index++)
            {
                retainedFrame[index] = sourceFrame[retainedIndices[index]];
            }

            retainedFrames[frameIndex] = retainedFrame;
        }

        return new LipSyncInferenceResult(retainedFrames, retainedNames, fps);
    }

    private static bool IsEyesControlledBlendshapeName(string blendshapeName)
        => _eyesControlledBlendshapeNames.Contains(NormalizeBlendshapeName(blendshapeName));

    private static IReadOnlySet<string> CreateEyesControlledBlendshapeNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string blendshapeName in EyesAnimationTreePaths.EyeBlendShapeNames)
        {
            _ = names.Add(NormalizeBlendshapeName(blendshapeName));
        }

        return names;
    }

    private readonly record struct EyeBlendshapeIndices(
        int LeftIn,
        int LeftOut,
        int LeftUp,
        int LeftDown,
        int RightIn,
        int RightOut,
        int RightUp,
        int RightDown
    );

    private static string? TryParseApiError(string jsonPayload)
    {
        if (string.IsNullOrWhiteSpace(jsonPayload))
        {
            return null;
        }

        try
        {
            A2fErrorResponse? error = JsonSerializer.Deserialize<A2fErrorResponse>(jsonPayload, _jsonOptions);
            return string.IsNullOrWhiteSpace(error?.Error) ? null : error.Error;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static float[] LoadAudioWaveform(AudioStreamWav audioStream)
    {
        if (audioStream.Format != AudioStreamWav.FormatEnum.Format16Bits)
        {
            throw new InvalidOperationException(
                $"LipSyncPlayer: expected AudioStreamWav format {AudioStreamWav.FormatEnum.Format16Bits}, got {audioStream.Format}.");
        }

        if (audioStream.MixRate != 16000)
        {
            throw new InvalidOperationException($"LipSyncPlayer: expected 16000 Hz audio, got {audioStream.MixRate} Hz.");
        }

        if (audioStream.Stereo)
        {
            throw new InvalidOperationException("LipSyncPlayer: expected mono audio stream, but stream is stereo.");
        }

        byte[] data = audioStream.Data.Length == 0
            ? throw new InvalidOperationException("LipSyncPlayer: AudioStreamWav contains no PCM data.")
            : audioStream.Data;

        return DecodePcm16Bytes(data);
    }

    private static float[] DecodePcm16Bytes(IReadOnlyList<byte> data)
    {
        if ((data.Count & 1) != 0)
        {
            throw new InvalidOperationException("LipSyncPlayer: PCM16 data length must be even.");
        }

        int sampleCount = data.Count / 2;
        float[] samples = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            int dataOffset = i * 2;
            short value = (short)(data[dataOffset] | (data[dataOffset + 1] << 8));
            samples[i] = value / 32768f;
        }

        return samples;
    }

    private static byte[] FloatsToBytesLittleEndian(float[] values)
    {
        byte[] bytes = new byte[values.Length * sizeof(float)];
        for (int i = 0; i < values.Length; i++)
        {
            uint bits = BitConverter.SingleToUInt32Bits(values[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * sizeof(float), sizeof(float)), bits);
        }

        return bytes;
    }

    private sealed class A2fErrorResponse
    {
        [JsonPropertyName("error")]
        public string Error
        {
            get;
            init;
        } = string.Empty;
    }

    private sealed class HealthResponse
    {
        [JsonPropertyName("status")]
        public string Status
        {
            get;
            init;
        } = string.Empty;
    }

    private sealed class A2fInferenceResponse
    {
        [JsonPropertyName("frames")]
        public float[][] Frames
        {
            get;
            init;
        } = [];

        [JsonPropertyName("blendshape_names")]
        public List<string> BlendshapeNames
        {
            get;
            init;
        } = [];

        [JsonPropertyName("fps")]
        public float Fps
        {
            get;
            init;
        } = 60f;

        [JsonPropertyName("jaw_frames")]
        public float[][]? JawFrames
        {
            get;
            init;
        }

        [JsonPropertyName("eye_rotation_frames")]
        public float[][]? EyeRotationFrames
        {
            get;
            init;
        }
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
}
