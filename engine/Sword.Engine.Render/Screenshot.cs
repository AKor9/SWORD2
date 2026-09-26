using System.Runtime.CompilerServices;
using Sword.Engine.Formats;
using Sword.Engine.Rhi;
using Vortice.Direct3D12;

namespace Sword.Engine.Render;

/// <summary>Снимок заднего буфера в PNG (или BMP по расширению .bmp), без зависимостей — для проверки картинки.</summary>
internal static class Screenshot
{
    private const int RowPitchAlignment = 256;

    /// <summary>Задний буфер на входе и выходе — в состоянии Present.</summary>
    public static ID3D12Resource RecordCapture(GraphicsDevice gd, ID3D12GraphicsCommandList list,
        out int width, out int height, out int rowPitch)
    {
        var source = gd.CurrentBackBuffer;
        width = gd.BackBufferWidth;
        height = gd.BackBufferHeight;
        rowPitch = (width * 4 + RowPitchAlignment - 1) / RowPitchAlignment * RowPitchAlignment;

        var readback = gd.Device.CreateCommittedResource(HeapType.Readback, HeapFlags.None,
            ResourceDescription.Buffer((ulong)rowPitch * (uint)height), ResourceStates.Common);
        readback.Name = "Sword.ScreenshotReadback";

        var footprint = new PlacedSubresourceFootPrint
        {
            Offset = 0,
            Footprint = new SubresourceFootPrint(GraphicsDevice.BackBufferFormat, (uint)width, (uint)height, 1, (uint)rowPitch),
        };

        list.ResourceBarrierTransition(source, ResourceStates.Present, ResourceStates.CopySource);
        list.CopyTextureRegion(new TextureCopyLocation(readback, footprint), 0, 0, 0, new TextureCopyLocation(source, 0), null);
        list.ResourceBarrierTransition(source, ResourceStates.CopySource, ResourceStates.Present);
        return readback;
    }

    /// <summary>
    /// Глубина сцены в буфер чтения (R32, reversed-Z: 0 — дальняя плоскость и очистка, 1 — ближняя). Состояние ресурса
    /// после копии — прежнее: граф его не заметит.
    /// </summary>
    public static ID3D12Resource? RecordDepthCapture(GraphicsDevice gd, ID3D12GraphicsCommandList list, GraphResource depth,
        out int width, out int height, out int rowPitch)
    {
        width = depth.Width;
        height = depth.Height;
        rowPitch = (width * 4 + RowPitchAlignment - 1) / RowPitchAlignment * RowPitchAlignment;
        if (depth.Handle is null || width <= 0 || height <= 0) return null;
        var readback = gd.Device.CreateCommittedResource(HeapType.Readback, HeapFlags.None,
            ResourceDescription.Buffer((ulong)rowPitch * (uint)height), ResourceStates.Common);
        readback.Name = "Sword.DepthReadback";
        var footprint = new PlacedSubresourceFootPrint
        {
            Offset = 0,
            Footprint = new SubresourceFootPrint(Vortice.DXGI.Format.R32_Typeless, (uint)width, (uint)height, 1, (uint)rowPitch),
        };
        list.ResourceBarrierTransition(depth.Handle, depth.State, ResourceStates.CopySource);
        list.CopyTextureRegion(new TextureCopyLocation(readback, footprint), 0, 0, 0, new TextureCopyLocation(depth.Handle, 0), null);
        list.ResourceBarrierTransition(depth.Handle, ResourceStates.CopySource, depth.State);
        return readback;
    }

