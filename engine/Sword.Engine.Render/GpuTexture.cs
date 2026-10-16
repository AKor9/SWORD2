using Sword.Engine.Formats;
using Sword.Engine.Rhi;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace Sword.Engine.Render;

/// <summary>
/// Раскладка текселей текстуры пака в видеопамяти (формат <c>.swtex</c> пекарни). sRGB — отдельно
/// (<see cref="GpuTexture.Srgb"/>): у R8, RG8, BC4, BC5 его не бывает.
/// </summary>
public enum GpuTextureFormat { R8, Rg8, Rgba8, Bc1, Bc4, Bc5, Bc7 }

/// <summary>
/// Текстура пака в видеопамяти (<see cref="Renderer.LoadTexture(string)"/>): массив слоёв одного размера с мипами из файла
/// <c>.swtex</c> как есть (без перекодирования), SRV — <c>Texture2DArray</c> в bindless-куче (<see cref="Srv"/>; один слой —
/// массив из одного). sRGB-формат декодирует выборка: шейдер получает линейное значение, фильтр — по линейным. Сэмплеры —
/// <c>gLinearClamp</c> (s0), <c>gLinearWrap</c> (s1), <c>gAnisoClamp</c> (s2), <c>gLinearBorder</c> (s3) из <c>common/pass.hlsli</c>.
/// Загрузка заливает и ждёт GPU — на старте модуля, не в кадре; <see cref="Dispose"/> — отложенное освобождение после кадров
/// в полёте. Живые текстуры рендера — <see cref="Renderer.Textures"/>.
/// </summary>
public sealed class GpuTexture : IDisposable
{
    private readonly GraphicsDevice _device;
    private readonly ID3D12Resource _resource;
    private readonly string[] _layers;
    private Action<GpuTexture>? _disposed;

    private GpuTexture(GraphicsDevice device, ID3D12Resource resource, int srv, string path, TextureData data, GpuTextureFormat format,
        bool srgb, long bytes, Action<GpuTexture> disposed)
    {
        _device = device;
        _resource = resource;
        _disposed = disposed;
        Srv = srv;
        Path = path;
        Width = data.Width;
        Height = data.Height;
        Mips = data.Mips;
        Slices = data.Slices;
        Format = format;
        Srgb = srgb;
        Bytes = bytes;
        _layers = [.. data.Layers.Select(l => l.Name)];
    }

    /// <summary>Bindless-индекс SRV (<c>Texture2DArray</c>, все мипы и слои).</summary>
    public int Srv { get; }

    /// <summary>Путь в контенте (или имя, под которым загружен файл не из контента).</summary>
    public string Path { get; }

    public int Width { get; }
    public int Height { get; }

    /// <summary>Уровней мипов вместе с нулевым.</summary>
    public int Mips { get; }

    /// <summary>Слоёв массива.</summary>
    public int Slices { get; }

    public GpuTextureFormat Format { get; }

    /// <summary>Тексели — sRGB: выборка и <c>Load</c> отдают линейные значения.</summary>
    public bool Srgb { get; }

    /// <summary>Видеопамять ресурса, байт (размещение D3D12 с выравниванием).</summary>
    public long Bytes { get; }

    /// <summary>Имена слоёв из файла (обычно имена исходников); пусто — слои без имён.</summary>
    public IReadOnlyList<string> Layers => _layers;

