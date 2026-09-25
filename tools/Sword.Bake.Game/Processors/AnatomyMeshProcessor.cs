using System.ComponentModel;
using Sword.Bake.Engine.Anim;
using Sword.Bake.Engine.Processors;
using Sword.Bake.Game.Anim;
using Sword.Engine.Formats;

namespace Sword.Bake.Game.Processors;

internal sealed class AnatomyMeshParams
{
    [Description("glTF анатомии на скелете пака (tools/ue/export_organs.py).")]
    [Source(SourceKind.File, Filter = "*.glb;*.gltf")]
    public string Mesh { get; set; } = "";
    [Description("Скелет и риг (единица anim.skeleton): кости эталонного экспорта и поза покоя, в которую переносится bind-поза источника.")]
    [AssetRef(SkeletonProcessor.ProcessorId)]
    public string Skeleton { get; set; } = "";
    [Description("Меш персонажа (единица skin.mesh): под его кожу прячется анатомия; пусто — не прятать.")]
    [AssetRef(SkinMeshProcessor.ProcessorId)]
    public string? Skin { get; set; }
    [Description("Насколько глубоко под кожей должна лежать анатомия, см.")]
    public float Margin { get; set; } = 0.8f;
    [Description("Меш в паке; пусто — <ID ассета>.swmesh.")]
    public string? Out { get; set; }
    [Description("Сантиметров в единице glTF: экспорт UE — 1.")]
    public float Unit { get; set; } = 1;
    [Description("Имена материалов по номеру материала glTF (экспортёр UE их не пишет); пусто — из glTF. Имя — это секция меша, по нему игра берёт материал.")]
    public List<string> Materials { get; set; } = [];
    [Description("Оставить только эти материалы; пусто — все. Из одного меша пака так выходят скелет и органы.")]
    public List<string> Keep { get; set; } = [];
    [Description("Статика на кость: имя кости рига (мозг — head); пусто — меш скинирован на скелет пака.")]
    public string? AttachBone { get; set; }
    [Description("Статика на кость: центр в системе персонажа в позе покоя, м (x влево, y вверх, z вперёд).")]
    public List<float> AttachCenter { get; set; } = [];
    [Description("Статика на кость: самая длинная сторона габаритов, м.")]
    public float AttachSize { get; set; }
    [Description("Бюджет треугольников: больше — меш прореживается схлопыванием рёбер (швы, края и секции не трогаются); 0 — как есть.")]
    public int MaxTriangles { get; set; }
}

/// <summary>
/// Анатомия на риге персонажа: скинированный меш пака (скелет, органы) переносится в bind-позу рига
/// (<see cref="AnatomyBake"/>) и кладётся в пак как обычный .swmesh — игра рисует его той же палитрой
/// и теми же плоскостями разрезов, что и кожу.
/// </summary>
internal sealed class AnatomyMeshProcessor : AssetProcessor<AnatomyMeshParams>
{
    public override string Id => "anatomy.mesh";
    public override string Title => "Анатомия (меш на риге персонажа)";
    public override IEnumerable<System.Reflection.Assembly> CodeAssemblies =>
        [GetType().Assembly, typeof(SkinBake).Assembly, typeof(SkinnedMeshData).Assembly];

    protected override AnatomyMeshParams Defaults(string assetId) => new();

    private static string OutPath(string assetId, AnatomyMeshParams p) => p.Out ?? assetId + ".swmesh";

    protected override IReadOnlyList<DeclaredOutput> Outputs(AssetManifest asset, AnatomyMeshParams p) => [DeclaredOutput.File(OutPath(asset.Id, p))];

    protected override void Process(BakeContext ctx, AnatomyMeshParams p)
    {
        AnatomyBake.Attach? attach = null;
        if (!string.IsNullOrEmpty(p.AttachBone))
        {
            if (p.AttachCenter.Count != 3 || p.AttachSize <= 0)
                throw new InvalidDataException("attachBone задан: нужны attachCenter [x, y, z] и attachSize > 0");
            attach = new AnatomyBake.Attach(p.AttachBone, new System.Numerics.Vector3(p.AttachCenter[0], p.AttachCenter[1], p.AttachCenter[2]), p.AttachSize);
        }
        var rig = ctx.Ref<SkeletonParams>(p.Skeleton);
        AnatomyBake.Run(ctx.InputFile(p.Mesh), ctx.InputDir(rig.Clips), ctx.InputFile(rig.RefPose),
            p.Skin is null ? null : ctx.InputFile(ctx.Ref<SkinMeshParams>(p.Skin).Mesh), ctx.Output(OutPath(ctx.Asset.Id, p)),
            p.Unit, p.Margin * 0.01f, ctx.Log,
            p.Materials.Count > 0 ? [.. p.Materials] : null, p.Keep.Count > 0 ? [.. p.Keep] : null, attach, p.MaxTriangles);
    }
}
