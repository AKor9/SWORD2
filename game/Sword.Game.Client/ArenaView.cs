using System.Numerics;
using Sword.Engine.Client;
using Sword.Engine.Diagnostics;
using Sword.Engine.Ecs;
using Sword.Game.Contracts;

namespace Sword.Game.Client;

/// <summary>
/// Боец в кадре: место на арене (<see cref="Slot"/>: игрок — 0, враги — 1…32; от него — цвет врага), корень, курс и
/// высота тела на рельефе — уже интерполированные по доле тика; поза последнего тика —
/// <see cref="PoseCount"/> модельных матриц с <see cref="PoseStart"/> в снимке кадра (<see cref="ArenaView.Pose"/>),
/// 0 — позы нет (рисуется поза привязки); капсулы тела, как показан боец, — <see cref="BodyCount"/> с
/// <see cref="BodyStart"/> (<see cref="ArenaView.Body"/>, 5в).
/// </summary>
public readonly record struct FighterView(Entity Entity, bool IsPlayer, Vector3 Root, float Facing, float Height,
    int PoseStart = 0, int PoseCount = 0, int Slot = 0, int BodyStart = 0, int BodyCount = 0)
{
    /// <summary>Мировая система скелета (<c>DuelArena.KinWorld</c> SWORD): поворот на курс, перенос в корень на высоте.</summary>
    public Matrix4x4 World => ArenaView.FighterWorld(Root, Facing, Height);
}

/// <summary>
/// Капсула тела бойца в кадре (5в): сегмент (<see cref="BodySegs"/>), центр и поворот (ось — локальная Y) — как показан
/// боец (<see cref="ArenaView.Carry"/>), радиус и полудлина оси, м.
/// </summary>
public readonly record struct ShownCapsule(int Segment, Vector3 Center, Quaternion Rotation, float Radius, float HalfLength)
{
    public Vector3 Axis => Vector3.Transform(Vector3.UnitY, Rotation);
    public Vector3 EndA => Center - Axis * HalfLength;
    public Vector3 EndB => Center + Axis * HalfLength;
}

/// <summary>
/// Мир представления арены (Extract, раздел 7 плана): что рисует кадр. Бойцы появляются и уходят по экспорту тика
/// (<c>Added&lt;Fighter&gt;</c>/<c>Removed&lt;Fighter&gt;</c> из очереди кадра), их корни — из мира симуляции между
/// прошлым и последним тиком по доле тика (<c>SkinCutPose.DrawnJoints</c> SWORD: корень — линейно, курс — по короткой
/// дуге; высоты тела от постановки стоп ещё нет — высота рельефа под интерполированным корнем по функции рельефа, как
/// <c>DuelArena.KinWorld(root, facing)</c> с <c>Terrain.Height</c>). Капсулы тел (вид <see cref="IBodyView"/>, 5в) — как
/// показан боец: постановка последнего тика, перенесённая из мира бойца на тике в показанный (<see cref="Carry"/>), — та
/// же поза тика в том же мире, что и меш, поэтому капсулы идут с персонажем без отставания. Точка тени и кольца игрока —
/// таз, как его показывает кадр; фокус камеры — таз между прошлой и нынешней постановкой по доле тика (<c>Program.cs</c>
/// SWORD). Рендер читает только этот мир; кадр без выделений памяти (буферы растут удвоением и остаются).
/// </summary>
public sealed class ArenaView
{
    private readonly Func<float, float, float> _height;
    private readonly List<Entity> _fighters = [];
    private readonly List<FighterView> _views = [];
    /// <summary>Позы бойцов кадра подряд — снимок пула поз после тиков кадра (рендер читает только его).</summary>
    private Matrix4x4[] _poses = [];
    /// <summary>Капсулы тел бойцов кадра подряд — как показаны бойцы.</summary>
    private ShownCapsule[] _bodies = [];
    private readonly BodySnapshot[] _snapshots = new BodySnapshot[BodySegs.Bodies];