    /// <summary>
    /// Цель графа (ID-буфер R32Uint, слой Rgba8 — 4 байта на пиксель; цвет HDR-сцены RGBA16F — 8) в буфер чтения. Состояние
    /// ресурса после копии — прежнее: граф его не заметит.
    /// </summary>
    public static ID3D12Resource RecordTextureCapture(GraphicsDevice gd, ID3D12GraphicsCommandList list, GraphResource texture,
        out int width, out int height, out int rowPitch)
    {
        width = texture.Width;
        height = texture.Height;
        int pixel = texture.TargetFormat == Vortice.DXGI.Format.R16G16B16A16_Float ? 8 : 4;
        rowPitch = (width * pixel + RowPitchAlignment - 1) / RowPitchAlignment * RowPitchAlignment;
        var readback = gd.Device.CreateCommittedResource(HeapType.Readback, HeapFlags.None,
            ResourceDescription.Buffer((ulong)rowPitch * (uint)height), ResourceStates.Common);
        readback.Name = "Sword.CaptureReadback." + texture.Name;
        var footprint = new PlacedSubresourceFootPrint
        {
            Offset = 0,
            Footprint = new SubresourceFootPrint(texture.TargetFormat, (uint)width, (uint)height, 1, (uint)rowPitch),
        };
        list.ResourceBarrierTransition(texture.Handle!, texture.State, ResourceStates.CopySource);
        list.CopyTextureRegion(new TextureCopyLocation(readback, footprint), 0, 0, 0, new TextureCopyLocation(texture.Handle!, 0), null);
        list.ResourceBarrierTransition(texture.Handle!, ResourceStates.CopySource, texture.State);
        return readback;
    }

    /// <summary>Цвет кода ID-буфера для глаз: 0 — чёрный, иначе яркий цвет по хешу кода (соседние коды — разные цвета).</summary>
    public static (byte R, byte G, byte B) IdColour(uint id)
    {
        if (id == 0) return (0, 0, 0);
        uint x = id * 0x9E3779B1u;
        x ^= x >> 15;
        x *= 0x85EBCA77u;
        x ^= x >> 13;
        return ((byte)(64 + (x & 0xBF)), (byte)(64 + (x >> 8 & 0xBF)), (byte)(64 + (x >> 16 & 0xBF)));
    }

    /// <summary>
    /// ID-буфер: PNG для глаз (<see cref="IdColour"/>), сырые коды (uint32 LE на пиксель, строки сверху вниз) и сводка по
    /// кодам — пикселей и рамка каждого (кроме 0), по возрастанию кода.
    /// </summary>
    /// <remarks>
    /// Пиксельный цикл по всему кадру — сразу оптимизированным (<c>AggressiveOptimization</c>): иначе многоуровневый JIT заменял
    /// его на ходу (OSR 4…5 мс на главном потоке в кадре снимка — docs/history/profiler.md). Вынос тела
    /// в метод на строку не спасает — строка шире 1000 пикселей сама долгий цикл; динамический PGO перекладке байтов не нужен.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static unsafe IReadOnlyList<IdCoverage> SaveIds(ID3D12Resource readback, string pngPath, string rawPath, int width, int height, int rowPitch)
    {
        byte* source = readback.Map<byte>(0);
        try
        {
            var raw = new byte[width * height * 4];
            var rgb = new byte[width * height * 3];
            var found = new Dictionary<uint, (int Pixels, int X0, int Y0, int X1, int Y1)>();
            for (int y = 0; y < height; y++)
            {
                var row = new ReadOnlySpan<uint>(source + (long)y * rowPitch, width);
                row.CopyTo(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(raw.AsSpan(y * width * 4, width * 4)));
                for (int x = 0; x < width; x++)
                {
                    uint id = row[x];
                    if (id == 0) continue;
                    var (r, g, b) = IdColour(id);
                    int i = (y * width + x) * 3;
                    rgb[i] = r;
                    rgb[i + 1] = g;
                    rgb[i + 2] = b;
                    found[id] = found.TryGetValue(id, out var c)
                        ? (c.Pixels + 1, Math.Min(c.X0, x), Math.Min(c.Y0, y), Math.Max(c.X1, x + 1), Math.Max(c.Y1, y + 1))
                        : (1, x, y, x + 1, y + 1);
                }
            }
            File.WriteAllBytes(pngPath, Png.Encode(rgb, width, height));
            File.WriteAllBytes(rawPath, raw);
            return [.. found.OrderBy(kv => kv.Key).Select(kv => new IdCoverage(kv.Key, kv.Value.Pixels, kv.Value.X0, kv.Value.Y0, kv.Value.X1, kv.Value.Y1))];
        }
        finally
        {
            readback.Unmap(0);
        }
    }

