using AlleyCat.Common;
using LanguageExt;

namespace AlleyCat.Control;

public interface IControl : IRunnable;

public interface IControllable
{
    Seq<IControl> Controls { get; }
}