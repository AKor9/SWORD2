using System.Numerics;
using Sword.Engine.Client;
using Sword.Engine.Render;
using Sword.Engine.Runtime;
using Sword.Game.Arena;
using Sword.Game.Client;
using Sword.Game.Contracts;
using Sword.Game.Cutting;
using Xunit.Abstractions;

namespace Sword.Game.Tests.Cutting;

/// <summary>
/// Части меша после разрезов на паке — как их рисует арена: клиентский разрез (<see cref="SkinCuts"/>) по записям симуляции, каждая часть —
/// позой своего куска (<see cref="ArenaView.PiecePose"/>). Кожа не тянется: ребро треугольника части в позе кадра не длиннее
/// <see cref="MaxStretch"/> своей длины в привязке.
/// </summary>
[Trait("Category", "Content")]
public sealed class SkinPartsTests(ITestOutputHelper output)
{
    /// <summary>Наибольшее удлинение ребра части (кожа у суставов в позе тянется и без разрезов, до ~1,6).</summary>
    private const float MaxStretch = 2f;

    /// <summary>Рёбра короче — не меряются (крышки и швы резака).</summary>
    private const float MinEdge = 0.01f;

    private sealed class CpuMeshes : ICutMeshes
    {
        public readonly Dictionary<int, CutMesh> Meshes = [];
        private int _next = 1;

        public int Add(string name, CutMesh mesh)
        {
            Meshes[_next] = mesh;
            return _next++;
        }

        public void Remove(int mesh) => Assert.True(Meshes.Remove(mesh), $"меш {mesh} освобождён дважды");

        public long Bytes(int mesh) => Meshes[mesh].Vertices.Length * 64L;
    }

