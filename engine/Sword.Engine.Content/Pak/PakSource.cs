using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Sword.Engine.Content;

/// <summary>
/// Контент из пака (<see cref="PakFormat"/>). Оглавление проверяется при открытии целиком: границы, пути, повторы.
/// Чтение — <see cref="RandomAccess"/> по общему дескриптору, без общей позиции файла, поэтому потоки
/// <see cref="OpenRead"/> можно читать из разных потоков одновременно. Открытый пак можно заменить новым
/// (пекарня переносит новый файл на его место): открывший дочитывает прежний.
/// </summary>
public sealed class PakSource : IContentSource, IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly Dictionary<string, PakEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly PakEntry[] _sorted;

    public string Location { get; }
    /// <summary>Файлы пака по возрастанию пути без учёта регистра.</summary>
    public IReadOnlyList<PakEntry> Entries => _sorted;
    public long Length { get; }

    /// <exception cref="InvalidDataException">Не пак, другая версия, обрезан или оглавление испорчено.</exception>
    public static PakSource Open(string path)
    {
        path = Path.GetFullPath(path);
        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, FileOptions.RandomAccess);
        try
        {
            return new PakSource(path, handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private PakSource(string path, SafeFileHandle handle)
    {
        Location = path;
        _handle = handle;
        Length = RandomAccess.GetLength(handle);

        Span<byte> header = stackalloc byte[PakFormat.HeaderSize];
        if (Length < PakFormat.HeaderSize || ReadExact(header, 0) != PakFormat.HeaderSize) throw Bad("короче заголовка");
        if (!header[..4].SequenceEqual(PakFormat.Magic)) throw Bad("не пак .swpak");
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
        if (version != PakFormat.Version) throw Bad($"версия {version}, ожидается {PakFormat.Version} — пересоберите пак");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        ulong tocOffset = BinaryPrimitives.ReadUInt64LittleEndian(header[16..]);
        ulong tocSize = BinaryPrimitives.ReadUInt64LittleEndian(header[24..]);
        if (count > PakFormat.MaxEntries || tocOffset < PakFormat.HeaderSize || tocSize < 4 || tocSize > int.MaxValue
            || tocOffset + tocSize != (ulong)Length)
            throw Bad("оглавление не на своём месте — пак обрезан или испорчен");

        var toc = new byte[tocSize];
        if (ReadExact(toc, (long)tocOffset) != toc.Length) throw Bad("файл обрезан");
        try
        {
            using var r = new BinaryReader(new MemoryStream(toc), Encoding.UTF8);
            for (uint i = 0; i < count; i++)
            {
                int n = r.ReadUInt16();
                if (n is 0 or > PakFormat.MaxPathBytes) throw Bad($"запись {i}: длина пути {n}");
                string name = Encoding.UTF8.GetString(r.ReadBytes(n));
                long offset = r.ReadInt64(), size = r.ReadInt64();
                ulong hash = r.ReadUInt64();
                string normal;
                try { normal = ContentPath.Normalize(name); }
                catch (ArgumentException) { throw Bad($"запись {i}: путь «{name}»"); }
                if (normal != name) throw Bad($"запись {i}: путь «{name}» не нормализован");
                if (offset < PakFormat.HeaderSize || size < 0 || offset > (long)tocOffset - size) throw Bad($"{name}: данные вне файла");
                if (!_entries.TryAdd(name, new PakEntry(name, offset, size, hash))) throw Bad($"{name} в паке дважды");
            }
            Span<byte> end = stackalloc byte[4];
            if (r.Read(end) != 4 || !end.SequenceEqual(PakFormat.TocEnd) || r.BaseStream.Position != toc.Length)
                throw Bad("оглавление повреждено");
        }
        catch (EndOfStreamException)
        {
            throw Bad("оглавление обрезано");
        }
        _sorted = [.. _entries.Values.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase)];
    }

    private InvalidDataException Bad(string what) => new($"{Location}: {what}");

    private int ReadExact(Span<byte> buffer, long offset)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = RandomAccess.Read(_handle, buffer[total..], offset + total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    public PakEntry? Find(string path) =>
        _entries.TryGetValue(Key(path), out var e) ? e : null;

    private static string Key(string path)
    {
        try { return ContentPath.Normalize(path); }
        catch (ArgumentException) { return "\0"; }
    }

    public bool Exists(string path) => _entries.ContainsKey(Key(path));

    public Stream OpenRead(string path)
    {
        if (!_entries.TryGetValue(Key(path), out var e)) throw new FileNotFoundException($"нет файла {Describe(path)}", Describe(path));
        return new PakEntryStream(_handle, e.Offset, e.Size);
    }

    public byte[] ReadAllBytes(string path)
    {
        if (!_entries.TryGetValue(Key(path), out var e)) throw new FileNotFoundException($"нет файла {Describe(path)}", Describe(path));
        var bytes = new byte[e.Size];
        if (ReadExact(bytes, e.Offset) != bytes.Length) throw Bad($"{e.Path}: данные обрезаны");
        return bytes;
    }

    public IReadOnlyList<string> List(string directory, string extension)
    {
        string dir = directory.Length == 0 ? "" : Key(directory) + "/";
        return _sorted
            .Where(e => e.Path.StartsWith(dir, StringComparison.OrdinalIgnoreCase) && e.Path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Path)
            .ToList();
    }

    public string Describe(string path) => $"{Location}:{path}";

    /// <summary>Файлы, чей хеш не совпал с оглавлением (читает пак целиком).</summary>
    public IEnumerable<PakEntry> Corrupted()
    {
        var buffer = new byte[1 << 20];
        foreach (var e in _sorted)
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long done = 0;
            while (done < e.Size)
            {
                int n = RandomAccess.Read(_handle, buffer.AsSpan(0, (int)Math.Min(buffer.Length, e.Size - done)), e.Offset + done);
                if (n <= 0) break;
                sha.AppendData(buffer, 0, n);
                done += n;
            }
            if (done != e.Size || PakFormat.HashOf(sha.GetHashAndReset()) != e.Hash) yield return e;
        }
    }

    public void Dispose() => _handle.Dispose();
}

