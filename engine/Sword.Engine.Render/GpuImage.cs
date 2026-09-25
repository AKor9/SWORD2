using Sword.Engine.Rhi;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace Sword.Engine.Render;

/// <summary>
/// Постоянная картинка в видеопамяти из пикселей RGBA8 (картинки интерфейса, предпросмотры инструментов): текстура
/// с мипами и SRV в bindless-куче — индекс для <see cref="Sword.Engine.Ui.UiDrawList.Image"/>. SRV — массив из одного
/// слоя (<c>Texture2DArray</c>): так читает режим спрайта UI-прохода (<c>common/ui.hlsl</c>). Создание заливает и ждёт
/// GPU — на загрузке, не в кадре; освобождение отложено до конца кадров в полёте. Перенос <c>GpuTexture</c> SWORD
/// 2eab13b для одной картинки RGBA8: там мипы приходили из пака, здесь — ящичным фильтром 2×2 на CPU (<see cref="BuildMips"/>).
/// </summary>
public sealed class GpuImage : IDisposable
{
    private readonly GraphicsDevice _device;
    private readonly ID3D12Resource _resource;
    private bool _disposed;

    private GpuImage(GraphicsDevice device, ID3D12Resource resource, int srv, string name, int width, int height, int mips, bool srgb)
    {
        _device = device;
        _resource = resource;
        Srv = srv;
        Name = name;
        Width = width;
        Height = height;
        Mips = mips;
        Srgb = srgb;
    }

    /// <summary>Bindless-индекс SRV — для <see cref="Sword.Engine.Ui.UiDrawList.Image"/>.</summary>
    public int Srv { get; }

