using LanguageExt;
using LanguageExt.Common;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using static LanguageExt.Prelude;

namespace AlleyCat.Io;

public readonly struct FileProvider : IFileProvider
{
    public IFileInfo GetFileInfo(string subPath) => new FileInfo(subPath);

    public IDirectoryContents GetDirectoryContents(string subPath) => new DirectoryInfo(subPath);

    public IChangeToken Watch(string filter) => NullChangeToken.Singleton;
}

public static class FileProviderExtensions
{
    public static Eff<string> ReadAllText(this IFileProvider provider, string path)
    {
        var file = provider.GetFileInfo(path);

        if (!file.Exists)
        {
            return Error.New($"File not found: {path}");
        }

        return liftIO(async () =>
        {
            await using var stream = file.CreateReadStream();
            using var reader = new StreamReader(stream);

            return await reader.ReadToEndAsync();
        });
    }
}