    [Fact]
    public void Parts_FollowTheirBodies_WithoutStretch()
    {
        var sim = Sword.Game.Bootstrap.SwordSimulation.Create(
            ConfigSet.Empty(["arena.enemies=7", "combat.enemiesAttack=false"]), BloodBodyTests.Pak.Value);
        var skin = BloodBodyTests.PakMesh.Value;
        var parts = new FighterSkinParts { BaseMesh = 0, MeshPath = "manny" };
        SkinCuts? cuts = null;
        var clock = new SimulationClock(sim, (_, _) => { }, 1, afterTick: t => cuts!.AfterTick(t));
        cuts = new SkinCuts(parts, new ArenaFighterPoses(clock.Link), skin, new ArenaSeverFeed(clock.Link));
        var meshes = new CpuMeshes();
        meshes.Meshes[0] = Base(skin);
        cuts.Init(new SkinnedMeshCpu
        {
            Positions = skin.Positions.ToArray(), Normals = skin.Normals.ToArray(), Indices = skin.Indices.ToArray(), Joints = skin.Joints.ToArray(),
            Weights = skin.Weights.ToArray(), InverseBind = skin.InverseBind.ToArray(), JointNames = skin.JointNames.ToArray(),
        }, meshes);
        var view = new ArenaView(ArenaTerrain.Height);
        var inverse = skin.InverseBind.ToArray();

        sim.Enqueue(new SceneCommand { Action = SceneAction.Place, Slot = 0, Position = new Vector2(0, 0), Facing = 0 });
        Vector2[] at = [new(0, 1.6f), new(2.2f, 1.6f), new(-2.5f, 2.5f), new(3f, -2f), new(-3f, -2f), new(0, -3f), new(-1.5f, -4f)];
        for (int s = 0; s < at.Length; s++)
            sim.Enqueue(new SceneCommand { Action = SceneAction.Place, Slot = s + 1, Position = at[s], Facing = MathF.PI });
        var worst = new Dictionary<string, (float Ratio, string Where)>();
        var islands = new Dictionary<string, string>();
        for (int t = 0; t < 600; t++)
        {
            if (t == 40) Probe(sim, 7, BodySegs.Torso, 300, HitKind.Thrust);   // смерть без разреза — отсчёт
            if (t == 60) Probe(sim, 4, BodySegs.Torso, 120);                    // торс пополам у живого
            if (t == 120) Probe(sim, 1, BodySegs.UArmL, 60);                    // рука у живого
            if (t == 240) Probe(sim, 1, BodySegs.FArmR, 60);
            if (t == 360) Probe(sim, 1, BodySegs.ThighR, 70);                   // нога — смерть
            if (t == 480) Probe(sim, 1, BodySegs.Head, 60);                     // голова трупа
            if (t == 150) Probe(sim, 2, BodySegs.FArmL, 40, HitKind.Blunt);     // перелом
            if (t == 300) Probe(sim, 2, BodySegs.Head, 80, HitKind.Blunt);      // голова в клочья
            if (t == 200) Probe(sim, 3, BodySegs.ShinL, 80, HitKind.Blunt);     // отрыв голени у живого
            if (t == 260) Probe(sim, 3, BodySegs.UArmR, 80, HitKind.Blunt);     // и плеча
            if (t == 330) Probe(sim, 3, BodySegs.UArmL, 60);                    // разрез после отрывов: первый кусок у бойца с частями
            if (t == 100) Probe(sim, 5, BodySegs.UArmR, 60, HitKind.Blunt);     // молот по рукам и торсу живого
            if (t == 180) Probe(sim, 5, BodySegs.Torso, 80, HitKind.Blunt);
            if (t == 280) Probe(sim, 5, BodySegs.ThighL, 80, HitKind.Blunt);
            if (t == 140) Probe(sim, 6, BodySegs.Head, 60);                     // голова живого — смерть, затем молот по трупу
            if (t == 220) Probe(sim, 6, BodySegs.Torso, 80, HitKind.Blunt);
            if (t == 320) Probe(sim, 6, BodySegs.ShinR, 80, HitKind.Blunt);
            clock.RunFrame(0);
            while (true)
            {
                cuts.Update();
                // кадр арены между обновлениями: рисуются только живые меши
                foreach (var fighter in view.Fighters)
                    if (parts.TryGet(FighterKey.Of(fighter.Entity), out var drawn))
                        foreach (var part in drawn)
                            Assert.True(meshes.Meshes.ContainsKey(part.Mesh), $"тик {t}: слот {fighter.Slot} рисует освобождённый меш {part.Mesh}");
                if (cuts.Idle) break;
                Thread.Sleep(1);
            }
            view.Extract(clock.Link, 1, allBodies: false);
            foreach (var fighter in view.Fighters)
            {
                if (!fighter.Severed || !parts.TryGet(FighterKey.Of(fighter.Entity), out var list))
                {
                    if (view.Pose(fighter).IsEmpty) continue;
                    var whole = Stretch(meshes.Meshes[0], inverse, World(view.Pose(fighter), fighter), skin.JointNames);
                    string k0 = $"целый слот {fighter.Slot}{(fighter.Corpse ? " труп" : "")}";
                    if (!worst.TryGetValue(k0, out var w0) || whole.Ratio > w0.Ratio) worst[k0] = (whole.Ratio, $"тик {t}: {whole.Where}");
                    continue;
                }
                Assert.True(fighter.PoseInWorld, $"боец {fighter.Slot} с частями — поза не в мире");
                foreach (var part in list)
                {
                    var (ratio, where) = Stretch(meshes.Meshes[part.Mesh], inverse, view.PiecePose(fighter, part.Piece), skin.JointNames);
                    string key = $"слот {fighter.Slot} кусок {part.Piece}";
                    if (!islands.ContainsKey(key)) islands[key] = Islands(meshes.Meshes[part.Mesh], skin.JointNames.ToArray());
                    if (!worst.TryGetValue(key, out var w) || ratio > w.Ratio) worst[key] = (ratio, $"тик {t}: {where}");
                }
            }
        }
        foreach (var (key, w) in worst.OrderBy(x => x.Key)) output.WriteLine($"{key}: {w.Ratio:F2} ({w.Where}) острова: {islands.GetValueOrDefault(key)}");
        Assert.True(cuts.Cuts >= 8, $"разрезов {cuts.Cuts}");
        var bad = worst.Where(x => x.Value.Ratio > MaxStretch).Select(x => $"{x.Key}: {x.Value.Ratio:F2} {x.Value.Where}").ToArray();
        Assert.True(bad.Length == 0, "кожа тянется: " + string.Join("; ", bad));
    }

