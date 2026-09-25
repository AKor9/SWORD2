using System.ComponentModel;
using Sword.Bake.Engine.Anim;
using Sword.Bake.Engine.Preview;
using Sword.Engine.Animation;
using Sword.Engine.Formats;

namespace Sword.Bake.Engine.Processors;

/// <summary>Параметры рига (единица anim.skeleton) — их читают по ссылке меш, наборы клипов, оружие и анатомия (<c>ctx.Ref</c>).</summary>
public sealed class SkeletonParams
{
    [Description("Эталонный raw-экспорт на этом скелете: кости и родители, пространство UE, клипы, по которым считается хват оружия.")]
    [Source(SourceKind.Directory)]
    public string Clips { get; set; } = "";
    [Description("skeleton.json с позой покоя скелета: bind-поза мешей и перенос пальцев.")]
    [Source(SourceKind.File, Filter = "*.json")]
    public string RefPose { get; set; } = "";
    [Description("Raw-экспорт исходного скелета тех же клипов — пальцы для хвата (ретаргет с потерей пястных костей).")]
    [Source(SourceKind.Directory)]
    public string? FingerSrc { get; set; }
}

/// <summary>
/// Скелет и риг персонажа: единица <c>skeletons/&lt;id&gt;</c> пишет библиотечный скелет <c>skeletons/&lt;id&gt;.swskeleton</c>
/// (кости и родители эталонного экспорта) и описывает риг, на который ссылаются остальные (<see cref="AssetRefAttribute"/>):
/// поза покоя, эталонные клипы и источник пальцев. Меш персонажа, наборы клипов, оружие и анатомия берут их отсюда,
/// а не повторяют в каждом манифесте.
/// </summary>
public sealed class SkeletonProcessor : AssetProcessor<SkeletonParams>, IAssetPreview
{
    /// <summary>Меш персонажа на этом скелете в позе покоя и кости; клипов нет (<see cref="AnimPreview"/>).</summary>
    public PreviewScene? Preview(Sword.Engine.Content.IContentSource content, IReadOnlyList<string> paths, int mode) =>
        AnimPreview.Build(content, paths, mode);

    public const string ProcessorId = "anim.skeleton";

    public override string Id => ProcessorId;
    public override string Title => "Скелет и риг персонажа";
    public override IEnumerable<System.Reflection.Assembly> CodeAssemblies => [GetType().Assembly, typeof(SkeletonFile).Assembly];

    /// <summary>ID скелета в библиотеке — последний сегмент ID единицы; сама единица — в папке skeletons/.</summary>
    public static string SkeletonId(string assetId)
    {
        if (!assetId.StartsWith(ClipLibrary.SkeletonDir + "/", StringComparison.Ordinal) || assetId.Count(ch => ch == '/') != 1)
            throw new ArgumentException($"скелет {assetId}: манифест должен лежать в {ClipLibrary.SkeletonDir}/ — игра ищет {ClipLibrary.SkeletonDir}/<id>{ClipLibrary.SkeletonExt}");
        string id = LastSegment(assetId);
        return AnimImport.IsValidId(id) ? id : throw new ArgumentException($"скелет «{id}»: только a-z, 0-9, _ . -");
    }

    /// <summary>Каталог и манифест эталонного экспорта: читается только manifest.json — кости, родители, пространство.</summary>
    public static string ManifestOf(string clipsDir) => clipsDir.TrimEnd('/', '\\') + "/manifest.json";

    protected override IReadOnlyList<DeclaredOutput> Outputs(AssetManifest asset, SkeletonParams p) =>
        [DeclaredOutput.File(asset.Id + ClipLibrary.SkeletonExt)];

    protected override void Process(BakeContext ctx, SkeletonParams p)
    {
        string id = SkeletonId(ctx.Asset.Id);
        string clips = Path.GetDirectoryName(ctx.InputFile(ManifestOf(p.Clips)))!;
        var man = RawAnimIO.ReadManifest(clips);
        RawAnimIO.RequireFullSkeleton(man);
        var skeleton = new SkeletonFile { Id = id, JointNames = man.Bones, JointParents = man.Parents };
        ContentFiles.Write(ctx.Output(ctx.Asset.Id + ClipLibrary.SkeletonExt), skeleton.Write);
        ctx.Log.WriteLine($"скелет {id}: {man.Bones.Length} суставов, хеш {skeleton.Hash:X8}");
    }
}

/// <summary>Параметры меша персонажа (единица skin.mesh) — анатомия (Б5) читает по ссылке путь меша, под чью кожу прячется.</summary>
public sealed class SkinMeshParams
{
    [Description("Скелет и риг (единица anim.skeleton): поза покоя и кости.")]
    [AssetRef(SkeletonProcessor.ProcessorId)]
    public string Skeleton { get; set; } = "";
    [Description("glTF скелетного меша персонажа (tools/ue/export_meshes.py).")]
    [Source(SourceKind.File, Filter = "*.glb;*.gltf")]
    public string Mesh { get; set; } = "";
    [Description("Меш в паке; пусто — <ID ассета>.swmesh.")]
    public string? Out { get; set; }
}

/// <summary>Скелетный меш персонажа (<see cref="SkinBake.Mesh"/>): bind-поза — поза покоя рига, суставы — кости его эталонного экспорта.</summary>
public sealed class SkinMeshProcessor : AssetProcessor<SkinMeshParams>, IAssetPreview
{
    /// <summary>Меш по секциям и сплошной (<see cref="MeshPreview"/>).</summary>
    public PreviewScene? Preview(Sword.Engine.Content.IContentSource content, IReadOnlyList<string> paths, int mode) =>
        MeshPreview.Build(content, paths, mode, withSkin: false);

    public const string ProcessorId = "skin.mesh";

    public override string Id => ProcessorId;
    public override string Title => "Меш персонажа на риге";
    public override IEnumerable<System.Reflection.Assembly> CodeAssemblies => [GetType().Assembly, typeof(SkinnedMeshData).Assembly];

    private static string OutPath(string assetId, SkinMeshParams p) => p.Out ?? assetId + ".swmesh";

    protected override IReadOnlyList<DeclaredOutput> Outputs(AssetManifest asset, SkinMeshParams p) => [DeclaredOutput.File(OutPath(asset.Id, p))];

    protected override void Process(BakeContext ctx, SkinMeshParams p)
    {
        var rig = ctx.Ref<SkeletonParams>(p.Skeleton);
        // от экспорта мешу нужны только кости и пространство — manifest.json, клипы не входы
        string clips = Path.GetDirectoryName(ctx.InputFile(SkeletonProcessor.ManifestOf(rig.Clips)))!;
        SkinBake.Mesh(ctx.InputFile(p.Mesh), ctx.InputFile(rig.RefPose), clips, ctx.Output(OutPath(ctx.Asset.Id, p)), ctx.Log);
    }
}