/// <summary>
/// Окно файла пака как поток: только чтение, с поиском. Свой буфер — загрузчики читают <c>BinaryReader</c>'ом
/// по четыре байта, и без буфера каждое число стоило бы системного вызова.
/// </summary>
internal sealed class PakEntryStream(SafeFileHandle handle, long start, long length) : Stream
{
    private readonly byte[] _buffer = new byte[(int)Math.Clamp(length, 1, 256 * 1024)];
    private long _bufferStart;
    private int _bufferCount;
    private long _position;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;

    public override long Position
    {
        get => _position;
        set => _position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public override int Read(Span<byte> destination)
    {
        int total = 0;
        while (destination.Length > 0 && _position < length)
        {
            if (_position < _bufferStart || _position >= _bufferStart + _bufferCount)
            {
                int want = (int)Math.Min(destination.Length, length - _position);
                if (want >= _buffer.Length)
                {
                    // крупное чтение — прямо в приёмник, мимо буфера
                    int direct = RandomAccess.Read(handle, destination[..want], start + _position);
                    if (direct <= 0) break;
                    _position += direct;
                    total += direct;
                    destination = destination[direct..];
                    continue;
                }
                _bufferStart = _position;
                _bufferCount = Math.Max(0, RandomAccess.Read(handle, _buffer.AsSpan(0, (int)Math.Min(_buffer.Length, length - _position)), start + _position));
                if (_bufferCount == 0) break;
            }
            int offset = (int)(_position - _bufferStart);
            int count = Math.Min(destination.Length, _bufferCount - offset);
            _buffer.AsSpan(offset, count).CopyTo(destination);
            destination = destination[count..];
            _position += count;
            total += count;
        }
        return total;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int ReadByte()
    {
        Span<byte> one = stackalloc byte[1];
        return Read(one) == 1 ? one[0] : -1;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => length + offset,
        };
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