    /// <summary>
    /// Игрок на паке (анимация боя и шагов) теряет кисть с мечом от удара врага: меч рисуется на своём теле, падает и не едет за
    /// игроком, который машет и уходит.
    /// </summary>
    [Theory]
    [InlineData(BodySegs.FArmR, false)]
    [InlineData(BodySegs.UArmR, false)]
    [InlineData(BodySegs.FArmR, true)]
    public void PlayerLosesSwordHand_SwordDrawnOnItsBody(int part, bool crowd)
    {
        string[] sets = crowd ? ["arena.enemies=3"] : ["arena.enemies=1", "combat.enemiesAttack=false"];
        var sim = Sword.Game.Bootstrap.SwordSimulation.Create(ConfigSet.Empty(sets), BloodBodyTests.Pak.Value);
        var clock = new SimulationClock(sim, (_, _) => { }, 1);
        var view = new ArenaView(ArenaTerrain.Height);
        void Frame()
        {
            clock.RunFrame(0);
            view.Extract(clock.Link, 1, allBodies: false);
        }
        sim.Enqueue(new SceneCommand { Action = SceneAction.Place, Slot = 0, Position = new Vector2(0, 0), Facing = 0 });
        sim.Enqueue(new SceneCommand { Action = SceneAction.Place, Slot = 1, Position = new Vector2(0, 1.6f), Facing = MathF.PI });
        for (int t = 0; t < 30; t++) Frame();
        sim.Enqueue(new StrikeProbe { Attacker = 1, Victim = 0, Part = part, Damage = 90, Kind = HitKind.Slash });
        Frame();
        float worst = 0, high = 0, travel = 0;
        Vector3 at = default, body = default, rest = default;
        for (int t = 0; t < 900; t++)
        {
            // игрок ходит кругами над выпавшим мечом и машет оставшейся рукой
            float a = t * 0.02f;
            sim.Enqueue(new PlayerCommand { Attack = t % 50 < 25, Move = t >= 60 ? new Vector2(MathF.Cos(a), MathF.Sin(a)) : default });
            Frame();
            var player = view.Fighters.Single(f => f.IsPlayer);
            if (player.Weapon < 0) continue;
            var reader = clock.Link.World;
            Assert.True(reader.TryGet<Severance>(player.Entity, out var sev) && sev.IsDetached(BodySegs.Weapon), "меч не отделён");
            var bodies = reader.View<IBodyView>();
            var blade = bodies.Body(reader.Get<BodyRef>(player.Entity), BodySegs.Weapon).Position;
            float d = Vector3.Distance(player.WeaponWorld.Translation, blade);
            if (d > worst) (worst, at, body) = (d, player.WeaponWorld.Translation, blade);
            if (t == 59) rest = blade;
            if (t >= 60)
            {
                high = MathF.Max(high, blade.Y - ArenaTerrain.Height(blade.X, blade.Z));
                travel = MathF.Max(travel, Vector3.Distance(blade, rest));
            }
        }
        output.WriteLine($"меч: выше земли до {high:F2} м, от места падения до {travel:F2} м");
        Assert.True(worst < 0.6f, $"меч рисуется в {at}, тело клинка в {body}");
        Assert.True(high < 0.4f, $"меч поднят на {high:F2} м");
    }

