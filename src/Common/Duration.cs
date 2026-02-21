using LanguageExt;

namespace AlleyCat.Common;

public static class DurationExtensions
{
    extension(Duration duration)
    {
        public double Seconds => duration.Milliseconds / 1000.0;
    }
}