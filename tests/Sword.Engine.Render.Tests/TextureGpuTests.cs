using System.Numerics;
using Sword.Engine.Content;
using Sword.Engine.Diagnostics;
using Sword.Engine.Formats;
using Sword.Engine.Platform;
using Sword.Engine.Platform.Win32;
using Xunit.Abstractions;

namespace Sword.Engine.Render.Tests;

/// <summary>
/// Текстуры пака в видеопамяти (<see cref="Renderer.LoadTexture(string)"/>) на настоящем устройстве со слоем отладки и GPU-валидацией:
/// тексели мипа 0 и последнего мипа, прочитанные шейдером (<c>Load</c> и выборка), совпадают с декодом файла на CPU по спецификации D3D
/// (<see cref="TextureDecode"/>) в пределах 1/255 (BCn — в пределах точности декода железом, <see cref="TextureDecode.Tolerance"/>) — для каждого формата, который пишет пекарня, и для каждой текстуры пака; sRGB при
/// выборке — линейное значение; нет файла или битый — исключение с путём; кадр с текстурой — 0 Б; освобождение — после кадров в полёте.
/// Пак берётся как у игры (<see cref="ContentLocator"/>, в том числе <c>SWORD_CONTENT</c>): так же проверяется временный пак выпечки.
/// </summary>
[Collection("gpu")]
[Trait("Category", "Gpu")]
public sealed class TextureGpuTests : IDisposable, ILogSink
{
    private const string Shader = "texture_tests.hlsl";
    private static readonly ComputePipelineDesc LoadPipeline = new(Shader, "CsLoad"), SamplePipeline = new(Shader, "CsSample");
    private static readonly GraphicsPipelineDesc Show = new(Shader, "VsFull", "PsShow");


    private readonly Win32Window _window;
    private readonly Renderer _renderer;
    private readonly List<string> _d3d12 = [];
    private readonly ITestOutputHelper _output;

    public TextureGpuTests(ITestOutputHelper output)
    {
        _output = output;
        Log.AddSink(this);
        _window = new Win32Window(new WindowOptions("Sword.Engine.Render.Tests.Texture", 64, 64, Activate: false));
        var contract = new ShaderContract();
        Renderer.Contribute(contract);
        _renderer = new Renderer(new RendererOptions(_window.NativeHandle, _window.Width, _window.Height, DebugLayer: true,
            ShaderRoot: Path.Combine(AppContext.BaseDirectory, "Shaders"), GpuValidation: true), contract);
    }

    void ILogSink.Write(in LogEntry entry)
    {
        if (entry.Category == "d3d12" && entry.Level >= LogLevel.Warning) _d3d12.Add(entry.Message);
    }

    public void Dispose()
    {
        _renderer.Dispose();
        _window.Dispose();
        Log.RemoveSink(this);
    }

    private void AssertNoDebugMessages() =>
        Assert.True(_d3d12.Count == 0, "сообщения отладочного слоя D3D12:\n" + string.Join("\n", _d3d12));

    /// <summary>Тексели мипа <paramref name="mip"/> слоя <paramref name="slice"/>, прочитанные шейдером (<c>Load</c> или выборка в центре текселя).</summary>
    private Vector4[] Fetch(GpuTexture texture, int slice, int mip, bool sample = false)
    {
        int w = TextureData.MipSize(texture.Width, mip), h = TextureData.MipSize(texture.Height, mip);
        using var storage = _renderer.CreateStorage<Vector4>("test.texels", (uint)(w * h));
        _window.PumpMessages();
        _renderer.Render(f =>
        {
            var s = f.Graph.Import(storage);
            f.Graph.AddComputePass("test.fetch").Write(s).Execute(ctx =>
            {
                ctx.SetPipeline(sample ? SamplePipeline : LoadPipeline);
                ctx.SetConstant(0, texture.Srv);
                ctx.SetConstant(1, f.Graph.UavOf(s));
                ctx.SetConstant(2, w);
                ctx.SetConstant(3, h);
                ctx.SetConstant(4, slice);
                ctx.SetConstant(5, mip);
                ctx.Dispatch((uint)(w + 7) / 8, (uint)(h + 7) / 8);
            });
        }, vsync: false);
        var texels = new Vector4[w * h];
        _renderer.ReadStorage(storage, texels.AsSpan());
        return texels;
    }

