using System.Diagnostics;
using Sword.Engine.Animation.Motion;
using Sword.Engine.Content;
using Xunit.Abstractions;
using static Sword.Engine.Animation.Tests.MotionBuildFixtures;

namespace Sword.Engine.Animation.Tests;

/// <summary>
/// Постройка базы из клипов (8б) на синтетических клипах: граф не зависит от порядка клипов; у нециклического клипа ровно одно
/// ребро — кадр выхода становится кадром входа; ранний выход — когда его шов дешевле; ничьи шва — меньший номер кадра; у каждого
/// искомого кадра полный горизонт будущего по графу, клип без продолжения не ищется в хвосте; прошлое — по клипу и политике;
/// разброс группы общий и не меньше пола; два построения — один отпечаток; суставы с движущимся смещением.
/// </summary>
public sealed class MotionDatabaseBuildTests
{
    private static MotionDatabase Build(ClipLibrary library, int[]? clips = null, IMotionGraphPolicy? policy = null,
        MotionBuildParams? parameters = null) =>
        MotionDatabase.Build(library, clips ?? All(library), Schema(), policy ?? new TestPolicy(), parameters ?? Params());

    /// <summary>Клип базы по короткому имени синтетического клипа.</summary>
    private static MotionClip Clip(MotionDatabase db, string name) => db.Clips[db.FindClip(Find(db.Library, name)[0])];

    /// <summary>Кадры выхода клипа (у которых есть вход).</summary>
    private static int[] Exits(MotionDatabase db, MotionClip clip) =>
        [.. Enumerable.Range(clip.Start, clip.Count).Where(f => db.Entry[f] >= 0)];

    private static string NameOf(MotionDatabase db, int frame) => db.Library.Clips[db.Clips[db.ClipOf[frame]].LibraryClip].ShortName;

    [Fact]
    public void Graph_DoesNotDependOnClipOrder()
    {
        var lib = Library(Standard());
        var forward = Build(lib);
        var reversed = Build(lib, [.. All(lib).Reverse()]);
        var shuffled = Build(lib, [3, 0, 6, 2, 5, 1, 4]);
        foreach (var other in new[] { reversed, shuffled })
        {
            Assert.Equal(forward.Fingerprint, other.Fingerprint);
            Assert.Equal(forward.Next.ToArray(), other.Next.ToArray());
            Assert.Equal(forward.Entry.ToArray(), other.Entry.ToArray());
            Assert.Equal(forward.Features.ToArray(), other.Features.ToArray());
        }
        Assert.Equal(forward.Clips.ToArray().Select(c => c.LibraryClip), All(lib));
    }

    [Fact]
    public void ExitFrameBecomesEntryFrame_ExactlyOneEdgePerClip()
    {
        var lib = Library(Standard());
        var policy = new TestPolicy();
        var db = Build(lib, policy: policy);
        int window = (int)MathF.Round(Params().ExitWindow * Rate);
        foreach (var clip in db.Clips)
        {
            var exits = Exits(db, clip);
            if (clip.Loop)
            {
                Assert.Empty(exits);
                continue;
            }
            int exit = Assert.Single(exits);
            int entry = db.Entry[exit];
            var target = db.Clips[db.ClipOf[entry]];
            // выход «становится» входом: следующий кадр и перенос корня — как у входа; вход — не выход и не последний кадр цикла
            Assert.Equal(db.Next[entry], db.Next[exit]);
            Assert.Equal(db.Interval[entry], db.Interval[exit]);
            Assert.Equal(-1, db.Entry[entry]);
            Assert.True(target.Loop && entry < target.End - 1, $"вход {entry} — в {NameOf(db, entry)}");
            Assert.InRange(exit, clip.End - 1 - window, clip.End - 1);
            Assert.True(db.Searchable[exit]);
        }
        Assert.Equal("Walk_Loop", NameOf(db, db.Entry[Assert.Single(Exits(db, Clip(db, "Walk_Start")))]));
        Assert.Equal("Idle_Loop", NameOf(db, db.Entry[Assert.Single(Exits(db, Clip(db, "Walk_Stop")))]));
        Assert.Equal("Walk_Loop", NameOf(db, db.Entry[Assert.Single(Exits(db, Clip(db, "Walk_Turn_Left")))]));
        Assert.Equal(3, db.Summary.Edges);
        // политику спрашивают только о нециклических клипах
        Assert.Equal(3, policy.ContinuationCalls);
    }

