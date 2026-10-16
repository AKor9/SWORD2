using System.ComponentModel;
using Sword.Bake.Anim;
using Sword.Engine.Animation;

namespace Sword.Bake.Converters;

internal sealed class AnimParams
{
    [Description("Рецепт скелета (его skeleton.json): ID скелета клипов, эталонные кости, поза покоя для переноса пальцев.")]
    [Source(Filter = "skeleton.json")]
    public string Rig { get; set; } = "";
    [Description("Raw-экспорт исходного скелета тех же клипов для переноса пальцев (ретаргет с потерей пястных костей); пусто — без переноса.")]
    [Source(directory: true)]
    public string? FingerSrc { get; set; }
    [Description("Наборы библиотеки: набор → маски папок пути UE клипа (* и ?); пустой список — все клипы. Каждый клип — ровно в один набор; " +
                 "ID клипа в игре — «набор/имя».")]
    public Dictionary<string, List<string>> Sets { get; set; } = [];
}

/// <summary>
/// Выгрузка анимации (manifest.json + clips/ raw-экспорта Unreal) → все клипы набора: <c>animations/&lt;набор&gt;/*.swclip</c>
/// (<see cref="AnimImport"/>). В один набор могут класть клипы несколько выгрузок; лишние клипы выключаются в менеджере ресурсов.
/// </summary>
internal sealed class AnimConverter : Converter<AnimParams>
{
    public override string Id => "anim";
    public override string Title => "Клипы выгрузки → наборы анимации";
    public override string Kind => "manifest.json выгрузки (рядом clips/)";

    public override bool Accepts(string file) =>
        Path.GetFileName(file).Equals("manifest.json", StringComparison.OrdinalIgnoreCase)
        && Directory.Exists(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file))!, "clips"));

    /// <summary>Один набор по имени каталога выгрузки («skin_gasp» → «gasp»).</summary>
    protected override AnimParams Default(BakeJob job)
    {
        string name = Path.GetFileName(Path.GetDirectoryName(job.Source)!).ToLowerInvariant();
        if (name.StartsWith("skin_", StringComparison.Ordinal)) name = name[5..];
        return new AnimParams { Rig = job.Baker.DefaultRig(), Sets = new() { [AnimImport.IsValidId(name) ? name : "clips"] = [] } };
    }

    protected override void Bake(BakeJob job, AnimParams p)
    {
        var rig = job.Baker.Rig(p.Rig);
        string anims = Path.GetDirectoryName(job.Source)!;
        string? fingers = p.FingerSrc is null ? null : job.InputDir(p.FingerSrc);

        // кости выгрузки — те же, что у эталона скелета: иначе клипы не сядут на меш персонажа
        var man = RawAnimIO.ReadManifest(anims);
        var reference = RawAnimIO.ReadManifest(rig.Clips);
        if (!man.Bones.SequenceEqual(reference.Bones) || !man.Parents.SequenceEqual(reference.Parents))
            throw new InvalidDataException($"кости выгрузки {anims} не совпадают со скелетом {rig.Id} ({man.Bones.Length} против {reference.Bones.Length})");

        var setOf = AnimImport.Route(man, p.Sets);
        AnimImport.Run(anims, setOf, rig.Id, (set, clip) => job.Output($"{ClipLibrary.AnimDir}/{set}/{clip}{ClipLibrary.ClipExt}"),
            job.Log, fingers, fingers is null ? null : rig.RefPose);
    }
}
