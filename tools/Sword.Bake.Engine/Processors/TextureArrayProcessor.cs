using System.ComponentModel;
using Sword.Bake.Engine.Anim;
using Sword.Bake.Engine.Texture;
using Sword.Engine.Formats;

namespace Sword.Bake.Engine.Processors;

internal sealed class TextureArrayParams
{
    [Description("Картинки-слои PNG по порядку. В имени файла можно * и ? — совпавшие файлы идут по алфавиту, каталог становится входом. " +
                 "Хвост «@x,y,сторона» — взять квадратный участок (пиксели исходника), «#имя» — своё имя слоя: так из атласа " +
                 "одного пропса выходят несколько слоёв (мясо и кость).")]
    [Source(SourceKind.File, Filter = "*.png")]
    public List<string> Layers { get; set; } = [];
    [Description("Сделать слои бесшовными: к краям подмешивается та же картинка, сдвинутая на полстороны. Для материалов, которые кладутся плиткой (трипланар).")]
    public bool Tile { get; set; }
    [Description("Что взять из картинки: luma, r, g, b, a (маска, BC4), normal (xy нормали, BC5) или rgb (цвет sRGB, BC1).")]
    public string Channel { get; set; } = "luma";
    [Description("Сторона слоя в паке, степень двойки; все слои приводятся к ней, исходное соотношение сторон хранится в слое.")]
    public int Size { get; set; } = 1024;
    [Description("Сжатие BC4/BC5; false — R8/RG8.")]
    public bool Compress { get; set; } = true;
    [Description("Слои (имя файла без расширения), которые отразить по горизонтали при запекании: у набора с направлением (хвост брызг) все слои должны смотреть в одну сторону — игра об исключениях не знает.")]
    public List<string> FlipU { get; set; } = [];
    [Description("Файл в паке; пусто — <ID ассета>.swtex.")]
    public string? Out { get; set; }
}

/// <summary>Набор картинок одного назначения (маски брызг, лужи) → массив текстур <c>.swtex</c> (<see cref="TextureBake"/>).</summary>
internal sealed class TextureArrayProcessor : AssetProcessor<TextureArrayParams>
{
    public override string Id => "texture.array";
    public override string Title => "Массив текстур (маски, нормали)";
    public override IEnumerable<System.Reflection.Assembly> CodeAssemblies => [GetType().Assembly, typeof(TextureData).Assembly];

    private static string OutPath(string assetId, TextureArrayParams p) => p.Out ?? assetId + TextureData.Extension;

    protected override IReadOnlyList<DeclaredOutput> Outputs(AssetManifest asset, TextureArrayParams p) => [DeclaredOutput.File(OutPath(asset.Id, p))];

