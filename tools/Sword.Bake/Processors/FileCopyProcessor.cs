using System.ComponentModel;

namespace Sword.Bake.Processors;

internal sealed class FileCopyParams
{
    [Description("Исходный файл.")]
    [Source(SourceKind.File)]
    public string From { get; set; } = "";
    [Description("Путь в паке.")]
    public string To { get; set; } = "";
}

/// <summary>Файл как есть: данные, которые игра читает без преобразования. Встроенный обработчик ядра.</summary>
internal sealed class FileCopyProcessor : AssetProcessor<FileCopyParams>
{
    public override string Id => "file.copy";
    public override string Title => "Копия файла";

    /// <summary>Отпечаток — только ядро: копирование не зависит от сборок, на которые ссылается пекарня.</summary>
    public override IEnumerable<System.Reflection.Assembly> CodeAssemblies => [GetType().Assembly];

    protected override FileCopyParams Defaults(string assetId) => new() { To = assetId };

    protected override IReadOnlyList<DeclaredOutput>? Outputs(AssetManifest asset, FileCopyParams p) =>
        p.To.Length == 0 ? [] : [DeclaredOutput.File(p.To)];

    protected override void Process(BakeContext ctx, FileCopyParams p)
    {
        if (p.To.Length == 0) throw new ArgumentException("нужен to — путь файла в паке");
        string from = ctx.InputFile(p.From);
        string to = ctx.Output(p.To);
        File.Copy(from, to, overwrite: true);
        ctx.Log.WriteLine($"{from} → {p.To}");
    }
}
