using System.Globalization;
using Sword.Bake.Engine.Texture;
using Sword.Engine.Content;
using Sword.Engine.Formats;

namespace Sword.Bake.Engine.Preview;

/// <summary>
/// Предпросмотр <c>.swtex</c>: каждый слой массива — картинка RGBA8 со всеми мипами, распакованными из файла (BC4/BC5 — на CPU,
/// так видны артефакты сжатия). Слой показывается в исходной пропорции (<see cref="TextureLayer.Aspect"/>).
/// Перенос <c>TexturePreview</c> SWORD 2eab13b.
/// </summary>
internal static class TexturePreview
{
    private static readonly string[] MaskModes = ["Значение", "Маска на шахматке"];
    private static readonly string[] NormalModes = ["Нормаль", "X", "Y"];
    private static readonly string[] ColourModes = ["Цвет", "Альфа"];

    public static PreviewScene? Build(IContentSource content, IReadOnlyList<string> paths, int mode)
    {
        var files = paths.Where(p => p.EndsWith(TextureData.Extension, StringComparison.OrdinalIgnoreCase)).ToList();
        if (files.Count == 0) return null;
        string path = files[0];
        TextureData data;
        using (var s = content.OpenRead(path)) data = TextureData.Read(s, content.Describe(path));
        var scene = new PreviewScene
        {
            Modes = data.Format switch
            {
                TexturePixelFormat.Bc4 or TexturePixelFormat.R8 => MaskModes,
                TexturePixelFormat.Bc5 or TexturePixelFormat.Rg8 => NormalModes,
                _ => ColourModes,
            },
        };
        mode = Math.Clamp(mode, 0, scene.Modes.Length - 1);
        long bytes = data.Subresources.Sum(s => (long)s.Length);
        scene.Info.Add(string.Create(CultureInfo.InvariantCulture,
            $"{path}: {data.Format}, {data.Width}×{data.Height}, мипов {data.Mips}, слоёв {data.Slices}, {bytes / 1048576.0:0.0} МБ в видеопамяти"));
        if (files.Count > 1) scene.Info.Add($"ещё текстур у ассета: {files.Count - 1} (показана первая)");
        if (data.Format is TexturePixelFormat.Bc7 or TexturePixelFormat.Bc7Srgb)
        {
            scene.Info.Add("BC7 предпросмотр не распаковывает");
            return scene;
        }

        var images = new PreviewImage[data.Slices];
        Parallel.For(0, data.Slices, s =>
        {
            var mips = new List<byte[]>(data.Mips);
            for (int m = 0; m < data.Mips; m++) mips.Add(Decode(data, s, m, mode));
            var layer = s < data.Layers.Length ? data.Layers[s] : new TextureLayer("", 1);
            float aspect = layer.Aspect > 0 ? layer.Aspect : 1;
            images[s] = new PreviewImage
            {
                Name = layer.Name.Length > 0 ? $"{s}: {layer.Name}" : $"слой {s}",
                Width = data.Width, Height = data.Height, Mips = mips,
                Aspect = aspect * data.Width / data.Height,
                Caption = string.Create(CultureInfo.InvariantCulture, $"пропорция {(MathF.Abs(aspect - 1) < 1e-3f ? "1" : aspect.ToString("0.##", CultureInfo.InvariantCulture))}:1"),
                Checker = scene.Modes == MaskModes && mode == 1 || scene.Modes == ColourModes,
            };
        });
        scene.Images.AddRange(images);
        return scene;
    }

    /// <summary>Мип слоя → RGBA8 по варианту показа.</summary>
    private static byte[] Decode(TextureData data, int slice, int mip, int mode)
    {
        int w = TextureData.MipSize(data.Width, mip), h = TextureData.MipSize(data.Height, mip);
        var src = data.Subresource(slice, mip);
        var rgba = new byte[w * h * 4];
        switch (data.Format)
        {
            case TexturePixelFormat.R8:
                for (int i = 0; i < w * h; i++) Mask(rgba, i, src[i], mode);
                break;
            case TexturePixelFormat.Rg8:
                for (int i = 0; i < w * h; i++) Normal(rgba, i, src[i * 2], src[i * 2 + 1], mode);
                break;
            case TexturePixelFormat.Rgba8:
            case TexturePixelFormat.Rgba8Srgb:
                for (int i = 0; i < w * h; i++)
                {
                    int o = i * 4;
                    if (mode == 1) rgba[o] = rgba[o + 1] = rgba[o + 2] = src[o + 3];
                    else src.AsSpan(o, 3).CopyTo(rgba.AsSpan(o));
                    rgba[o + 3] = mode == 1 ? (byte)255 : src[o + 3];
                }
                break;
            case TexturePixelFormat.Bc1:
            case TexturePixelFormat.Bc1Srgb:
                DecodeBc1(src, w, h, rgba);
                break;
            case TexturePixelFormat.Bc4:
            case TexturePixelFormat.Bc5:
                DecodeBc45(src, w, h, data.Format == TexturePixelFormat.Bc5, mode, rgba);
                break;
        }
        return rgba;
    }

    private static void DecodeBc1(byte[] src, int w, int h, byte[] rgba)
    {
        int bw = (w + 3) / 4, bh = (h + 3) / 4;
        Span<byte> px = stackalloc byte[64];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                BlockCompress.DecodeBc1(src.AsSpan((by * bw + bx) * 8, 8), px);
                for (int k = 0; k < 16; k++)
                {
                    int x = bx * 4 + k % 4, y = by * 4 + k / 4;
                    if (x >= w || y >= h) continue;
                    px.Slice(k * 4, 4).CopyTo(rgba.AsSpan((y * w + x) * 4));
                }
            }
    }

