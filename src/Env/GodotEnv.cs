using AlleyCat.Async;
using AlleyCat.Io;
using AlleyCat.Scene;
using Godot;
using Microsoft.Extensions.FileProviders;

namespace AlleyCat.Env;

public readonly struct GodotEnv(
    SceneTree sceneTree,
    ITaskQueue taskQueue,
    IServiceProvider? services = null
) : IEnv
{
    public static GodotEnv? Instance { get; set; }

    public IScene Scene { get; } = new GodotScene(sceneTree);

    public IFileProvider FileProvider { get; } = new FileProvider();

    public ITaskQueue TaskQueue { get; } = taskQueue;

    public object? GetService(Type serviceType) => services?.GetService(serviceType);
}