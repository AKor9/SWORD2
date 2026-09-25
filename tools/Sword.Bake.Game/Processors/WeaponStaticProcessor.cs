using System.ComponentModel;
using Sword.Bake.Engine.Anim;
using Sword.Bake.Engine.Processors;
using Sword.Bake.Game.Anim;
using Sword.Bake.Game.Preview;
using Sword.Engine.Formats;
using Sword.Game.Data;

namespace Sword.Bake.Game.Processors;

internal sealed class WeaponStaticParams
{
    [Description("glTF статического меша оружия (tools/ue/export_weapons.py).")]
    [Source(SourceKind.File, Filter = "*.glb;*.gltf")]
    public string Mesh { get; set; } = "";
    [Description("Скелет и риг (единица anim.skeleton): оружие крепится по кулаку его эталонных клипов с перенесёнными пальцами.")]
    [AssetRef(SkeletonProcessor.ProcessorId)]
    public string Skeleton { get; set; } = "";
    [Description("Меш в паке; пусто — <ID ассета>.swmesh (игра берёт оружие из weapons/), зоны крови — рядом, .swblood.")]
    public string? Out { get; set; }
    [Description("Середина хвата, см от навершия; пусто — посередине рукояти.")]
    public float? Grip { get; set; }
    [Description("Сантиметров в единице glTF: экспорт UE — 1.")]
    public float Unit { get; set; } = 1;
    [Description("Зоны крови: форма, границы рукояти и лезвия, слоты материалов → зона.")]
    public WeaponBloodParams Blood { get; set; } = new();
}

/// <summary>Клинковое оружие: статический меш, приведённый к системе меча и закреплённый в кулаке (<see cref="WeaponBake"/>).</summary>
internal sealed class WeaponStaticProcessor : AssetProcessor<WeaponStaticParams>, IAssetPreview
{
    /// <summary>Меш оружия, окрашенный по зонам крови (.swblood), контуры зон и главная ось (<see cref="WeaponPreview"/>).</summary>
    public PreviewScene? Preview(Sword.Engine.Content.IContentSource content, IReadOnlyList<string> paths, int mode) =>
        WeaponPreview.Build(content, paths, mode);

    public override string Id => "weapon.static";
    public override string Title => "Оружие (статический меш в кисти)";
    public override IEnumerable<System.Reflection.Assembly> CodeAssemblies =>
        [GetType().Assembly, typeof(SkinBake).Assembly, typeof(SkinnedMeshData).Assembly, typeof(WeaponBloodData).Assembly];

    protected override WeaponStaticParams Defaults(string assetId) => new();

    /// <summary>Меш и зоны крови рядом с ним: weapons/&lt;имя&gt;.swmesh и .swblood.</summary>
    private static (string Mesh, string Blood) OutPaths(string assetId, WeaponStaticParams p)
    {
        string mesh = p.Out ?? assetId + ".swmesh";
        return (mesh, Path.ChangeExtension(mesh, WeaponBloodData.Extension).Replace('\\', '/'));
    }

    protected override IReadOnlyList<DeclaredOutput> Outputs(AssetManifest asset, WeaponStaticParams p)
    {
        var (mesh, blood) = OutPaths(asset.Id, p);
        return [DeclaredOutput.File(mesh), DeclaredOutput.File(blood)];
    }

    protected override void Process(BakeContext ctx, WeaponStaticParams p)
    {
        var (mesh, blood) = OutPaths(ctx.Asset.Id, p);
        var rig = ctx.Ref<SkeletonParams>(p.Skeleton);
        WeaponBake.Run(ctx.InputFile(p.Mesh), ctx.InputDir(rig.Clips), ctx.InputFile(rig.RefPose),
            rig.FingerSrc is null ? null : ctx.InputDir(rig.FingerSrc), ctx.Output(mesh), p.Grip, p.Unit, p.Blood,
            ctx.Output(blood), ctx.Log);
    }
}