    /// <summary>
    /// Мип 0 и последний мип первого и последнего слоя: GPU = декод CPU в допуске (<see cref="TextureDecode.Tolerance"/>) в пространстве хранения (у sRGB значение GPU
    /// кодируется обратно в sRGB). Вернёт наибольшее расхождение.
    /// </summary>
    private float AssertMatchesCpu(GpuTexture texture, TextureData data, string what)
    {
        float worst = 0;
        foreach (int slice in new[] { 0, data.Slices - 1 }.Distinct())
            foreach (int mip in new[] { 0, data.Mips - 1 }.Distinct())
            {
                var expected = TextureDecode.Decode(data, slice, mip);
                var got = Fetch(texture, slice, mip);
                int w = TextureData.MipSize(data.Width, mip);
                for (int i = 0; i < expected.Length; i++)
                {
                    var g = got[i];
                    if (texture.Srgb) g = new Vector4(TextureDecode.ToSrgb(g.X), TextureDecode.ToSrgb(g.Y), TextureDecode.ToSrgb(g.Z), g.W);
                    for (int c = 0; c < 4; c++)
                    {
                        float d = MathF.Abs(g[c] - expected[i][c]);
                        worst = MathF.Max(worst, d);
                        if (d > TextureDecode.Tolerance(data, slice, mip, i % w, i / w, c))
                            Assert.Fail($"{what}: слой {slice} мип {mip} тексель ({i % w}, {i / w}) канал {c}: GPU {g[c] * 255:F3}/255, CPU " +
                                $"{expected[i][c] * 255:F3}/255; блок {TextureDecode.BlockHex(data, slice, mip, i % w, i / w)}");
                    }
                }
            }
        return worst;
    }

    /// <summary>Случайные подресурсы (любые байты — допустимые блоки BCn, включая трёхцветный BC1 с прозрачным чёрным).</summary>
    private static TextureData Synthetic(TexturePixelFormat format, int width, int height, int slices, int seed)
    {
        int mips = 1 + (int)Math.Log2(Math.Max(width, height));
        var data = new TextureData
        {
            Format = format, Width = width, Height = height, Mips = mips, Slices = slices, Subresources = new byte[mips * slices][],
            Layers = [.. Enumerable.Range(0, slices).Select(s => new TextureLayer($"layer{s}", (float)width / height))],
        };
        var rng = new Random(seed);
        for (int s = 0; s < slices; s++)
            for (int m = 0; m < mips; m++)
                rng.NextBytes(data.Subresources[s * mips + m] = new byte[data.MipBytes(m)]);
        return data;
    }

    private GpuTexture Load(string name, TextureData data)
    {
        using var ms = new MemoryStream();
        data.Write(ms);
        ms.Position = 0;
        return _renderer.LoadTexture(name, ms);
    }

    [Theory]
    [InlineData(TexturePixelFormat.R8)]
    [InlineData(TexturePixelFormat.Rg8)]
    [InlineData(TexturePixelFormat.Rgba8)]
    [InlineData(TexturePixelFormat.Rgba8Srgb)]
    [InlineData(TexturePixelFormat.Bc1)]
    [InlineData(TexturePixelFormat.Bc1Srgb)]
    [InlineData(TexturePixelFormat.Bc4)]
    [InlineData(TexturePixelFormat.Bc5)]
    public void EveryBakerFormat_Mip0AndLastMip_MatchCpuDecode(TexturePixelFormat format)
    {
        // 16×8, 3 слоя, полная цепочка до 1×1: у BCn младшие мипы меньше блока
        var data = Synthetic(format, 16, 8, 3, (int)format);
        using var texture = Load($"synthetic/{format}", data);
        Assert.Equal((16, 8, 3, 5), (texture.Width, texture.Height, texture.Slices, texture.Mips));
        Assert.Equal(format is TexturePixelFormat.Rgba8Srgb or TexturePixelFormat.Bc1Srgb, texture.Srgb);
        Assert.Equal(format switch
        {
            TexturePixelFormat.R8 => GpuTextureFormat.R8,
            TexturePixelFormat.Rg8 => GpuTextureFormat.Rg8,
            TexturePixelFormat.Rgba8 or TexturePixelFormat.Rgba8Srgb => GpuTextureFormat.Rgba8,
            TexturePixelFormat.Bc1 or TexturePixelFormat.Bc1Srgb => GpuTextureFormat.Bc1,
            TexturePixelFormat.Bc4 => GpuTextureFormat.Bc4,
            _ => GpuTextureFormat.Bc5,
        }, texture.Format);
        Assert.Equal(2, texture.FindLayer("LAYER2"));
        Assert.True(texture.Bytes > 0);
        Assert.Contains(texture, _renderer.Textures);
        float worst = AssertMatchesCpu(texture, data, format.ToString());
        _output.WriteLine($"{format}: наибольшее расхождение GPU и CPU {worst * 255:F3}/255");
        AssertNoDebugMessages();
    }

