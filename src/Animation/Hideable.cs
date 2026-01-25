using AlleyCat.Common;
using LanguageExt;

namespace AlleyCat.Animation;

public interface IAnimatedHideable : IAnimatable, IHideable
{
    Option<AnimationName> ShowAnimation { get; }

    Option<AnimationName> HideAnimation { get; }

    IO<Unit> IHideable.SetVisible(bool visible) =>
        (visible ? ShowAnimation : HideAnimation).Match(
            x => IO.lift(() => AnimationPlayer.Play(x)),
            () => SetVisible(visible)
        );
}