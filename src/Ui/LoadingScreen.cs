using System.Reactive.Linq;
using AlleyCat.Animation;
using AlleyCat.Common;
using AlleyCat.Env;
using Godot;
using LanguageExt;
using static LanguageExt.Prelude;
using Range = Godot.Range;

namespace AlleyCat.Ui;

public interface ILoadingScreen : IProgressReporterControl, IAnimatedControl;

public class LoadingScreen(
    Label label,
    ProgressBar progressBar,
    Godot.Control control,
    AnimationPlayer animationPlayer,
    Option<AnimationName> showAnimation = default,
    Option<AnimationName> hideAnimation = default
) : ILoadingScreen, IRunnable
{
    public ProgressBar ProgressBar => progressBar;

    public Godot.Control Control => control;

    public AnimationPlayer AnimationPlayer => animationPlayer;

    public Option<AnimationName> ShowAnimation => showAnimation;

    public Option<AnimationName> HideAnimation => hideAnimation;

    public Eff<IEnv, IDisposable> Run()
    {
        var onProgress = Observable
            .FromEvent<Range.ValueChangedEventHandler, double>(
                handler => new Range.ValueChangedEventHandler(handler),
                add => progressBar.ValueChanged += add,
                remove => progressBar.ValueChanged -= remove);

        var onJobStateChange = onProgress
            .Select(x => x >= progressBar.MaxValue)
            .DistinctUntilChanged();

        return liftEff(() => onJobStateChange.Subscribe(finished =>
        {
            label.Visible = finished;
            progressBar.Visible = !finished;
        }));
    }
}