    /// <param name="height">Высота рельефа в точке (x, z) — функция рельефа (<c>ArenaTerrain.Height</c>), не сетка рисования.</param>
    public ArenaView(Func<float, float, float> height) => _height = height;

    private CpuProfiler _profiler = CpuProfiler.Off;
    private int _posesZone, _bodiesZone;

    /// <summary>
    /// Профайлер CPU кадра (точка Па): копия поз бойцов — зона <c>arena.extract.poses</c>, чтение и перенос капсул тел —
    /// <c>arena.extract.bodies</c> (по вызову на бойца). Задаёт клиент арены из профайлера рендера.
    /// </summary>
    public CpuProfiler Profiler
    {
        get => _profiler;
        set
        {
            _posesZone = value.Zone("arena.extract.poses");
            _bodiesZone = value.Zone("arena.extract.bodies");
            _profiler = value;
        }
    }

    /// <summary>Бойцов, пропущенных кадром: у сущности нет корня или корня прошлого тика (ошибка симуляции).</summary>
    public int Skipped { get; private set; }

    /// <summary>Бойцы кадра.</summary>
    public IReadOnlyList<FighterView> Fighters => _views;

    /// <summary>Боец игрока в кадре; нет — до первого тика с его появлением.</summary>
    public FighterView? Player { get; private set; }

    /// <summary>
    /// Точка тени и кольца игрока на земле (<c>Program.Fill</c> SWORD: таз тела) — центр капсулы таза, как её показывает кадр:
    /// идёт вместе с Manny (в SWORD — таз последнего тика без интерполяции, и кольцо дрожит относительно плавно идущего
    /// бойца); нет игрока или его тела — null.
    /// </summary>
    public Vector3? PlayerMarker { get; private set; }

    /// <summary>
    /// Фокус камеры (<c>Program.cs</c> SWORD: <c>Lerp(pel.RenderPos, pel.Pos, renderAlpha) with { Y = 0 }</c>) — центр таза
    /// между прошлой и нынешней постановкой по доле тика этого кадра; камера следующего кадра идёт за ним (как в SWORD —
    /// по доле прошлого кадра). Нет игрока или его тела — null.
    /// </summary>
    public Vector3? PlayerFocus { get; private set; }

    /// <summary>Капсул тел в кадре.</summary>
    public int BodyCount { get; private set; }

    /// <summary>Доля тика кадра.</summary>
    public float Alpha { get; private set; } = 1;

    /// <summary>Номер последнего тика, время симуляции на нём и шаг тика — снимок кадра для констант рендера.</summary>
    public ulong LastTick { get; private set; }
    public float Time { get; private set; }
    public float TickDt { get; private set; }

    /// <summary>
    /// Корень игрока в симуляции на последнем тике (не интерполированный) — итог Sword.exe, сверка с Headless; нет игрока
    /// в кадре — null.
    /// </summary>
    public Transform? PlayerAtTick { get; private set; }

