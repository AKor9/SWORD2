using System.Numerics;
using System.Runtime.InteropServices;
using Sword.Engine.Animation.Motion;
using Sword.Engine.Formats;

namespace Sword.Engine.Animation.Tests;

/// <summary>
/// Клипы проигрывателя на скелете тестов: гладкие, позы далеко друг от друга (разрыв без инерциализации — десятые доли
/// радиана); root и pelvis двигают смещение, spine — нет. База — <see cref="MotionFixtures.Plain"/> и ранний выход
/// <see cref="Exit"/> → <see cref="Entry"/>.
/// </summary>
internal static class MotionPlayerFixtures
{
    /// <summary>Тик игры, с; полупериод затухания смещений, с; темп (продвижение = Dt × темп — рёбра пересекаются посреди тика).</summary>
    public const float Dt = 1f / 120, Halflife = 0.15f, Tempo = 1.13f;

    /// <summary>Walk_Loop — кадры 0…24 (цикл), Walk_Stop — 25…43, Idle_Loop — 44…74 (цикл).</summary>
    public const int Walk = 0, Stop = 25, StopLast = 43, Idle = 44, IdleLast = 74;

    /// <summary>Ранний выход Walk_Stop (кадр 38) во вход Idle_Loop (кадр 49).</summary>
    public const int Exit = 38, Entry = 49;

    public static ClipLibrary Library() => TestClips.Library(
        Smooth("Walk_Loop", 25, loop: true, offset: 0, amplitude: 0.5f, cycles: 1, drift: 0.05f, turn: 0.01f),
        Smooth("Walk_Stop", 19, loop: false, offset: 0.5f, amplitude: 0.35f, cycles: 0.75f, drift: 0.03f, turn: -0.008f),
        Smooth("Idle_Loop", 31, loop: true, offset: -0.4f, amplitude: 0.25f, cycles: 1, drift: 0.002f, turn: 0));

