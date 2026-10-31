using System.IO.MemoryMappedFiles;

namespace NorthCommander.Core.Viewer;

/// <summary>
/// Read-only random access to a file of any size through a memory-mapped file.
/// Only a small sliding view (<see cref="WindowSize"/>) is mapped at a time, so multi-gigabyte
/// files cost address space for one window, not RAM for the whole file.
/// </summary>
public sealed class MappedFileSource : IDisposable
{
    public const long WindowSize = 4 * 1024 * 1024;

    private readonly Lock _gate = new();
    private readonly MemoryMappedFile? _map;
    private MemoryMappedViewAccessor? _view;
    private long _viewStart;
    private long _viewLength;
    private bool _disposed;

    public MappedFileSource(string path)
    {
        // ReadWrite/Delete sharing: log files that are still being written can be viewed.
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.RandomAccess);
        try
        {
            Length = stream.Length;
            if (Length == 0)
            {
                // Empty files cannot be mapped.
                stream.Dispose();
                return;
            }

            _map = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public long Length { get; }

    /// <summary>Copies up to <paramref name="count"/> bytes starting at <paramref name="offset"/>; returns the number copied.</summary>
    public int Read(long offset, byte[] buffer, int index, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (_map is null || offset >= Length || count <= 0)
        {
            return 0;
        }

        count = (int)Math.Min(count, Length - offset);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var done = 0;
            while (done < count)
            {
                var position = offset + done;
                EnsureView(position);
                var chunk = (int)Math.Min(count - done, _viewStart + _viewLength - position);
                _view!.ReadArray(position - _viewStart, buffer, index + done, chunk);
                done += chunk;
            }

            return done;
        }
    }

    private void EnsureView(long position)
    {
        if (_view is not null && position >= _viewStart && position < _viewStart + _viewLength)
        {
            return;
        }

        _view?.Dispose();
        _viewStart = position / WindowSize * WindowSize;
        _viewLength = Math.Min(WindowSize, Length - _viewStart);
        _view = _map!.CreateViewAccessor(_viewStart, _viewLength, MemoryMappedFileAccess.Read);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _view?.Dispose();
            _map?.Dispose();
        }
    }
}