    [Fact]
    public void Srgb_LoadAndSample_GiveLinearValue()
    {
        // RGBA8 sRGB, все тексели (128, 64, 255, 128): линейные 0.2158605, 0.0512695, 1; альфа не декодируется — 128/255.
        var data = Synthetic(TexturePixelFormat.Rgba8Srgb, 4, 4, 1, 0);
        foreach (var sub in data.Subresources)
            for (int i = 0; i < sub.Length; i += 4) (sub[i], sub[i + 1], sub[i + 2], sub[i + 3]) = (128, 64, 255, 128);
        using var texture = Load("known.srgb", data);
        var expected = new Vector4(0.2158605f, 0.0512695f, 1, 128 / 255f);
        foreach (bool sample in new[] { false, true })
            foreach (int mip in new[] { 0, 2 })
                foreach (var v in Fetch(texture, 0, mip, sample))
                    Assert.True(Vector4.Distance(v, expected) < 2e-4f, $"{(sample ? "выборка" : "Load")} мип {mip}: {v}, ожидалось {expected}");

        // BC1 sRGB: сплошной блок с опорой 565 (16, 32, 8) → sRGB (16/31, 32/63, 8/31) → линейный.
        var bc1 = Synthetic(TexturePixelFormat.Bc1Srgb, 4, 4, 1, 0);
        ushort c = 16 << 11 | 32 << 5 | 8;
        foreach (var sub in bc1.Subresources) sub.AsSpan().Clear();
        foreach (var sub in bc1.Subresources) (sub[0], sub[1], sub[2], sub[3]) = ((byte)c, (byte)(c >> 8), (byte)c, (byte)(c >> 8));
        using var bc1Texture = Load("known.bc1srgb", bc1);
        var linear = new Vector3(TextureDecode.FromSrgb(16 / 31f), TextureDecode.FromSrgb(32 / 63f), TextureDecode.FromSrgb(8 / 31f));
        foreach (var v in Fetch(bc1Texture, 0, 0, sample: true))
            Assert.True(Vector3.Distance(new Vector3(v.X, v.Y, v.Z), linear) < 3e-3f && v.W == 1, $"BC1 sRGB: {v}, ожидалось {linear}");
        AssertNoDebugMessages();
    }

    [Fact]
    public void Bc7_SolidBlock_Loads()
    {
        // Режим 6: опоры RGBA по 7 бит + общий бит p; все индексы 0 → цвет опоры 0 = (7 бит ≪ 1) | p0.
        var data = Synthetic(TexturePixelFormat.Bc7, 4, 4, 1, 0);
        var (r, g, b, a, p) = (100, 50, 20, 127, 1);
        foreach (var sub in data.Subresources)
        {
            var bits = new System.Collections.BitArray(128);
            int at = 0;
            void Put(int value, int count)
            {
                for (int i = 0; i < count; i++) bits[at++] = ((value >> i) & 1) != 0;
            }
            Put(1 << 6, 7);
            foreach (int v in new[] { r, r, g, g, b, b, a, a }) Put(v, 7);
            Put(p, 1);
            Put(p, 1);
            bits.CopyTo(sub, 0);
        }
        using var texture = Load("known.bc7", data);
        Assert.Equal((GpuTextureFormat.Bc7, false), (texture.Format, texture.Srgb));
        var expected = new Vector4(r * 2 + p, g * 2 + p, b * 2 + p, a * 2 + p) / 255;
        foreach (int mip in new[] { 0, 2 })
            foreach (var v in Fetch(texture, 0, mip))
                Assert.True(Vector4.Distance(v, expected) < 1e-3f, $"BC7 мип {mip}: {v * 255}, ожидалось {expected * 255}");
        AssertNoDebugMessages();
    }

