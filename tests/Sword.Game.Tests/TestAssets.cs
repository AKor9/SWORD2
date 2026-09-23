using System.Numerics;
using Sword.Engine.Content;
using Sword.Engine.Formats;
using Sword.Engine.Runtime;
using Sword.Game.Animation;
using Sword.Game.Bootstrap;

namespace Sword.Game.Tests;

/// <summary>
/// Контент тестов игры в памяти: скелет «ue5_manny» из синтетических суставов (<c>SynthSkeleton</c> тестов SWORD), меш с
/// его bind-матрицами и клипы по именам пака по умолчанию: локомоция — стойка, ходьба и бег в 8 сторон с корневым
/// движением <see cref="WalkSpeed"/> и <see cref="RunSpeed"/> м/с, и поворот на месте (90° влево, вправо, разворот; 4г).
/// У каждого клипа своя поза (номер клипа — в наклоне бедра), чтобы по позе было видно, какой клип играет.
/// </summary>
public static class TestAssets
{
    /// <summary>
    /// Суставы: ноги, позвоночник и ключицы (этапы 2–4) и в конце — суставы капсул тела (5б: <c>spine_01</c>, шея, голова,
    /// руки; номера прежних суставов не сдвинуты).
    /// </summary>
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
    /// Поворот клипов поворота на месте за весь клип, рад (+ — влево): 90° влево, вправо и разворот (точка 4г), и их сдвиг
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

    /// <summary>Контент: скелет, меш, клипы; <paramref name="actionsJson"/> — combat/actions.json (null — нет файла).</summary>
    public static MemorySource Content(string? actionsJson = null)
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
        content.Add("anim/sword_1h/1HM_Idle_Standing.swclip", Clip(n++, Vector3.Zero).Write);
        string[] dirs = ["Forward", "ForwardRight", "Right", "BackRight", "Back", "BackLeft", "Left", "ForwardLeft"];
        for (int d = 0; d < 8; d++) content.Add($"anim/sword_1h/1HM_Walk{dirs[d]}.swclip", Clip(n++, Dir(d) * WalkSpeed).Write);
        for (int d = 0; d < 8; d++) content.Add($"anim/sword_1h/1HM_Run{dirs[d]}.swclip", Clip(n++, Dir(d) * RunSpeed).Write);
        content.Add("anim/sword_1h/1HM_TurnLeft90.swclip", TurnClip(n++, TurnLeftYaw).Write);
        content.Add("anim/sword_1h/1HM_TurnRight90.swclip", TurnClip(n++, TurnRightYaw).Write);
        content.Add("anim/sword_1h/1HM_Turn180.swclip", TurnClip(n++, Turn180Yaw).Write);
        if (actionsJson is not null) content.Add("combat/actions.json", System.Text.Encoding.UTF8.GetBytes(actionsJson));
        return content;
    }

    private static CharacterAssets? _shared;

    /// <summary>Ассеты тестов (только чтение — их можно делить между симуляциями).</summary>
    public static CharacterAssets Assets => _shared ??= CharacterAssets.Load(Content());

    /// <summary>Симуляция игры на тестовом контенте — та же композиция, что у Sword.exe и Sword.Headless.</summary>
    public static Simulation Simulation(ConfigSet? config = null) => SwordSimulation.Create(config ?? ConfigSet.Empty(), Assets);
}
