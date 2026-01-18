using AlleyCat.Common;
using LanguageExt;

namespace AlleyCat.Animation.BlendShape;

public readonly record struct BlendShapeAnim(
    Seq<Map<string, float>> Frames,
    FrameRate FrameRate
);