    [Fact]
    public void EarlyExit_WhenItsSeamIsCheaper()
    {
        // старт с «росчерком» в последних кадрах (таз проседает на 15 см): шов последнего кадра дорог — выход раньше, до росчерка
        var flourish = WalkStart("Walk_Start_Flourish", t => t > 0.85 ? -0.15 : 0);
        var lib = Library(IdleLoop(), WalkLoop(), flourish);
        var db = Build(lib);
        var clip = Clip(db, "Walk_Start_Flourish");
        int exit = Assert.Single(Exits(db, clip)), last = clip.End - 1;
        Assert.True(exit < clip.Start + 26 && exit >= last - 15, $"выход — кадр {exit - clip.Start} клипа из {clip.Count}");
        Assert.Equal("Walk_Loop", NameOf(db, db.Entry[exit]));
        Assert.Equal(1, db.Summary.EarlyExits);
        // кадры после раннего выхода до горизонта графа не имеют — не ищутся
        for (int f = exit + 1; f <= last; f++) Assert.False(db.Searchable[f]);

        // без окна раннего выхода — выход последний кадр
        var late = Build(lib, parameters: Params(exitWindow: 0));
        Assert.Equal(last, Assert.Single(Exits(late, Clip(late, "Walk_Start_Flourish"))));
        Assert.Equal(0, late.Summary.EarlyExits);
    }

    [Theory]
    [InlineData(3e-5, 1e-4f, "Walk_Loop")]     // шов второго цикла дешевле на ~6·10⁻⁵ доли — ничья: меньший номер кадра
    [InlineData(3e-5, 0f, "Walk_Loop_B")]      // без доли ничьей — строго дешевле
    [InlineData(0.1, 1e-4f, "Walk_Loop_B")]    // дешевле заметно — выбирается
    [InlineData(0, 0f, "Walk_Loop")]           // точные копии — меньший номер кадра
    public void SeamTie_PrefersSmallerFrame(double ratio, float tie, string expected)
    {
        // таз старта выше на 3 см; у копии цикла — выше на ratio·3 см: её шов дешевле на ~2·ratio доли
        const double lift = 0.03;
        var lib = Library(WalkLoop("Walk_Loop"), WalkLoop("Walk_Loop_B", lift * ratio), WalkStart(pelvis: _ => lift));
        var db = Build(lib, parameters: Params(seamTie: tie));
        int exit = Assert.Single(Exits(db, Clip(db, "Walk_Start")));
        Assert.Equal(expected, NameOf(db, db.Entry[exit]));
    }

    [Fact]
    public void NearZeroSeams_AreTies_FirstFrameOfWindowWins()
    {
        // хвост старта — копия цикла: цены шва ранних выходов ~10⁻¹⁰ и различаются только шумом округления (так на паке у
        // Run_Start_RR_Lfoot); ничья — в пределах доли от max(цена, 1): берётся первый кадр окна и вход той же фазы, а не «шумовой»
        // минимум. Последний кадр испорчен (таз) — ранний выход заметно дешевле.
        var db = Build(Library(WalkLoop(), RunLoop(), WalkStartLong(t => t > 1.98 ? -0.1 : 0)));
        var clip = Clip(db, "Walk_Start_Long");
        var loop = Clip(db, "Walk_Loop");
        int exit = Assert.Single(Exits(db, clip));
        Assert.Equal(clip.End - 1 - 15, exit);
        Assert.Equal(loop.Start + 15, db.Entry[exit]);
    }

    [Fact]
    public void Interval_OnTurningLoop_IsConstantArc_SeamToSecondFrame()
    {
        // круг: перенос корня за кадр — одна и та же дуга вдали от начала клипа (ложного сдвига нет); шов цикла — во второй кадр
        const double speed = 1.5, omega = 2 * Math.PI / 8, dt = 1 / 30.0, radius = speed / omega;
        var db = Build(Library(CircleLoop()));
        var step = new Pose2((float)(radius * (1 - Math.Cos(omega * dt))), (float)(radius * Math.Sin(omega * dt)), (float)(omega * dt));
        for (int f = 0; f < db.FrameCount; f++)
        {
            var interval = db.Interval[f];
            Assert.True(MathF.Abs(interval.X - step.X) < 1e-5f && MathF.Abs(interval.Z - step.Z) < 1e-5f
                && MathF.Abs(interval.Yaw - step.Yaw) < 1e-5f, $"кадр {f}: {interval}, а дуга {step}");
        }
        int last = db.FrameCount - 1;
        Assert.Equal(1, db.Next[last]);
        Assert.Equal(db.Interval[0], db.Interval[last]);
        Assert.False(db.Searchable[last]);
        Assert.Equal(last, db.Rows.Length);
    }