    public string Name { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>Уровней мипов вместе с нулевым.</summary>
    public int Mips { get; }

    /// <summary>Пиксели — sRGB (выборка переводит в линейные).</summary>
    public bool Srgb { get; }

    /// <summary>Полная цепочка мипов до 1×1: 1 + ⌊log₂ max(w, h)⌋.</summary>
    public static int MipCount(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        return 1 + (int)Math.Log2(Math.Max(width, height));
    }

    /// <summary>
    /// Цепочка мипов из пикселей RGBA8 (строки подряд, без промежутков): уровень 0 — копия, каждый следующий — вдвое меньше
    /// (не меньше 1), пиксель — среднее 2×2 предыдущего с округлением; у нечётной стороны последний столбец (строка)
    /// берётся со своим соседом по краю.
    /// </summary>
    /// <exception cref="ArgumentException">Пикселей не столько, сколько в картинке, или мипов больше полной цепочки.</exception>
    public static byte[][] BuildMips(int width, int height, ReadOnlySpan<byte> rgba, int mips)
    {
        int full = MipCount(width, height);
        if (mips < 1 || mips > full) throw new ArgumentOutOfRangeException(nameof(mips), mips, $"мипов 1…{full} для {width}×{height}");
        if (rgba.Length != width * height * 4)
            throw new ArgumentException($"пикселей {rgba.Length} байт, для {width}×{height} RGBA8 нужно {width * height * 4}", nameof(rgba));
        var levels = new byte[mips][];
        levels[0] = rgba.ToArray();
        int w = width, h = height;
        for (int m = 1; m < mips; m++)
        {
            int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
            levels[m] = Downsample(levels[m - 1], w, h, nw, nh);
            (w, h) = (nw, nh);
        }
        return levels;
    }

    private static byte[] Downsample(byte[] src, int w, int h, int nw, int nh)
    {
        var dst = new byte[nw * nh * 4];
        for (int y = 0; y < nh; y++) DownsampleRow(src, w, h, dst, nw, y);
        return dst;
    }

    private static void DownsampleRow(byte[] src, int w, int h, byte[] dst, int nw, int y)
    {
        int y0 = Math.Min(2 * y, h - 1), y1 = Math.Min(2 * y + 1, h - 1);
        for (int x = 0; x < nw; x++)
        {
            int x0 = Math.Min(2 * x, w - 1), x1 = Math.Min(2 * x + 1, w - 1);
            int a = (y0 * w + x0) * 4, b = (y0 * w + x1) * 4, c = (y1 * w + x0) * 4, d = (y1 * w + x1) * 4, o = (y * nw + x) * 4;
            for (int k = 0; k < 4; k++) dst[o + k] = (byte)((src[a + k] + src[b + k] + src[c + k] + src[d + k] + 2) / 4);
        }
    }

    /// <summary>Создать с заливкой и ожиданием GPU (<see cref="Renderer.CreateImage"/>).</summary>
    internal static GpuImage Create(GraphicsDevice gd, string name, int width, int height, ReadOnlySpan<byte> rgba, bool mips, bool srgb) =>
        Create(gd, name, width, height, BuildMips(width, height, rgba, mips ? MipCount(width, height) : 1), srgb);

    /// <summary>
    /// Создать из готовой цепочки мипов (мип i — max(1, w≫i) × max(1, h≫i) RGBA8): распакованные мипы текстуры в предпросмотре
    /// пекарни показываются как есть, а не пересчитываются из нулевого.
    /// </summary>
    /// <exception cref="ArgumentException">Мипов нет, больше полной цепочки или размер мипа не тот.</exception>
    internal static GpuImage Create(GraphicsDevice gd, string name, int width, int height, IReadOnlyList<byte[]> mips, bool srgb)
    {
        int full = MipCount(width, height);
        if (mips.Count < 1 || mips.Count > full) throw new ArgumentException($"мипов {mips.Count}, для {width}×{height} — 1…{full}", nameof(mips));
        for (int m = 0; m < mips.Count; m++)
        {
            int need = Math.Max(1, width >> m) * Math.Max(1, height >> m) * 4;
            if (mips[m].Length != need) throw new ArgumentException($"мип {m}: {mips[m].Length} байт, нужно {need}", nameof(mips));
        }
        return Create(gd, name, width, height, [.. mips], srgb);
    }

    private static unsafe GpuImage Create(GraphicsDevice gd, string name, int width, int height, byte[][] levels, bool srgb)
    {
        var format = srgb ? Format.R8G8B8A8_UNorm_SRgb : Format.R8G8B8A8_UNorm;
        var desc = ResourceDescription.Texture2D(format, (uint)width, (uint)height, 1, (ushort)levels.Length);
        var resource = gd.Device.CreateCommittedResource(HeapType.Default, HeapFlags.None, desc, ResourceStates.CopyDest);
        resource.Name = name;
        int srv = -1;
        try
        {
            uint count = (uint)levels.Length;
            var layouts = new PlacedSubresourceFootPrint[count];
            var rows = new uint[count];
            var rowSizes = new ulong[count];
            gd.Device.GetCopyableFootprints(desc, 0, count, 0, layouts, rows, rowSizes, out ulong total);
            using (var upload = gd.CreateUploadBuffer(total, name + ".Upload"))
            {
                byte* mapped = upload.Map<byte>(0);
                int w = width, h = height;
                for (int m = 0; m < levels.Length; m++)
                {
                    int rowBytes = w * 4;
                    byte* dst = mapped + layouts[m].Offset;
                    for (int y = 0; y < h; y++)
                        levels[m].AsSpan(y * rowBytes, rowBytes).CopyTo(new Span<byte>(dst + (long)y * layouts[m].Footprint.RowPitch, rowBytes));
                    (w, h) = (Math.Max(1, w / 2), Math.Max(1, h / 2));
                }
                upload.Unmap(0);
                gd.ExecuteImmediate(list =>
                {
                    for (uint i = 0; i < count; i++)
                        list.CopyTextureRegion(new TextureCopyLocation(resource, i), 0, 0, 0, new TextureCopyLocation(upload, layouts[i]), null);
                    list.ResourceBarrierTransition(resource, ResourceStates.CopyDest,
                        ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource);
                });
            }

            srv = gd.Bindless.Allocate();
            gd.Device.CreateShaderResourceView(resource, new ShaderResourceViewDescription
            {
                Format = format,
                ViewDimension = ShaderResourceViewDimension.Texture2DArray,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Texture2DArray = new Texture2DArrayShaderResourceView
                {
                    MostDetailedMip = 0, MipLevels = count, FirstArraySlice = 0, ArraySize = 1,
                },
            }, gd.Bindless.Cpu(srv));
            return new GpuImage(gd, resource, srv, name, width, height, levels.Length, srgb);
        }
        catch
        {
            if (srv >= 0) gd.DeferRelease(resource, srv);
            else resource.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _device.DeferRelease(_resource, Srv);
    }

    public override string ToString() => $"{Name} [{Width}×{Height}, мипов {Mips}{(Srgb ? ", sRGB" : "")}]";
}
