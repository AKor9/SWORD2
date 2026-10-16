using System.Numerics;
using Sword.Engine.Animation.Motion;

namespace Sword.Engine.Animation.Tests;

/// <summary>
/// Синтетический скелет с ногами и клипы походки 30 кадров/с: корень задан формулой, стопа — треугольная волна (в опоре стоит
/// в мире, в переносе — поднята); политика графа — по измеренным скоростям, без имён.
/// </summary>
internal static class MotionBuildFixtures
{
    public const float Rate = 30;
    public const int Root = 0, Pelvis = 1, ThighL = 2, FootL = 3, ThighR = 4, FootR = 5, Spine = 6;
    public static readonly string[] Joints = ["root", "pelvis", "thigh_l", "foot_l", "thigh_r", "foot_r", "spine"];
    public static readonly int[] Parents = [-1, 0, 1, 2, 1, 4, 1];

    /// <summary>Высота голеностопа на земле, м; таз над корнем; подъём стопы в переносе.</summary>
    public const float Ankle = 0.08f, PelvisHeight = 0.95f, Lift = 0.12f;

    public static Skeleton Skeleton() => new("synth", Joints, Parents);

    /// <summary>Детектор контактов для голеностопа при 30 кадрах/с (типичные значения документации).</summary>
    public static readonly ContactParams Contacts = new(0.05f, 0.07f, 0.35f, 0.55f, 2);

    public static MotionBuildParams Params(float seamTie = 1e-4f, float exitWindow = 0.5f) =>
        new(Contacts, exitWindow, 0.1f, 0.001f, seamTie);

    /// <summary>Все виды групп: траектория 1/3, 2/3, 1 с, прошлое −1/3 с, корень, стопы, таз, курс таза по бёдрам; контакты — стопы.</summary>
    public static MotionSchema Schema() => new([1f / 3, 2f / 3, 1f], [-1f / 3],
    [
        new("trajectory", FeatureKind.TrajectoryPosition, 3.5f, 0.2f),
        new("trajectory-direction", FeatureKind.TrajectoryDirection, 1.5f, 0.2f),
        new("past", FeatureKind.PastPosition, 0.5f, 0.1f),
        new("root-velocity", FeatureKind.RootVelocity, 4f, 0.2f),
        new("root-yaw-rate", FeatureKind.RootYawRate, 0.5f, 0.5f),
        new("feet", FeatureKind.JointPosition, 0.4f, 0.03f, [FootL, FootR]),
        new("feet-velocity", FeatureKind.JointVelocity, 0.5f, 0.2f, [FootL, FootR]),
        new("pelvis", FeatureKind.JointPosition, 0.25f, 0.02f, [Pelvis]),
        new("pelvis-velocity", FeatureKind.JointVelocity, 0.5f, 0.1f, [Pelvis]),
        new("pelvis-facing", FeatureKind.JointPairFacing, 0.5f, 0.1f, [ThighL, ThighR]),
    ], [FootL, FootR]);

    /// <summary>
    /// Синтетический клип: корень по времени — (x, z) и курс; скорость шага по времени (длина шага = скорость · цикл / 2, 0 — стопы
    /// рядом, без подъёма); цикл шага, с; добавка к высоте таза по времени (для «росчерка» в конце клипа).
    /// </summary>
    public sealed record Synth(string Name, bool Loop, int Frames, Func<double, (double X, double Z, double Yaw)> Root,
        Func<double, double> Speed, double Cycle = 1, Func<double, double>? PelvisExtra = null);

    // ── набор клипов ─────────────────────────────────────────────────────────────────────

    /// <summary>Стойка: цикл 2 с на месте, стопы на земле.</summary>
    public static Synth IdleLoop(string name = "Idle_Loop") => new(name, true, 61, _ => (0, 0, 0), _ => 0, Cycle: 2);

    /// <summary>Шаг вперёд 1,5 м/с, цикл 1 с (31 кадр: последний = первый).</summary>
    public static Synth WalkLoop(string name = "Walk_Loop", double pelvis = 0) =>
        new(name, true, 31, t => (0, 1.5 * t, 0), _ => 1.5, PelvisExtra: _ => pelvis);

