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
/// <see cref="BodyStart"/> (<see cref="ArenaView.Body"/>, 5в); меч в руке (6б) — номер <see cref="Weapon"/> в таблице оружия
/// (−1 — меча нет) и мир его меша <see cref="WeaponWorld"/> (кисть позы, как показан боец); клинок, как показан боец, —
/// <see cref="Blade"/> (только когда его рисуют).
/// </summary>
public readonly record struct FighterView(Entity Entity, bool IsPlayer, Vector3 Root, float Facing, float Height,
    int PoseStart = 0, int PoseCount = 0, int Slot = 0, int BodyStart = 0, int BodyCount = 0, int Weapon = -1,
    Matrix4x4 WeaponWorld = default, ShownBlade? Blade = null)
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
/// Клинок бойца в кадре (6б, оверлей <c>blades</c>): основание и кончик тика и прошлого тика (<see cref="Contracts.Blade"/>),
/// перенесённые из мира бойца на тике в показанный (<see cref="ArenaView.Carry"/>, как капсулы) — при доле тика 1 ровно клинок
/// симуляции. Прошлый переносится тем же переносом (мир бойца прошлого тика не хранится): это отрезок, который клинок заметает
/// за тик, относительно показанного бойца.
/// </summary>
public readonly record struct ShownBlade(Vector3 Base, Vector3 Tip, Vector3 PrevBase, Vector3 PrevTip);

