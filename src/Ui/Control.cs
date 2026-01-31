using AlleyCat.Common;
using LanguageExt;

namespace AlleyCat.Ui;

public interface IControl : IHideable
{
    Godot.Control Control { get; }

    IO<bool> IHideable.IsVisible => IO.lift(() => Control.Visible);

    IO<Unit> IHideable.SetVisible(bool visible) => IO.lift(() =>
    {
        Control.Visible = visible;
    });
}