using System.Globalization;
using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Animation.Motion;
using Sword.Engine.Runtime;
using Sword.Game.Bootstrap;
using Sword.Game.Contracts;
using Sword.Game.Locomotion.Motion;
using Xunit.Abstractions;

namespace Sword.Game.Tests;

/// <summary>
/// Политика GASP на паке (категория Content): все 444 имени разбираются, отбор — 440 клипов, база строится, у каждого
/// нециклического клипа есть продолжение, отпечаток не зависит от порядка; две симуляции берут одну базу. Время — не бюджет.
/// </summary>
[Trait("Category", "Content")]
public sealed class GaspMotionPackTests(ITestOutputHelper output)
{
    private static ClipLibrary Library => TestContent.Reference.Value.Assets.Clips;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    [Fact]
    public void AllLocomotionNames_Parse()
    {
        var lib = Library;
        var names = new List<(string Short, GaspClipName Name)>();
        var bad = new List<string>();
        foreach (var clip in lib.Clips)
        {
            if (clip.Source != GaspMotion.Source) continue;
            if (GaspClipName.TryParse(clip.ShortName, out var name, out string error)) names.Add((clip.ShortName, name));
            else bad.Add($"{clip.ShortName}: {error}");
        }
        foreach (var group in names.GroupBy(n => (n.Name.Kind, n.Name.Gait)).OrderBy(g => g.Key))
            output.WriteLine($"{group.Key.Kind,-16} {group.Key.Gait,-5} {group.Count(),3}  например {group.First().Short}");
        foreach (var tail in names.Where(n => n.Name.Tail.Length > 0 || n.Name.Side != GaspSide.None && n.Name.Kind == GaspKind.Loop))
            output.WriteLine($"хвост или отклонение: {tail.Short} → {tail.Name}");

        Assert.Empty(bad);
        Assert.Equal(444, names.Count);
        int Count(GaspKind kind) => names.Count(n => n.Name.Kind == kind);
        Assert.Equal(1, Count(GaspKind.Idle));
        Assert.Equal(8, Count(GaspKind.TurnInPlace));
        Assert.Equal(24, Count(GaspKind.Loop));
        Assert.Equal(40, Count(GaspKind.Start));
        Assert.Equal(40, Count(GaspKind.Stop));
        Assert.Equal(40, Count(GaspKind.Pivot));
        Assert.Equal(18, Count(GaspKind.Reface));
        Assert.Equal(32, Count(GaspKind.MoveTurn));
        Assert.Equal(12, Count(GaspKind.Arc));
        Assert.Equal(221, Count(GaspKind.DirectionChange));
        Assert.Equal(4, Count(GaspKind.GaitTransition));
        Assert.Equal(4, Count(GaspKind.CircleStrafe));
        // имя сходится с данными: цикл по имени — цикл клипа (стойка, циклы хода, стрейф по кругу)
        foreach (var (shortName, name) in names)
            Assert.True(lib.Clips[lib.FindClip($"{GaspMotion.Source}/{shortName}")].Loop == name.Kind is GaspKind.Idle or GaspKind.Loop or GaspKind.CircleStrafe,
                $"{shortName}: цикл по данным {lib.Clips[lib.FindClip($"{GaspMotion.Source}/{shortName}")].Loop}, вид {name.Kind}");
    }

    [Fact]
    public void DefaultSelection_Takes410_WithoutCircleStrafeAndRunStarts()
    {
        var selection = GaspMotion.Select(Library);
        Assert.Equal(444, selection.SourceClips);
        Assert.Equal(410, selection.Count);   // без 4 Circle_Strafe, 18 Run_Start, 10 Run_Reface_Start, 2 Transition_Walk_to_Run
        foreach (var name in selection.Names) Assert.NotEqual(GaspKind.CircleStrafe, name.Kind);
        foreach (int c in selection.Clips)
        {
            string n = Library.Clips[c].ShortName;
            Assert.False(n.Contains("Run_Start") || n.Contains("Run_Reface_Start") || n.Contains("Transition_Walk_to_Run"), n);
        }
        Assert.Contains(selection.Clips.ToArray(), c => Library.Clips[c].ShortName.Contains("Walk_Start"));
        Assert.Equal(444, GaspMotion.Select(Library, "").Count);
        var clips = selection.Clips;
        for (int i = 1; i < clips.Length; i++) Assert.True(clips[i] > clips[i - 1]);
    }

