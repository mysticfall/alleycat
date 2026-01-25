using AlleyCat.Actor;
using AlleyCat.Async;
using AlleyCat.Env;
using AlleyCat.Scene;
using AlleyCat.Tests.Async;
using LanguageExt;
using Microsoft.Extensions.FileProviders;

namespace AlleyCat.Tests.Env;

public class MockEnv(
    Seq<IActor> actors = default,
    string resourceRoot = ".",
    string userRoot = "."
) : IEnv
{
    public IScene Scene { get; } = new MockScene(actors);

    public IFileProvider FileProvider { get; } = new MockFileProvider(resourceRoot, userRoot);

    public ITaskQueue TaskQueue { get; } = new MockTaskQueue();

    public object? GetService(Type serviceType) => null;
}