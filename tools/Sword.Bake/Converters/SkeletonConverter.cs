using System.ComponentModel;
using Sword.Bake.Anim;
using Sword.Engine.Animation;
using Sword.Engine.Formats;

namespace Sword.Bake.Converters;

/// <summary>Рецепт скелета — риг, на который ссылаются (поле rig) анимация, меш персонажа, оружие и анатомия (<see cref="Baker.Rig"/>).</summary>
public sealed class SkeletonParams
{
    [Description("ID скелета в библиотеке: файл skeletons/<id>.swskeleton; пусто — имя каталога skeleton.json.")]
    public string Id { get; set; } = "";
    [Description("Эталонный raw-экспорт на этом скелете: кости и родители, пространство UE, клипы, по которым считается хват оружия.")]
    [Source(directory: true)]
    public string Clips { get; set; } = "";
    [Description("Raw-экспорт исходного скелета тех же клипов — пальцы для хвата (ретаргет с потерей пястных костей).")]
    [Source(directory: true)]
    public string? FingerSrc { get; set; }
}

/// <summary>
/// Поза покоя <c>skeleton.json</c> + эталонный экспорт → библиотечный скелет <c>skeletons/&lt;id&gt;.swskeleton</c> (кости и
/// родители эталонного экспорта). Рецепт скелета описывает риг: на него ссылаются остальные конвертеры.
/// </summary>
internal sealed class SkeletonConverter : Converter<SkeletonParams>
{
    public const string ConverterId = "skeleton";

    public override string Id => ConverterId;
    public override string Title => "Скелет и риг персонажа";
    public override string Kind => "skeleton.json (поза покоя)";

    public override bool Accepts(string file) => Path.GetFileName(file).Equals("skeleton.json", StringComparison.OrdinalIgnoreCase);

    /// <summary>ID скелета: из рецепта или по имени каталога позы покоя.</summary>
    public static string SkeletonId(SkeletonParams p, string source)
    {
        string id = p.Id.Length > 0 ? p.Id : Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(source))!).ToLowerInvariant();
        return AnimImport.IsValidId(id) ? id : throw new ArgumentException($"скелет «{id}»: только a-z, 0-9, _ . -");
    }

    protected override void Bake(BakeJob job, SkeletonParams p)
    {
        string id = SkeletonId(p, job.Source);
        var man = RawAnimIO.ReadManifest(job.InputDir(p.Clips));
        RawAnimIO.RequireFullSkeleton(man);
        var skeleton = new SkeletonFile { Id = id, JointNames = man.Bones, JointParents = man.Parents };
        ContentFiles.Write(job.Output(ClipLibrary.SkeletonFile(id)), skeleton.Write);
        job.Log.WriteLine($"скелет {id}: {man.Bones.Length} суставов, хеш {skeleton.Hash:X8}");
    }
}

internal sealed class CharacterMeshParams
{
    [Description("Рецепт скелета (его skeleton.json): поза покоя и кости.")]
    [Source(Filter = "skeleton.json")]
    public string Rig { get; set; } = "";
    [Description("Меш в Content/; пусто — meshes/characters/<имя файла>.swmesh.")]
    public string? Out { get; set; }
}

/// <summary>Скелетный меш персонажа glTF → <c>meshes/characters/&lt;имя&gt;.swmesh</c> (<see cref="SkinBake.Mesh"/>): bind-поза — поза покоя рига.</summary>
internal sealed class CharacterMeshConverter : Converter<CharacterMeshParams>
{
    public override string Id => "character";
    public override string Title => "Меш персонажа на риге";
    public override string Kind => "*.glb, *.gltf";

    public override bool Accepts(string file) => HasExtension(file, ".glb", ".gltf");

    protected override CharacterMeshParams Default(BakeJob job) => new() { Rig = job.Baker.DefaultRig() };

    protected override void Bake(BakeJob job, CharacterMeshParams p)
    {
        var rig = job.Baker.Rig(p.Rig);
        SkinBake.Mesh(job.Source, rig.RefPose, rig.Clips, job.Output(p.Out ?? $"meshes/characters/{NameOf(job.Source)}.swmesh"), job.Log);
    }
}