    [Fact]
    [Trait("Category", "Content")]
    public void EveryPakTexture_MatchesCpuDecode()
    {
        using var pak = ContentLocator.Open();
        var paths = pak.List("", TextureData.Extension);
        Assert.NotEmpty(paths);
        var formats = new SortedSet<string>();
        foreach (string path in paths)
        {
            TextureData data;
            using (var s = pak.OpenRead(path)) data = TextureData.Read(s, path);
            using var texture = _renderer.LoadTexture(path);
            Assert.Equal((path, data.Width, data.Height, data.Slices, data.Mips), (texture.Path, texture.Width, texture.Height, texture.Slices, texture.Mips));
            Assert.Equal(data.Layers.Select(l => l.Name), texture.Layers);
            float worst = AssertMatchesCpu(texture, data, path);
            formats.Add(data.Format.ToString());
            _output.WriteLine($"{texture}: GPU − CPU до {worst * 255:F3}/255");
        }
        _output.WriteLine($"{pak.Location}: {paths.Count} текстур, форматы {string.Join(", ", formats)}");
        Assert.Empty(_renderer.Textures);
        AssertNoDebugMessages();
    }

    [Fact]
    [Trait("Category", "Content")]
    public void MissingOrBrokenFile_ThrowsWithPath()
    {
        var missing = Assert.Throws<FileNotFoundException>(() => _renderer.LoadTexture("effects/nothing/here.swtex"));
        Assert.Contains("effects/nothing/here.swtex", missing.Message);
        using var pak = ContentLocator.Open();
        string font = pak.List("ui/fonts", ".swfont")[0];
        var notTexture = Assert.Throws<InvalidDataException>(() => _renderer.LoadTexture(font));
        Assert.Contains(font, notTexture.Message);

        var data = Synthetic(TexturePixelFormat.Bc4, 8, 8, 1, 1);
        using var ms = new MemoryStream();
        data.Write(ms);
        var bytes = ms.ToArray();
        var truncated = Assert.Throws<InvalidDataException>(() => _renderer.LoadTexture("broken/truncated", new MemoryStream(bytes[..^10])));
        Assert.Contains("broken/truncated", truncated.Message);
        bytes[0] = (byte)'X';
        var magic = Assert.Throws<InvalidDataException>(() => _renderer.LoadTexture("broken/magic", new MemoryStream(bytes)));
        Assert.Contains("broken/magic", magic.Message);
        // BCn со стороной мипа 0 не кратной 4 D3D12 не примет — отказ до создания ресурса
        var odd = Synthetic(TexturePixelFormat.Bc1, 6, 6, 1, 2);
        var badSize = Assert.Throws<InvalidDataException>(() => Load("broken/odd", odd));
        Assert.Contains("broken/odd", badSize.Message);
        Assert.Empty(_renderer.Textures);
        AssertNoDebugMessages();
    }

    [Fact]
    public void FrameSamplingTexture_AllocatesNothing_DisposeWaitsForFramesInFlight()
    {
        var texture = Load("frame.texture", Synthetic(TexturePixelFormat.Bc1Srgb, 64, 64, 2, 5));
        int srv = texture.Srv;
        Action<RenderPassContext> draw = ctx =>
        {
            ctx.SetPipeline(Show);
            ctx.SetConstant(0, srv);
            ctx.SetConstant(2, ctx.Width);
            ctx.SetConstant(3, ctx.Height);
            ctx.Draw(3);
        };
        Action<RenderFrame> frame = f => f.Graph.AddPass("test.show").Color(f.Graph.BackBuffer).Execute(draw);
        for (int i = 0; i < 30; i++) _renderer.Render(frame, vsync: false);
        const int frames = 200;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < frames; i++) _renderer.Render(frame, vsync: false);
        long bytes = (GC.GetAllocatedBytesForCurrentThread() - before) / frames;
        Assert.True(bytes == 0, $"кадр с выборкой текстуры выделил {bytes} Б");

