using System.ComponentModel;
using System.Reflection;
using Sword.Bake.Engine.Anim;
using Sword.Bake.Engine.Font;
using Sword.Engine.Formats;

namespace Sword.Bake.Engine.Processors;

internal sealed class FontVectorParams
{
    [Description("TrueType-шрифт (квадратичные контуры; OpenType с CFF не подойдёт).")]
    [Source(SourceKind.File, Filter = "*.ttf")]
    public string Font { get; set; } = "";
    [Description("Роли интерфейса, которые берёт шрифт: Body, Head, Label, NumLight, Num. Каждая роль — ровно у одного шрифта. Пусто — шрифт запекается, но игра его не загружает.")]
    public List<string> Roles { get; set; } = [];
    [Description("Файл шрифта в паке; пусто — <ID ассета>.swfont (игра загружает все .swfont из ui/fonts/).")]
    public string Out { get; set; } = "";
    [Description("Файлы, копируемые в пак рядом со шрифтом (лицензия).")]
    [Source(SourceKind.File)]
    public List<string> Extra { get; set; } = [];
}

/// <summary>
/// Один шрифт интерфейса → <c>ui/fonts/&lt;роль&gt;.swfont</c>: TrueType-контуры для GPU-векторного текста, роли и сам ttf
/// для HarfBuzz (<see cref="UiFontBaker"/>). Игра склеивает все шрифты при загрузке.
/// </summary>
internal sealed class FontVectorProcessor : AssetProcessor<FontVectorParams>
{
    public override string Id => "font.vector";
    public override string Title => "Шрифт интерфейса";
    /// <summary>2 — файл на шрифт с ролями и ttf внутри (прежде — все гарнитуры одним файлом).</summary>
    public override int Version => 2;

    public override IEnumerable<Assembly> CodeAssemblies =>
        [GetType().Assembly, typeof(UiVectorFontFormat).Assembly, typeof(StbTrueTypeSharp.StbTrueType).Assembly];

    private static string OutPath(string assetId, FontVectorParams p) => p.Out.Length > 0 ? p.Out : assetId + UiVectorFontFormat.Extension;

    /// <summary>Приложение (лицензия) — рядом со шрифтом, в той же папке.</summary>
    private static string ExtraPath(string outPath, string extra) =>
        (outPath.LastIndexOf('/') is var slash and >= 0 ? outPath[..(slash + 1)] : "") + Path.GetFileName(extra);

    /// <summary>Шрифт и файлы-приложения (лицензия) рядом с ним.</summary>
    protected override IReadOnlyList<DeclaredOutput> Outputs(AssetManifest asset, FontVectorParams p)
    {
        string outPath = OutPath(asset.Id, p);
        return [DeclaredOutput.File(outPath), .. p.Extra.Select(e => DeclaredOutput.File(ExtraPath(outPath, e)))];
    }

    protected override void Process(BakeContext ctx, FontVectorParams p)
    {
        string outPath = OutPath(ctx.Asset.Id, p);
        if (!outPath.StartsWith(UiVectorFontFormat.Dir + "/", StringComparison.OrdinalIgnoreCase) || !outPath.EndsWith(UiVectorFontFormat.Extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"шрифт «{outPath}»: игра загружает только {UiVectorFontFormat.Dir}/*{UiVectorFontFormat.Extension}");
        string ttf = ctx.InputFile(p.Font);
        string output = ctx.Output(outPath);
        byte[] font = UiFontBaker.Bake(File.ReadAllBytes(ttf), ttf, Path.GetFileNameWithoutExtension(outPath), p.Roles, out string report);
        ContentFiles.Write(output, s => s.Write(font));
        ctx.Log.WriteLine(report);
        ctx.Log.WriteLine($"записано {outPath} ({new FileInfo(output).Length / 1024.0:F1} КБ)");
        foreach (var extra in p.Extra)
        {
            string from = ctx.InputFile(extra);
            File.Copy(from, ctx.Output(ExtraPath(outPath, from)), overwrite: true);
        }
    }
}
