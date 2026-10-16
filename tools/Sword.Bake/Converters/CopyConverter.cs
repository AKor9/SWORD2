using System.ComponentModel;

namespace Sword.Bake.Converters;

internal sealed class CopyParams
{
    [Description("Путь в Content/; пусто — data/<имя файла>.")]
    public string? Out { get; set; }
}

/// <summary>Файл данных как есть: то, что игра читает без преобразования (действия боя). Выгрузку анимации и позу покоя печёт не он.</summary>
internal sealed class CopyConverter : Converter<CopyParams>
{
    public override string Id => "copy";
    public override string Title => "Копия файла данных";
    public override string Kind => "*.json, *.txt";

    public override bool Accepts(string file) =>
        HasExtension(file, ".json", ".txt") && !file.EndsWith(Recipe.Extension, StringComparison.OrdinalIgnoreCase)
        && !Baker.Converters.Any(c => c != this && c.Accepts(file));

    protected override void Bake(BakeJob job, CopyParams p)
    {
        string to = p.Out ?? $"data/{Path.GetFileName(job.Source)}";
        File.Copy(job.Source, job.Output(to), overwrite: true);
        job.Log.WriteLine($"{job.Source} → {to}");
    }
}
