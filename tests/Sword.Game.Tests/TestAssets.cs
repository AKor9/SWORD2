using System.Numerics;
using Sword.Engine.Content;
using Sword.Engine.Formats;
using Sword.Engine.Runtime;
using Sword.Game.Animation;
using Sword.Game.Bootstrap;
using Sword.Game.Contracts;

namespace Sword.Game.Tests;

/// <summary>
/// Контент тестов игры в памяти: скелет «ue5_manny» из синтетических суставов, меш и клипы по именам пака — стойка, ходьба и
/// бег в 8 сторон (<see cref="WalkSpeed"/>, <see cref="RunSpeed"/> м/с), поворот на месте. У каждого клипа своя поза (номер
/// клипа — в наклоне бедра), чтобы по позе было видно, какой клип играет.
/// </summary>
public static class TestAssets
{
    /// <summary>Суставы: ноги, позвоночник, ключицы, в конце — суставы капсул тела (номера прежних не сдвинуты).</summary>
    public static readonly string[] Joints =
    [
        "root", "pelvis", "thigh_l", "calf_l", "foot_l", "ball_l", "thigh_r", "calf_r", "foot_r", "ball_r", "spine_05", "clavicle_l", "clavicle_r",
        "spine_01", "neck_01", "head", "upperarm_l", "lowerarm_l", "hand_l", "upperarm_r", "lowerarm_r", "hand_r",
    ];
    public static readonly int[] Parents = [-1, 0, 1, 2, 3, 4, 1, 6, 7, 8, 1, 10, 10, 1, 10, 14, 11, 16, 17, 12, 19, 20];
    public static readonly Vector3[] Offsets =
    [
        Vector3.Zero, new(0, 0.95f, 0), new(0.1f, -0.05f, 0), new(0, -0.43f, 0), new(0, -0.42f, 0), new(0, -0.05f, 0.12f),
        new(-0.1f, -0.05f, 0), new(0, -0.43f, 0), new(0, -0.42f, 0), new(0, -0.05f, 0.12f), new(0, 0.45f, 0), new(0.15f, 0.1f, 0),
        new(-0.15f, 0.1f, 0),
        new(0, 0.1f, 0), new(0, 0.2f, 0), new(0, 0.1f, 0.02f), new(0.05f, -0.05f, 0), new(0.05f, -0.28f, 0), new(0, -0.26f, 0.02f),
        new(-0.05f, -0.05f, 0), new(-0.05f, -0.28f, 0), new(0, -0.26f, 0.02f),
    ];

    public const float WalkSpeed = 1.5f, RunSpeed = 4f, Rate = 30;
    public const int Frames = 31;

    /// <summary>Направление стороны d в системе персонажа: 0 — вперёд (+Z), 2 — вправо (−X), по часовой стрелке.</summary>
    public static Vector3 Dir(int d)
    {
        float a = d * MathF.PI / 4;
        return new Vector3(-MathF.Sin(a), 0, MathF.Cos(a));
    }

    /// <summary>Наклон бедра клипа — его «подпись» в позе.</summary>
    public static float Signature(int clipNumber) => 0.05f * (clipNumber + 1);

    /// <summary>
    /// Поворот клипов поворота на месте за весь клип, рад (+ — влево): 90° влево, вправо и разворот, и их сдвиг
    /// корня вперёд, м (у клипов пака поворот тоже чуть смещает корень).
    /// </summary>
    public const float TurnLeftYaw = MathF.PI / 2, TurnRightYaw = -MathF.PI / 2, Turn180Yaw = MathF.PI, TurnDrift = 0.1f;

    /// <summary>Клип поворота на месте: не цикл, yaw корня растёт сглаженной ступенью до <paramref name="yaw"/>, корень чуть вперёд.</summary>
    private static ClipFile TurnClip(int number, float yaw)
    {
        var clip = Clip(number, Vector3.Zero);
        clip.Loop = false;
        for (int f = 0; f < Frames; f++)
        {
            float k = f / (float)(Frames - 1);
            k = k * k * (3 - 2 * k);
            clip.RootYaw[f] = yaw * k;
            clip.RootMotion[f] = new Vector3(0, 0, TurnDrift * k);
        }
        return clip;
    }

    /// <summary>Оси взмаха плеча ударов slash1–4: разные направления замаха.</summary>
    private static readonly Vector3[] SlashAxes = [Vector3.UnitZ, Vector3.UnitX, -Vector3.UnitZ, -Vector3.UnitX];

    /// <summary>Скорость корня клипов уклонения, м/с (1,5 м за клип).</summary>
    public const float EvadeSpeed = 1.5f;

    /// <summary>Правое плечо (<c>upperarm_r</c>) и взмах удара: 1,2 рад за кадры 6…12 (0,2–0,4 с) сглаженной ступенью.</summary>
    public const int SwingJoint = 19, SwingFrom = 6, SwingTo = 12;
    public const float SwingAngle = 1.2f;