    [Fact]
    public void EverySearchableFrame_HasFullHorizonByGraph()
    {
        var lib = Library(Standard());
        var db = Build(lib);
        var schema = db.Schema;
        int position = schema.Groups[schema.Find(FeatureKind.TrajectoryPosition)].Offset;
        int direction = schema.Groups[schema.Find(FeatureKind.TrajectoryDirection)].Offset;
        var raw = new float[db.FeatureCount];
        int[] steps = [10, 20, 30];
        foreach (int f in db.Rows)
        {
            var at = Pose2.Identity;
            int current = f;
            db.RawFeatures(f, raw);
            for (int s = 1, k = 0; s <= 30; s++)
            {
                Assert.True(db.Next[current] >= 0, $"кадр {f} ({NameOf(db, f)}): граф оборвался на шаге {s}");
                at = Pose2.Compose(at, db.Interval[current]);
                current = db.Next[current];
                if (s != steps[k]) continue;
                Assert.True(MathF.Abs(raw[position + 2 * k] - at.X) < 1e-3f && MathF.Abs(raw[position + 2 * k + 1] - at.Z) < 1e-3f,
                    $"кадр {f}, момент {k}: ({raw[position + 2 * k]}, {raw[position + 2 * k + 1]}), по графу {at}");
                Assert.Equal(MathF.Sin(at.Yaw), raw[direction + 2 * k], 1e-4f);
                Assert.Equal(MathF.Cos(at.Yaw), raw[direction + 2 * k + 1], 1e-4f);
                k++;
            }
        }
        // неискомый кадр — последний кадр цикла или без полного горизонта
        for (int f = 0; f < db.FrameCount; f++)
        {
            if (db.Searchable[f]) continue;
            var clip = db.Clips[db.ClipOf[f]];
            if (clip.Loop)
            {
                Assert.Equal(clip.End - 1, f);
                continue;
            }
            int current = f, s = 0;
            while (s < 30 && db.Next[current] >= 0)
            {
                current = db.Next[current];
                s++;
            }
            Assert.True(s < 30, $"кадр {f} ({NameOf(db, f)}) с полным горизонтом не ищется");
        }
    }

    [Fact]
    public void ClipWithoutContinuation_ItsTailIsNotSearchable()
    {
        var lib = Library(IdleLoop(), WalkLoop(), WalkStop());
        var db = Build(lib, policy: new TestPolicy(Find(lib, "Walk_Stop")));
        var clip = Clip(db, "Walk_Stop");
        Assert.Empty(Exits(db, clip));
        Assert.Equal(-1, db.Next[clip.End - 1]);
        // 46 кадров, горизонт 30 шагов: кадры 0…15 доходят до конца клипа, остальные — нет
        for (int i = 0; i < clip.Count; i++) Assert.Equal(i <= 15, db.Searchable[clip.Start + i]);
        Assert.Equal(0, db.Summary.Edges);

        // с продолжением — ищется и хвост (до раннего выхода, если он есть)
        var continued = Build(lib);
        int exit = Assert.Single(Exits(continued, clip));
        for (int f = clip.Start; f <= exit; f++) Assert.True(continued.Searchable[f]);
    }