    protected override void Process(BakeContext ctx, TextureArrayParams p)
    {
        if (p.Layers.Count == 0) throw new ArgumentException("layers: нужен хотя бы один файл");
        var channel = TextureBake.ParseChannel(p.Channel);
        string outPath = OutPath(ctx.Asset.Id, p);
        if (!outPath.EndsWith(TextureData.Extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"out «{outPath}»: расширение {TextureData.Extension}");

        // слой: файл, имя и участок (хвосты «@x,y,сторона» и «#имя» у записи)
        var entries = new List<(string File, string Name, int[]? Crop)>();
        foreach (var raw in p.Layers)
        {
            string entry = raw, layerName = "";
            int[]? crop = null;
            int hash = entry.LastIndexOf('#');
            if (hash >= 0)
            {
                layerName = entry[(hash + 1)..];
                entry = entry[..hash];
            }
            int at = entry.LastIndexOf('@');
            if (at >= 0)
            {
                crop = entry[(at + 1)..].Split(',').Select(int.Parse).ToArray();
                if (crop.Length != 3 || crop[2] <= 0) throw new ArgumentException($"layers: «{raw}» — участок «@x,y,сторона»");
                entry = entry[..at];
            }
            string fileName = Path.GetFileName(entry);
            if (fileName.AsSpan().IndexOfAny('*', '?') < 0)
            {
                string file = ctx.InputFile(entry);
                entries.Add((file, layerName.Length > 0 ? layerName : Path.GetFileNameWithoutExtension(file), crop));
                continue;
            }
            string dir = ctx.InputDir(entry[..^fileName.Length].TrimEnd('/', '\\'));
            var matched = Directory.GetFiles(dir, fileName).Order(StringComparer.OrdinalIgnoreCase).ToList();
            if (matched.Count == 0) throw new FileNotFoundException($"layers: «{raw}» — ни одного файла");
            entries.AddRange(matched.Select(m => (m, Path.GetFileNameWithoutExtension(m), crop)));
        }
        var duplicate = entries.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"layers: слой «{duplicate.Key}» встречается дважды — имена слоёв должны различаться (хвост «#имя»)");

        // файл читается один раз, даже если из него режется несколько слоёв (4K PNG — сотни МБ во float)
        var sources = entries.Select(e => e.File).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var read = new FloatImage[sources.Count];
        System.Threading.Tasks.Parallel.For(0, sources.Count, i => read[i] = PngReader.Read(sources[i]));
        ctx.Cancellation.ThrowIfCancellationRequested();
        var images = new (string, FloatImage)[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            var img = read[sources.FindIndex(s => string.Equals(s, entries[i].File, StringComparison.OrdinalIgnoreCase))];
            if (entries[i].Crop is { } c) img = Crop(img, c[0], c[1], c[2], entries[i].Name);
            images[i] = (entries[i].Name, img);
        }
        foreach (string flip in p.FlipU)
        {
            int i = Array.FindIndex(images, im => string.Equals(im.Item1, flip, StringComparison.OrdinalIgnoreCase));
            if (i < 0) throw new ArgumentException($"flipU: слоя «{flip}» нет среди layers");
            images[i].Item2 = FlipHorizontal(images[i].Item2);
        }

        if (p.Tile)
            for (int i = 0; i < images.Length; i++) images[i].Item2 = MakeTileable(images[i].Item2);

        var data = TextureBake.Build(images, channel, p.Size, p.Compress);
        string output = ctx.Output(outPath);
        ContentFiles.Write(output, data.Write);
        for (int i = 0; i < images.Length; i++)
        {
            bool flipped = p.FlipU.Contains(images[i].Item1, StringComparer.OrdinalIgnoreCase);
            ctx.Log.WriteLine($"  [{i}] {images[i].Item1} {images[i].Item2.Width}×{images[i].Item2.Height}{(flipped ? " (отражён по U)" : "")}");
        }
        ctx.Log.WriteLine($"записано {outPath}: {data.Slices} слоёв {data.Width}² {data.Format}, мипов {data.Mips}, {new FileInfo(output).Length / 1048576.0:F1} МБ");
    }

    private static FloatImage Crop(FloatImage src, int x0, int y0, int size, string name)
    {
        if (x0 < 0 || y0 < 0 || x0 + size > src.Width || y0 + size > src.Height)
            throw new ArgumentException($"layers: участок слоя «{name}» ({x0},{y0},{size}) за краем картинки {src.Width}×{src.Height}");
        var dst = new FloatImage(size, size, src.Channels);
        for (int y = 0; y < size; y++)
            Array.Copy(src.Pixels, ((y0 + y) * src.Width + x0) * src.Channels, dst.Pixels, y * size * src.Channels, size * src.Channels);
        return dst;
    }

    /// <summary>
    /// Бесшовная плитка: та же картинка, сдвинутая на полстороны, подмешивается к краям. У сдвинутой на краю —
    /// её середина, а середина непрерывна через край, поэтому стык исчезает; в центре остаётся исходная.
    /// </summary>
    private static FloatImage MakeTileable(FloatImage src)
    {
        int w = src.Width, h = src.Height, ch = src.Channels;
        var dst = new FloatImage(w, h, ch);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float ex = MathF.Abs(2f * x / (w - 1) - 1), ey = MathF.Abs(2f * y / (h - 1) - 1);
                float m = Math.Clamp((MathF.Max(ex, ey) - 0.55f) / 0.4f, 0, 1);
                m = m * m * (3 - 2 * m);
                int sx = (x + w / 2) % w, sy = (y + h / 2) % h;
                for (int c = 0; c < ch; c++) dst[x, y, c] = src[x, y, c] * (1 - m) + src[sx, sy, c] * m;
            }
        return dst;
    }

    private static FloatImage FlipHorizontal(FloatImage src)
    {
        var dst = new FloatImage(src.Width, src.Height, src.Channels);
        for (int y = 0; y < src.Height; y++)
            for (int x = 0; x < src.Width; x++)
                for (int c = 0; c < src.Channels; c++)
                    dst[src.Width - 1 - x, y, c] = src[x, y, c];
        return dst;
    }
}
