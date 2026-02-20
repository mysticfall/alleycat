using System.Reactive.Linq;
using AlleyCat.Animation;
using AlleyCat.Common;
using AlleyCat.Env;
using Godot;
using LanguageExt;
using Range = Godot.Range;

namespace AlleyCat.Ui;

public interface ILoadingScreen : IProgressReporterControl, IAnimatedControl;

public class LoadingScreen : ILoadingScreen, IRunnable
{
    public ProgressBar ProgressBar { get; }

    public Godot.Control Control { get; }

    public AnimationPlayer AnimationPlayer { get; }

    public Option<AnimationName> ShowAnimation { get; }

    public Option<AnimationName> HideAnimation { get; }

    public Eff<IEnv, IDisposable> Run { get; }

    public LoadingScreen(
        Label label,
        ProgressBar progressBar,
        Godot.Control control,
        AnimationPlayer animationPlayer,
        Option<AnimationName> showAnimation = default,
        Option<AnimationName> hideAnimation = default
    )
    {
        ProgressBar = progressBar;
        Control = control;
        AnimationPlayer = animationPlayer;
        ShowAnimation = showAnimation;
        HideAnimation = hideAnimation;

        var onProgress = Observable
            .FromEvent<Range.ValueChangedEventHandler, double>(
                handler => new Range.ValueChangedEventHandler(handler),
                add => progressBar.ValueChanged += add,
                remove => progressBar.ValueChanged -= remove);

        var onJobStateChange = onProgress
            .Select(x => x >= progressBar.MaxValue)
            .DistinctUntilChanged();

        Run = IO.lift(() =>
            onJobStateChange.Subscribe(finished =>
            {
                label.Visible = finished;
                ProgressBar.Visible = !finished;
            })
        );
    }
}