using System.Collections;
using EnsureThat;
using AlleyCat.Common;
using Godot;
using Microsoft.Extensions.FileProviders;

namespace AlleyCat.Io;

public struct FileEnumerator : IEnumerator<IFileInfo?>
{
    private const string CurrentDir = ".";

    private const string ParentDir = "..";

    public IFileInfo? Current { get; private set; }

    object? IEnumerator.Current => Current;

    private readonly DirAccess _directory;

    private readonly string _path;

    private readonly bool _endsWithSeparator;

    public FileEnumerator(string path)
    {
        Ensure.That(path, nameof(path)).IsNotNull();

        _path = path;
        _endsWithSeparator = _path.EndsWith(FileInfo.Separator);

        _directory = DirAccess.Open(path);

        if (_directory == null)
        {
            DirAccess
                .GetOpenError()
                .ThrowOnError(e => $"Failed to open directory '{path}': {e}");
        }

        _directory!.ListDirBegin().ThrowOnError();
    }

    public bool MoveNext()
    {
        var path = _directory.GetNext();

        while (path is CurrentDir or ParentDir)
        {
            path = _directory.GetNext();
        }

        var hasNext = !string.IsNullOrEmpty(path);

        if (hasNext)
        {
            var absolute = string.Join(_endsWithSeparator ? "" : "/", _path, path);

            if (_directory.CurrentIsDir())
            {
                Current = new DirectoryInfo(absolute);
            }
            else
            {
                Current = new FileInfo(absolute);
            }
        }
        else
        {
            Current = null;
        }

        return hasNext;
    }

    public void Reset()
    {
        _directory.ListDirEnd();
        _directory.ListDirBegin();
    }

    public void Dispose()
    {
        _directory.ListDirEnd();
        _directory.Dispose();
    }
}