    /// <summary>
    /// Гладкий клип: повороты суставов — вокруг своих осей, угол — сдвиг клипа + синус фазы (у цикла — ровно период за клип,
    /// последний кадр = первому); смещения root и pelvis колеблются, spine стоит; корень едет вперёд с поворотом.
    /// </summary>
    public static ClipSource Smooth(string name, int frames, bool loop, float offset, float amplitude, float cycles, float drift, float turn)
    {
        int jc = TestClips.Joints.Length;
        var rot = new Quaternion[frames * jc];
        var pos = new Vector3[frames * jc];
        var motion = new Vector3[frames];
        var yaw = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            float phase = MathF.Tau * cycles * f / (frames - 1);
            for (int j = 0; j < jc; j++)
            {
                var axis = Vector3.Normalize(new Vector3(1, j, 0.5f + 0.3f * j));
                rot[f * jc + j] = Quaternion.CreateFromAxisAngle(axis, offset * (1 + 0.25f * j) + 0.3f * j + amplitude * MathF.Sin(phase + j));
            }
            pos[f * jc + MotionFixtures.Root] = new Vector3(0.02f * MathF.Sin(phase), 0.01f * MathF.Cos(phase) + 0.02f * offset, 0);
            pos[f * jc + MotionFixtures.Pelvis] = new Vector3(0.03f * MathF.Cos(phase), 0.95f + 0.04f * MathF.Sin(phase) + 0.05f * offset, 0.01f);
            pos[f * jc + MotionFixtures.Spine] = new Vector3(0, 0.25f, 0.02f);
            motion[f] = new Vector3(0, 0, drift * f);
            yaw[f] = turn * f;
        }
        return TestClips.Source("locomotion", name, new ClipFile
        {
            Skeleton = "test", SkeletonHash = SkeletonFile.ComputeHash(TestClips.Joints, TestClips.Parents), SampleRate = 30,
            Frames = frames, Joints = jc, Loop = loop, LocalRot = rot, LocalPos = pos, RootMotion = motion, RootYaw = yaw,
        });
    }

    /// <summary>База из <see cref="Library"/>: граф внутри клипа; <paramref name="exit"/> — с ранним выходом <see cref="Exit"/> → <see cref="Entry"/>.</summary>
    public static MotionDatabase Database(ClipLibrary library, bool exit = false)
    {
        var schema = MotionFixtures.Schema();
        var a = MotionFixtures.Plain(library, schema, [0, 1, 2]);
        if (exit) AddExit(a, Exit, Entry);
        return a.Build(library, schema);
    }

    /// <summary>Кадр выхода <paramref name="exit"/> «становится» кадром входа <paramref name="entry"/>: его Next и Interval — как у входа.</summary>
    public static void AddExit(MotionFixtures.Arrays a, int exit, int entry)
    {
        a.Entry[exit] = entry;
        a.Next[exit] = a.Next[entry];
        a.Interval[exit] = a.Interval[entry];
    }

    public static (Quaternion[] Rot, Vector3[] Pos) FramePose(MotionDatabase db, int frame)
    {
        int jc = db.Library.JointCount, o = db.LibraryFrame(frame) * jc;
        return (db.Library.LocalRot.Slice(o, jc).ToArray(), db.Library.LocalPos.Slice(o, jc).ToArray());
    }

    /// <summary>Угол поворота между кватернионами, рад (точный и около нуля — через вектор поворота).</summary>
    public static float Angle(Quaternion a, Quaternion b) => AnimMath.RotationVector(a, b).Length();

    /// <summary>Наибольшее по суставам изменение поворота (рад) и смещения (м) между двумя позами.</summary>
    public static (float Rot, float Pos) MaxChange(ReadOnlySpan<Quaternion> r0, ReadOnlySpan<Vector3> p0, ReadOnlySpan<Quaternion> r1,
        ReadOnlySpan<Vector3> p1)
    {
        float rot = 0, pos = 0;
        for (int j = 0; j < r0.Length; j++)
        {
            rot = MathF.Max(rot, Angle(r0[j], r1[j]));
            pos = MathF.Max(pos, Vector3.Distance(p0[j], p1[j]));
        }
        return (rot, pos);
    }

    /// <summary>Биты чисел — для побитового сравнения (−0 ≠ +0).</summary>
    public static int[] Bits<T>(ReadOnlySpan<T> values) where T : unmanaged => MemoryMarshal.Cast<T, int>(values).ToArray();

    /// <summary>Перенос корня — в пределах <paramref name="eps"/> (курс — по модулю 2π).</summary>
    public static void Near(Pose2 expected, Pose2 actual, float eps, string what = "")
    {
        Assert.True(MathF.Abs(expected.X - actual.X) <= eps && MathF.Abs(expected.Z - actual.Z) <= eps
            && MathF.Abs(Pose2.Wrap(expected.Yaw - actual.Yaw)) <= eps, $"{what}: ожидался {expected}, а {actual}");
    }
}

/// <summary>Один проигрыватель в своём пуле: поза выхода и хэндл; шаг — задание и тик пула.</summary>
internal sealed class PlayerRig
{
    public readonly MotionDatabase Database;
    public readonly PosePool Pool;
    public readonly MotionPlayers Players;
    public readonly PoseHandle Pose;
    public readonly MotionPlayerHandle Player;

    public PlayerRig(MotionDatabase db)
    {
        Database = db;
        Pool = new PosePool(db.Library);
        Players = new MotionPlayers(db);
        Pose = Pool.Alloc();
        Player = Players.Alloc();
    }

    /// <summary>Шаг: продвижение на <paramref name="frames"/> кадров клипа (секунды — /30), переход, тик пула.</summary>
    public PoseTickStats Step(float frames, int transition = -1, float dt = MotionPlayerFixtures.Dt, float halflife = MotionPlayerFixtures.Halflife)
    {
        Pool.Motion(Pose, Players, Player, new MotionStep(frames / Database.SampleRate, dt, transition, halflife));
        return Pool.Tick();
    }

    /// <summary>Обычный тик: продвижение Dt × темп.</summary>
    public PoseTickStats Tick(int transition = -1) => Step(MotionPlayerFixtures.Dt * MotionPlayerFixtures.Tempo * Database.SampleRate, transition);

    public MotionPlayerState State => Players.State(Player);
    public Quaternion[] Rot => Pool.LocalRot(Pose).ToArray();
    public Vector3[] Pos => Pool.LocalPos(Pose).ToArray();
}