    private static void DecodeBc45(byte[] src, int w, int h, bool two, int mode, byte[] rgba)
    {
        int bw = (w + 3) / 4, bh = (h + 3) / 4, blockBytes = two ? 16 : 8;
        Span<byte> r = stackalloc byte[16];
        Span<byte> g = stackalloc byte[16];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                var block = src.AsSpan((by * bw + bx) * blockBytes, blockBytes);
                BlockCompress.DecodeBc4(block[..8], r);
                if (two) BlockCompress.DecodeBc4(block[8..], g);
                for (int k = 0; k < 16; k++)
                {
                    int x = bx * 4 + k % 4, y = by * 4 + k / 4;
                    if (x >= w || y >= h) continue;
                    if (two) Normal(rgba, y * w + x, r[k], g[k], mode);
                    else Mask(rgba, y * w + x, r[k], mode);
                }
            }
    }

    /// <summary>Один канал: серым или белым с этой прозрачностью.</summary>
    private static void Mask(byte[] rgba, int i, byte v, int mode)
    {
        int o = i * 4;
        if (mode == 1)
        {
            rgba[o] = rgba[o + 1] = rgba[o + 2] = 255;
            rgba[o + 3] = v;
        }
        else
        {
            rgba[o] = rgba[o + 1] = rgba[o + 2] = v;
            rgba[o + 3] = 255;
        }
    }

    /// <summary>Нормаль xy (0…255 ↔ −1…1): цветом с восстановленной z или одной осью серым.</summary>
    private static void Normal(byte[] rgba, int i, byte x, byte y, int mode)
    {
        int o = i * 4;
        rgba[o + 3] = 255;
        if (mode != 0)
        {
            rgba[o] = rgba[o + 1] = rgba[o + 2] = mode == 1 ? x : y;
            return;
        }
        float nx = x / 127.5f - 1, ny = y / 127.5f - 1;
        float nz = MathF.Sqrt(MathF.Max(0, 1 - nx * nx - ny * ny));
        rgba[o] = x;
        rgba[o + 1] = y;
        rgba[o + 2] = (byte)(nz * 127.5f + 127.5f);
    }
}
