using System.Buffers.Binary;

namespace Sword.Engine.Content;

/// <summary>
/// Пак контента <c>.swpak</c> — весь запечённый контент одним файлом для релиза. Порядок байтов — little-endian.
/// <code>
/// заголовок, 32 Б:  "SWPK" | версия u16 | флаги u16 (0) | файлов u32 | резерв u32 (0) | смещение оглавления u64 | размер оглавления u64
/// данные:           содержимое файлов подряд, начало каждого выровнено на 16 Б (под отображение в память)
/// оглавление:       на файл — длина пути u16 | путь UTF-8 («/», как <see cref="ContentPath"/>) | смещение u64 | размер u64 | хеш u64;
///                   в конце "KPWS". Оглавление — последнее в файле: пак, обрезанный при записи, не откроется.
/// </code>
/// Хеш файла — первые 8 байт SHA-256 содержимого: проверка целостности (<see cref="PakSource.Corrupted"/>), не защита.
/// Сжатия нет: клипы и меши — плотные массивы float, выигрыш не окупает распаковку при загрузке.
/// </summary>
public static class PakFormat
{
    public static ReadOnlySpan<byte> Magic => "SWPK"u8;
    public static ReadOnlySpan<byte> TocEnd => "KPWS"u8;
    public const ushort Version = 1;
    public const int HeaderSize = 32;
    public const int Alignment = 16;
    public const int MaxEntries = 1_000_000;
    public const int MaxPathBytes = 1024;
    public const string Extension = ".swpak";

    /// <summary>Хеш файла из SHA-256 его содержимого.</summary>
    public static ulong HashOf(ReadOnlySpan<byte> sha256) => BinaryPrimitives.ReadUInt64LittleEndian(sha256);

    /// <summary>Хеш содержимого потока — как его запишет <see cref="PakWriter"/>.</summary>
    public static ulong HashOf(Stream stream) => HashOf(System.Security.Cryptography.SHA256.HashData(stream));

    /// <summary>Хеш в текстовом виде — так его хранят сведения пекарни о сборке пака (<see cref="PakBuildInfo"/>).</summary>
    public static string HashText(ulong hash) => hash.ToString("X16");
}

/// <summary>Файл в паке: путь внутри контента, где лежат байты и их хеш.</summary>
public readonly record struct PakEntry(string Path, long Offset, long Size, ulong Hash);