    [Fact]
    public void Past_FollowsClipAndPolicy()
    {
        var lib = Library(IdleLoop(), WalkLoop(), WalkStart(), WalkStop());
        var db = Build(lib);
        int past = db.Schema.Groups[db.Schema.Find(FeatureKind.PastPosition)].Offset;
        var raw = new float[db.FeatureCount];
        void Past(string clip, int frame, float z)
        {
            db.RawFeatures(Clip(db, clip).Start + frame, raw);
            Assert.True(MathF.Abs(raw[past]) < 1e-4f && MathF.Abs(raw[past + 1] - z) < 1e-4f,
                $"{clip}, кадр {frame}: прошлое ({raw[past]}, {raw[past + 1]}), ожидалось (0, {z})");
        }
        // цикл: 10 кадров назад через шов — 0,5 м при 1,5 м/с
        Past("Walk_Loop", 0, -0.5f);
        Past("Walk_Loop", 3, -0.5f);
        // старт с места (скорость начала < 0,3) — «стоял» в первом кадре
        Past("Walk_Start", 0, 0);
        Past("Walk_Start", 5, -0.75f * (5 / 30f) * (5 / 30f));
        // стоп с ходу — назад со скоростью начала (1,45 м/с за первые 0,1 с)
        Past("Walk_Stop", 0, -1.45f / 3);
        Past("Walk_Stop", 4, -1.45f * 6 / 30 - (1.5f * 4 / 30 - 0.5f * (4 / 30f) * (4 / 30f)));
        // внутри клипа — корень клипа
        Past("Walk_Stop", 15, (1.5f * 5 / 30 - 0.5f * (5 / 30f) * (5 / 30f)) - (1.5f * 0.5f - 0.5f * 0.25f));
    }

    [Fact]
    public void GroupStd_IsSharedAndAtLeastMinStd()
    {
        // без поворотов: ω и курс траектории не меняются — разброс равен полу группы
        var lib = Library(IdleLoop(), WalkLoop(), RunLoop(), WalkStart(), WalkStop());
        var db = Build(lib);
        foreach (var g in db.Schema.Groups)
        {
            var std = db.Std.Slice(g.Offset, g.Size);
            foreach (float s in std) Assert.Equal(std[0], s);
            Assert.True(std[0] >= g.MinStd, $"группа {g.Name}: разброс {std[0]} ниже пола {g.MinStd}");
        }
        Assert.Equal(0.5f, db.Std[db.Schema.Groups[db.Schema.Find("root-yaw-rate")].Offset]);
        Assert.Equal(0.2f, db.Std[db.Schema.Groups[db.Schema.Find("trajectory-direction")].Offset]);

        // среднее и разброс — по искомым кадрам: нормализованные признаки искомых кадров в среднем 0; разброс скорости корня — среднее
        // разбросов её признаков
        int fc = db.FeatureCount;
        var sum = new double[fc];
        foreach (int f in db.Rows)
            for (int i = 0; i < fc; i++) sum[i] += db.FeatureRow(f)[i];
        for (int i = 0; i < fc; i++) Assert.True(Math.Abs(sum[i] / db.Rows.Length) < 1e-4, $"признак {i}: среднее {sum[i] / db.Rows.Length}");
        var velocity = db.Schema.Groups[db.Schema.Find(FeatureKind.RootVelocity)];
        var raw = new float[fc];
        double groupStd = 0;
        for (int i = velocity.Offset; i < velocity.End; i++)
        {
            double m = 0, q = 0;
            foreach (int f in db.Rows)
            {
                db.RawFeatures(f, raw);
                m += raw[i];
            }
            m /= db.Rows.Length;
            foreach (int f in db.Rows)
            {
                db.RawFeatures(f, raw);
                q += (raw[i] - m) * (raw[i] - m);
            }
            groupStd += Math.Sqrt(q / db.Rows.Length);
        }
        Assert.Equal(Math.Max(groupStd / velocity.Size, velocity.MinStd), db.Std[velocity.Offset], 1e-4);
    }

    [Fact]
    public void TwoBuilds_SameFingerprint_SummaryMatchesData()
    {
        var lib = Library(Standard());
        var a = Build(lib);
        var b = Build(lib);
        Assert.Equal(a.Fingerprint, b.Fingerprint);
        Assert.Equal(a.Features.ToArray(), b.Features.ToArray());
        Assert.Equal(a.Summary with { BuildMilliseconds = 0 }, b.Summary with { BuildMilliseconds = 0 });
        int frames = Standard().Sum(c => c.Frames);
        Assert.Equal(new MotionDatabaseSummary(7, frames, a.Rows.Length, 3, a.Summary.EarlyExits, a.Summary.BuildMilliseconds), a.Summary);
        Assert.True(a.Summary.BuildMilliseconds > 0);
    }

    [Fact]
    public void RootVelocity_Contacts_MovingJoints_ComeFromKinematics()
    {
        var lib = Library(Standard());
        var db = Build(lib);
        var kin = ClipKinematics.Compute(lib, All(lib), Schema(), Contacts);
        Assert.Equal(kin.RootVelocity.ToArray(), db.RootVelocity.ToArray());
        Assert.Equal(kin.Contacts.ToArray(), db.Contacts.ToArray());
        // локальные смещения двигаются у таза (качается) и стоп (синтетический шаг — смещением); у корня, бёдер, спины — нет
        Assert.Equal([Pelvis, FootL, FootR], db.MovingJoints.ToArray());
    }