    /// <summary>
    /// Боец без левой руки держит меч правой: меч рисуется в кисти — клинок рисунка совпадает с клинком удара тика (поза живого с
    /// частями уже в мире, мир бойца не прикладывается дважды). Игрок и враг, стоят и ходят.
    /// </summary>
    [Theory]
    [InlineData(0, BodySegs.FArmL)]
    [InlineData(0, BodySegs.UArmL)]
    [InlineData(1, BodySegs.UArmL)]
    public void LeftArmLost_SwordStaysInHand(int victim, int part)
    {
        var sim = Sword.Game.Bootstrap.SwordSimulation.Create(ConfigSet.Empty(["arena.enemies=1", "combat.enemiesAttack=false"]),
            BloodBodyTests.Pak.Value);
        var clock = new SimulationClock(sim, (_, _) => { }, 1);
        var view = new ArenaView(ArenaTerrain.Height);
        void Frame()
        {
            clock.RunFrame(0);
            view.Extract(clock.Link, 1, allBodies: false);
        }
        sim.Enqueue(new SceneCommand { Action = SceneAction.Place, Slot = 0, Position = new Vector2(2, 3), Facing = 1 });
        sim.Enqueue(new SceneCommand { Action = SceneAction.Place, Slot = 1, Position = new Vector2(2, 4.6f), Facing = MathF.PI });
        for (int t = 0; t < 30; t++) Frame();
        sim.Enqueue(new StrikeProbe { Attacker = 1 - victim, Victim = victim, Part = part, Damage = 90, Kind = HitKind.Slash });
        Frame();
        var reader = clock.Link.World;
        var weapons = reader.View<ICombatView>().Weapons;
        float worst = 0;
        int checkedFrames = 0;
        for (int t = 0; t < 120; t++)
        {
            if (victim == 0) sim.Enqueue(new PlayerCommand { Attack = t % 50 < 25, Move = t >= 60 ? new Vector2(1, 0) : default });
            Frame();
            var f = view.Fighters.Single(x => x.Slot == victim);
            Assert.True(f.Severed && reader.TryGet<Severance>(f.Entity, out var sev) && !sev.IsDetached(BodySegs.Weapon), "левая рука не отделена или меч выпал");
            Assert.True(f.Weapon >= 0, "меч не рисуется");
            var blade = reader.Get<Blade>(f.Entity);
            var drawn = Vector3.Transform(weapons[f.Weapon].BladeBase, f.WeaponWorld);
            worst = MathF.Max(worst, Vector3.Distance(drawn, blade.Base));
            checkedFrames++;
        }
        output.WriteLine($"кадров {checkedFrames}, расхождение основания клинка до {worst:F3} м");
        Assert.True(worst < 0.05f, $"меч рисуется в {worst:F2} м от кисти");
    }

    /// <summary>Лента отделений с подменой события (плоскость разреза).</summary>
    private sealed class ChangedFeed(ISeverFeed inner, Func<LimbDetached, LimbDetached> change) : ISeverFeed
    {
        public ulong Tick => inner.Tick;

        public ReadOnlySpan<SeverRecord> Detached => inner.Detached.ToArray().Select(r => r with { Event = change(r.Event) }).ToArray();
    }

