using Godot;

namespace AlleyCat.Tests.XR;

/// <summary>One representative frame of the pinned metacarpal directional-envelope oracle (XR-002 TR28.7).</summary>
/// <param name="Marker">Pose window marker, e.g. <c>RESET_NEUTRAL</c>.</param>
/// <param name="FrameIndex">Trace frame index of the selected settled-window median frame.</param>
/// <param name="SourceRelation">Traced wrist→metacarpal relation S (float32-rounded).</param>
/// <param name="ExpectedWritten">Oracle-predicted <c>writtenLocal</c> under the current directional response.</param>
/// <param name="PreGainSwingDegrees">Pre-gain swing angle in degrees.</param>
/// <param name="PostGainSwingDegrees">Directionally gained swing angle in degrees.</param>
internal sealed record PinnedReplayFrame(
    string Marker,
    int FrameIndex,
    Quaternion SourceRelation,
    Quaternion ExpectedWritten,
    float PreGainSwingDegrees,
    float PostGainSwingDegrees);

/// <summary>
/// The per-side pinned oracle for the XR-002 TR28.7 anchored hand-frame correspondence and current directional
/// envelope. Its 18 fixtures are the rotated bilateral pair for all nine captured pose families in source
/// representative pose-family oracle. They carry the binding-frame values (metacarpal
/// frame <c>(l, b, h)</c>, palm plane <c>(u, t, n_palm,H)</c>, Reset-key neutral, <c>Q0</c> anchor, and
/// <c>K_meta</c> gain), settled neutral-window mean source relation, traced relations, and predicted outputs.
/// The current C1 response applies <c>smoothstep(h, 0.400, 0.625) × smoothstep(mirrored-b, 0.050, 0.150)</c>
/// to the available gain; the unconditional-gain counterfactual is the discrimination baseline. The immutable
/// input trace marker <c>THUMB_OPPOSITION_BREACH</c> identifies the samples represented by the current
/// runtime/guidance family <c>THUMB_OPPOSITION_MAX</c>.
/// </summary>
internal sealed record PinnedReplaySide(
    float SwingGain,
    Quaternion NeutralAnchor,
    Quaternion Neutral,
    Vector3 Longitudinal,
    Vector3 Bend,
    Vector3 Splay,
    Vector3 PalmLongitudinal,
    Vector3 PalmSpanAxis,
    Vector3 PalmNormal,
    Quaternion NeutralMeanSourceRelation,
    PinnedReplayFrame[] Frames)
{
    // Left: K_meta = 2.00, Q0 = 33.17 degrees about an axis in the (b, h) plane; pairing signs (t: -1, n: +1).
    public static readonly PinnedReplaySide Left = new(
        2.0f,
        new Quaternion(-0.1273963451385498f, 0.019958913326263428f, 0.25467225909233093f, 0.9583913087844849f),
        new Quaternion(-0.21418680250644684f, 0.6738871932029724f, 0.21418674290180206f, 0.673887312412262f),
        new Vector3(-0.6894838213920593f, 0.6086642146110535f, -0.39260655641555786f),
        new Vector3(-0.7223097085952759f, -0.6179797053337097f, 0.3104347884654999f),
        new Vector3(-0.05367233604192734f, 0.4976232945919037f, 0.8657311201095581f),
        new Vector3(-0.01984940655529499f, 0.9990062117576599f, 0.03990752995014191f),
        new Vector3(-0.999762237071991f, -0.020193127915263176f, 0.00822834949940443f),
        new Vector3(0.009026030078530312f, -0.03973471373319626f, 0.9991694688796997f),
        new Quaternion(-0.12057094275951385f, 0.5340858101844788f, 0.38534015417099f, 0.7427839040756226f),
        [
            new("RESET_NEUTRAL", 1068, new(-0.04785086f, 0.56230736f, 0.46444228f, 0.6825058f), new(-0.16941956f, 0.70126697f, 0.3133485f, 0.61752276f), 14.396881f, 14.396881f),
            new("HAND_EXTENDED_TOGETHER", 2915, new(-0.23166943f, 0.49898964f, 0.17024675f, 0.8175296f), new(-0.27243005f, 0.6290499f, -0.02469681f, 0.7276456f), 29.375937f, 29.375937f),
            new("FIST", 4631, new(0.051190518f, 0.6543204f, 0.3893359f, 0.64626765f), new(0.002577848f, 0.8863736f, 0.27814758f, 0.3700935f), 25.0768f, 50.1536f),
            new("SPREAD", 6101, new(-0.15144102f, 0.49507013f, 0.41209248f, 0.7497673f), new(-0.23368205f, 0.63640565f, 0.23157333f, 0.6976778f), 5.903189f, 5.903189f),
            new("THUMB_OPPOSITION", 8580, new(-0.023783227f, 0.7370851f, 0.16266775f, 0.65549916f), new(-0.15108417f, 0.80199885f, 0.010305637f, 0.57781065f), 30.66672f, 30.66672f),
            new("THUMB_OPPOSITION_SAFE", 10135, new(-0.07579277f, 0.6791836f, 0.18648723f, 0.7058241f), new(-0.18128021f, 0.7622729f, 0.030617232f, 0.6205966f), 24.471563f, 24.471563f),
            new("THUMB_OPPOSITION_MAX", 12214, new(-0.010016209f, 0.75125355f, 0.15946195f, 0.64038247f), new(-0.14295298f, 0.8117097f, 0.007117965f, 0.5662519f), 32.218685f, 32.218685f),
            new("THUMB_PINKY_CONTACT", 14027, new(0.11481385f, 0.73294955f, 0.29670635f, 0.6013053f), new(0.08394218f, 0.9673521f, 0.0827248f, 0.22436631f), 36.599464f, 73.19893f),
            new("RESET_NEUTRAL_RETURN", 15721, new(-0.14474282f, 0.5018821f, 0.41362184f, 0.74570835f), new(-0.22934856f, 0.6421229f, 0.23549551f, 0.6925455f), 5.1774435f, 5.1774435f),
        ]);

    // Right: K_meta = 2.25, Q0 = 22.34 degrees about an axis in the (b, h) plane; pairing signs (t: +1, n: +1).
    public static readonly PinnedReplaySide Right = new(
        2.25f,
        new Quaternion(-0.10855846107006073f, 0.020310375839471817f, -0.15915964543819427f, 0.9810559153556824f),
        new Quaternion(0.21418680250644684f, 0.6738871932029724f, 0.21418674290180206f, -0.673887312412262f),
        new Vector3(0.6894838213920593f, 0.6086642742156982f, -0.3926064968109131f),
        new Vector3(0.7223097085952759f, -0.6179797053337097f, 0.3104347884654999f),
        new Vector3(-0.05367228761315346f, -0.4976232647895813f, -0.8657311201095581f),
        new Vector3(0.01984940655529499f, 0.9990062117576599f, 0.03990752995014191f),
        new Vector3(0.999762237071991f, -0.020193127915263176f, 0.00822834949940443f),
        new Vector3(-0.009026030078530312f, -0.03973471373319626f, 0.9991694688796997f),
        new Quaternion(-0.04769066721200943f, -0.573541522026062f, -0.43561333417892456f, 0.6921103596687317f),
        [
            new("RESET_NEUTRAL", 1316, new(-0.034886863f, -0.5858202f, -0.4286334f, 0.6869287f), new(0.19607276f, 0.703681f, 0.2061355f, -0.6510735f), 2.1615696f, 4.8635316f),
            new("HAND_EXTENDED_TOGETHER", 2811, new(-0.21677937f, -0.52308923f, -0.16712645f, 0.80712646f), new(0.2946898f, 0.5923345f, -0.09918647f, -0.74327636f), 39.245083f, 39.245083f),
            new("FIST", 4369, new(0.07513449f, -0.6520054f, -0.41929197f, 0.62724644f), new(0.04303323f, 0.8653935f, 0.23772639f, -0.43900836f), 17.857695f, 40.179813f),
            new("SPREAD", 6656, new(-0.09712423f, -0.54909694f, -0.39900145f, 0.72791296f), new(0.2424856f, 0.64472544f, 0.16203538f, -0.7065935f), 8.45431f, 8.45431f),
            new("THUMB_OPPOSITION", 7863, new(0.10506614f, -0.73986876f, -0.2756195f, 0.6046397f), new(0.06814714f, 0.885069f, 0.019646997f, -0.4600248f), 30.951796f, 44.589077f),
            new("THUMB_OPPOSITION_SAFE", 9743, new(0.070968606f, -0.7082086f, -0.30478993f, 0.63285625f), new(0.10838355f, 0.8421928f, 0.06737505f, -0.52385575f), 24.630875f, 33.24598f),
            new("THUMB_OPPOSITION_MAX", 11963, new(0.01520187f, -0.7451247f, -0.19240238f, 0.63838816f), new(0.16346365f, 0.78945345f, -0.01819636f, -0.5913644f), 31.846594f, 31.846594f),
            new("THUMB_PINKY_CONTACT", 13909, new(0.13941303f, -0.74577624f, -0.29017118f, 0.5832518f), new(-0.058221504f, 0.96507585f, -0.044071343f, -0.2515882f), 33.670364f, 74.12386f),
            new("RESET_NEUTRAL_RETURN", 15918, new(-0.00538488f, -0.6108294f, -0.41267255f, 0.6756921f), new(0.15518443f, 0.7650384f, 0.18517306f, -0.5969464f), 6.942181f, 15.619907f),
        ]);
}