    /// <summary>Бег вперёд 3,5 м/с, цикл 0,7 с (22 кадра).</summary>
    public static Synth RunLoop() => new("Run_Loop", true, 22, t => (0, 3.5 * t, 0), _ => 3.5, Cycle: 0.7);

    /// <summary>Шаг влево (+X) 1,5 м/с.</summary>
    public static Synth StrafeLeftLoop() => new("Strafe_Left_Loop", true, 31, t => (1.5 * t, 0, 0), _ => 1.5);

    /// <summary>Старт: скорость 0 → 1,5 м/с за 1 с (31 кадр); <paramref name="pelvis"/> — добавка к тазу по времени.</summary>
    public static Synth WalkStart(string name = "Walk_Start", Func<double, double>? pelvis = null) =>
        new(name, false, 31, t => (0, 0.75 * t * t, 0), t => 1.5 * t, PelvisExtra: pelvis);

    /// <summary>Длинный старт (61 кадр): разгон за 1 с, затем 1 с шага — хвост совпадает с циклом (цена шва ~0).</summary>
    public static Synth WalkStartLong(Func<double, double>? pelvis = null) => new("Walk_Start_Long", false, 61,
        t => (0, t <= 1 ? 0.75 * t * t : 0.75 + 1.5 * (t - 1), 0), t => 1.5 * Math.Min(t, 1), PelvisExtra: pelvis);

    /// <summary>Стоп: 1,5 → 0 м/с за 1,5 с (46 кадров), в конце — стопы рядом, как в стойке.</summary>
    public static Synth WalkStop() => new("Walk_Stop", false, 46, t => (0, 1.5 * t - 0.5 * t * t, 0), t => 1.5 - t);

    /// <summary>Шаг 1,5 м/с с поворотом влево на 90° за 1 с (дуга).</summary>
    public static Synth WalkTurnLeft() => Arc("Walk_Turn_Left", false, 31, 1.5, Math.PI / 2);

    /// <summary>Круг: 1,5 м/с, оборот за 8 с (241 кадр) — далеко от начала клипа при повороте.</summary>
    public static Synth CircleLoop() => Arc("Circle_Loop", true, 241, 1.5, 2 * Math.PI / 8);

    /// <summary>Равномерное движение по дуге: скорость <paramref name="speed"/> вперёд, поворот влево <paramref name="omega"/> рад/с.</summary>
    public static Synth Arc(string name, bool loop, int frames, double speed, double omega) => new(name, loop, frames,
        t => (speed / omega * (1 - Math.Cos(omega * t)), speed / omega * Math.Sin(omega * t), omega * t), _ => speed);

    /// <summary>Обычный набор: стойка, шаг, бег, стрейф, старт, стоп, поворот.</summary>
    public static Synth[] Standard() => [IdleLoop(), WalkLoop(), RunLoop(), StrafeLeftLoop(), WalkStart(), WalkStop(), WalkTurnLeft()];

    // ── библиотека ───────────────────────────────────────────────────────────────────────

    public static ClipLibrary Library(params Synth[] clips)
    {
        var skeleton = Skeleton();
        int jc = Joints.Length;
        var list = new List<ClipLibrary.Clip>();
        var rot = new List<Quaternion>();
        var pos = new List<Vector3>();
        var motion = new List<Vector3>();
        var yaw = new List<float>();
        var r = new Quaternion[jc];
        var p = new Vector3[jc];
        foreach (var clip in clips)
        {
            list.Add(new ClipLibrary.Clip("synth/" + clip.Name, motion.Count, clip.Frames, clip.Loop));
            for (int i = 0; i < clip.Frames; i++)
            {
                double t = i / (double)Rate;
                var root = clip.Root(t);
                motion.Add(new Vector3((float)root.X, 0, (float)root.Z));
                yaw.Add((float)root.Yaw);
                Pose(t, clip.Speed(t), clip.Cycle, clip.PelvisExtra?.Invoke(t) ?? 0, r, p);
                rot.AddRange(r);
                pos.AddRange(p);
            }
        }
        return new ClipLibrary(skeleton, Rate, [.. list], [.. rot], [.. pos], [.. motion], [.. yaw]);
    }

