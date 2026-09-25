using System.Buffers.Binary;
using System.IO.Compression;

namespace Sword.Engine.Formats;

/// <summary>
/// Кодировщик PNG без зависимостей: 8 бит RGB, без чересстрочности, фильтр строк Sub (пиксель минус левый сосед —
/// сжимается заметно лучше сырых строк), сжатие — ZLibStream из .NET.
/// </summary>
public static class Png
{
    private static ReadOnlySpan<byte> Signature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
    private const byte FilterSub = 1;

    /// <summary>Строки сверху вниз, 3 байта на пиксель (R, G, B), без выравнивания.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> rgb, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        int row = width * 3;
        if (rgb.Length != row * height) throw new ArgumentException($"ждали {row * height} Б RGB, дано {rgb.Length}", nameof(rgb));

        using var png = new MemoryStream();
        png.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;    // бит на канал
        header[9] = 2;    // RGB
        header[10] = 0;   // deflate
        header[11] = 0;   // фильтры строк стандартные
        header[12] = 0;   // без чересстрочности
        WriteChunk(png, "IHDR", header);

        using (var raw = new MemoryStream())
        {
            using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            {
                var line = new byte[1 + row];
                line[0] = FilterSub;
                for (int y = 0; y < height; y++)
                {
                    var src = rgb.Slice(y * row, row);
                    for (int i = 0; i < 3; i++) line[1 + i] = src[i];
                    for (int i = 3; i < row; i++) line[1 + i] = (byte)(src[i] - src[i - 3]);
                    z.Write(line);
                }
            }
            WriteChunk(png, "IDAT", raw.GetBuffer().AsSpan(0, (int)raw.Length));
        }
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(buf, data.Length);
        for (int i = 0; i < 4; i++) buf[4 + i] = (byte)type[i];
        s.Write(buf);
        s.Write(data);
        uint crc = Crc32.Update(Crc32.Update(0xFFFFFFFFu, buf[4..]), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(buf, crc);
        s.Write(buf[..4]);
    }

    /// <summary>CRC-32 PNG (многочлен 0xEDB88320) — по типу и данным чанка.</summary>
    internal static class Crc32
    {
        private static readonly uint[] Table = Build();

        private static uint[] Build()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        /// <remarks>
        /// Сразу оптимизированным: цикл по байтам чанка (сотни КБ за вызов) многоуровневый JIT заменял на ходу (OSR на главном
        /// потоке в кадре снимка); PGO табличному CRC не нужен.
        /// </remarks>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        public static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (byte b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}
