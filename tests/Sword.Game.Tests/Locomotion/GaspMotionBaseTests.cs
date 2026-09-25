using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Animation.Motion;
using Sword.Game.Locomotion.Motion;

namespace Sword.Game.Tests;

/// <summary>
/// Отбор, схема и база на процесс (8д) на синтетической библиотеке со скелетом из суставов UE5 Manny (<see cref="TestAssets.Joints"/>):
/// отбор по источнику и регулярке, ошибки данных — с понятной причиной, постройка с политикой GASP (старт уходит в цикл шага, стоп — в
/// стойку), кэш — одна база на библиотеку и отбор, другая библиотека — своя база, одновременные запросы — одна постройка.
/// </summary>
public sealed class GaspMotionBaseTests
{
    private const float Rate = 30;

    private sealed record Synth(string Id, bool Loop, int Frames, Func<double, (double X, double Z, double Yaw)> Root);

    private static readonly Synth[] Standard =
    [
        new("locomotion/M_Neutral_Stand_Idle_Loop", true, 61, _ => (0, 0, 0)),
        new("locomotion/M_Neutral_Walk_Loop_F", true, 31, t => (0, 1.5 * t, 0)),
        new("locomotion/M_Neutral_Walk_Loop_B", true, 31, t => (0, -1.5 * t, 0)),
        new("locomotion/M_Neutral_Walk_Start_F_Lfoot", false, 31, t => (0, 0.75 * t * t, 0)),
        new("locomotion/M_Neutral_Walk_Stop_F_Lfoot", false, 46, t => (0, 1.5 * t - 0.5 * t * t, 0)),
        new("locomotion/M_Neutral_Walk_Circle_Strafe_L", true, 61, t => (1 / Math.PI * (1 - Math.Cos(Math.PI * t)), 1 / Math.PI * Math.Sin(Math.PI * t), Math.PI * t)),
        new("sword_1h/1HM_Idle_Standing", true, 31, _ => (0, 0, 0)),
    ];

    /// <summary>
    /// Библиотека: скелет — суставы <see cref="TestAssets.Joints"/> (без <paramref name="drop"/> — последний сустав переименован),
    /// поза — смещения <see cref="TestAssets.Offsets"/>, стопы ходят вперёд-назад по фазе клипа.
    /// </summary>
    private static ClipLibrary Library(Synth[]? clips = null, string? drop = null)
    {
        clips ??= Standard;
        string[] joints = [.. TestAssets.Joints.Select(j => j == drop ? j + "_x" : j)];
        var skeleton = new Skeleton("ue5_manny", joints, TestAssets.Parents);
        int jc = joints.Length, footL = Array.IndexOf(joints, "foot_l"), footR = Array.IndexOf(joints, "foot_r");
        var list = new List<ClipLibrary.Clip>();
        var rot = new List<Quaternion>();
        var pos = new List<Vector3>();
        var motion = new List<Vector3>();
        var yaw = new List<float>();
        foreach (var clip in clips)
        {
            list.Add(new ClipLibrary.Clip(clip.Id, motion.Count, clip.Frames, clip.Loop));
            for (int f = 0; f < clip.Frames; f++)
            {
                double t = f / (double)Rate;
                var root = clip.Root(t);
                motion.Add(new Vector3((float)root.X, 0, (float)root.Z));
                yaw.Add((float)root.Yaw);
                float swing = 0.15f * MathF.Sin(MathF.Tau * f / (clip.Frames - 1));
                for (int j = 0; j < jc; j++)
                {
                    rot.Add(Quaternion.Identity);
                    var offset = TestAssets.Offsets[j];
                    if (j == footL) offset.Z += swing;
                    if (j == footR) offset.Z -= swing;
                    pos.Add(offset);
                }
            }
        }
        return new ClipLibrary(skeleton, Rate, [.. list], [.. rot], [.. pos], [.. motion], [.. yaw]);
    }