    [Theory]
    [InlineData("повтор")]
    [InlineData("вне библиотеки")]
    [InlineData("нет клипов")]
    [InlineData("сустав схемы вне скелета")]
    [InlineData("политика — клип вне базы")]
    [InlineData("окно выхода меньше 0")]
    [InlineData("доля ничьей 1")]
    [InlineData("пороги контактов")]
    public void RejectsBadInput(string what)
    {
        var lib = Library(IdleLoop(), WalkLoop(), WalkStop());
        int[] clips = [0, 1, 2];
        var schema = Schema();
        IMotionGraphPolicy policy = new TestPolicy();
        var p = Params();
        switch (what)
        {
            case "повтор": clips = [0, 1, 1]; break;
            case "вне библиотеки": clips = [0, 3]; break;
            case "нет клипов": clips = []; break;
            case "сустав схемы вне скелета": schema = new MotionSchema([], [], [new("j", FeatureKind.JointPosition, 1, 0.1f, [7])]); break;
            case "политика — клип вне базы": policy = new OutOfRangePolicy(); break;
            case "окно выхода меньше 0": p = p with { ExitWindow = -0.1f }; break;
            case "доля ничьей 1": p = p with { SeamTie = 1 }; break;
            case "пороги контактов": p = p with { Contacts = p.Contacts with { SpeedExit = 0 } }; break;
            default: throw new ArgumentOutOfRangeException(nameof(what), what, "нет такого случая");
        }
        Assert.ThrowsAny<ArgumentException>(() => MotionDatabase.Build(lib, clips, schema, policy, p));
    }

    [Fact]
    public void NoSearchableFrames_IsDataError()
    {
        // один нециклический клип короче горизонта и без продолжения — искать не в чем
        var lib = Library(new Synth("Short", false, 20, t => (0, t, 0), _ => 1));
        var e = Assert.Throws<InvalidDataException>(() => Build(lib));
        Assert.Contains("искомого", e.Message);
    }

    private sealed class OutOfRangePolicy : IMotionGraphPolicy
    {
        public void Continuations(int clip, ReadOnlySpan<MotionClipInfo> clips, List<int> candidates) => candidates.Add(clips.Length);
        public MotionPast Past(int clip, ReadOnlySpan<MotionClipInfo> clips) => MotionPast.Standing;
    }
}

/// <summary>Клипы источника <c>locomotion</c> пака контента (скелет <c>ue5_manny</c>) — чтение один раз на класс тестов.</summary>
public sealed class PackLocomotion
{
    public const string SkeletonId = "ue5_manny", Source = "locomotion";

