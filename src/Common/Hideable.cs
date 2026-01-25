using LanguageExt;

namespace AlleyCat.Common;

public interface IHideable
{
    IO<bool> IsVisible { get; }

    IO<Unit> SetVisible(bool visible);
}

public static class HideableExtensions
{
    extension(IHideable hideable)
    {
        public IO<bool> IsHidden => hideable.IsVisible.Map(x => !x);

        public IO<Unit> Show() => hideable.SetVisible(true);

        public IO<Unit> Hide() => hideable.SetVisible(false);
    }
}