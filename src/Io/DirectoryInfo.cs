using System.Collections;
using Godot;
using Microsoft.Extensions.FileProviders;

namespace AlleyCat.Io;

public readonly record struct DirectoryInfo(string Path) : IFileInfo, IDirectoryContents
{
    public string Name { get; } = Path.LastIndexOf('/') < 0 ? "" : Path[(Path.LastIndexOf('/') + 1)..];

    public bool Exists => DirAccess.DirExistsAbsolute(Path);

    public bool IsDirectory => true;

    public long Length => -1;

    public string PhysicalPath => ProjectSettings.GlobalizePath(Path);

    public DateTimeOffset LastModified => new(0, TimeSpan.Zero);

    public Stream CreateReadStream() => throw new NotImplementedException();

    public IEnumerator<IFileInfo> GetEnumerator() => new FileEnumerator(Path);

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}