    /// <summary>Локальная поза кадра: повороты единичные, модельные положения — сумма смещений от корня.</summary>
    private static void Pose(double t, double speed, double cycle, double pelvisExtra, Span<Quaternion> rot, Span<Vector3> pos)
    {
        rot.Fill(Quaternion.Identity);
        double phase = t / cycle;
        double stride = speed * cycle / 2;
        var pelvis = new Vector3(0, (float)(PelvisHeight + 0.02 * Math.Cos(4 * Math.PI * phase) + pelvisExtra), 0);
        var thighL = new Vector3(0.1f, -0.05f, 0);
        var thighR = new Vector3(-0.1f, -0.05f, 0);
        pos[Root] = Vector3.Zero;
        pos[Pelvis] = pelvis;
        pos[ThighL] = thighL;
        pos[ThighR] = thighR;
        pos[FootL] = Foot(phase, stride, 0.1f) - pelvis - thighL;
        pos[FootR] = Foot(phase + 0.5, stride, -0.1f) - pelvis - thighR;
        pos[Spine] = new Vector3(0, 0.2f, 0);
    }

    /// <summary>Модельное положение стопы: опора — назад по земле, перенос — вперёд с подъёмом.</summary>
    public static Vector3 Foot(double phase, double stride, float x)
    {
        double p = phase - Math.Floor(phase);
        double z, lift = 0;
        if (p < 0.5) z = stride / 2 - stride * (p / 0.5);
        else
        {
            double q = (p - 0.5) / 0.5;
            z = -stride / 2 + stride * q;
            lift = Lift * Math.Min(1, stride / 0.2) * Math.Sin(Math.PI * q);
        }
        return new Vector3(x, (float)(Ankle + lift), (float)z);
    }

    /// <summary>Номера клипов библиотеки по коротким именам.</summary>
    public static int[] Find(ClipLibrary library, params string[] names) =>
        [.. names.Select(n => library.FindClip("synth/" + n) is var i and >= 0 ? i : throw new ArgumentException($"нет клипа {n}"))];

    public static int[] All(ClipLibrary library) => [.. Enumerable.Range(0, library.ClipCount)];

    /// <summary>
    /// Тестовая политика без имён клипов: выход медленнее 0,3 м/с — клипы со средней скоростью ниже 0,3 (кроме себя); иначе — циклы
    /// со средней скоростью от 0,3, чья средняя скорость в пределах 30° от направления выхода; прошлое — «стоял», если скорость
    /// начала ниже 0,3. <paramref name="withoutContinuation"/> — номера клипов библиотеки без продолжения.
    /// </summary>
    public sealed class TestPolicy(params int[] withoutContinuation) : IMotionGraphPolicy
    {
        public const float StandSpeed = 0.3f;
        public static readonly float DirectionWindow = MathF.PI / 6;

        public int ContinuationCalls { get; private set; }

        public void Continuations(int clip, ReadOnlySpan<MotionClipInfo> clips, List<int> candidates)
        {
            ContinuationCalls++;
            var exit = clips[clip];
            if (withoutContinuation.Contains(exit.LibraryClip)) return;
            for (int d = 0; d < clips.Length; d++)
            {
                if (d == clip) continue;
                var c = clips[d];
                if (exit.ExitSpeed < StandSpeed)
                {
                    if (c.MeanSpeed < StandSpeed) candidates.Add(d);
                }
                else if (c.Loop && c.MeanSpeed >= StandSpeed && MathF.Abs(Pose2.Wrap(c.MeanDirection - exit.ExitDirection)) <= DirectionWindow)
                    candidates.Add(d);
            }
        }

        public MotionPast Past(int clip, ReadOnlySpan<MotionClipInfo> clips) =>
            clips[clip].StartVelocity.Length() < StandSpeed ? MotionPast.Standing : MotionPast.StartVelocity;
    }
}