    /// <summary>
    /// Торс пополам на любой высоте и с наклоном плоскости: кусок — один остров с головой и обеими кистями, тело — один остров со стопами,
    /// без головы и кистей (плечи не остаются у культи, руки не висят отдельно от груди).
    /// </summary>
    [Fact]
    public void TorsoCut_AnyHeight_PieceKeepsHeadAndArms()
    {
        var sim = Sword.Game.Bootstrap.SwordSimulation.Create(
            ConfigSet.Empty(["arena.enemies=1", "combat.enemiesAttack=false"]), BloodBodyTests.Pak.Value);
        var skin = BloodBodyTests.PakMesh.Value;
        var names = skin.JointNames.ToArray();
        var cpu = new SkinnedMeshCpu
        {
            Positions = skin.Positions.ToArray(), Normals = skin.Normals.ToArray(), Indices = skin.Indices.ToArray(), Joints = skin.Joints.ToArray(),
            Weights = skin.Weights.ToArray(), InverseBind = skin.InverseBind.ToArray(), JointNames = names,
        };
        ArenaFighterPoses? poses = null;
        ArenaSeverFeed? feed = null;
        var failures = new List<string>();
        int variants = 0;
        void Check(ulong tick)
        {
            if (feed!.Detached.IsEmpty || feed.Detached[0].Event.Segment != BodySegs.Torso) return;
            var e0 = feed.Detached[0].Event;
            foreach (var (axis, tilt) in new[] { (Vector3.UnitX, 0f), (Vector3.UnitX, 0.5f), (Vector3.UnitX, -0.5f), (Vector3.UnitZ, 0.5f), (Vector3.UnitZ, -0.5f) })
                for (float offset = -0.2f; offset <= 0.201f; offset += 0.05f)
                {
                    var normal = Vector3.Transform(Vector3.UnitY, Quaternion.CreateFromAxisAngle(axis, tilt));
                    var parts = new FighterSkinParts { BaseMesh = 0, MeshPath = "manny" };
                    var meshes = new CpuMeshes();
                    meshes.Meshes[0] = Base(skin);
                    ushort carried = Carried(poses!, feed.Detached[0].Fighter, e0 with { Normal = normal, Offset = offset });
                    var cuts = new SkinCuts(parts, poses!, skin, new ChangedFeed(feed, e => e with { Normal = normal, Offset = offset, Carried = carried }));
                    cuts.Init(cpu, meshes);
                    cuts.AfterTick(tick);
                    while (true)
                    {
                        cuts.Update();
                        if (cuts.Idle) break;
                        Thread.Sleep(1);
                    }
                    variants++;
                    string at = $"плоскость {axis}·{tilt:F1} на {offset:F2} м";
                    if (!parts.TryGet(feed.Detached[0].Fighter, out var list) || list.Length != 2)
                    {
                        failures.Add($"{at}: частей {list.Length}");
                        continue;
                    }
                    // голова и руки — с куском, если ушли с ним, иначе у тела; стопы — у тела; каждая часть — один остров
                    (string Name, int Segment)[] ends = [("head", BodySegs.Head), ("hand_l", BodySegs.UArmL), ("hand_r", BodySegs.UArmR), ("foot_l", -1), ("foot_r", -1)];
                    foreach (var part in list)
                    {
                        var mesh = meshes.Meshes[part.Mesh];
                        var (sizes, has) = (IslandSizes(mesh), Dominants(mesh, names));
                        bool piece = part.Piece >= 0;
                        var wrong = ends.Where(x => has.Contains(x.Name) != (piece == (x.Segment >= 0 && (carried & (1 << x.Segment)) != 0))).Select(x => x.Name);
                        if (sizes.Count != 1 || wrong.Any())
                            failures.Add($"{at}, {(piece ? "кусок" : "тело")} (с куском {carried:X}): острова {string.Join("/", sizes)}, не на месте {string.Join(",", wrong)}");
                    }
                }
        }
        var clock = new SimulationClock(sim, (_, _) => { }, 1, afterTick: Check);
        poses = new ArenaFighterPoses(clock.Link);
        feed = new ArenaSeverFeed(clock.Link);
        sim.Enqueue(new SceneCommand { Action = SceneAction.Place, Slot = 0, Position = new Vector2(0, 0), Facing = 0 });
        sim.Enqueue(new SceneCommand { Action = SceneAction.Place, Slot = 1, Position = new Vector2(0, 1.6f), Facing = MathF.PI });
        for (int t = 0; t < 62; t++)
        {
            if (t == 60) Probe(sim, 1, BodySegs.Torso, 120);
            clock.RunFrame(0);
        }
        Assert.True(variants > 0, "разреза торса не было");
        output.WriteLine($"вариантов {variants}, плохих {failures.Count}");
        foreach (var f in failures) output.WriteLine(f);
        Assert.Empty(failures);
    }

