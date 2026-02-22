using System.Reactive.Disposables;
using System.Reactive.Linq;
using AlleyCat.Control;
using AlleyCat.Env;
using LanguageExt;

namespace AlleyCat.Locomotion;

public class LocomotionControl : IControl
{
    public Eff<IEnv, IDisposable> Run { get; }

    public LocomotionControl(
        ILocomotion locomotion,
        IInput2d movementInput,
        IInput2d rotationInput
    )
    {
        Run =
            from d1 in IO.lift(() =>
                movementInput.OnInput
                    .Select(locomotion.Move)
                    .Subscribe(x => x.Run())
            )
            from d2 in IO.lift(() =>
                rotationInput.OnInput
                    .Select(locomotion.Turn)
                    .Subscribe(x => x.Run())
            )
            select (IDisposable)new CompositeDisposable(d1, d2);
    }
}