/// <summary>
/// Мир представления арены (Extract, раздел 7 плана): что рисует кадр. Бойцы появляются и уходят по экспорту тика
/// (<c>Added&lt;Fighter&gt;</c>/<c>Removed&lt;Fighter&gt;</c> из очереди кадра), их корни — из мира симуляции между
/// прошлым и последним тиком по доле тика (<c>SkinCutPose.DrawnJoints</c> SWORD: корень — линейно, курс — по короткой
/// дуге; высоты тела от постановки стоп ещё нет — высота рельефа под интерполированным корнем по функции рельефа, как
/// <c>DuelArena.KinWorld(root, facing)</c> с <c>Terrain.Height</c>). Капсулы тел (вид <see cref="IBodyView"/>, 5в) — как
/// показан боец: постановка последнего тика, перенесённая из мира бойца на тике в показанный (<see cref="Carry"/>), — та
/// же поза тика в том же мире, что и меш, поэтому капсулы идут с персонажем без отставания. Точка тени и кольца игрока —
/// таз, как его показывает кадр; фокус камеры — таз между прошлой и нынешней постановкой по доле тика (<c>Program.cs</c>
/// SWORD). Рендер читает только этот мир; кадр без выделений памяти (буферы растут удвоением и остаются). Исправления после Пд:
/// капсулы всех бойцов снимаются, только когда нужны (<c>allBodies</c>: F7, <c>--overlay capsules</c>, слой снимка), иначе —
/// только таз игрока (камера, тень, кольцо): 429 капсул скрытых тел — 0,016 мс CPU кадра впустую. 6б: меч в руке — мир меша
/// оружия — модельная матрица кисти позы тика в показанном мире бойца (<c>SkinCutPose.WeaponWorld</c> SWORD: кисть из
/// <c>DrawnJoints</c>): меч идёт с кистью Manny без отставания; клинки (оверлей <c>blades</c>) — только когда их рисуют.
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

    /// <summary>Бойцов кадра с мечом в руке и клинков, снятых кадром (6б).</summary>
    public int WeaponCount { get; private set; }
    public int BladeCount { get; private set; }

    /// <summary>
    /// Бой последнего тика (6е, HUD): очки жизни игрока (нет игрока или здоровья — полные), мёртв ли он, живых врагов кадра (бойцы не на
    /// месте 0 без метки <see cref="Dead"/>).
    /// </summary>
    public float PlayerHp { get; private set; } = Health.Full;
    public bool PlayerDead { get; private set; }
    public int AliveEnemies { get; private set; }

    /// <summary>Модельная матрица кисти позы привязки — меч бойца, у которого позы ещё нет (до первого тика).</summary>
    private Matrix4x4 _referenceHand = Matrix4x4.Identity;

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
    /// <param name="allBodies">
    /// Капсулы тел всех бойцов (их рисуют — F7, <c>--overlay capsules</c> — или заказан слой снимка); false — только таз игрока
    /// (<see cref="PlayerMarker"/>, <see cref="PlayerFocus"/> — те же числа), у бойцов кадра капсул нет (<see cref="BodyCount"/> 0).
    /// </param>
    /// <param name="blades">Клинки бойцов (F7, <c>--overlay blades</c>, слой снимка); false — у бойцов кадра клинка нет.</param>
    public void Extract(SimulationLink simulation, float alpha, bool allBodies = true, bool blades = false)
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
        world.TryView<ICombatView>(out var combat);
        int hand = combat?.HandJoint ?? -1;
        if (hand >= 0 && poses is not null && poses.ReferenceModel() is var reference && hand < reference.Length) _referenceHand = reference[hand];
        var profiler = _profiler;
        int posed = 0, bodied = 0, armed = 0, bladed = 0, alive = 0;
        PlayerHp = Health.Full;
        PlayerDead = false;
        foreach (var e in _fighters)
        {
            if (!world.TryGet<Transform>(e, out var cur) || !world.TryGet<PrevTransform>(e, out var prev))
            {
                Skipped++;
                continue;
            }
            var view = Interpolate(e, world.Has<PlayerTag>(e), prev, cur, alpha, _height);
            if (world.TryGet<FighterSlot>(e, out var slot)) view = view with { Slot = slot.Index };
            // Бой (6е, HUD): здоровье и смерть игрока, живые враги — снимок последнего тика.
            bool dead = world.Has<Dead>(e);
            if (view.IsPlayer)
            {
                PlayerHp = world.TryGet<Health>(e, out var health) ? health.Hp : Health.Full;
                PlayerDead = dead;
            }
            else if (!dead) alive++;
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
            // Меч — жёсткое вложение в кисть позы тика в показанном мире (как меш); позы нет — кисть позы привязки.
            if (hand >= 0 && world.TryGet<WeaponSlot>(e, out var weapon) && (uint)weapon.Index < (uint)combat!.Weapons.Length)
            {
                var handModel = view.PoseCount > hand ? _poses[view.PoseStart + hand] : _referenceHand;
                view = view with { Weapon = weapon.Index, WeaponWorld = handModel * view.World };
                armed++;
                if (blades && world.TryGet<Blade>(e, out var blade) && blade.Weapon >= 0)
                {
                    view = view with { Blade = CarryBlade(blade, cur, view) };
                    bladed++;
                }
            }
            // Тело — постановка последнего тика в мире, где кадр показывает бойца (как меш): без отставания от персонажа.
            profiler.Begin(_bodiesZone);
            if (!allBodies)
            {
                if (view.IsPlayer && bodies is not null && world.TryGet<BodyRef>(e, out var playerBody)) PlayerPelvis(bodies, playerBody, cur, view, alpha);
            }
            else if (bodies is not null && world.TryGet<BodyRef>(e, out var bodyRef) && ReadBody(bodies, bodyRef) is > 0 and var n)
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
        WeaponCount = armed;
        BladeCount = bladed;
        AliveEnemies = alive;
    }

    /// <summary>Клинок тика и прошлого тика в показанном мире бойца (<see cref="ShownBlade"/>).</summary>
    private ShownBlade CarryBlade(in Blade blade, in Transform cur, in FighterView view)
    {
        var tickWorld = FighterWorld(cur.Position, cur.Facing, _height(cur.Position.X, cur.Position.Z));
        Matrix4x4.Invert(tickWorld, out var back);
        var carry = back * view.World;
        return new ShownBlade(Vector3.Transform(blade.Base, carry), Vector3.Transform(blade.Tip, carry),
            Vector3.Transform(blade.PrevBase, carry), Vector3.Transform(blade.PrevTip, carry));
    }

    /// <summary>
    /// Только таз игрока (капсулы не нужны кадру): точка тени и кольца и фокус камеры — те же выражения, что при чтении всего тела.
    /// </summary>
    private void PlayerPelvis(IBodyView bodies, BodyRef body, in Transform cur, in FighterView view, float alpha)
    {
        int pelvis = BodySegs.Body(BodySegs.Pelvis);
        if (pelvis >= Math.Min(bodies.BodyCount(body), _snapshots.Length)) return;
        var snapshot = bodies.Body(body, pelvis);
        var tickWorld = FighterWorld(cur.Position, cur.Facing, _height(cur.Position.X, cur.Position.Z));
        Matrix4x4.Invert(tickWorld, out var back);
        var carry = back * view.World;
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, view.Facing - cur.Facing);
        PlayerMarker = Carry(snapshot, BodySegs.Segment(pelvis), carry, turn).Center with { Y = 0 };
        PlayerFocus = Lerp(snapshot, alpha).Center with { Y = 0 };
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
