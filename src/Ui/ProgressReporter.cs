using AlleyCat.Common;
using Godot;
using LanguageExt;

namespace AlleyCat.Ui;

public interface IProgressReporterControl : IProgressReporter, IControl
{
    protected ProgressBar ProgressBar { get; }

    IO<Unit> IProgressReporter.Report(NormalisedRatio ratio) =>
        IO.lift(() =>
        {
            var min = ProgressBar.MinValue;
            var max = ProgressBar.MaxValue;

            ProgressBar.Value = min + ratio.Value * (max - min);
        });
}