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
