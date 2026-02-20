using EnsureThat;
using LanguageExt;

namespace AlleyCat.Common;

public static class DisposableExtensions
{
    private readonly struct ForkIoDisposable<T>(ForkIO<T> io) : IDisposable
    {
        public void Dispose() => io.Cancel.Run();
    }

    public static IDisposable AsDisposable<T>(this ForkIO<T> io) =>
        new ForkIoDisposable<T>(io);

    public static void DisposeQuietly(this IDisposable disposable)
    {
        Ensure.That(disposable, nameof(disposable)).IsNotNull();

        try
        {
            disposable.Dispose();
        }
        catch (Exception)
        {
            // ignored
        }
    }
}