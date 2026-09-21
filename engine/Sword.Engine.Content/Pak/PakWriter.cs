using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Sword.Engine.Content;

/// <summary>
/// Запись пака (<see cref="PakFormat"/>): <see cref="Add"/> по файлу, затем <see cref="Commit"/>. Пишется во временный
/// файл рядом и переносится на место только в <see cref="Commit"/>: прерванная запись не портит прежний пак.
/// </summary>
public sealed class PakWriter : IDisposable
{
    private readonly string _path, _tmp;
    private readonly FileStream _file;
    private readonly List<PakEntry> _entries = [];
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly byte[] _buffer = new byte[1 << 20];
    private bool _done;

    public PakWriter(string path)
    {
        _path = Path.GetFullPath(path);
        _tmp = _path + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _file = new FileStream(_tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16);
        _file.Write(new byte[PakFormat.HeaderSize]);
    }

    public IReadOnlyList<PakEntry> Entries => _entries;

    /// <exception cref="ArgumentException">Путь не годится или уже добавлен (без учёта регистра).</exception>
    public PakEntry Add(string contentPath, Stream source)
    {
        string path = ContentPath.Normalize(contentPath);
        if (Encoding.UTF8.GetByteCount(path) > PakFormat.MaxPathBytes) throw new ArgumentException($"путь «{path}» длиннее {PakFormat.MaxPathBytes} Б");
        if (_entries.Count >= PakFormat.MaxEntries) throw new ArgumentException($"файлов больше {PakFormat.MaxEntries}");
        if (!_paths.Add(path)) throw new ArgumentException($"«{path}» в паке дважды");

        Pad();
        long offset = _file.Position;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long size = 0;
        int read;
        while ((read = source.Read(_buffer)) > 0)
        {
            sha.AppendData(_buffer, 0, read);
            _file.Write(_buffer, 0, read);
            size += read;
        }
        var entry = new PakEntry(path, offset, size, PakFormat.HashOf(sha.GetHashAndReset()));
        _entries.Add(entry);
        return entry;
    }

    /// <summary>Записать оглавление и заголовок, перенести пак на место.</summary>
    public void Commit()
    {
        if (_done) throw new InvalidOperationException("пак уже записан");
        Pad();
        long tocOffset = _file.Position;
        using (var w = new BinaryWriter(_file, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var e in _entries)
            {
                byte[] name = Encoding.UTF8.GetBytes(e.Path);
                w.Write((ushort)name.Length);
                w.Write(name);
                w.Write(e.Offset);
                w.Write(e.Size);
                w.Write(e.Hash);
            }
            w.Write(PakFormat.TocEnd);
        }
        long tocSize = _file.Position - tocOffset;

        Span<byte> header = stackalloc byte[PakFormat.HeaderSize];
        header.Clear();
        PakFormat.Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], PakFormat.Version);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], (uint)_entries.Count);
        BinaryPrimitives.WriteUInt64LittleEndian(header[16..], (ulong)tocOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(header[24..], (ulong)tocSize);
        _file.Position = 0;
        _file.Write(header);
        _file.Flush(flushToDisk: true);
        _file.Dispose();
        ReplaceFile(_tmp, _path);
        _done = true;
    }

    /// <summary>
    /// Перенести готовый пак на место прежнего. Прежний может быть открыт игрой или Studio (<see cref="PakSource"/>
    /// открывает с FILE_SHARE_DELETE): обычная замена такой файл не трогает, поэтому при отказе — переименование
    /// с POSIX-семантикой (Windows 10 1809+, NTFS): имя переходит к новому файлу, открывшие дочитывают прежний.
    /// </summary>
    private static void ReplaceFile(string from, string to)
    {
        try
        {
            File.Move(from, to, overwrite: true);
            return;
        }
        catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && OperatingSystem.IsWindows() && File.Exists(to))
        {
            if (!PosixRename(from, to)) throw;
        }
    }

    private static unsafe bool PosixRename(string from, string to)
    {
        const uint DELETE = 0x00010000, FILE_SHARE_ALL = 7, OPEN_EXISTING = 3;
        const int FileRenameInfoEx = 22;
        const uint FILE_RENAME_FLAG_REPLACE_IF_EXISTS = 1, FILE_RENAME_FLAG_POSIX_SEMANTICS = 2;
        using var handle = CreateFileW(from, DELETE, FILE_SHARE_ALL, 0, OPEN_EXISTING, 0, 0);
        if (handle.IsInvalid) return false;
        string target = Path.GetFullPath(to);
        // FILE_RENAME_INFO: Flags u32 | выравнивание | RootDirectory HANDLE | FileNameLength u32 | FileName WCHAR[]
        int nameOffset = 8 + sizeof(nint) + 4;
        int size = nameOffset + (target.Length + 1) * 2;
        byte* info = stackalloc byte[size];
        new Span<byte>(info, size).Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(info, 4), FILE_RENAME_FLAG_REPLACE_IF_EXISTS | FILE_RENAME_FLAG_POSIX_SEMANTICS);
        BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(info + 8 + sizeof(nint), 4), (uint)(target.Length * 2));
        target.AsSpan().CopyTo(new Span<char>(info + nameOffset, target.Length));
        return SetFileInformationByHandle(handle, FileRenameInfoEx, info, (uint)size);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool SetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int infoClass, byte* info, uint size);

    private void Pad()
    {
        int pad = (int)((PakFormat.Alignment - _file.Position % PakFormat.Alignment) % PakFormat.Alignment);
        if (pad > 0) _file.Write(new byte[pad]);
    }

    public void Dispose()
    {
        if (_done) return;
        _done = true;
        _file.Dispose();
        try { File.Delete(_tmp); }
        catch (IOException) { }
    }
}