    /// <summary>Номер слоя по имени (без учёта регистра); нет — −1.</summary>
    public int FindLayer(string name) => Array.FindIndex(_layers, l => string.Equals(l, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Формат DXGI и его вид в видеопамяти для раскладки файла.</summary>
    internal static (Format Dxgi, GpuTextureFormat Format, bool Srgb) FormatOf(TexturePixelFormat f) => f switch
    {
        TexturePixelFormat.R8 => (Vortice.DXGI.Format.R8_UNorm, GpuTextureFormat.R8, false),
        TexturePixelFormat.Rg8 => (Vortice.DXGI.Format.R8G8_UNorm, GpuTextureFormat.Rg8, false),
        TexturePixelFormat.Rgba8 => (Vortice.DXGI.Format.R8G8B8A8_UNorm, GpuTextureFormat.Rgba8, false),
        TexturePixelFormat.Rgba8Srgb => (Vortice.DXGI.Format.R8G8B8A8_UNorm_SRgb, GpuTextureFormat.Rgba8, true),
        TexturePixelFormat.Bc1 => (Vortice.DXGI.Format.BC1_UNorm, GpuTextureFormat.Bc1, false),
        TexturePixelFormat.Bc1Srgb => (Vortice.DXGI.Format.BC1_UNorm_SRgb, GpuTextureFormat.Bc1, true),
        TexturePixelFormat.Bc4 => (Vortice.DXGI.Format.BC4_UNorm, GpuTextureFormat.Bc4, false),
        TexturePixelFormat.Bc5 => (Vortice.DXGI.Format.BC5_UNorm, GpuTextureFormat.Bc5, false),
        TexturePixelFormat.Bc7 => (Vortice.DXGI.Format.BC7_UNorm, GpuTextureFormat.Bc7, false),
        TexturePixelFormat.Bc7Srgb => (Vortice.DXGI.Format.BC7_UNorm_SRgb, GpuTextureFormat.Bc7, true),
        _ => throw new InvalidDataException($"неизвестный формат текстуры {(uint)f}"),
    };

    /// <summary>Залить прочитанный файл в видеопамять с ожиданием GPU (<see cref="Renderer.LoadTexture(string)"/>).</summary>
    /// <exception cref="InvalidDataException">Формат или размер, которых не примет D3D12 (BCn — сторона мипа 0 не кратна 4).</exception>
    internal static unsafe GpuTexture Create(GraphicsDevice gd, string path, TextureData data, Action<GpuTexture> disposed)
    {
        var (dxgi, format, srgb) = FormatOf(data.Format);
        int block = TextureData.BlockInfo(data.Format).Block;
        if (data.Width % block != 0 || data.Height % block != 0)
            throw new InvalidDataException($"{path}: {data.Format} {data.Width}×{data.Height} — сторона мипа 0 блочного формата кратна {block}");
        var desc = ResourceDescription.Texture2D(dxgi, (uint)data.Width, (uint)data.Height, (ushort)data.Slices, (ushort)data.Mips);
        long bytes = (long)gd.Device.GetResourceAllocationInfo(0, desc).SizeInBytes;
        // Создаётся и остаётся в COMMON: копия продвигает её в COPY_DEST неявно, барьер возвращает в COMMON, чтение шейдером продвигает
        // неявно (текстура только для чтения). Состояние после заливки равно состоянию создания: откат отслеживаемого состояния к состоянию
        // создания (так бывает у GPU-валидации под нагрузкой GPU) не превращается в «чтение из COPY_DEST».
        var resource = gd.Device.CreateCommittedResource(HeapType.Default, HeapFlags.None, desc, ResourceStates.Common);
        resource.Name = path;
        int srv = -1;
        try
        {
            // Подресурс D3D12 — мип + слой × мипов, как у файла: [слой * Mips + мип].
            uint count = (uint)(data.Mips * data.Slices);
            var layouts = new PlacedSubresourceFootPrint[count];
            var rows = new uint[count];
            var rowSizes = new ulong[count];
            gd.Device.GetCopyableFootprints(desc, 0, count, 0, layouts, rows, rowSizes, out ulong total);
            using (var upload = gd.CreateUploadBuffer(total, path + ".Upload"))
            {
                byte* mapped = upload.Map<byte>(0);
                for (int i = 0; i < count; i++)
                    CopySubresource(data, i, mapped + layouts[i].Offset, layouts[i].Footprint.RowPitch, rows[i], rowSizes[i], path);
                upload.Unmap(0);
                gd.ExecuteImmediate(list =>
                {
                    for (uint i = 0; i < count; i++)
                        list.CopyTextureRegion(new TextureCopyLocation(resource, i), 0, 0, 0, new TextureCopyLocation(upload, layouts[i]), null);
                    list.ResourceBarrierTransition(resource, ResourceStates.CopyDest, ResourceStates.Common);
                });
            }

            srv = gd.Bindless.Allocate();
            gd.Device.CreateShaderResourceView(resource, new ShaderResourceViewDescription
            {
                Format = dxgi,
                ViewDimension = ShaderResourceViewDimension.Texture2DArray,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Texture2DArray = new Texture2DArrayShaderResourceView
                {
                    MostDetailedMip = 0, MipLevels = (uint)data.Mips, FirstArraySlice = 0, ArraySize = (uint)data.Slices,
                },
            }, gd.Bindless.Cpu(srv));
            return new GpuTexture(gd, resource, srv, path, data, format, srgb, bytes, disposed);
        }
        catch
        {
            if (srv >= 0) gd.DeferRelease(resource, srv);
            else resource.Dispose();
            throw;
        }
    }

    /// <summary>Ряды подресурса файла (без выравнивания) → ряды раскладки D3D12 (шаг <paramref name="pitch"/>).</summary>
    private static unsafe void CopySubresource(TextureData data, int index, byte* dst, uint pitch, uint rows, ulong rowSize, string path)
    {
        int mip = index % data.Mips;
        var src = data.Subresources[index];
        int rowBytes = data.RowBytes(mip);
        if ((ulong)rowBytes != rowSize || data.RowCount(mip) != rows)
            throw new InvalidDataException($"{path}: мип {mip} — ряд {rowBytes} Б × {data.RowCount(mip)}, D3D12 ждёт {rowSize} Б × {rows}");
        for (int y = 0; y < rows; y++)
            src.AsSpan(y * rowBytes, rowBytes).CopyTo(new Span<byte>(dst + (long)y * pitch, rowBytes));
    }

    public void Dispose()
    {
        var disposed = Interlocked.Exchange(ref _disposed, null);
        if (disposed is null) return;
        disposed(this);
        _device.DeferRelease(_resource, Srv);
    }

    public override string ToString() =>
        $"{Path} [{Width}×{Height}, слоёв {Slices}, мипов {Mips}, {Format}{(Srgb ? " sRGB" : "")}, {Bytes / 1024.0:F0} КБ]";
}
