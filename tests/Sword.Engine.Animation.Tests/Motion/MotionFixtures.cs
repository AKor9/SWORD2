using System.Numerics;
using Sword.Engine.Animation.Motion;
using static Sword.Engine.Animation.Tests.TestClips;

namespace Sword.Engine.Animation.Tests;

/// <summary>
/// Схема на скелете тестов (root, pelvis, spine), библиотека из трёх клипов и база из массивов: внутри клипа f → f + 1,
/// шов цикла на второй кадр, у нециклического последний кадр — без продолжения.
/// </summary>
internal static class MotionFixtures
{
    public const int Root = 0, Pelvis = 1, Spine = 2;

    /// <summary>Все виды групп: траектория 3 момента, прошлое 1, корень, суставы, курс по паре; контакт — таз.</summary>
    public static MotionSchema Schema() => new([1f / 3, 2f / 3, 1f], [-1f / 3],
    [
        new("trajectory", FeatureKind.TrajectoryPosition, 3.5f, 0.2f),
        new("trajectory-direction", FeatureKind.TrajectoryDirection, 1.5f, 0.2f),
        new("past", FeatureKind.PastPosition, 0.5f, 0.1f),
        new("root-velocity", FeatureKind.RootVelocity, 4f, 0.2f),
        new("root-yaw-rate", FeatureKind.RootYawRate, 0.5f, 0.5f),
        new("joints", FeatureKind.JointPosition, 0.4f, 0.03f, [Pelvis, Spine]),
        new("pelvis-velocity", FeatureKind.JointVelocity, 0.5f, 0.1f, [Pelvis]),
        new("facing", FeatureKind.JointPairFacing, 0.25f, 0.1f, [Spine, Pelvis]),
    ], [Pelvis]);

    /// <summary>Цикл 20 кадров (0…19), нециклический 12 (20…31), цикл 16 (32…47) — все с поворотом корня.</summary>
    public static ClipLibrary Library() => TestClips.Library(
        Turning("locomotion", "Walk_Loop", 20, 0.1f, loop: true),
        Turning("locomotion", "Walk_Stop", 12, 0.7f, loop: false),
        Turning("locomotion", "Idle_Loop", 16, 1.3f, loop: true));

    /// <summary>Готовые массивы базы — меняются тестом до постройки (<see cref="Build"/>).</summary>
    public sealed class Arrays
    {
        public required MotionClip[] Clips;
        public required int[] Next, Entry, MovingJoints;
        public required Pose2[] Interval;
        public required bool[] Searchable;
        public required float[] Features, Mean, Std;
        public required Vector3[] RootVelocity;
        public required byte[] Contacts;

        public MotionDatabase Build(ClipLibrary library, MotionSchema schema) => new(library, schema, Clips, Next, Interval, Entry,
            Searchable, Features, Mean, Std, RootVelocity, Contacts, MovingJoints);
    }

    /// <summary>
    /// Массивы базы из клипов <paramref name="libraryClips"/> (по возрастанию): граф внутри клипа, перенос корня — из корневого
    /// движения библиотеки (<see cref="Pose2.Relative"/> соседних кадров), признаки — случайные нормализованные (сид), среднее
    /// <c>0,1·i</c>, разброс <c>1 + 0,01·i</c>. Искомые — все кадры со следующим, кроме последнего кадра цикла (он = первый).
    /// </summary>
    public static Arrays Plain(ClipLibrary library, MotionSchema schema, int[] libraryClips, int seed = 1)
    {
        var clips = new MotionClip[libraryClips.Length];
        for (int c = 0, start = 0; c < clips.Length; start += clips[c].Count, c++)
        {
            var l = library.Clips[libraryClips[c]];
            clips[c] = new MotionClip(libraryClips[c], start, l.Count, l.Loop);
        }
        int frames = clips[^1].End, fc = schema.FeatureCount;
        var next = new int[frames];
        var interval = new Pose2[frames];
        var searchable = new bool[frames];
        var velocity = new Vector3[frames];
        var contacts = new byte[frames];
        foreach (var clip in clips)
        {
            int lib = library.Clips[clip.LibraryClip].Start;
            for (int i = 0; i < clip.Count; i++)
            {
                int f = clip.Start + i;
                int n = i < clip.Count - 1 ? f + 1 : clip.Loop ? clip.Start + 1 : -1;
                next[f] = n;
                // шов цикла: последний кадр = первый, интервал — как у первого
                int from = n < 0 ? i : i < clip.Count - 1 ? i : 0;
                interval[f] = n < 0 ? Pose2.Identity : Pose2.Relative(Frame(library, lib + from), Frame(library, lib + from + 1));
                searchable[f] = n >= 0 && !(clip.Loop && i == clip.Count - 1);
                velocity[f] = new Vector3(interval[f].X, 0, interval[f].Z) * library.SampleRate;
                contacts[f] = (byte)(i / 4 % 2);
            }
        }
        var random = new Random(seed);
        var features = new float[frames * fc];
        for (int i = 0; i < features.Length; i++) features[i] = (float)(random.NextDouble() * 4 - 2);
        return new Arrays
        {
            Clips = clips, Next = next, Interval = interval, Entry = Enumerable.Repeat(-1, frames).ToArray(), Searchable = searchable,
            Features = features, Mean = [.. Enumerable.Range(0, fc).Select(i => 0.1f * i)],
            Std = [.. Enumerable.Range(0, fc).Select(i => 1 + 0.01f * i)], RootVelocity = velocity, Contacts = contacts,
            MovingJoints = [Root, Pelvis],
        };
    }

    /// <summary>Система корня кадра библиотеки в системе первого кадра клипа.</summary>
    public static Pose2 Frame(ClipLibrary library, int libraryFrame) =>
        Pose2.From(ClipLibrary.MotionMatrix(library.RootMotion[libraryFrame], library.RootYaw[libraryFrame]));
}
