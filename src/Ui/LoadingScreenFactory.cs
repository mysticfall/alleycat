using AlleyCat.Animation;
using AlleyCat.Common;
using AlleyCat.Env;
using AlleyCat.Service.Typed;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Ui;

[GlobalClass]
public partial class LoadingScreenFactory : NodeFactory<ILoadingScreen>
{
    [ExportGroup("Control")] [Export] public Label? Label { get; set; }

    [Export] public ProgressBar? ProgressBar { get; set; }

    [Export] public Godot.Control? Control { get; set; }

    [ExportGroup("Animation")] [Export] public AnimationPlayer? AnimationPlayer { get; set; }

    [Export] public string? ShowAnimation { get; set; }

    [Export] public string? HideAnimation { get; set; }

    protected override Eff<IEnv, ILoadingScreen> CreateService(ILoggerFactory loggerFactory) =>
        from label in Label.Require("Label is not set.")
        from progressBar in ProgressBar.Require("Progressbar is not set.")
        from control in Control.Require("Control is not set.")
        from animPlayer in AnimationPlayer.Require("Animation player is not set.")
        from showAnim in Optional(ShowAnimation)
            .Traverse(AnimationName.Create)
            .As()
            .ToEff(identity)
        from hideAnim in Optional(HideAnimation)
            .Traverse(AnimationName.Create)
            .As()
            .ToEff(identity)
        select (ILoadingScreen)new LoadingScreen(
            label,
            progressBar,
            control,
            animPlayer,
            showAnim,
            hideAnim
        );
}