    /// <summary>Мир представления кадра — всё, что читает рендер, снимается здесь; <c>BuildFrame</c> симуляцию не читает.</summary>
    public void Extract(SimulationLink simulation, float alpha)
    {
        var world = simulation.World;
        ApplyFighterRecords(simulation.Frame<Added<Fighter>>(), simulation.Frame<Removed<Fighter>>());
        // Итог сверяется с миром: взаимный порядок снятия и добавления в стадиях одного тика двумя очередями не передаётся,
        // а мир после тиков кадра — правда о том, кто сейчас боец. Циклом, не RemoveAll с лямбдой: кадр без выделений.
        for (int i = _fighters.Count - 1; i >= 0; i--)
            if (!world.Has<Fighter>(_fighters[i])) _fighters.RemoveAt(i);

        Alpha = alpha;
        LastTick = simulation.Tick > 0 ? simulation.Tick - 1 : 0;
        Time = simulation.Time;
        TickDt = simulation.Dt;
        Player = null;
        PlayerAtTick = null;
        PlayerMarker = PlayerFocus = null;
        Skipped = 0;
        _views.Clear();
        world.TryView<IAnimationView>(out var poses);
        world.TryView<IBodyView>(out var bodies);
        var profiler = _profiler;
        int posed = 0, bodied = 0;
        foreach (var e in _fighters)
        {
            if (!world.TryGet<Transform>(e, out var cur) || !world.TryGet<PrevTransform>(e, out var prev))
            {
                Skipped++;
                continue;
            }
            var view = Interpolate(e, world.Has<PlayerTag>(e), prev, cur, alpha, _height);
            if (world.TryGet<FighterSlot>(e, out var slot)) view = view with { Slot = slot.Index };
            // Поза — последнего тика, без интерполяции между тиками (как SkinnedCharacters SWORD): плавно идёт корень.
            profiler.Begin(_posesZone);
            if (poses is not null && world.TryGet<PoseRef>(e, out var poseRef) && poses.Model(poseRef) is { Length: > 0 } model)
            {
                if (_poses.Length < posed + model.Length) Array.Resize(ref _poses, Math.Max(_poses.Length * 2, posed + model.Length));
                model.CopyTo(_poses.AsSpan(posed));
                view = view with { PoseStart = posed, PoseCount = model.Length };
                posed += model.Length;
            }
            profiler.End(_posesZone);
            // Тело — постановка последнего тика в мире, где кадр показывает бойца (как меш): без отставания от персонажа.
            profiler.Begin(_bodiesZone);
            if (bodies is not null && world.TryGet<BodyRef>(e, out var bodyRef) && ReadBody(bodies, bodyRef) is > 0 and var n)
            {
                if (_bodies.Length < bodied + n) Array.Resize(ref _bodies, Math.Max(_bodies.Length * 2, bodied + n));
                var tickWorld = FighterWorld(cur.Position, cur.Facing, _height(cur.Position.X, cur.Position.Z));
                Matrix4x4.Invert(tickWorld, out var back);
                var carry = back * view.World;
                var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, view.Facing - cur.Facing);
                for (int i = 0; i < n; i++) _bodies[bodied + i] = Carry(_snapshots[i], BodySegs.Segment(i), carry, turn);
                view = view with { BodyStart = bodied, BodyCount = n };
                int pelvis = BodySegs.Body(BodySegs.Pelvis);
                if (view.IsPlayer && pelvis < n)
                {
                    PlayerMarker = _bodies[bodied + pelvis].Center with { Y = 0 };
                    PlayerFocus = Lerp(_snapshots[pelvis], alpha).Center with { Y = 0 };
                }
                bodied += n;
            }
            profiler.End(_bodiesZone);
            _views.Add(view);
            if (view.IsPlayer)
            {
                Player = view;
                PlayerAtTick = cur;
            }
        }
        BodyCount = bodied;
    }

    /// <summary>Тела сочленения в <see cref="_snapshots"/> (не больше 13); вернёт, сколько.</summary>
    private int ReadBody(IBodyView bodies, BodyRef body)
    {
        int n = Math.Min(bodies.BodyCount(body), _snapshots.Length);
        for (int i = 0; i < n; i++) _snapshots[i] = bodies.Body(body, i);
        return n;
    }

    /// <summary>Поза бойца кадра — модельные матрицы суставов последнего тика; пусто — позы нет.</summary>
    public ReadOnlySpan<Matrix4x4> Pose(in FighterView fighter) => _poses.AsSpan(fighter.PoseStart, fighter.PoseCount);

    /// <summary>Капсулы тела бойца кадра — как показан боец; пусто — тела нет.</summary>
    public ReadOnlySpan<ShownCapsule> Body(in FighterView fighter) => _bodies.AsSpan(fighter.BodyStart, fighter.BodyCount);

    /// <summary>
    /// Появления и уходы бойцов — по тикам кадра по порядку, внутри тика уход раньше появления (нормализация барьера:
    /// снят и добавлен заново — <c>Removed</c>, затем <c>Added</c>).
    /// </summary>
    private void ApplyFighterRecords(ReadOnlySpan<(ulong Tick, Added<Fighter> Record)> added,
        ReadOnlySpan<(ulong Tick, Removed<Fighter> Record)> removed)
    {
        int a = 0, r = 0;
        while (a < added.Length || r < removed.Length)
        {
            ulong tick = Math.Min(a < added.Length ? added[a].Tick : ulong.MaxValue, r < removed.Length ? removed[r].Tick : ulong.MaxValue);
            for (; r < removed.Length && removed[r].Tick == tick; r++) _fighters.Remove(removed[r].Record.Entity);
            for (; a < added.Length && added[a].Tick == tick; a++)
                if (!_fighters.Contains(added[a].Record.Entity)) _fighters.Add(added[a].Record.Entity);
        }
    }

    /// <summary>Интерполяция корня между прошлым и последним тиком (<c>SkinCutPose.DrawnJoints</c> SWORD).</summary>
    public static FighterView Interpolate(Entity e, bool isPlayer, in PrevTransform prev, in Transform cur, float alpha,
        Func<float, float, float> height)
    {
        var root = Vector3.Lerp(prev.Position, cur.Position, alpha);
        float facing = prev.Facing + MathF.IEEERemainder(cur.Facing - prev.Facing, MathF.Tau) * alpha;
        return new FighterView(e, isPlayer, root, facing, height(root.X, root.Z));
    }

    /// <summary>Мировая система скелета (<c>DuelArena.KinWorld</c> SWORD): поворот на курс, перенос в корень на высоте.</summary>
    public static Matrix4x4 FighterWorld(Vector3 root, float facing, float height)
    {
        var m = Matrix4x4.CreateFromQuaternion(Quaternion.CreateFromAxisAngle(Vector3.UnitY, facing));
        m.Translation = new Vector3(root.X, height, root.Z);
        return m;
    }

    /// <summary>
    /// Капсула живого бойца, как показан боец (<c>SkinCutPose.DrawnJoints</c> SWORD: поза тика в интерполированном мире
    /// бойца): постановка последнего тика переносится из мира бойца на тике в показанный — <paramref name="carry"/> =
    /// обратный мир на тике · показанный мир, <paramref name="turn"/> — его поворот (на разность курсов). Меш рисуется той же
    /// позой тика в том же показанном мире, поэтому капсулы стоят на нём при любой доле тика; при доле 1 — ровно постановка
    /// тика.
    /// </summary>
    public static ShownCapsule Carry(in BodySnapshot body, int segment, in Matrix4x4 carry, Quaternion turn) =>
        new(segment, Vector3.Transform(body.Position, carry), Quaternion.Normalize(Quaternion.Concatenate(body.Rotation, turn)),
            body.Radius, body.HalfLength);

    /// <summary>
    /// Тело между прошлой и нынешней постановкой по доле тика (<c>Fighter.BodyMatrix(b, alpha)</c> SWORD: центр — линейно,
    /// поворот — <c>Slerp</c>; у кинематического тела прошлая постановка — это <c>RenderPos/RenderRot</c> SWORD). Так SWORD
    /// ведёт камеру за тазом; так же будут рисоваться тела, которые не следуют позе (трупы, обрубки — этапы 7, 11). Капсулы
    /// живого бойца рисуются <see cref="Carry"/>: интерполяция постановок отстаёт от меша на (1 − доля) изменения позы за тик.
    /// </summary>
    public static (Vector3 Center, Quaternion Rotation) Lerp(in BodySnapshot body, float alpha) =>
        (Vector3.Lerp(body.PrevPosition, body.Position, alpha), Quaternion.Slerp(body.PrevRotation, body.Rotation, alpha));
}
