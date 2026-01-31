using LanguageExt;

namespace AlleyCat.Common;

public interface IProgressReporter
{
    IO<Unit> Report(NormalisedRatio ratio);
}