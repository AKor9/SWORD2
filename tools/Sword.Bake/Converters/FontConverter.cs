using System.ComponentModel;
using Sword.Bake.Anim;
using Sword.Bake.Font;
using Sword.Engine.Formats;

namespace Sword.Bake.Converters;

internal sealed class FontParams
{
    [Description("Имя шрифта: файл fonts/<имя>.swfont.")]
    public string Name { get; set; } = "";
    [Description("Роли интерфейса, которые берёт шрифт: Body, Head, Label, NumLight, Num. Каждая роль — ровно у одного шрифта. Пусто — шрифт запекается, но игра его не загружает.")]
    public List<string> Roles { get; set; } = [];
    [Description("Файлы, копируемые рядом со шрифтом (лицензия).")]
    [Source(Filter = "*.txt")]
    public List<string> Extra { get; set; } = [];
}

/// <summary>
/// Шрифт TrueType → <c>fonts/&lt;имя&gt;.swfont</c>: контуры для GPU-векторного текста, роли и сам ttf для HarfBuzz
/// (<see cref="UiFontBaker"/>); файлы-приложения (лицензия) — рядом. Игра склеивает все шрифты при загрузке.
/// </summary>
internal sealed class FontConverter : Converter<FontParams>
{
    public override string Id => "font";
    public override string Title => "Шрифт интерфейса";
    public override string Kind => "*.ttf";

    public override bool Accepts(string file) => HasExtension(file, ".ttf");

    protected override FontParams Default(BakeJob job) => new() { Name = NameOf(job.Source) };

    protected override void Bake(BakeJob job, FontParams p)
    {
        if (!AnimImport.IsValidId(p.Name)) throw new ArgumentException($"name «{p.Name}»: только a-z, 0-9, _ . -");
        string outPath = $"{UiVectorFontFormat.Dir}/{p.Name}{UiVectorFontFormat.Extension}";
        string output = job.Output(outPath);
        byte[] font = UiFontBaker.Bake(File.ReadAllBytes(job.Source), job.Source, p.Name, p.Roles, out string report);
        ContentFiles.Write(output, s => s.Write(font));
        job.Log.WriteLine(report);
        job.Log.WriteLine($"записано {outPath} ({new FileInfo(output).Length / 1024.0:F1} КБ)");
        foreach (var extra in p.Extra)
        {
            string from = job.Input(extra);
            File.Copy(from, job.Output($"{UiVectorFontFormat.Dir}/{Path.GetFileName(from)}"), overwrite: true);
        }
    }
}
