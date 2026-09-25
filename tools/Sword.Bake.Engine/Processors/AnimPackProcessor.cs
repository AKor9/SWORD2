using System.ComponentModel;
using Sword.Bake.Engine.Anim;
using Sword.Bake.Engine.Preview;
using Sword.Engine.Animation;
using Sword.Engine.Formats;

namespace Sword.Bake.Engine.Processors;

internal sealed class AnimPackParams
{
    [Description("Скелет и риг (единица anim.skeleton): ID скелета клипов, эталонные кости, поза покоя для переноса пальцев.")]
    [AssetRef(SkeletonProcessor.ProcessorId)]
    public string Skeleton { get; set; } = "";
    [Description("Raw-экспорт полного скелета (manifest.json + clips/).")]
    [Source(SourceKind.Directory)]
    public string Anims { get; set; } = "";
    [Description("Raw-экспорт исходного скелета тех же клипов для переноса пальцев (ретаргет с потерей пястных костей).")]
    [Source(SourceKind.Directory)]
    public string? FingerSrc { get; set; }
    [Description("Наборы библиотеки: набор → маски папок пути UE клипа (* и ?); пустой список — все клипы. Каждый клип — ровно в один набор; " +
                 "ID клипа в игре — «набор/имя».")]
    public Dictionary<string, List<string>> Sets { get; set; } = [];
}

/// <summary>
/// Прогон экспорта клипов → наборы библиотеки <c>anim/&lt;набор&gt;/*.swclip</c> (<see cref="AnimImport"/>). Единица сборки —
/// экспорт (обычно по импорту: «anim/combat_master_ext»), ассеты контента — наборы по игровому смыслу («anim/katana»);
/// в один набор могут класть клипы несколько единиц. Скелет библиотеки пишет единица anim.skeleton — здесь только сверка
/// костей экспорта с её эталоном, поэтому экспорты собираются параллельно.
/// </summary>
internal sealed class AnimPackProcessor : AssetProcessor<AnimPackParams>, IAssetPreview
{
    /// <summary>Клипы наборов на меше персонажа (<see cref="AnimPreview"/>).</summary>
    public PreviewScene? Preview(Sword.Engine.Content.IContentSource content, IReadOnlyList<string> paths, int mode) =>
        AnimPreview.Build(content, paths, mode);

    public override string Id => "anim.pack";
    public override string Title => "Клипы экспорта → наборы библиотеки";
    /// <summary>Импорт не пересобирает клипы с тем же хешем исходника: прежние клипы берутся из пака.</summary>
    public override bool Incremental => true;
    /// <summary>
    /// Код, от которого зависит выход: плагин, анимация (система корневого движения) и форматы (запись клипа) — не ядро
    /// пекарни. Правка любой из них делает ассет устаревшим, но клип с тем же хешем исходника не перезаписывается:
    /// правка выпечки клипа — поднять <see cref="AnimImport.ToolVersion"/>.
    /// </summary>
    public override IEnumerable<System.Reflection.Assembly> CodeAssemblies =>
        [GetType().Assembly, typeof(ClipLibrary).Assembly, typeof(ClipFile).Assembly];

    protected override AnimPackParams Defaults(string assetId) => new() { Sets = new() { [LastSegment(assetId)] = [] } };

    /// <summary>Каталоги наборов; имя набора неверно — неизвестно (ошибку скажет сборка).</summary>
    protected override IReadOnlyList<DeclaredOutput>? Outputs(AssetManifest asset, AnimPackParams p) =>
        p.Sets.Count == 0 || p.Sets.Keys.Any(s => !AnimImport.IsValidId(s))
            ? null
            : p.Sets.Keys.Select(s => DeclaredOutput.Directory($"{ClipLibrary.AnimDir}/{s}")).ToList();

    protected override void Process(BakeContext ctx, AnimPackParams p)
    {
        var rig = ctx.Ref<SkeletonParams>(p.Skeleton);
        string skeletonId = SkeletonProcessor.SkeletonId(p.Skeleton);
        string anims = ctx.InputDir(p.Anims);
        string? fingers = p.FingerSrc is null ? null : ctx.InputDir(p.FingerSrc);
        string? refPose = fingers is null ? null : ctx.InputFile(rig.RefPose);

        // кости экспорта — те же, что у эталона скелета: иначе клипы не сядут на меш персонажа
        var man = RawAnimIO.ReadManifest(anims);
        var reference = RawAnimIO.ReadManifest(Path.GetDirectoryName(ctx.InputFile(SkeletonProcessor.ManifestOf(rig.Clips)))!);
        if (!man.Bones.SequenceEqual(reference.Bones) || !man.Parents.SequenceEqual(reference.Parents))
            throw new InvalidDataException($"кости экспорта {p.Anims} не совпадают со скелетом {p.Skeleton} ({man.Bones.Length} против {reference.Bones.Length})");

        var setOf = AnimImport.Route(man, p.Sets);
        foreach (var set in p.Sets.Keys) ctx.OutputDir($"{ClipLibrary.AnimDir}/{set}");
        AnimImport.Run(anims, setOf, skeletonId, ctx.OutputRoot, ctx.Log, fingers, refPose);
    }
}