    [Fact]
    public void Base_BuildsWithGaspPolicy_StableFingerprint()
    {
        var lib = Library;
        var built = GaspMotionBase.Build(lib);
        var db = built.Database;
        var s = db.Summary;
        output.WriteLine(built.StartLine());
        output.WriteLine(built.SpeedsLine());
        output.WriteLine(built.Policy.ToString());
        output.WriteLine($"блоков {db.BlockCount}, движущихся суставов {db.MovingJoints.Length}, признаков {db.FeatureCount}, " +
            $"DataYawRate {new MotionQuery(db).DataYawRate.ToString("0.00", Inv)} рад/с");
        output.WriteLine($"разброс групп: {string.Join(", ", db.Schema.Groups.ToArray().Select(g => $"{g.Name} {db.Std[g.Offset].ToString("0.###", Inv)}"))}");

        // повтор и обратный порядок клипов (своя политика со счётом измерений) — тот же отпечаток
        var again = GaspMotionBase.Build(lib);
        var selection = GaspMotion.Select(lib);
        var recording = new Recording(new GaspGraphPolicy(selection.Clips, selection.Names));
        var reversed = MotionDatabase.Build(lib, [.. selection.Clips.ToArray().Reverse()], GaspMotion.Schema(lib.Skeleton), recording, GaspMotion.BuildParams);
        output.WriteLine($"повторная постройка {again.Database.Summary.BuildMilliseconds.ToString("F0", Inv)} мс, обратный порядок {reversed.Summary.BuildMilliseconds.ToString("F0", Inv)} мс");
        Measurements(built, recording.Clips);

        Assert.Equal(db.Fingerprint, again.Database.Fingerprint);
        Assert.Equal(db.Fingerprint, reversed.Fingerprint);
        Assert.Equal(410, s.Clips);
        Assert.Equal(db.Clips.ToArray().Sum(c => c.Count), s.Frames);
        Assert.True(s.Searchable > s.Frames / 2, $"искомых {s.Searchable} из {s.Frames}");
        int nonLoops = db.Clips.ToArray().Count(c => !c.Loop);
        Assert.Equal(nonLoops, s.Edges);   // у каждого нециклического клипа — ровно одно ребро
        Assert.True(s.EarlyExits <= s.Edges);
        Assert.True(built.WithoutContinuation.IsEmpty, "без продолжения: " +
            string.Join(", ", built.WithoutContinuation.ToArray().Select(c => lib.Clips[db.Clips[c].LibraryClip].ShortName)));
        Assert.Equal(nonLoops, built.Policy.StandingPast + built.Policy.MovingPast);
        Assert.Equal(0, built.Policy.None);
        foreach (var g in db.Schema.Groups) Assert.True(db.Std[g.Offset] >= g.MinStd);
        Assert.True(built.Speeds.HasWalk && built.Speeds.HasRun);
    }