    /// <summary>Дети торса, уходящие с куском по плоскости <paramref name="e"/>, — по телам тика (культя — со сдвигом записи).</summary>
    private static ushort Carried(ArenaFighterPoses poses, ulong fighter, in LimbDetached e)
    {
        var segments = poses.Segments(poses.Find(fighter));
        var positions = new Vector3[BodySegs.Count];
        var rotations = new Quaternion[BodySegs.Count];
        var halves = new float[BodySegs.Count];
        for (int s = 0; s < BodySegs.Count; s++) (positions[s], rotations[s], halves[s]) = (segments[s].Position, segments[s].Rotation, segments[s].HalfLength);
        var center = positions[e.Segment] - Vector3.Transform(Vector3.UnitY, rotations[e.Segment]) * e.StumpShift;
        ushort children = (ushort)(1 << BodySegs.Head | 1 << BodySegs.UArmL | 1 << BodySegs.UArmR);
        return SeverGeometry.Carried(e.Segment, e.Normal, e.Offset, center, rotations[e.Segment], positions, rotations, halves, children);
    }

    /// <summary>Размеры островов части (≥ 2 % треугольников).</summary>
    private static List<int> IslandSizes(CutMesh mesh)
    {
        var idx = mesh.Indices;
        var parent = new int[mesh.Vertices.Length];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;
        int Find(int x)
        {
            while (parent[x] != x) x = parent[x] = parent[parent[x]];
            return x;
        }
        var weld = new Dictionary<(int, int, int), int>();
        foreach (uint u in idx)
        {
            var p = mesh.Vertices[u].Position * 2000;
            var cell = ((int)MathF.Round(p.X), (int)MathF.Round(p.Y), (int)MathF.Round(p.Z));
            if (weld.TryGetValue(cell, out int other)) parent[Find((int)u)] = Find(other);
            else weld[cell] = (int)u;
        }
        for (int k = 0; k < idx.Length; k += 3)
        {
            parent[Find((int)idx[k + 1])] = Find((int)idx[k]);
            parent[Find((int)idx[k + 2])] = Find((int)idx[k]);
        }
        var size = new Dictionary<int, int>();
        for (int k = 0; k < idx.Length; k += 3) size[Find((int)idx[k])] = size.GetValueOrDefault(Find((int)idx[k])) + 1;
        return size.Values.Where(s => s * 50 >= idx.Length / 3).OrderDescending().ToList();
    }

    /// <summary>Имена главных суставов вершин части.</summary>
    private static HashSet<string> Dominants(CutMesh mesh, string[] names)
    {
        var set = new HashSet<string>();
        foreach (uint i in mesh.Indices)
        {
            var v = mesh.Vertices[i];
            int best = 0;
            for (int k = 1; k < 4; k++)
                if (CutField.Weight(v, k) > CutField.Weight(v, best)) best = k;
            int j = (int)(v.Joints >> (8 * best) & 0xFF);
            if (j < names.Length) set.Add(names[j]);
        }
        return set;
    }

    private static Matrix4x4[] World(ReadOnlySpan<Matrix4x4> pose, in FighterView fighter)
    {
        var joints = pose.ToArray();
        if (!fighter.PoseInWorld)
            for (int j = 0; j < joints.Length; j++) joints[j] *= fighter.World;
        return joints;
    }

    private static CutMesh Base(BloodBodyTests.PakSkinMesh skin)
    {
        var v = new CutVertex[skin.Positions.Length];
        for (int i = 0; i < v.Length; i++)
            v[i] = new CutVertex { Position = skin.Positions[i], Joints = skin.Joints[i], Weights = new(skin.Weights[i].X, skin.Weights[i].Y, skin.Weights[i].Z) };
        return new CutMesh { Vertices = v, Indices = skin.Indices.ToArray() };
    }