    [Fact]
    public void Select_BySourceAndPattern_Sorted()
    {
        var lib = Library();
        var selection = GaspMotion.Select(lib);
        Assert.Equal(6, selection.SourceClips);
        Assert.Equal(5, selection.Count);                                   // без стрейфа по кругу и чужого источника
        Assert.Equal([0, 1, 2, 3, 4], selection.Clips.ToArray());
        Assert.Equal(GaspKind.Start, selection.Names[3].Kind);
        Assert.Equal(GaspMotion.DefaultClips, selection.Pattern);
        Assert.Equal([1, 2], GaspMotion.Select(lib, "_Loop_").Clips.ToArray());
        Assert.Equal(6, GaspMotion.Select(lib, "").Count);
    }

    [Fact]
    public void DataErrors_AreClear()
    {
        var lib = Library();
        var bad = Assert.Throws<ArgumentException>(() => GaspMotion.Select(lib, "("));
        Assert.Contains("не регулярка", bad.Message);
        var empty = Assert.Throws<InvalidDataException>(() => GaspMotion.Select(lib, "Jump"));
        Assert.Contains("ни одного из 6", empty.Message);
        var foreign = Assert.Throws<InvalidDataException>(() => GaspMotion.Select(Library([Standard[6]])));
        Assert.Contains("нет клипов источника «locomotion»", foreign.Message);

        // неразобранное имя в отборе — ошибка; вне отбора — не мешает
        var withHop = Library([.. Standard, new("locomotion/M_Neutral_Walk_Hop_F", true, 31, t => (0, t, 0))]);
        var hop = Assert.Throws<InvalidDataException>(() => GaspMotion.Select(withHop));
        Assert.Contains("M_Neutral_Walk_Hop_F", hop.Message);
        Assert.Contains("неизвестный вид «Hop»", hop.Message);
        Assert.Equal(2, GaspMotion.Select(withHop, "_Loop_").Count);
        Assert.Throws<InvalidDataException>(() => GaspMotionBase.Build(withHop));

        // нет сустава схемы — ошибка с именем сустава и группы
        var noClavicle = Assert.Throws<InvalidDataException>(() => GaspMotion.Schema(Library(drop: "clavicle_l").Skeleton));
        Assert.Contains("«clavicle_l»", noClavicle.Message);
        Assert.Contains("курс груди", noClavicle.Message);
        var noBall = Assert.Throws<InvalidDataException>(() => GaspMotionBase.Build(Library(drop: "ball_r")));
        Assert.Contains("«ball_r»", noBall.Message);
        Assert.Contains("контакты стоп", noBall.Message);
    }

    [Fact]
    public void Schema_Gasp45_ContactsAnklesAndBalls()
    {
        var skeleton = Library().Skeleton;
        var schema = GaspMotion.Schema(skeleton);
        Assert.Equal(45, schema.FeatureCount);
        Assert.Equal(12, schema.GroupCount);
        Assert.Equal([1f / 3, 2f / 3, 1f], schema.FutureTimes.ToArray());
        Assert.Equal([-1f / 3], schema.PastTimes.ToArray());
        Assert.Equal(["foot_l", "foot_r", "ball_l", "ball_r"], schema.ContactJoints.ToArray().Select(j => skeleton.JointNames[j]));
        Assert.Equal(["foot_l", "foot_r"], schema.GroupJoints(schema.Find("стопы")).ToArray().Select(j => skeleton.JointNames[j]));
        Assert.Equal(["thigh_l", "thigh_r"], schema.GroupJoints(schema.Find("курс таза")).ToArray().Select(j => skeleton.JointNames[j]));
        Assert.Equal([3.5f, 1.5f, 0.5f, 4f, 0.5f, 0.4f, 0.5f, 0.25f, 0.25f, 0.5f, 0.5f, 0.25f], schema.DefaultWeights.ToArray());
        Assert.Equal(0.2f, schema.Groups[schema.Find(FeatureKind.TrajectoryPosition)].MinStd);
    }