    /// <summary>Измерения постройки по видам: скорости начала и выхода, прошлое, ранние выходы; циклы хода — средняя скорость и направление.</summary>
    private void Measurements(GaspMotionBase built, MotionClipInfo[] infos)
    {
        var db = built.Database;
        var names = built.Names.ToArray();
        var lib = db.Library;
        foreach (var kind in Enum.GetValues<GaspKind>())
        {
            var clips = Enumerable.Range(0, db.ClipCount).Where(c => names[c].Kind == kind && !db.Clips[c].Loop).ToList();
            if (clips.Count == 0) continue;
            var start = clips.Select(c => infos[c].StartVelocity.Length()).ToList();
            var exit = clips.Select(c => infos[c].ExitSpeed).ToList();
            int standing = clips.Count(c => GaspGraphPolicy.PastOf(infos[c]) == MotionPast.Standing);
            int early = clips.Count(c => Exit(db, c) != db.Clips[c].End - 1);
            output.WriteLine(string.Create(Inv, $"{kind,-16} клипов {clips.Count,3}: начало {start.Min():0.00}…{start.Max():0.00} м/с (стоял {standing}), " +
                $"выход {exit.Min():0.00}…{exit.Max():0.00} м/с, ранних выходов {early}"));
        }
        // контакты (биты: голеностопы L, R, носки L, R) — доля кадров циклов хода
        foreach (var gait in new[] { GaspGait.Walk, GaspGait.Run })
        {
            var loops = Enumerable.Range(0, db.ClipCount).Where(c => db.Clips[c].Loop && names[c].Kind == GaspKind.Loop && names[c].Gait == gait).ToList();
            double frames = loops.Sum(c => db.Clips[c].Count);
            double Share(Func<int, bool> contact) => loops.Sum(c => Enumerable.Range(db.Clips[c].Start, db.Clips[c].Count).Count(contact)) / frames;
            output.WriteLine(string.Create(Inv, $"контакты циклов {gait}: голеностоп L {Share(f => db.Contact(f, 0)):P0} R {Share(f => db.Contact(f, 1)):P0}, " +
                $"носок L {Share(f => db.Contact(f, 2)):P0} R {Share(f => db.Contact(f, 3)):P0}, стопа (голеностоп или носок) L " +
                $"{Share(f => db.Contact(f, 0) || db.Contact(f, 2)):P0} R {Share(f => db.Contact(f, 1) || db.Contact(f, 3)):P0}"));
        }
        for (int c = 0; c < db.ClipCount; c++)
        {
            if (!db.Clips[c].Loop) continue;
            var v = GaitSpeedModel.MeanVelocity(db, db.Clips[c]);
            float dir = MathF.Atan2(v.X, v.Z) * 180 / MathF.PI;
            output.WriteLine(string.Create(Inv, $"цикл {lib.Clips[db.Clips[c].LibraryClip].ShortName,-34} средняя {new Vector2(v.X, v.Z).Length():0.00} м/с " +
                $"{dir,7:0.0}°, по хорде {infos[c].MeanSpeed:0.00} м/с {infos[c].MeanDirection * 180 / MathF.PI,7:0.0}°, поворот за клип {infos[c].Turn * 180 / MathF.PI:0.0}°"));
        }
    }

    /// <summary>Кадр выхода нециклического клипа базы (у которого <see cref="MotionDatabase.Entry"/> ≥ 0); нет — −1.</summary>
    private static int Exit(MotionDatabase db, int clip)
    {
        var c = db.Clips[clip];
        for (int f = c.Start; f < c.End; f++)
            if (db.Entry[f] >= 0) return f;
        return -1;
    }

    /// <summary>Две симуляции берут базу так, как модуль, — из библиотеки <see cref="PosePool"/>: один экземпляр на процесс.</summary>
    [Fact]
    public void TwoSimulations_ShareOneBase()
    {
        var assets = TestContent.Reference.Value.Assets;
        GaspMotionBase? first = null, second = null;
        Simulation.Build(ConfigSet.Empty(), GameStages.All, b => GameCore.Configure(b, assets), [.. SwordSimulation.Modules(), new Probe(m => first = m)]);
        Simulation.Build(ConfigSet.Empty(), GameStages.All, b => GameCore.Configure(b, assets), [.. SwordSimulation.Modules(), new Probe(m => second = m)]);
        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Same(first, GaspMotionBase.For(assets.Clips, GaspMotion.DefaultClips));
        output.WriteLine(first.StartLine());
    }

    /// <summary>Модуль-зонд: при сборке мира берёт базу из библиотеки пула поз (как модуль Locomotion).</summary>
    private sealed class Probe(Action<GaspMotionBase> take) : IModule
    {
        public string Id => "mm_probe";

        public void Configure(SimulationBuilder b) => b.OnWorldBuilt(w => take(GaspMotionBase.For(w.Resource<PosePool>().Clips)));
    }

    /// <summary>Политика-обёртка: запоминает измерения клипов, которые база отдаёт политике (для вывода теста).</summary>
    private sealed class Recording(GaspGraphPolicy policy) : IMotionGraphPolicy
    {
        public MotionClipInfo[] Clips { get; private set; } = [];

        public void Continuations(int clip, ReadOnlySpan<MotionClipInfo> clips, List<int> candidates)
        {
            if (Clips.Length == 0) Clips = clips.ToArray();
            policy.Continuations(clip, clips, candidates);
        }

        public MotionPast Past(int clip, ReadOnlySpan<MotionClipInfo> clips)
        {
            if (Clips.Length == 0) Clips = clips.ToArray();
            return policy.Past(clip, clips);
        }
    }
}
