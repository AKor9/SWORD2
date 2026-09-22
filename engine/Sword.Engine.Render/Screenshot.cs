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
    /// Глубина в PNG для глаз (и модели): ближе — светлее, дальняя плоскость — чёрная; яркость растянута на найденный
    /// диапазон. Возвращает диапазон глубины и долю пикселей с геометрией.
    /// </summary>
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

    /// <summary>Записать снятый кадр: расширение .bmp — BMP, иначе PNG.</summary>
    public static unsafe void Save(ID3D12Resource readback, string path, int width, int height, int rowPitch)
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
                return;
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
        }
        finally
        {
            readback.Unmap(0);
        }
    }
}