    /// <summary>
    /// Слой снимка (Rgba8, цвет умножен на альфу) поверх цвета кадра <paramref name="under"/> (RGB по строкам; null — на
    /// чёрном) в PNG: <c>слой + цвет · (1 − альфа слоя)</c>.
    /// </summary>
    /// <remarks>
    /// Пиксельный цикл по всему кадру — сразу оптимизированным (<c>AggressiveOptimization</c>): иначе многоуровневый JIT заменял
    /// его на ходу (OSR 4…5 мс на главном потоке в кадре снимка — docs/history/profiler.md). Вынос тела
    /// в метод на строку не спасает — строка шире 1000 пикселей сама долгий цикл; динамический PGO перекладке байтов не нужен.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static unsafe void SaveLayer(ID3D12Resource readback, string path, int width, int height, int rowPitch, byte[]? under)
    {
        byte* source = readback.Map<byte>(0);
        try
        {
            var rgb = new byte[width * height * 3];
            for (int y = 0; y < height; y++)
            {
                byte* src = source + (long)y * rowPitch;
                for (int x = 0; x < width; x++)
                {
                    int i = (y * width + x) * 3;
                    int keep = 255 - src[x * 4 + 3];
                    for (int c = 0; c < 3; c++)
                        rgb[i + c] = (byte)Math.Min(255, src[x * 4 + c] + (under is null ? 0 : (under[i + c] * keep + 127) / 255));
                }
            }
            File.WriteAllBytes(path, Png.Encode(rgb, width, height));
        }
        finally
        {
            readback.Unmap(0);
        }
    }

    /// <summary>
    /// Глубина в PNG для глаз (и модели): ближе — светлее, дальняя плоскость — чёрная; яркость растянута на найденный
    /// диапазон. Возвращает диапазон глубины и долю пикселей с геометрией.
    /// </summary>
    /// <remarks>
    /// Пиксельный цикл по всему кадру — сразу оптимизированным (<c>AggressiveOptimization</c>): иначе многоуровневый JIT заменял
    /// его на ходу (OSR 4…5 мс на главном потоке в кадре снимка — docs/history/profiler.md). Вынос тела
    /// в метод на строку не спасает — строка шире 1000 пикселей сама долгий цикл; динамический PGO перекладке байтов не нужен.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static unsafe (float Min, float Max, float Coverage) SaveDepth(ID3D12Resource readback, string path, int width, int height, int rowPitch)
    {
        byte* source = readback.Map<byte>(0);
        try
        {
            float min = float.MaxValue, max = 0;
            long covered = 0;
            for (int y = 0; y < height; y++)
            {
                var row = new ReadOnlySpan<float>(source + (long)y * rowPitch, width);
                foreach (float d in row)
                    if (d > 0)
                    {
                        covered++;
                        min = MathF.Min(min, d);
                        max = MathF.Max(max, d);
                    }
            }
            if (covered == 0) min = max = 0;
            float range = MathF.Max(max - min, 1e-9f);
            var rgb = new byte[width * height * 3];
            for (int y = 0; y < height; y++)
            {
                var row = new ReadOnlySpan<float>(source + (long)y * rowPitch, width);
                for (int x = 0; x < width; x++)
                {
                    float d = row[x];
                    byte v = d > 0 ? (byte)(32 + 223 * Math.Clamp((d - min) / range, 0, 1)) : (byte)0;
                    int i = (y * width + x) * 3;
                    rgb[i] = rgb[i + 1] = rgb[i + 2] = v;
                }
            }
            File.WriteAllBytes(path, Png.Encode(rgb, width, height));
            return (min, max, covered / (float)((long)width * height));
        }
        finally
        {
            readback.Unmap(0);
        }
    }

