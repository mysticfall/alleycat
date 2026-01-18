using Godot;
using Microsoft.Extensions.FileProviders;
using FileAccess = Godot.FileAccess;

namespace AlleyCat.Io;

public readonly record struct FileInfo(string Path) : IFileInfo
{
    public const string Separator = "/";

    public string Name { get; } = Path.LastIndexOf('/') < 0 ? "" : Path[(Path.LastIndexOf('/') + 1)..];

    public bool Exists => FileAccess.FileExists(Path);

    public bool IsDirectory => false;

    public long Length => FileAccess.GetSize(Path);

    public string PhysicalPath => ProjectSettings.GlobalizePath(Path);

    public DateTimeOffset LastModified => new((long)FileAccess.GetModifiedTime(Path), TimeSpan.Zero);

    public Stream CreateReadStream() => FileStream.Open(Path);
}