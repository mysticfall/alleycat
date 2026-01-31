using AlleyCat.Animation;
using AlleyCat.Common;
using LanguageExt;

namespace AlleyCat.Ui;

public interface IAnimatedControl : IAnimatable, IControl
{
    Option<AnimationName> ShowAnimation { get; }

    Option<AnimationName> HideAnimation { get; }

    IO<Unit> IHideable.SetVisible(bool visible) =>
        (visible ? ShowAnimation : HideAnimation).Match(
            x => IO.lift(() => AnimationPlayer.Play(x)),
            () => SetVisible(visible)
        );
}