    /// <summary>Клип удара: не цикл, плечо взмахивает вокруг <paramref name="axis"/> — кончик клинка быстрее 6 м/с (окно урона).</summary>
    private static ClipFile SwingClip(int number, Vector3 axis)
    {
        var clip = Clip(number, Vector3.Zero);
        clip.Loop = false;
        int jc = Joints.Length;
        for (int f = 0; f < Frames; f++)
        {
            float k = Math.Clamp((f - SwingFrom) / (float)(SwingTo - SwingFrom), 0, 1);
            clip.LocalRot[f * jc + SwingJoint] = Quaternion.CreateFromAxisAngle(axis, SwingAngle * k * k * (3 - 2 * k));
        }
        return clip;
    }

    /// <summary>Сустав корпуса (<c>spine_05</c>, маска верха 1), которым отклоняется реакция на попадание.</summary>
    public const int HitJoint = 10;

    /// <summary>
    /// Клип реакции на попадание: не цикл, корпус (<see cref="HitJoint"/>) отклоняется на 0,5 рад к кадру 6 и возвращается, корень
    /// отходит на 0,3 м (<paramref name="back"/> = −1 — назад, 1 — вперёд: удар в спину толкает вперёд).
    /// </summary>
    private static ClipFile HitClip(int number, float back)
    {
        var clip = Clip(number, Vector3.Zero);
        clip.Loop = false;
        int jc = Joints.Length;
        for (int f = 0; f < Frames; f++)
        {
            float k = f <= 6 ? f / 6f : Math.Max(0, 1 - (f - 6) / 18f);
            clip.LocalRot[f * jc + HitJoint] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.5f * back * k);
            clip.RootMotion[f] = new Vector3(0, 0, 0.3f * back * Math.Min(1, f / 12f));
        }
        return clip;
    }

    private static ClipFile Clip(int number, Vector3 velocity)
    {
        uint hash = SkeletonFile.ComputeHash(Joints, Parents);
        int jc = Joints.Length;
        var rot = new Quaternion[Frames * jc];
        var pos = new Vector3[Frames * jc];
        for (int f = 0; f < Frames; f++)
            for (int j = 0; j < jc; j++)
            {
                float swing = j == 2 ? Signature(number) + 0.3f * MathF.Sin(MathF.Tau * f / (Frames - 1)) : 0;
                rot[f * jc + j] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, swing);
                pos[f * jc + j] = Offsets[j];
            }
        return new ClipFile
        {
            Skeleton = "ue5_manny", SkeletonHash = hash, SampleRate = Rate, Frames = Frames, Joints = jc, Loop = true,
            LocalRot = rot, LocalPos = pos,
            RootMotion = Enumerable.Range(0, Frames).Select(f => velocity * (f / Rate)).ToArray(),
            RootYaw = new float[Frames],
        };
    }

    /// <summary>Контент: скелет, меш, клипы, мечи (если <paramref name="weapons"/>); <paramref name="actionsJson"/> — data/combat/actions.json.</summary>
    public static MemorySource Content(string? actionsJson = null, bool weapons = true)
    {
        var content = new MemorySource("тест");
        content.Add("skeletons/ue5_manny.swskeleton", new SkeletonFile { Id = "ue5_manny", JointNames = Joints, JointParents = Parents }.Write);
        var model = new Matrix4x4[Joints.Length];
        for (int j = 0; j < Joints.Length; j++)
            model[j] = Parents[j] < 0 ? Matrix4x4.CreateTranslation(Offsets[j]) : Matrix4x4.CreateTranslation(Offsets[j]) * model[Parents[j]];
        content.Add(CharacterAssets.MeshFile, new SkinnedMeshData
        {
            Vertices = [new SkinVertex(), new SkinVertex(), new SkinVertex()], Indices = [0, 1, 2],
            JointNames = Joints, JointParents = Parents,
            InverseBind = model.Select(m => Matrix4x4.Invert(m, out var inv) ? inv : throw new InvalidOperationException()).ToArray(),
        }.Write);
        int n = 0;
        content.Add("animations/sword_1h/1HM_Idle_Standing.swclip", Clip(n++, Vector3.Zero).Write);
        string[] dirs = ["Forward", "ForwardRight", "Right", "BackRight", "Back", "BackLeft", "Left", "ForwardLeft"];
        for (int d = 0; d < 8; d++) content.Add($"animations/sword_1h/1HM_Walk{dirs[d]}.swclip", Clip(n++, Dir(d) * WalkSpeed).Write);
        for (int d = 0; d < 8; d++) content.Add($"animations/sword_1h/1HM_Run{dirs[d]}.swclip", Clip(n++, Dir(d) * RunSpeed).Write);
        content.Add("animations/sword_1h/1HM_TurnLeft90.swclip", TurnClip(n++, TurnLeftYaw).Write);
        content.Add("animations/sword_1h/1HM_TurnRight90.swclip", TurnClip(n++, TurnRightYaw).Write);
        content.Add("animations/sword_1h/1HM_Turn180.swclip", TurnClip(n++, Turn180Yaw).Write);
        // Бой: удары slash1–4 и уколы stab1–3 — взмах плеча (у удара есть окно урона), уклонения в 4 стороны. Свой источник —
        // после «sword_1h» по алфавиту: номера прежних клипов в библиотеке не сдвигаются.
        for (int k = 0; k < 4; k++) content.Add($"animations/test_combat/1HM_Slash0{k + 1}.swclip", SwingClip(n++, SlashAxes[k]).Write);
        for (int k = 0; k < 3; k++) content.Add($"animations/test_combat/1HM_Stab0{k + 1}.swclip", SwingClip(n++, Vector3.UnitX).Write);
        string[] evades = ["Forward", "Back", "Left", "Right"];
        Vector3[] evadeDirs = [Dir(0), Dir(4), Dir(6), Dir(2)];
        for (int k = 0; k < 4; k++)
        {
            var evade = Clip(n++, evadeDirs[k] * EvadeSpeed);
            evade.Loop = false;
            content.Add($"animations/test_combat/1HM_Evade_{evades[k]}.swclip", evade.Write);
        }
        // Реакции на попадание: корпус (spine_05) и плечо отклоняются назад — у вздрагивания игрока есть что добавить к верху тела,
        // у реакции врага — сдвиг корня назад. Свой источник — после «test_combat»: номера прежних клипов не сдвигаются.
        string[] hits = ["1HM_HitReact01", "1HM_HitReact02", "1HM_HitReact_Behind01", "Anim_PS_Hit_Front_Big_RM"];
        for (int k = 0; k < hits.Length; k++) content.Add($"animations/test_hit/{hits[k]}.swclip", HitClip(n++, k == 2 ? 1 : -1).Write);
        if (actionsJson is not null) content.Add("data/combat/actions.json", System.Text.Encoding.UTF8.GetBytes(actionsJson));
        // Оружие: два статических меша в системе кисти — по алфавиту katana (0), sword_1h (1, меч по умолчанию).
        if (!weapons) return content;
        content.Add("meshes/weapons/sword_1h.swmesh", Weapon(SwordTip).Write);
        content.Add("meshes/weapons/katana.swmesh", Weapon(KatanaTip).Write);
        return content;
    }

    /// <summary>Кончики клинков тестовых мечей в системе кисти: самая дальняя вершина меша.</summary>
    public static readonly Vector3 SwordTip = new(-0.2f, 1.0f, -0.1f), KatanaTip = new(-0.1f, 0.9f, 0);

    /// <summary>Статический меш меча: гарда у кисти и клинок до <paramref name="tip"/> (вершины — ближе кончика).</summary>
    public static SkinnedMeshData Weapon(Vector3 tip) => new()
    {
        Vertices =
        [
            new SkinVertex { Position = new Vector3(0.05f, 0.1f, 0) }, new SkinVertex { Position = new Vector3(-0.05f, 0.1f, 0) },
            new SkinVertex { Position = tip * 0.5f }, new SkinVertex { Position = tip },
        ],
        Indices = [0, 1, 2, 1, 3, 2],
    };

    private static CharacterAssets? _shared;

    /// <summary>Ассеты тестов (только чтение — их можно делить между симуляциями).</summary>
    public static CharacterAssets Assets => _shared ??= CharacterAssets.Load(Content());

    /// <summary>Симуляция игры на тестовом контенте — та же композиция, что у Sword.exe и Sword.Headless.</summary>
    public static Simulation Simulation(ConfigSet? config = null) => SwordSimulation.Create(config ?? ConfigSet.Empty(), Assets);

    /// <summary>Симуляция и ресурс-класс её мира (пул поз, хранилище подсистемы), который берёт модуль-зонд при сборке мира.</summary>
    public static (Simulation Sim, T Resource) WithResource<T>(ConfigSet config, CharacterAssets? assets = null) where T : class
    {
        var probe = new ResourceProbe<T>();
        var sim = Build(config, assets ?? Assets, probe);
        return (sim, probe.Resource!);
    }

    /// <summary>Симуляция игры на тестовом контенте с модулями теста (зонды) сверх модулей игры.</summary>
    public static Simulation WithModules(ConfigSet config, params IModule[] extra) => Build(config, Assets, extra);

    private static Simulation Build(ConfigSet config, CharacterAssets assets, params IModule[] extra) =>
        Engine.Runtime.Simulation.Build(config, GameStages.All, b => GameCore.Configure(b, assets),
            [.. SwordSimulation.Modules(), .. extra], SwordSimulation.ClientModuleIds);

    private sealed class ResourceProbe<T> : IModule where T : class
    {
        public T? Resource;

        public string Id => "resource_probe";

        public void Configure(SimulationBuilder b) => b.OnWorldBuilt(w => Resource = w.Resource<T>());
    }
}