    /// <summary>Ступени карты HDR: log2 яркости входа тонмапа → цвет (<see cref="HdrCapture.Path"/>).</summary>
    private static readonly (float Stop, float R, float G, float B)[] HdrStops =
    [
        (-8, 0, 0, 0), (-6, 0, 0, 0.5f), (-4, 0, 0.3f, 1), (-2.4739f, 0, 0.8f, 0.2f), (-1, 0.6f, 0.9f, 0), (0, 1, 1, 0),
        (2, 1, 0, 0), (4, 1, 0, 1), (6, 1, 1, 1),
    ];

    /// <summary>Цвет карты HDR для яркости <paramref name="luminance"/> (линейно между ступенями log2).</summary>
    public static (byte R, byte G, byte B) HdrColour(float luminance)
    {
        if (!float.IsFinite(luminance)) return (0, 255, 255);
        float stop = luminance > 0 ? MathF.Log2(luminance) : float.NegativeInfinity;
        var stops = HdrStops;
        if (stop <= stops[0].Stop) return (0, 0, 0);
        int i = 1;
        while (i < stops.Length - 1 && stop > stops[i].Stop) i++;
        var (s0, r0, g0, b0) = stops[i - 1];
        var (s1, r1, g1, b1) = stops[i];
        float t = Math.Clamp((stop - s0) / (s1 - s0), 0, 1);
        return ((byte)MathF.Round((r0 + (r1 - r0) * t) * 255), (byte)MathF.Round((g0 + (g1 - g0) * t) * 255),
            (byte)MathF.Round((b0 + (b1 - b0) * t) * 255));
    }