    public PackLocomotion()
    {
        long started = Stopwatch.GetTimestamp();
        using var pak = ContentLocator.Open();
        string skeletonPath = ClipLibrary.SkeletonFile(SkeletonId);
        Skeleton skeleton;
        using (var s = pak.OpenRead(skeletonPath)) skeleton = Skeleton.Read(s, pak.Describe(skeletonPath));
        var files = pak.List($"{ClipLibrary.AnimDir}/{Source}", ClipLibrary.ClipExt)
            .Select(p => new ClipSource(p, () => pak.OpenRead(p), pak.Describe(p))).ToList();
        Library = ClipLibrary.Load(skeleton, files) ?? throw new InvalidDataException($"в паке нет клипов {Source} скелета {SkeletonId}");
        LoadMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    public ClipLibrary Library { get; }
    public double LoadMilliseconds { get; }
}

/// <summary>
/// База на клипах <c>locomotion</c> пака (категория Content) с тестовой политикой без имён GASP и раскладкой 45 признаков: постройка
/// проходит, сводка печатается, отпечаток двух построений (прямой и обратный порядок клипов) один. Время постройки — не бюджет.
/// </summary>
[Trait("Category", "Content")]
public sealed class MotionDatabasePackTests(PackLocomotion pack, ITestOutputHelper output) : IClassFixture<PackLocomotion>
{
    /// <summary>Раскладка 45 признаков (как в <c>MotionSchemaTests</c>) по именам суставов скелета пака.</summary>
    private static MotionSchema Schema(Skeleton skeleton)
    {
        int J(string name) => skeleton.Find(name) is var j and >= 0 ? j : throw new InvalidDataException($"в скелете нет сустава {name}");
        int footL = J("foot_l"), footR = J("foot_r");
        return new MotionSchema([1f / 3, 2f / 3, 1f], [-1f / 3],
        [
            new("траектория", FeatureKind.TrajectoryPosition, 3.5f, 0.2f),
            new("курс траектории", FeatureKind.TrajectoryDirection, 1.5f, 0.2f),
            new("прошлое", FeatureKind.PastPosition, 0.5f, 0.1f),
            new("скорость корня", FeatureKind.RootVelocity, 4f, 0.2f),
            new("ω корня", FeatureKind.RootYawRate, 0.5f, 0.5f),
            new("стопы", FeatureKind.JointPosition, 0.4f, 0.03f, [footL, footR]),
            new("v стоп", FeatureKind.JointVelocity, 0.5f, 0.2f, [footL, footR]),
            new("колени", FeatureKind.JointPosition, 0.25f, 0.03f, [J("calf_l"), J("calf_r")]),
            new("таз", FeatureKind.JointPosition, 0.25f, 0.02f, [J("pelvis")]),
            new("v таза", FeatureKind.JointVelocity, 0.5f, 0.1f, [J("pelvis")]),
            new("курс таза", FeatureKind.JointPairFacing, 0.5f, 0.1f, [J("thigh_l"), J("thigh_r")]),
            new("курс груди", FeatureKind.JointPairFacing, 0.25f, 0.1f, [J("clavicle_l"), J("clavicle_r")]),
        ], [footL, footR]);
    }

    [Fact]
    public void Locomotion_Builds_SameFingerprintInAnyOrder()
    {
        var lib = pack.Library;
        var schema = Schema(lib.Skeleton);
        int[] clips = All(lib);
        var db = MotionDatabase.Build(lib, clips, schema, new TestPolicy(), Params());
        var again = MotionDatabase.Build(lib, [.. clips.Reverse()], schema, new TestPolicy(), Params());
        var s = db.Summary;
        output.WriteLine($"пак: клипов {Source(lib)} — {lib.ClipCount}, кадров {lib.FrameCount}, чтение {pack.LoadMilliseconds:F0} мс");
        output.WriteLine($"база: клипов {s.Clips}, кадров {s.Frames}, искомых {s.Searchable}, рёбер {s.Edges}, ранних выходов {s.EarlyExits}, " +
            $"блоков {db.BlockCount}, движущихся суставов {db.MovingJoints.Length}, отпечаток {db.Fingerprint:X16}, граф {GraphHash(db):X16}");
        output.WriteLine($"постройка: {s.BuildMilliseconds:F0} мс, повторная (обратный порядок) {again.Summary.BuildMilliseconds:F0} мс");
        output.WriteLine($"разброс групп: {string.Join(", ", schema.Groups.ToArray().Select(g => $"{g.Name} {db.Std[g.Offset]:0.###}"))}");

        Assert.Equal(db.Fingerprint, again.Fingerprint);
        Assert.Equal(lib.ClipCount, s.Clips);
        Assert.Equal(lib.FrameCount, s.Frames);
        Assert.True(s.Searchable > s.Frames / 2, $"искомых {s.Searchable} из {s.Frames}");
        Assert.True(s.Edges > 0 && s.EarlyExits <= s.Edges);
        foreach (var g in schema.Groups) Assert.True(db.Std[g.Offset] >= g.MinStd);
    }

    private static string Source(ClipLibrary lib) => lib.Clips[0].Source;

    /// <summary>
    /// Хеш одного графа (следующий кадр, вход, искомый) — без признаков: показывает, что граф устойчив там, где признаки расходятся
    /// в последних битах (другой класс процессора, research-mm-data §4.1).
    /// </summary>
    private static ulong GraphHash(MotionDatabase db)
    {
        ulong h = 14695981039346656037UL;
        void Add(int v)
        {
            h ^= (uint)v;
            h *= 1099511628211UL;
        }
        for (int f = 0; f < db.FrameCount; f++)
        {
            Add(db.Next[f]);
            Add(db.Entry[f]);
            Add(db.Searchable[f] ? 1 : 0);
        }
        return h;
    }
}