    [Fact]
    public void Build_StartGoesToWalkLoop_StopToIdle()
    {
        var built = GaspMotionBase.Build(Library());
        var db = built.Database;
        Assert.Equal(5, db.ClipCount);
        Assert.Equal(2, db.Summary.Edges);
        Assert.True(built.WithoutContinuation.IsEmpty);
        Assert.Equal(new GaspPolicyStats(1, 1, 0, 0, 0, 0, 1, 1), built.Policy);   // старт стоял, стоп — шёл
        Assert.Equal(1, db.ClipOf[EntryOf(db, 3)]);                        // старт → цикл шага вперёд
        Assert.Equal(0, db.ClipOf[EntryOf(db, 4)]);                        // стоп → стойка
        Assert.Equal(1.5f, built.Speeds.Walk(0), 1e-3f);
        Assert.Equal(1.5f, built.Speeds.Walk(MathF.PI), 1e-3f);
        Assert.False(built.Speeds.HasRun);
        Assert.Contains("клипов 5 из 6 «locomotion»", built.StartLine());
        Assert.Contains($"отпечаток {db.Fingerprint:X16}", built.StartLine());
        Assert.Contains("шаг [0° 1.50", built.SpeedsLine());
    }

    private static int EntryOf(MotionDatabase db, int clip)
    {
        var c = db.Clips[clip];
        for (int f = c.Start; f < c.End; f++)
            if (db.Entry[f] >= 0) return db.Entry[f];
        return -1;
    }

    [Fact]
    public void Cache_OneBasePerLibraryAndPattern()
    {
        var lib = Library();
        var first = GaspMotionBase.For(lib);
        Assert.Same(first, GaspMotionBase.For(lib));
        Assert.Same(first, GaspMotionBase.For(lib, GaspMotion.DefaultClips));
        var loops = GaspMotionBase.For(lib, "_Loop_|Idle");
        Assert.NotSame(first, loops);
        Assert.Equal(3, loops.Database.ClipCount);
        Assert.Same(loops, GaspMotionBase.For(lib, "_Loop_|Idle"));

        var other = GaspMotionBase.For(Library());
        Assert.NotSame(first, other);
        Assert.Equal(first.Database.Fingerprint, other.Database.Fingerprint);   // те же данные — та же база, но своя
    }

    [Fact]
    public void Cache_ConcurrentRequests_BuildOnce()
    {
        var lib = Library();
        var bases = new GaspMotionBase[8];
        Parallel.For(0, bases.Length, i => bases[i] = GaspMotionBase.For(lib));
        Assert.All(bases, b => Assert.Same(bases[0], b));
    }

    [Fact]
    public void Cache_DataError_SameErrorEachTime()
    {
        var lib = Library([Standard[6]]);
        var a = Assert.Throws<InvalidDataException>(() => GaspMotionBase.For(lib));
        var b = Assert.Throws<InvalidDataException>(() => GaspMotionBase.For(lib));
        Assert.Same(a, b);
    }

    /// <summary>
    /// База на процесс и её части общие для симуляций и потоков: наружу — ни массивов, ни <see cref="Span{T}"/>, ни сеттеров, ни
    /// изменяемых статических полей (как <c>AnimationTests.SharedAnimationAssets_ExposeNoWritableData</c>).
    /// </summary>
    [Theory]
    [InlineData(typeof(GaspMotionBase))]
    [InlineData(typeof(GaitSpeedModel))]
    [InlineData(typeof(GaspSelection))]
    [InlineData(typeof(GaspMotion))]
    [InlineData(typeof(GaspClipName))]
    public void SharedMotionData_ExposeNoWritableData(Type type)
    {
        const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly;
        var problems = new List<string>();
        foreach (var f in type.GetFields(all))
            if (!f.IsLiteral && (!f.IsInitOnly || f.FieldType.IsArray)) problems.Add($"поле {f.Name}");
        foreach (var p in type.GetProperties(all))
        {
            if (p.SetMethod is { IsPublic: true } && !(type.IsValueType && p.SetMethod.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(System.Runtime.CompilerServices.IsExternalInit))))
                problems.Add($"сеттер {p.Name}");
            if (p.PropertyType.IsArray || p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(Span<>))
                problems.Add($"свойство {p.Name}: {p.PropertyType.Name}");
        }
        foreach (var m in type.GetMethods(all).Where(m => !m.IsSpecialName))
            if (m.ReturnType.IsArray || m.ReturnType.IsGenericType && m.ReturnType.GetGenericTypeDefinition() == typeof(Span<>))
                problems.Add($"метод {m.Name}: {m.ReturnType.Name}");
        Assert.True(problems.Count == 0, $"{type.Name}: " + string.Join(", ", problems));
    }
}
