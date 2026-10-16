using System.ComponentModel;
using Sword.Bake.Anim;

namespace Sword.Bake.Converters;

internal sealed class AnatomyParams
{
    [Description("Рецепт скелета (его skeleton.json): кости эталонного экспорта и поза покоя, в которую переносится bind-поза источника.")]
    [Source(Filter = "skeleton.json")]
    public string Rig { get; set; } = "";
    [Description("glTF меша персонажа: под его кожу прячется анатомия; пусто — не прятать.")]
    [Source(Filter = "*.glb;*.gltf")]
    public string? Skin { get; set; }
    [Description("Насколько глубоко под кожей должна лежать анатомия, см.")]
    public float Margin { get; set; } = 0.8f;
    [Description("Меш в Content/; пусто — meshes/characters/anatomy/<имя файла>.swmesh.")]
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
/// Анатомия glTF на риге персонажа: скинированный меш (скелет, органы) переносится в bind-позу рига (<see cref="AnatomyBake"/>)
/// и пишется обычным .swmesh — игра рисует его той же палитрой и теми же плоскостями разрезов, что и кожу.
/// </summary>
internal sealed class AnatomyConverter : Converter<AnatomyParams>
{
    public override string Id => "anatomy";
    public override string Title => "Анатомия (меш на риге персонажа)";
    public override string Kind => "*.glb, *.gltf";

    public override bool Accepts(string file) => HasExtension(file, ".glb", ".gltf");

    protected override AnatomyParams Default(BakeJob job) => new() { Rig = job.Baker.DefaultRig() };

    protected override void Bake(BakeJob job, AnatomyParams p)
    {
        AnatomyBake.Attach? attach = null;
        if (!string.IsNullOrEmpty(p.AttachBone))
        {
            if (p.AttachCenter.Count != 3 || p.AttachSize <= 0)
                throw new InvalidDataException("attachBone задан: нужны attachCenter [x, y, z] и attachSize > 0");
            attach = new AnatomyBake.Attach(p.AttachBone, new System.Numerics.Vector3(p.AttachCenter[0], p.AttachCenter[1], p.AttachCenter[2]), p.AttachSize);
        }
        var rig = job.Baker.Rig(p.Rig);
        AnatomyBake.Run(job.Source, rig.Clips, rig.RefPose, p.Skin is null ? null : job.Input(p.Skin),
            job.Output(p.Out ?? $"meshes/characters/anatomy/{NameOf(job.Source)}.swmesh"), p.Unit, p.Margin * 0.01f, job.Log,
            p.Materials.Count > 0 ? [.. p.Materials] : null, p.Keep.Count > 0 ? [.. p.Keep] : null, attach, p.MaxTriangles);
    }
}
