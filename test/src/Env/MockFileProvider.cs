using AlleyCat.Io;
using LanguageExt.UnsafeValueAccess;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace AlleyCat.Tests.Env;

public class MockFileProvider(string resourceRoot, string userRoot) : IFileProvider
{
    private readonly PhysicalFileProvider _resourceFileProvider = new (resourceRoot);
    private readonly PhysicalFileProvider _userFileProvider = new (userRoot);

    public IFileInfo GetFileInfo(string subpath)
    {
        var p = ResourcePath.Create(subpath).ValueUnsafe();

        return p.Scheme switch
        {
            ResourcePath.ResourceScheme.Resource => _resourceFileProvider.GetFileInfo(p.Path),
            ResourcePath.ResourceScheme.User => _userFileProvider.GetFileInfo(p.Path),
            _ => throw new NotImplementedException()
        };
    }

    public IDirectoryContents GetDirectoryContents(string subpath)
    {
        var p = ResourcePath.Create(subpath).ValueUnsafe();

        return p.Scheme switch
        {
            ResourcePath.ResourceScheme.Resource => _resourceFileProvider.GetDirectoryContents(p.Path),
            ResourcePath.ResourceScheme.User => _userFileProvider.GetDirectoryContents(p.Path),
            _ => throw new NotImplementedException()
        };
    }

    public IChangeToken Watch(string filter) => throw new NotImplementedException();
}