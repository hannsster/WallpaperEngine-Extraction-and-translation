using System.Buffers.Binary;
using System.Text;

namespace WallpaperStudio.Services;

/// <summary>
/// Read-only access to a Wallpaper Engine <c>.pkg</c> (PKGV) archive. The index is
/// parsed once and stored in memory; entry data is read on demand by seeking, so
/// multi-hundred-megabyte packages are never loaded whole.
/// </summary>
public sealed class PkgArchive : IDisposable
{
    private const int MaxEntries = 1_000_000;
    private const int MaxNameLength = 4096;
    private const int MaxEntryLength = 512 * 1024 * 1024;

    private readonly FileStream _stream;
    private readonly Dictionary<string, Entry> _entries;
    private bool _disposed;

    private PkgArchive(FileStream stream, Dictionary<string, Entry> entries)
    {
        _stream = stream;
        _entries = entries;
    }

    public int Count => _entries.Count;

    public IEnumerable<string> Names => _entries.Keys;

    public static bool TryOpen(string path, out PkgArchive? archive, out string? error)
    {
        archive = null;
        error = null;

        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                PathHelper.ToExtendedPath(path),
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                64 * 1024,
                FileOptions.RandomAccess);

            var length = stream.Length;
            if (length < 32)
            {
                error = "The package is too small to be a valid PKGV archive.";
                return false;
            }

            Span<byte> header = stackalloc byte[16];
            if (stream.Read(header) != header.Length)
            {
                error = "The package header could not be read.";
                return false;
            }

            if (header[4] != (byte)'P' || header[5] != (byte)'K' || header[6] != (byte)'G' || header[7] != (byte)'V')
            {
                error = "This file is not a PKGV package.";
                return false;
            }

            var count = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(12, 4));
            if (count < 0 || count > MaxEntries)
            {
                error = "The package index reports an implausible entry count.";
                return false;
            }

            var entries = new Dictionary<string, Entry>(count, StringComparer.Ordinal);
            var buffer = new byte[MaxNameLength + 8];
            for (var index = 0; index < count; index++)
            {
                if (!ReadExact(stream, buffer, 4, out _))
                {
                    error = "The package index ended unexpectedly.";
                    return false;
                }

                var nameLength = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(0, 4));
                if (nameLength <= 0 || nameLength > MaxNameLength)
                {
                    error = "The package index contains an invalid entry name length.";
                    return false;
                }

                if (!ReadExact(stream, buffer, nameLength + 8, out _))
                {
                    error = "The package index ended unexpectedly.";
                    return false;
                }

                var name = Encoding.UTF8.GetString(buffer, 0, nameLength);
                var dataOffset = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(nameLength, 4));
                var dataLength = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(nameLength + 4, 4));
                if (dataOffset < 0 || dataLength < 0 || dataLength > MaxEntryLength)
                {
                    error = "The package index contains an invalid entry offset.";
                    return false;
                }

                entries[name] = new Entry(dataOffset, dataLength);
            }

            var dataBase = stream.Position;
            foreach (var entry in entries.Values)
            {
                var end = dataBase + entry.Offset + entry.Length;
                if (end > length)
                {
                    error = "The package index points past the end of the file.";
                    return false;
                }
            }

            archive = new PkgArchive(stream, entries) { DataBase = dataBase };
            stream = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Logger.Debug($"Could not open the package '{path}': {ex.Message}");
            return false;
        }
        finally
        {
            stream?.Dispose();
        }
    }

    public bool Contains(string name) => _entries.ContainsKey(name);

    public bool TryRead(string name, out byte[] data)
    {
        data = Array.Empty<byte>();
        if (_disposed || !_entries.TryGetValue(name, out var entry) || entry.Length <= 0)
        {
            return false;
        }

        try
        {
            _stream.Seek(DataBase + entry.Offset, SeekOrigin.Begin);
            var buffer = new byte[entry.Length];
            return ReadExact(_stream, buffer, entry.Length, out var read) && read == entry.Length && Assign(buffer, out data);
        }
        catch (Exception ex)
        {
            Logger.Debug($"Could not read '{name}' from the package: {ex.Message}");
            return false;
        }
    }

    /// <summary>Reads at most <paramref name="maxBytes"/> bytes from the start of an entry (for header probing).</summary>
    public bool TryReadRange(string name, int maxBytes, out byte[] data)
    {
        data = Array.Empty<byte>();
        if (_disposed || maxBytes <= 0 || !_entries.TryGetValue(name, out var entry) || entry.Length <= 0)
        {
            return false;
        }

        var length = Math.Min(maxBytes, entry.Length);
        try
        {
            _stream.Seek(DataBase + entry.Offset, SeekOrigin.Begin);
            var buffer = new byte[length];
            return ReadExact(_stream, buffer, length, out var read) && read == length && Assign(buffer, out data);
        }
        catch (Exception ex)
        {
            Logger.Debug($"Could not read '{name}' from the package: {ex.Message}");
            return false;
        }
    }

    private static bool Assign(byte[] buffer, out byte[] data)
    {
        data = buffer;
        return true;
    }

    private static bool ReadExact(Stream stream, byte[] buffer, int count, out int read)
    {
        read = 0;
        while (read < count)
        {
            var chunk = stream.Read(buffer, read, count - read);
            if (chunk <= 0)
            {
                return false;
            }

            read += chunk;
        }

        return true;
    }

    private long DataBase { get; init; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream.Dispose();
    }

    private readonly record struct Entry(int Offset, int Length);
}