        // Кадр читает текстуру и ещё в полёте — освобождение отложено; следующие кадры и новая текстура не видят ошибок GPU-валидации.
        _renderer.Render(frame, vsync: false);
        texture.Dispose();
        texture.Dispose();
        Assert.DoesNotContain(texture, _renderer.Textures);
        Action<RenderFrame> empty = f => f.Graph.AddPass("test.clear").Color(f.Graph.BackBuffer, new Vector4(0, 0, 0, 1));
        for (int i = 0; i < 4; i++) _renderer.Render(empty, vsync: false);
        using var next = Load("frame.next", Synthetic(TexturePixelFormat.Bc4, 64, 64, 1, 6));
        srv = next.Srv;
        for (int i = 0; i < 4; i++) _renderer.Render(frame, vsync: false);
        _renderer.WaitForIdle();
        AssertNoDebugMessages();
    }
}

/// <summary>
/// Декод мипа <c>.swtex</c> на CPU по спецификации D3D (во float): RGBA 0…1 в пространстве хранения — sRGB не декодируется
/// (BC1 sRGB интерполируется в sRGB, как требует D3D). R8 — (r, 0, 0, 1), RG8 и BC5 — (r, g, 0, 1), BC4 — (r, 0, 0, 1).
/// </summary>
internal static class TextureDecode
{
    public static float ToSrgb(float v)
    {
        v = Math.Clamp(v, 0, 1);
        return v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1 / 2.4f) - 0.055f;
    }

    public static float FromSrgb(float v)
    {
        v = Math.Clamp(v, 0, 1);
        return v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
    }

    public static Vector4[] Decode(TextureData data, int slice, int mip)
    {
        int w = TextureData.MipSize(data.Width, mip), h = TextureData.MipSize(data.Height, mip);
        var src = data.Subresource(slice, mip);
        var o = new Vector4[w * h];
        switch (data.Format)
        {
            case TexturePixelFormat.R8:
                for (int i = 0; i < o.Length; i++) o[i] = new(src[i] / 255f, 0, 0, 1);
                break;
            case TexturePixelFormat.Rg8:
                for (int i = 0; i < o.Length; i++) o[i] = new(src[i * 2] / 255f, src[i * 2 + 1] / 255f, 0, 1);
                break;
            case TexturePixelFormat.Rgba8 or TexturePixelFormat.Rgba8Srgb:
                for (int i = 0; i < o.Length; i++) o[i] = new Vector4(src[i * 4], src[i * 4 + 1], src[i * 4 + 2], src[i * 4 + 3]) / 255f;
                break;
            case TexturePixelFormat.Bc1 or TexturePixelFormat.Bc1Srgb or TexturePixelFormat.Bc4 or TexturePixelFormat.Bc5:
                Blocks(data.Format, src, w, h, o);
                break;
            default:
                throw new NotSupportedException($"CPU-декода {data.Format} нет");
        }
        return o;
    }

    private static void Blocks(TexturePixelFormat format, byte[] src, int w, int h, Vector4[] o)
    {
        int bw = (w + 3) / 4, bh = (h + 3) / 4, bytes = TextureData.BlockInfo(format).Bytes;
        Span<Vector4> block = stackalloc Vector4[16];
        Span<float> red = stackalloc float[16], green = stackalloc float[16];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                var b = src.AsSpan((by * bw + bx) * bytes, bytes);
                switch (format)
                {
                    case TexturePixelFormat.Bc1 or TexturePixelFormat.Bc1Srgb:
                        Bc1(b, block);
                        break;
                    case TexturePixelFormat.Bc4:
                        Bc4(b, red);
                        for (int k = 0; k < 16; k++) block[k] = new(red[k], 0, 0, 1);
                        break;
                    default:
                        Bc4(b[..8], red);
                        Bc4(b[8..], green);
                        for (int k = 0; k < 16; k++) block[k] = new(red[k], green[k], 0, 1);
                        break;
                }
                for (int k = 0; k < 16; k++)
                {
                    int x = bx * 4 + k % 4, y = by * 4 + k / 4;
                    if (x < w && y < h) o[y * w + x] = block[k];
                }
            }
    }

    /// <summary>
    /// Допуск GPU против декода по спецификации D3D у текселя (x, y) канала <paramref name="channel"/>: несжатые — 1/255; у BCn веса
    /// градаций на железе не 1/3, 1/7, 1/5 (RTX 5070: BC1 — 11/16 вместо 2/3, BC4 — 3/16 вместо 1/5; до 0.027 размаха опор) — 1/255 +
    /// 3 % размаха опор блока в канале, у BC1 ещё полшага 5 бит (градация округляется до 5:6:5 и расширяется повтором бит).
    /// Ошибка раскладки, блока, индекса, формата или sRGB даёт десятки /255 на случайных блоках — допуск её не прячет.
    /// </summary>
    public static float Tolerance(TextureData data, int slice, int mip, int x, int y, int channel)
    {
        const float unit = 1 / 255f + 1e-5f;
        switch (data.Format)
        {
            case TexturePixelFormat.Bc1 or TexturePixelFormat.Bc1Srgb:
            {
                if (channel == 3) return unit;
                var b = Block(data, slice, mip, x, y);
                var span = Vector4.Abs(From565(b[0] | b[1] << 8) - From565(b[2] | b[3] << 8));
                return unit + 0.5f / 31 + 0.03f * span[channel];
            }
            case TexturePixelFormat.Bc4 or TexturePixelFormat.Bc5:
            {
                if (channel > (data.Format == TexturePixelFormat.Bc5 ? 1 : 0)) return unit;
                var b = Block(data, slice, mip, x, y)[(channel * 8)..];
                return unit + 0.03f * Math.Abs(b[0] - b[1]) / 255f;
            }
            default:
                return unit;
        }
    }

    /// <summary>Байты блока BCn с текселем (x, y).</summary>
    private static ReadOnlySpan<byte> Block(TextureData data, int slice, int mip, int x, int y)
    {
        int bytes = TextureData.BlockInfo(data.Format).Bytes, bw = TextureData.BlocksAcross(data.Format, TextureData.MipSize(data.Width, mip));
        return data.Subresource(slice, mip).AsSpan(((y / 4) * bw + x / 4) * bytes, bytes);
    }

    /// <summary>Байты блока BCn с текселем (x, y) — для сообщения о расхождении; у несжатых форматов — пусто.</summary>
    public static string BlockHex(TextureData data, int slice, int mip, int x, int y) =>
        TextureData.BlockInfo(data.Format).Block == 1 ? "" : Convert.ToHexString(Block(data, slice, mip, x, y)) + $", индекс {(y % 4) * 4 + x % 4}";

    private static Vector4 From565(int c) => new(((c >> 11) & 31) / 31f, ((c >> 5) & 63) / 63f, (c & 31) / 31f, 1);

    private static void Bc1(ReadOnlySpan<byte> b, Span<Vector4> texels)
    {
        int c0 = b[0] | b[1] << 8, c1 = b[2] | b[3] << 8;
        Span<Vector4> pal = [From565(c0), From565(c1), default, default];
        if (c0 > c1)
        {
            pal[2] = (2 * pal[0] + pal[1]) / 3;
            pal[3] = (pal[0] + 2 * pal[1]) / 3;
        }
        else
        {
            pal[2] = (pal[0] + pal[1]) / 2;
            pal[3] = Vector4.Zero;   // прозрачный чёрный
        }
        uint bits = (uint)(b[4] | b[5] << 8 | b[6] << 16 | b[7] << 24);
        for (int k = 0; k < 16; k++) texels[k] = pal[(int)((bits >> (2 * k)) & 3)];
    }

    private static void Bc4(ReadOnlySpan<byte> b, Span<float> texels)
    {
        float r0 = b[0] / 255f, r1 = b[1] / 255f;
        Span<float> pal = stackalloc float[8];
        pal[0] = r0;
        pal[1] = r1;
        if (b[0] > b[1])
            for (int i = 2; i < 8; i++) pal[i] = ((8 - i) * r0 + (i - 1) * r1) / 7;
        else
        {
            for (int i = 2; i < 6; i++) pal[i] = ((6 - i) * r0 + (i - 1) * r1) / 5;
            pal[6] = 0;
            pal[7] = 1;
        }
        ulong bits = 0;
        for (int i = 0; i < 6; i++) bits |= (ulong)b[2 + i] << (8 * i);
        for (int k = 0; k < 16; k++) texels[k] = pal[(int)((bits >> (3 * k)) & 7)];
    }
}