    /// <summary>Наибольшее удлинение ребра треугольника части в позе (палитра InverseBind · сустав в мире, как у рендера).</summary>
    private static (float Ratio, string Where) Stretch(CutMesh mesh, Matrix4x4[] inverse, ReadOnlySpan<Matrix4x4> joints, ReadOnlySpan<string> names)
    {
        var palette = new Matrix4x4[inverse.Length];
        for (int j = 0; j < palette.Length; j++) palette[j] = inverse[j] * joints[j];
        var posed = new Vector3[mesh.Vertices.Length];
        var used = new bool[posed.Length];
        foreach (uint i in mesh.Indices) used[i] = true;
        for (int i = 0; i < posed.Length; i++)
            if (used[i]) posed[i] = CutField.Posed(mesh.Vertices[i], palette);
        float best = 0;
        int a0 = 0, b0 = 0;
        var idx = mesh.Indices;
        for (int k = 0; k < idx.Length; k += 3)
            for (int e = 0; e < 3; e++)
            {
                int a = (int)idx[k + e], b = (int)idx[k + (e + 1) % 3];
                float rest = Vector3.Distance(mesh.Vertices[a].Position, mesh.Vertices[b].Position);
                if (rest < MinEdge) continue;
                float extension = Vector3.Distance(posed[a], posed[b]) - rest;
                if (extension > best) (best, a0, b0) = (extension, a, b);
            }
        float rest0 = Vector3.Distance(mesh.Vertices[a0].Position, mesh.Vertices[b0].Position);
        return (best, $"{rest0 * 100:F1} см → {Vector3.Distance(posed[a0], posed[b0]) * 100:F1} см; {Joints(mesh.Vertices[a0], names)} — {Joints(mesh.Vertices[b0], names)}");
    }

    /// <summary>Острова части (треугольники, связанные общими позициями вершин в привязке): число треугольников и главный сустав.</summary>
    private static string Islands(CutMesh mesh, string[] names)
    {
        var idx = mesh.Indices;
        int n = idx.Length / 3;
        var parent = new int[mesh.Vertices.Length];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;
        int Find(int x)
        {
            while (parent[x] != x) x = parent[x] = parent[parent[x]];
            return x;
        }
        var weld = new Dictionary<(int, int, int), int>();
        foreach (uint u in idx)
        {
            var p = mesh.Vertices[u].Position * 2000;
            var cell = ((int)MathF.Round(p.X), (int)MathF.Round(p.Y), (int)MathF.Round(p.Z));
            if (weld.TryGetValue(cell, out int other)) parent[Find((int)u)] = Find(other);
            else weld[cell] = (int)u;
        }
        for (int k = 0; k < idx.Length; k += 3)
        {
            parent[Find((int)idx[k + 1])] = Find((int)idx[k]);
            parent[Find((int)idx[k + 2])] = Find((int)idx[k]);
        }
        var size = new Dictionary<int, (int Count, int Vertex)>();
        for (int k = 0; k < idx.Length; k += 3)
        {
            int r = Find((int)idx[k]);
            size[r] = size.TryGetValue(r, out var s) ? (s.Count + 1, s.Vertex) : (1, (int)idx[k]);
        }
        return string.Join(", ", size.Values.Where(s => s.Count * 50 >= n).OrderByDescending(s => s.Count)
            .Select(s => $"{s.Count} [{Joints(mesh.Vertices[s.Vertex], names)}]")) + $" из {size.Count}";
    }

    private static string Joints(in CutVertex v, ReadOnlySpan<string> names)
    {
        var s = new List<string>();
        for (int k = 0; k < 4; k++)
            if (CutField.Weight(v, k) is var w && w > 0.05f && (int)(v.Joints >> (8 * k) & 0xFF) is var j && j < names.Length)
                s.Add($"{names[j]} {w:F2}");
        return string.Join(",", s);
    }

    private static void Probe(Simulation sim, int victim, int part, float damage, HitKind kind = HitKind.Slash) =>
        sim.Enqueue(new StrikeProbe { Attacker = 0, Victim = victim, Part = part, Damage = damage, Kind = kind });
}
