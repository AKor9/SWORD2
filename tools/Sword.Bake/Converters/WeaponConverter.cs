using System.ComponentModel;
using Sword.Bake.Anim;
using Sword.Game.Data;

namespace Sword.Bake.Converters;

internal sealed class WeaponParams
{
    [Description("Рецепт скелета (его skeleton.json): оружие крепится по кулаку его эталонных клипов с перенесёнными пальцами.")]
    [Source(Filter = "skeleton.json")]
    public string Rig { get; set; } = "";
    [Description("Имя оружия: меш meshes/weapons/<имя>.swmesh, зоны крови data/weapons/<имя>.swblood.")]
    public string Name { get; set; } = "";
    [Description("Середина хвата, см от навершия; пусто — посередине рукояти.")]
    public float? Grip { get; set; }
    [Description("Сантиметров в единице glTF: экспорт UE — 1.")]
    public float Unit { get; set; } = 1;
    [Description("Секции по материалам glTF (имя материала — имя секции): игра красит части оружия по имени; нет — один цвет на всё оружие.")]
    public bool Sections { get; set; }
    [Description("Зоны крови: форма, границы рукояти и лезвия, слоты материалов → зона.")]
    public WeaponBloodParams Blood { get; set; } = new();
}

/// <summary>
/// Статический меш клинкового оружия glTF → меш в системе меча, закреплённый в кулаке (<see cref="WeaponBake"/>):
/// <c>meshes/weapons/&lt;имя&gt;.swmesh</c> и зоны крови <c>data/weapons/&lt;имя&gt;.swblood</c>.
/// </summary>
internal sealed class WeaponConverter : Converter<WeaponParams>
{
    public const string BloodDir = "data/weapons";

    public override string Id => "weapon";
    public override string Title => "Оружие (статический меш в кисти)";
    public override string Kind => "*.glb, *.gltf";

    public override bool Accepts(string file) => HasExtension(file, ".glb", ".gltf");

    protected override WeaponParams Default(BakeJob job)
    {
        string name = NameOf(job.Source);
        return new WeaponParams { Rig = job.Baker.DefaultRig(), Name = name.StartsWith("sm_", StringComparison.Ordinal) ? name[3..] : name };
    }

    protected override void Bake(BakeJob job, WeaponParams p)
    {
        if (!AnimImport.IsValidId(p.Name)) throw new ArgumentException($"name «{p.Name}»: только a-z, 0-9, _ . -");
        var rig = job.Baker.Rig(p.Rig);
        WeaponBake.Run(job.Source, rig.Clips, rig.RefPose, rig.FingerSrc, job.Output($"{WeaponMesh.Dir}/{p.Name}{WeaponMesh.Ext}"), p.Grip, p.Unit, p.Sections,
            p.Blood, job.Output($"{BloodDir}/{p.Name}{WeaponBloodData.Extension}"), job.Log);
    }
}
