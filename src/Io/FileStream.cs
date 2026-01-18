using EnsureThat;
using AlleyCat.Common;
using Godot;
using File = Godot.FileAccess;
using FileAccess = System.IO.FileAccess;

namespace AlleyCat.Io;

public class FileStream : Stream
{
    public override bool CanSeek => _file.IsOpen();

    public override bool CanRead =>
        _file.IsOpen() && (_access & FileAccess.Read) != 0 && !_file.EofReached();

    public override bool CanWrite => _file.IsOpen() && (_access & FileAccess.Write) != 0;

    public override long Length => (long)_file.GetLength();

    public override long Position
    {
        get => (long)_file.GetPosition();
        set => _file.Seek((ulong)value);
    }

    private readonly File _file;

    private readonly FileAccess _access;

    private bool _closed;

    private FileStream(File file, FileAccess access)
    {
        Ensure.That(file, nameof(file)).IsNotNull();

        Ensure.Bool.IsTrue(
            file.IsOpen(),
            nameof(file),
            (in opt) => opt.WithMessage("File is closed."));

        file.GetError().ThrowOnError();

        _file = file;
        _access = access;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        CheckClosed();

        switch (origin)
        {
            case SeekOrigin.Begin:
                _file.Seek((ulong)offset);
                break;
            case SeekOrigin.Current:
                _file.Seek((ulong)(Position + offset));
                break;
            case SeekOrigin.End:
                _file.SeekEnd(offset);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(origin), origin, null);
        }

        CheckErrors();

        return Position;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        Ensure.That(buffer, nameof(buffer)).IsNotNull();

        CheckClosed();

        var remaining = (int)(Length - Position);

        var size = Math.Min(Math.Min(buffer.Length - offset, count), remaining);
        var data = _file.GetBuffer(size);

        CheckErrors();

        Array.Copy(data, 0, buffer, offset, data.Length);

        return data.Length;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        Ensure.That(buffer, nameof(buffer)).IsNotNull();
        Ensure.That(offset, nameof(offset)).IsGte(0);
        Ensure.That(count, nameof(count)).IsGte(0);

        CheckClosed();

        var size = Math.Min(buffer.Length - offset, count);

        if (offset == 0 && buffer.Length <= count)
        {
            _file.StoreBuffer(buffer);
        }
        else
        {
            var data = new byte[size];

            Array.Copy(buffer, offset, data, 0, size);

            _file.StoreBuffer(data);
        }

        CheckErrors();
    }

    public override void SetLength(long value) => throw new NotImplementedException();

    public override void Flush()
    {
    }

    protected override void Dispose(bool disposing)
    {
        if (GodotObject.IsInstanceValid(_file) && _file.IsOpen())
        {
            _file.Close();
        }

        _file.DisposeQuietly();
        _closed = true;

        base.Dispose(disposing);
    }

    private void CheckClosed()
    {
        if (!_closed) return;

        throw new ObjectDisposedException("The file was already closed.");
    }

    private void CheckErrors() => _file.GetError().ThrowOnError();

    public static FileStream Open(string path, FileAccess access = FileAccess.Read)
    {
        Ensure.String.IsNotNullOrWhiteSpace(path, nameof(path));

        var file = File.Open(path, ToModeFlags(access));

        file.GetError().ThrowOnError(e => $"Failed to open file: '{path}' ({e}).");

        return new FileStream(file, access);
    }

    private static File.ModeFlags ToModeFlags(FileAccess access) => access switch
    {
        FileAccess.Read => File.ModeFlags.Read,
        FileAccess.Write => File.ModeFlags.Write,
        FileAccess.ReadWrite => File.ModeFlags.ReadWrite,
        _ => throw new ArgumentOutOfRangeException(nameof(access), access, null)
    };
}