    /// <summary>
    /// Цвет HDR-сцены (RGBA16F) из буфера чтения: яркость входа тонмапа (× <paramref name="exposure"/>, Rec.709) по кадру и, если
    /// заданы пути, карта в ложных цветах (<see cref="HdrColour"/>) и сырой цвет (<c>.bin</c>, half RGBA на пиксель);
    /// <paramref name="meanAlpha"/> — средняя альфа (у цели ½ прозрачного — непрозрачность).
    /// </summary>
    /// <remarks>
    /// Пиксельный цикл по всему кадру — сразу оптимизированным (<c>AggressiveOptimization</c>): иначе многоуровневый JIT заменял
    /// его на ходу (OSR на главном потоке в кадре снимка); PGO переводу half и сумме не нужен.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static unsafe HdrCapture SaveHdr(ID3D12Resource readback, float exposure, int width, int height, int rowPitch, string? pngPath,
        string? rawPath, out float meanAlpha)
    {
        byte* source = readback.Map<byte>(0);
        try
        {
            var rgb = pngPath is null ? null : new byte[width * height * 3];
            var raw = rawPath is null ? null : new byte[width * height * 8];
            double sum = 0, alpha = 0;
            float max = 0;
            long over = 0, finite = 0;
            int nonFinite = 0;
            for (int y = 0; y < height; y++)
            {
                var row = new ReadOnlySpan<Half>(source + (long)y * rowPitch, width * 4);
                if (raw is not null) System.Runtime.InteropServices.MemoryMarshal.AsBytes(row).CopyTo(raw.AsSpan(y * width * 8, width * 8));
                for (int x = 0; x < width; x++)
                {
                    float r = (float)row[x * 4], g = (float)row[x * 4 + 1], b = (float)row[x * 4 + 2];
                    alpha += (float)row[x * 4 + 3];
                    float l = (0.2126f * r + 0.7152f * g + 0.0722f * b) * exposure;
                    bool ok = float.IsFinite(r) && float.IsFinite(g) && float.IsFinite(b);
                    if (ok)
                    {
                        finite++;
                        sum += l;
                        if (l > max) max = l;
                        if (l > 1) over++;
                    }
                    else nonFinite++;
                    if (rgb is not null)
                    {
                        var (cr, cg, cb) = HdrColour(ok ? l : float.NaN);
                        int i = (y * width + x) * 3;
                        rgb[i] = cr;
                        rgb[i + 1] = cg;
                        rgb[i + 2] = cb;
                    }
                }
            }
            if (rgb is not null) File.WriteAllBytes(pngPath!, Png.Encode(rgb, width, height));
            if (raw is not null) File.WriteAllBytes(rawPath!, raw);
            long pixels = (long)width * height;
            meanAlpha = pixels > 0 ? (float)(alpha / pixels) : 0;
            return new HdrCapture(exposure, max, finite > 0 ? (float)(sum / finite) : 0, pixels > 0 ? over / (float)pixels : 0, nonFinite)
            {
                Path = pngPath, RawPath = rawPath,
            };
        }
        finally
        {
            readback.Unmap(0);
        }
    }

    /// <summary>Записать снятый кадр: расширение .bmp — BMP, иначе PNG; вернёт цвет кадра (RGB по строкам) — под слои снимка.</summary>
    /// <remarks>
    /// Пиксельный цикл по всему кадру — сразу оптимизированным (<c>AggressiveOptimization</c>): иначе многоуровневый JIT заменял
    /// его на ходу (OSR 4…5 мс на главном потоке в кадре снимка — docs/history/profiler.md). Вынос тела
    /// в метод на строку не спасает — строка шире 1000 пикселей сама долгий цикл; динамический PGO перекладке байтов не нужен.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static unsafe byte[] Save(ID3D12Resource readback, string path, int width, int height, int rowPitch)
    {
        byte* source = readback.Map<byte>(0);
        try
        {
            if (!path.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
            {
                var rgb = new byte[width * height * 3];
                for (int y = 0; y < height; y++)
                {
                    byte* src = source + (long)y * rowPitch;
                    int dst = y * width * 3;
                    for (int x = 0; x < width; x++)
                    {
                        rgb[dst + x * 3] = src[x * 4];
                        rgb[dst + x * 3 + 1] = src[x * 4 + 1];
                        rgb[dst + x * 3 + 2] = src[x * 4 + 2];
                    }
                }
                File.WriteAllBytes(path, Png.Encode(rgb, width, height));
                return rgb;
            }

            int stride = (width * 3 + 3) & ~3;
            int imageSize = stride * height;
            var file = new byte[54 + imageSize];
            file[0] = (byte)'B'; file[1] = (byte)'M';
            BitConverter.TryWriteBytes(file.AsSpan(2), 54 + imageSize);
            BitConverter.TryWriteBytes(file.AsSpan(10), 54);
            BitConverter.TryWriteBytes(file.AsSpan(14), 40);
            BitConverter.TryWriteBytes(file.AsSpan(18), width);
            BitConverter.TryWriteBytes(file.AsSpan(22), height);
            BitConverter.TryWriteBytes(file.AsSpan(26), (short)1);
            BitConverter.TryWriteBytes(file.AsSpan(28), (short)24);
            BitConverter.TryWriteBytes(file.AsSpan(34), imageSize);

            for (int y = 0; y < height; y++)
            {
                byte* src = source + (long)(height - 1 - y) * rowPitch;
                int dst = 54 + y * stride;
                for (int x = 0; x < width; x++)
                {
                    file[dst + x * 3] = src[x * 4 + 2];
                    file[dst + x * 3 + 1] = src[x * 4 + 1];
                    file[dst + x * 3 + 2] = src[x * 4];
                }
            }
            File.WriteAllBytes(path, file);
            var colour = new byte[width * height * 3];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    for (int c = 0; c < 3; c++)
                        colour[(y * width + x) * 3 + c] = source[(long)y * rowPitch + x * 4 + c];
            return colour;
        }
        finally
        {
            readback.Unmap(0);
        }
    }
}
