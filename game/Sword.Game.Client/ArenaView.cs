using System.Numerics;
using Sword.Engine.Client;
using Sword.Engine.Diagnostics;
using Sword.Engine.Ecs;
using Sword.Game.Contracts;

namespace Sword.Game.Client;

/// <summary>
/// Боец в кадре: корень, курс и высота уже интерполированы по доле тика; поза — последнего тика (<see cref="ArenaView.Pose"/>,
/// у трупа — мировые матрицы по телам), капсулы — <see cref="ArenaView.Body"/>, суставы — <see cref="ArenaView.Joints"/>.
/// <see cref="Slot"/>: игрок — 0, враги — 1…32; <see cref="Weapon"/> −1 — меча нет.
/// </summary>
public readonly record struct FighterView(Entity Entity, bool IsPlayer, Vector3 Root, float Facing, float Height,
    int PoseStart = 0, int PoseCount = 0, int Slot = 0, int BodyStart = 0, int BodyCount = 0, int Weapon = -1,
    Matrix4x4 WeaponWorld = default, ShownBlade? Blade = null, bool Dead = false, bool Corpse = false,
    Vector3 BoundsMin = default, Vector3 BoundsMax = default, int BodyJointStart = 0, int BodyJointCount = 0, bool WorldPose = false,
    int PieceStart = 0, int PieceCount = 0, bool Severed = false)
{
    /// <summary>Поза — мировые матрицы суставов (труп или живой с отделёнными частями): мир экземпляра — единичный.</summary>
    public bool PoseInWorld => Corpse || WorldPose;

    /// <summary>Мировая система скелета: поворот на курс, перенос в корень на высоте.</summary>
    public Matrix4x4 World => ArenaView.FighterWorld(Root, Facing, Height);
}

/// <summary>
/// Капсула тела бойца в кадре (как показан боец; у трупа — <see cref="ArenaView.Shown"/>): радиус и полудлина оси, м,
/// ось — локальная Y; спящий труп физика не считает.
/// </summary>
public readonly record struct ShownCapsule(int Segment, Vector3 Center, Quaternion Rotation, float Radius, float HalfLength,
    bool Simulated = false, bool Grounded = false, bool Asleep = false)
{
    public Vector3 Axis => Vector3.Transform(Vector3.UnitY, Rotation);
    public Vector3 EndA => Center - Axis * HalfLength;
    public Vector3 EndB => Center + Axis * HalfLength;
}

/// <summary>
/// Клинок бойца в кадре (оверлей <c>blades</c>), перенесённый в показанный мир бойца (<see cref="ArenaView.Carry"/>).
/// Прошлый — тем же переносом: отрезок, который клинок заметает за тик относительно показанного бойца.
/// </summary>
public readonly record struct ShownBlade(Vector3 Base, Vector3 Tip, Vector3 PrevBase, Vector3 PrevTip);

/// <summary>
/// Сустав тела бойца в кадре (оверлей <c>joints</c>): точки на ребёнке и родителе в мире (расстояние — ошибка точки);
/// выключен — запястье трупа (меч выпал); ошибка точки, м, и выход за предел, рад (<see cref="IPhysicsDebugView"/>).
/// </summary>
public readonly record struct ShownJoint(int Parent, int Child, Vector3 Point, Vector3 ParentPoint, bool Enabled, bool Hinge, float PointError,
    float Excess);

/// <summary>
/// Трупы кадра (F3 «Рэгдолл»): в покое — скорость центра масс тел ≤ <see cref="ArenaView.RestSpeed"/>; скорости, м/с и рад/с,
/// — по телам тика, не показанным.
/// </summary>
public readonly record struct CorpseFrameStats(int Corpses, int AtRest, int SimulatedBodies, int GroundedBodies, float MaxSpeed,
    float MaxAngularSpeed, int Asleep = 0);

/// <summary>
/// Мир представления арены (Extract): что рисует кадр; рендер читает только его, кадр без выделений памяти (буферы
/// растут удвоением). Корни — между прошлым и последним тиком по доле тика (курс — по короткой дуге), поза — тоже (модельные
/// матрицы прошлого и последнего тика); капсулы — постановки обоих тиков, перенесённые в показанный мир бойца и смешанные так же
/// (<see cref="Carry"/>, <c>Carrier</c>), чтобы идти с мешем без отставания; клинок оверлея — отрезки тика симуляции. Капсулы
/// всех бойцов снимаются только по запросу (иначе — таз игрока): 429 скрытых капсул — 0,016 мс CPU кадра впустую.
/// Труп с привязкой скина рисуется по телам (<see cref="Shown"/>): сустав j = привязка[j] × тело его сегмента.
/// </summary>
public sealed class ArenaView
{
    private readonly Func<float, float, float> _height;
    private readonly List<Entity> _fighters = [];
    private readonly List<FighterView> _views = [];
    /// <summary>Позы бойцов кадра подряд — снимок пула поз после тиков кадра (у трупа — мировые матрицы суставов по телам).</summary>
    private Matrix4x4[] _poses = [];
    /// <summary>Капсулы тел бойцов кадра подряд — как показаны бойцы.</summary>
    private ShownCapsule[] _bodies = [];
    private const int MaxBodies = BodySegs.Bodies + Severance.MaxPieces;
    private readonly BodySnapshot[] _snapshots = new BodySnapshot[MaxBodies];
    /// <summary>Показанные системы тел бойца, которого сейчас снимает кадр (центр и поворот, вектор слева); тела 14… — куски.</summary>
    private readonly Matrix4x4[] _shown = new Matrix4x4[MaxBodies];
    private readonly Quaternion[] _shownRotation = new Quaternion[MaxBodies];
    /// <summary>Тело сочленения каждого сустава скелета (сегмент сустава — <see cref="IBodyView.JointSegment"/>).</summary>
    private int[] _jointBodies = [];
    /// <summary>Обратная система тела клинка в кисти по номеру меча: кисть = это × тело клинка.</summary>
    private Matrix4x4[] _bladeInHand = [];
    private ShownJoint[] _joints = [];
    private PhysicsContactView[] _contacts = [];
    private int _posed, _bodied, _jointed;

    // Виды симуляции — на время Extract (ссылки на объекты видов, без выделений).
    private IAnimationView? _poseView;
    private IBodyView? _bodyView;
    private ICombatView? _combatView;
    private ISkinView? _skinView;
    private IPhysicsDebugView? _debugView;
    private int _hand = -1;

    /// <param name="height">Высота рельефа в точке (x, z) — функция рельефа (<c>ArenaTerrain.Height</c>), не сетка рисования.</param>
    public ArenaView(Func<float, float, float> height) => _height = height;

    private CpuProfiler _profiler = CpuProfiler.Off;
    private int _posesZone, _bodiesZone;

    /// <summary>Профайлер CPU кадра (зоны <c>arena.extract.poses</c>, <c>arena.extract.bodies</c>); задаёт клиент арены.</summary>
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
    /// Точка тени и кольца игрока — центр капсулы таза, как её показывает кадр (идёт с мешем, не дрожит); нет игрока — null.
    /// </summary>
    public Vector3? PlayerMarker { get; private set; }

    /// <summary>
    /// Фокус камеры — центр таза между прошлой и нынешней постановкой по доле тика этого кадра; у трупа — таз, как его
    /// показывает кадр. Нет игрока или его тела — null.
    /// </summary>
    public Vector3? PlayerFocus { get; private set; }

    /// <summary>Капсул тел в кадре.</summary>
    public int BodyCount { get; private set; }

    /// <summary>Бойцов кадра с мечом в руке и клинков, снятых кадром.</summary>
    public int WeaponCount { get; private set; }
    public int BladeCount { get; private set; }

    /// <summary>Трупов кадра, нарисованных по телам (мёртвые с привязкой скина).</summary>
    public int CorpseCount { get; private set; }

    /// <summary>Скорость центра масс тел трупа, м/с, до которой труп — в покое (playbook, «Труп на физике», п. 2: ≤ 0,05 м/с).</summary>
    public const float RestSpeed = 0.05f;

    /// <summary>Трупы кадра: в покое, тела на физике и на земле, наибольшие скорости — строки группы F3 «Рэгдолл».</summary>
    public CorpseFrameStats CorpseStats { get; private set; }

    // Накопители трупов кадра (Extract → CorpseStats).
    private int _restCorpses, _simulatedBodies, _groundedBodies, _asleepCorpses;
    private float _maxSpeed, _maxAngularSpeed;

    /// <summary>Суставов тел, снятых кадром для оверлея <c>joints</c> (0 — не снимались).</summary>
    public int JointTotal { get; private set; }

    /// <summary>Трасса физики включена (оверлей <c>contacts</c> без неё пуст).</summary>
    public bool TraceOn { get; private set; }

    /// <summary>Контакты последнего подшага, снятые кадром (только когда их рисуют и трасса включена), и не поместившиеся в трассу.</summary>
    public ReadOnlySpan<PhysicsContactView> Contacts => _contacts.AsSpan(0, ContactCount);
    public int ContactCount { get; private set; }
    public int LostContacts { get; private set; }

    /// <summary>Бой последнего тика (HUD): очки жизни игрока (нет здоровья — полные), мёртв ли он, живых врагов кадра.</summary>
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

    /// <summary>Корень игрока на последнем тике (не интерполированный) — для сверки с Headless; нет игрока — null.</summary>
    public Transform? PlayerAtTick { get; private set; }

    /// <summary>Мир представления кадра — всё, что читает рендер, снимается здесь; <c>BuildFrame</c> симуляцию не читает.</summary>
    /// <param name="allBodies">Капсулы всех бойцов (F7); false — только таз игрока, у бойцов <see cref="BodyCount"/> 0.</param>
    /// <param name="blades">Клинки бойцов (F7).</param>
    /// <param name="joints">Суставы тел (J) — и капсулы, на которых они стоят.</param>
    /// <param name="contacts">Контакты последнего подшага из трассы физики (K).</param>
    public void Extract(SimulationLink simulation, float alpha, bool allBodies = true, bool blades = false, bool joints = false,
        bool contacts = false)
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
        world.TryView(out _poseView);
        world.TryView(out _bodyView);
        world.TryView(out _combatView);
        world.TryView(out _skinView);
        world.TryView(out _debugView);
        _hand = _combatView?.HandJoint ?? -1;
        if (_hand >= 0 && _poseView is not null && _poseView.ReferenceModel() is var reference && _hand < reference.Length) _referenceHand = reference[_hand];
        PrepareRig();
        _posed = _bodied = _jointed = 0;
        _restCorpses = _simulatedBodies = _groundedBodies = _asleepCorpses = 0;
        _maxSpeed = _maxAngularSpeed = 0;
        int armed = 0, bladed = 0, alive = 0, corpses = 0;
        PlayerHp = Health.Full;
        PlayerDead = false;
        foreach (var e in _fighters)
        {
            if (!world.TryGet<Transform>(e, out var cur) || !world.TryGet<PrevTransform>(e, out var prev))
            {
                Skipped++;
                continue;
            }
            var view = ExtractFighter(world, e, prev, cur, alpha, allBodies, blades, joints);
            // Бой (HUD): здоровье и смерть игрока, живые враги — снимок последнего тика.
            if (view.IsPlayer)
            {
                PlayerHp = world.TryGet<Health>(e, out var health) ? health.Hp : Health.Full;
                PlayerDead = view.Dead;
            }
            else if (!view.Dead) alive++;
            if (view.Weapon >= 0) armed++;
            if (view.Blade is not null) bladed++;
            if (view.Corpse) corpses++;
            _views.Add(view);
            if (view.IsPlayer)
            {
                Player = view;
                PlayerAtTick = cur;
            }
        }
        BodyCount = _bodied;
        JointTotal = _jointed;
        WeaponCount = armed;
        BladeCount = bladed;
        AliveEnemies = alive;
        CorpseCount = corpses;
        CorpseStats = new CorpseFrameStats(corpses, _restCorpses, _simulatedBodies, _groundedBodies, _maxSpeed, _maxAngularSpeed, _asleepCorpses);
        ExtractContacts(contacts);
    }

    /// <summary>Таблицы кадра, которые меняются только со скелетом и таблицей оружия: тело сустава, тело клинка в кисти каждого меча.</summary>
    private void PrepareRig()
    {
        if (_bodyView is { } bodies && _jointBodies.Length != bodies.JointCount)
        {
            _jointBodies = new int[bodies.JointCount];
            for (int j = 0; j < _jointBodies.Length; j++) _jointBodies[j] = BodySegs.Body(bodies.JointSegment(j));
        }
        if (_combatView is { } combat && _bladeInHand.Length != combat.Weapons.Length)
        {
            var weapons = combat.Weapons;
            _bladeInHand = new Matrix4x4[weapons.Length];
            for (int w = 0; w < weapons.Length; w++) _bladeInHand[w] = BladeInHandInverse(weapons[w].Body);
        }
    }

    /// <summary>Обратная система тела клинка в системе кисти: мир кисти = это × тело клинка — меч рисуется на теле клинка.</summary>
    public static Matrix4x4 BladeInHandInverse(in BladeBody body)
    {
        var y = body.Axis;
        var x = Vector3.Normalize(body.XHint - y * Vector3.Dot(body.XHint, y));
        var z = Vector3.Cross(x, y);
        var rotation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1)));
        var m = Matrix4x4.CreateFromQuaternion(rotation);
        m.Translation = body.Center;
        return Matrix4x4.Invert(m, out var inverse) ? inverse : Matrix4x4.Identity;
    }

    /// <summary>Боец кадра (тело цикла по бойцам — свой метод: у трупа — циклы по суставам и телам).</summary>
    private FighterView ExtractFighter(WorldReader world, Entity e, in PrevTransform prev, in Transform cur, float alpha, bool allBodies,
        bool blades, bool joints)
    {
        var view = Interpolate(e, world.Has<PlayerTag>(e), prev, cur, alpha, _height);
        if (world.TryGet<FighterSlot>(e, out var slot)) view = view with { Slot = slot.Index };
        bool dead = world.Has<Dead>(e);
        view = view with { Dead = dead };
        bool hasBody = world.TryGet<BodyRef>(e, out var bodyRef) && _bodyView is not null;
        int weapon = world.TryGet<WeaponSlot>(e, out var w) && _combatView is not null && (uint)w.Index < (uint)_combatView.Weapons.Length ? w.Index : -1;

        // Труп: мёртвый с привязкой скина и годным телом — по телам.
        bool severed = world.TryGet<Severance>(e, out var severance);
        // отделения есть (и отрыв без кусков) — части меша бойца рисуются вместо целого
        if (severed) view = view with { Severed = (severance.Cut | severance.Torn | severance.Detached) != 0 };
        if (dead && hasBody && _skinView is not null && _skinView.TryGet(bodyRef, out var binding, out _) && binding.Length == _jointBodies.Length
            && ReadBody(_bodyView!, bodyRef) is >= BodySegs.Bodies and var bodyCount)
        {
            _profiler.Begin(_posesZone);
            view = Corpse(view, binding, alpha, weapon, bodyCount);
            if (severed) view = Pieces(view, bodyRef, severance);
            _profiler.End(_posesZone);
            _profiler.Begin(_bodiesZone);
            if (allBodies || joints) view = CorpseCapsules(view);
            if (joints) view = Joints(view, bodyRef);
            _profiler.End(_bodiesZone);
            return view;
        }

        bool between = false;
        // Поза — между прошлым и последним тиком по доле тика, как корень: иначе кадр без нового тика повторял позу, а корень шёл
        // дальше (рывок частей тела и дрожь опорной стопы на частоте кадра выше частоты тика).
        _profiler.Begin(_posesZone);
        if (_poseView is not null && world.TryGet<PoseRef>(e, out var poseRef) && _poseView.Model(poseRef) is { Length: > 0 } model)
        {
            if (_poses.Length < _posed + model.Length) Array.Resize(ref _poses, Math.Max(_poses.Length * 2, _posed + model.Length));
            var shown = _poses.AsSpan(_posed, model.Length);
            var previous = _poseView.PreviousModel(poseRef);
            between = alpha < 1 && previous.Length == model.Length;
            if (between) LerpPose(previous, model, alpha, shown);
            else model.CopyTo(shown);
            view = view with { PoseStart = _posed, PoseCount = model.Length };
            _posed += model.Length;
            // Живой с отделёнными частями: поза — в мире; отделённое — по телам (привязка снята при отделении), куски — свои позы.
            if (severed && hasBody && _skinView is not null && _skinView.TryGet(bodyRef, out var detached, out _) && detached.Length == model.Length
                && detached.Length == _jointBodies.Length && ReadBody(_bodyView!, bodyRef) is >= BodySegs.Bodies and var count)
            {
                for (int k = 0; k < count; k++)
                {
                    var (center, rotation) = Shown(_snapshots[k], alpha);
                    _shown[k] = BodySystem(center, rotation);
                    _shownRotation[k] = rotation;
                }
                var pose = _poses.AsSpan(view.PoseStart, view.PoseCount);
                LivingJoints(pose, view.World, pose);
                DetachedJoints(detached, _jointBodies, _shown, severance.Detached, pose);
                view = Pieces(view with { WorldPose = true }, bodyRef, severance);
            }
        }
        _profiler.End(_posesZone);
        // Меч — жёсткое вложение в кисть позы тика в показанном мире (как меш); позы нет — кисть позы привязки.
        if (severed && severance.IsDetached(BodySegs.Weapon) && hasBody && weapon >= 0 && weapon < _bladeInHand.Length
            && ReadBody(_bodyView!, bodyRef) > BodySegs.Weapon)
        {
            // меч из отрубленной руки — на своём теле (на физике), а не в кисти позы
            var (center, rotation) = Shown(_snapshots[BodySegs.Body(BodySegs.Weapon)], alpha);
            view = view with { Weapon = weapon, WeaponWorld = _bladeInHand[weapon] * BodySystem(center, rotation) };
        }
        else if (_hand >= 0 && weapon >= 0)
        {
            // поза живого с отделёнными частями уже в мире (LivingJoints выше) — второй раз мир бойца не прикладывается
            var handModel = view.PoseCount > _hand ? _poses[view.PoseStart + _hand] : _referenceHand;
            view = view with { Weapon = weapon, WeaponWorld = view.PoseInWorld && view.PoseCount > _hand ? handModel : handModel * view.World };
            if (blades && world.TryGet<Blade>(e, out var blade) && blade.Weapon >= 0) view = view with { Blade = CarryBlade(blade, cur, view) };
        }
        // Тело — постановки тиков в мире, где кадр показывает бойца (как меш): без отставания от персонажа.
        _profiler.Begin(_bodiesZone);
        if (!allBodies && !joints)
        {
            if (view.IsPlayer && hasBody) PlayerPelvis(_bodyView!, bodyRef, prev, cur, view, alpha, between);
        }
        else if (hasBody && ReadBody(_bodyView!, bodyRef) is > 0 and var n)
        {
            if (_bodies.Length < _bodied + n) Array.Resize(ref _bodies, Math.Max(_bodies.Length * 2, _bodied + n));
            var carry = new Carrier(this, prev, cur, view, alpha, between);
            for (int i = 0; i < n; i++)
            {
                var c = _bodies[_bodied + i] = carry.Body(_snapshots[i], BodySegs.Segment(i));
                _shown[i] = BodySystem(c.Center, c.Rotation);
            }
            view = view with { BodyStart = _bodied, BodyCount = n };
            int pelvis = BodySegs.Body(BodySegs.Pelvis);
            if (view.IsPlayer && pelvis < n)
            {
                PlayerMarker = _bodies[_bodied + pelvis].Center with { Y = 0 };
                PlayerFocus = Lerp(_snapshots[pelvis], alpha).Center with { Y = 0 };
            }
            _bodied += n;
            if (joints) view = Joints(view, bodyRef);
        }
        _profiler.End(_bodiesZone);
        return view;
    }

    /// <summary>
    /// Позы кусков бойца (части меша с куском рисуются ими): на кусок — поза бойца, где суставы сегмента, от которого он отрезан, — по
    /// привязке куска и показанному телу куска. Поза бойца уже в мире.
    /// </summary>
    private FighterView Pieces(FighterView view, BodyRef bodyRef, in Severance severance)
    {
        int n = Math.Min(severance.Pieces, Severance.MaxPieces), size = view.PoseCount;
        if (n == 0 || size == 0) return view;
        if (_poses.Length < _posed + n * size) Array.Resize(ref _poses, Math.Max(_poses.Length * 2, _posed + n * size));
        int start = _posed;
        for (int k = 0; k < n; k++)
        {
            var block = _poses.AsSpan(_posed, size);
            var pose = _poses.AsSpan(view.PoseStart, size);
            if (_skinView!.TryGetGhost(bodyRef, k, out var ghost) && ghost.Length == size)
                PieceJoints(pose, ghost, _jointBodies, severance.Piece[k].Segment, _shown[BodySegs.Bodies + k], block);
            else pose.CopyTo(block);
            _posed += size;
        }
        return view with { PieceStart = start, PieceCount = n };
    }

    /// <summary>Поза куска <paramref name="piece"/> бойца кадра (мировые суставы); куска нет — поза бойца.</summary>
    public ReadOnlySpan<Matrix4x4> PiecePose(in FighterView fighter, int piece) =>
        (uint)piece < (uint)fighter.PieceCount ? _poses.AsSpan(fighter.PieceStart + piece * fighter.PoseCount, fighter.PoseCount) : Pose(fighter);

    /// <summary>Труп по телам: суставы — привязка × показанное тело сегмента, меч — на теле клинка, рамка — по капсулам (и кускам).</summary>
    private FighterView Corpse(FighterView view, ReadOnlySpan<Matrix4x4> binding, float alpha, int weapon, int bodies)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var momentum = Vector3.Zero;
        float mass = 0;
        for (int k = 0; k < bodies; k++)
        {
            ref readonly var b = ref _snapshots[k];
            var (center, rotation) = Shown(b, alpha);
            _shown[k] = BodySystem(center, rotation);
            _shownRotation[k] = rotation;
            var axis = Vector3.Transform(Vector3.UnitY, rotation) * b.HalfLength;
            var r = new Vector3(b.Radius);
            min = Vector3.Min(min, Vector3.Min(center - axis, center + axis) - r);
            max = Vector3.Max(max, Vector3.Max(center - axis, center + axis) + r);
            // Трупы кадра (строки F3 «Рэгдолл»): тела на физике, на земле, наибольшие скорости, импульс — для скорости центра масс.
            momentum += b.Velocity * b.Mass;
            mass += b.Mass;
            if (!b.Simulated) continue;
            _simulatedBodies++;
            if (b.Grounded) _groundedBodies++;
            _maxSpeed = MathF.Max(_maxSpeed, b.Velocity.Length());
            _maxAngularSpeed = MathF.Max(_maxAngularSpeed, b.AngularVelocity.Length());
        }
        if (mass > 0 && (momentum / mass).Length() <= RestSpeed) _restCorpses++;
        if (_snapshots[0].Asleep) _asleepCorpses++;   // сон — у сочленения, у всех его тел один
        if (_poses.Length < _posed + binding.Length) Array.Resize(ref _poses, Math.Max(_poses.Length * 2, _posed + binding.Length));
        SkinJoints(binding, _poses.AsSpan(_posed, binding.Length));
        view = view with { PoseStart = _posed, PoseCount = binding.Length, Corpse = true, BoundsMin = min, BoundsMax = max };
        _posed += binding.Length;
        if (weapon >= 0 && weapon < _bladeInHand.Length)
            view = view with { Weapon = weapon, WeaponWorld = _bladeInHand[weapon] * _shown[BodySegs.Body(BodySegs.Weapon)] };
        if (view.IsPlayer)
        {
            var pelvis = _shown[BodySegs.Body(BodySegs.Pelvis)].Translation with { Y = 0 };
            PlayerMarker = pelvis;
            PlayerFocus = pelvis;
        }
        return view;
    }

    /// <summary>Сустав <c>j</c> трупа в мире — привязка[j] × показанное тело его сегмента.</summary>
    private void SkinJoints(ReadOnlySpan<Matrix4x4> binding, Span<Matrix4x4> joints) => CorpseJoints(binding, _jointBodies, _shown, joints);

    /// <summary>
    /// Суставы трупа в мире — вход палитры скина: <c>joints[j] = binding[j] · bodies[jointBodies[j]]</c> (привязка × система тела сегмента
    /// сустава). Кадр — по показанным телам, позы тика (<see cref="ArenaFighterPoses"/>) — по телам тика.
    /// </summary>
    public static void CorpseJoints(ReadOnlySpan<Matrix4x4> binding, ReadOnlySpan<int> jointBodies, ReadOnlySpan<Matrix4x4> bodies,
        Span<Matrix4x4> joints)
    {
        for (int j = 0; j < joints.Length; j++) joints[j] = binding[j] * bodies[jointBodies[j]];
    }

    /// <summary>Суставы отделённых сегментов живого — по телам: <c>binding[j] · bodies[jointBodies[j]]</c> там, где сегмент в маске.</summary>
    public static void DetachedJoints(ReadOnlySpan<Matrix4x4> binding, ReadOnlySpan<int> jointBodies, ReadOnlySpan<Matrix4x4> bodies, ushort detached,
        Span<Matrix4x4> joints)
    {
        for (int j = 0; j < joints.Length; j++)
            if ((detached & (1 << jointBodies[j])) != 0) joints[j] = binding[j] * bodies[jointBodies[j]];
    }

    /// <summary>
    /// Суставы куска в мире: поза бойца, где суставы сегмента <paramref name="segment"/> — по привязке куска и его телу
    /// (<paramref name="piece"/>) — вход палитры части меша с куском.
    /// </summary>
    public static void PieceJoints(ReadOnlySpan<Matrix4x4> pose, ReadOnlySpan<Matrix4x4> ghost, ReadOnlySpan<int> jointBodies, int segment,
        in Matrix4x4 piece, Span<Matrix4x4> joints)
    {
        for (int j = 0; j < joints.Length; j++) joints[j] = jointBodies[j] == segment ? ghost[j] * piece : pose[j];
    }

    /// <summary>
    /// Суставы живого в мире — вход палитры скина (<c>SkinnedMeshes.AddPosed</c>: InverseBind · модельная · мир): <c>model[j] · world</c>.
    /// </summary>
    public static void LivingJoints(ReadOnlySpan<Matrix4x4> model, in Matrix4x4 world, Span<Matrix4x4> joints)
    {
        for (int j = 0; j < joints.Length; j++) joints[j] = model[j] * world;
    }

    /// <summary>Мир бойца на тике: корень тика на высоте рельефа под ним (кадр показывает его же, перенесённым по доле тика).</summary>
    public static Matrix4x4 TickWorld(in Transform cur, Func<float, float, float> height) =>
        FighterWorld(cur.Position, cur.Facing, height(cur.Position.X, cur.Position.Z));

    /// <summary>Капсулы трупа — показанные тела (как скин): на физике — с отметкой.</summary>
    private FighterView CorpseCapsules(FighterView view)
    {
        int n = BodySegs.Bodies;
        if (_bodies.Length < _bodied + n) Array.Resize(ref _bodies, Math.Max(_bodies.Length * 2, _bodied + n));
        for (int k = 0; k < n; k++)
        {
            ref readonly var b = ref _snapshots[k];
            _bodies[_bodied + k] = new ShownCapsule(BodySegs.Segment(k), _shown[k].Translation, _shownRotation[k], b.Radius, b.HalfLength,
                b.Simulated, b.Grounded, b.Asleep);
        }
        view = view with { BodyStart = _bodied, BodyCount = n };
        _bodied += n;
        return view;
    }

    /// <summary>Суставы тела бойца (оверлей <c>joints</c>): точки на ребёнке и родителе — по показанным телам (<see cref="_shown"/>).</summary>
    private FighterView Joints(FighterView view, BodyRef body)
    {
        if (_debugView is not { } debug || view.BodyCount == 0) return view;
        int count = debug.JointCount(body);
        if (count <= 0) return view;
        if (_joints.Length < _jointed + count) Array.Resize(ref _joints, Math.Max(_joints.Length * 2, _jointed + count));
        int start = _jointed, n = view.BodyCount;
        for (int i = 0; i < count; i++)
        {
            var j = debug.Joint(body, i);
            if ((uint)j.Parent >= (uint)n || (uint)j.Child >= (uint)n) continue;
            _joints[_jointed++] = new ShownJoint(j.Parent, j.Child, Vector3.Transform(j.ChildAnchor, _shown[j.Child]),
                Vector3.Transform(j.ParentAnchor, _shown[j.Parent]), j.Enabled, j.Hinge, j.PointError, j.Excess);
        }
        return view with { BodyJointStart = start, BodyJointCount = _jointed - start };
    }

    /// <summary>Контакты последнего подшага: копия из вида (рендер читает только мир представления).</summary>
    private void ExtractContacts(bool contacts)
    {
        TraceOn = _debugView?.TraceOn == true;
        ContactCount = LostContacts = 0;
        if (!contacts || !TraceOn) return;
        var source = _debugView!.Contacts;
        if (_contacts.Length < source.Length) Array.Resize(ref _contacts, Math.Max(_contacts.Length * 2, source.Length));
        source.CopyTo(_contacts);
        ContactCount = source.Length;
        LostContacts = _debugView.LostContacts;
    }

    /// <summary>Клинок тика и прошлого тика в показанном мире бойца (<see cref="ShownBlade"/>).</summary>
    private ShownBlade CarryBlade(in Blade blade, in Transform cur, in FighterView view)
    {
        var tickWorld = TickWorld(cur, _height);
        Matrix4x4.Invert(tickWorld, out var back);
        var carry = back * view.World;
        return new ShownBlade(Vector3.Transform(blade.Base, carry), Vector3.Transform(blade.Tip, carry),
            Vector3.Transform(blade.PrevBase, carry), Vector3.Transform(blade.PrevTip, carry));
    }

    /// <summary>
    /// Только таз игрока (капсулы не нужны кадру): точка тени и кольца и фокус камеры — те же выражения, что при чтении всего тела.
    /// </summary>
    private void PlayerPelvis(IBodyView bodies, BodyRef body, in PrevTransform prev, in Transform cur, in FighterView view, float alpha, bool between)
    {
        int pelvis = BodySegs.Body(BodySegs.Pelvis);
        if (pelvis >= Math.Min(bodies.BodyCount(body), _snapshots.Length)) return;
        var snapshot = bodies.Body(body, pelvis);
        PlayerMarker = new Carrier(this, prev, cur, view, alpha, between).Body(snapshot, BodySegs.Segment(pelvis)).Center with { Y = 0 };
        PlayerFocus = Lerp(snapshot, alpha).Center with { Y = 0 };
    }

    /// <summary>
    /// Постановка живого бойца там, где кадр показывает его меш: постановка тика переносится из мира на тике в показанный мир
    /// (<see cref="Carry"/>); если поза показана между тиками (<paramref name="between"/>), так же переносится постановка прошлого
    /// тика (поза начала тика тела) из мира прошлого тика, и между ними — по доле тика, как суставы позы.
    /// </summary>
    private readonly struct Carrier
    {
        private readonly Matrix4x4 _carry, _carryPrev;
        private readonly Quaternion _turn, _turnPrev;
        private readonly float _alpha;
        private readonly bool _between;

        public Carrier(ArenaView owner, in PrevTransform prev, in Transform cur, in FighterView view, float alpha, bool between)
        {
            Matrix4x4.Invert(TickWorld(cur, owner._height), out var back);
            _carry = back * view.World;
            _turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, view.Facing - cur.Facing);
            _alpha = alpha;
            _between = between;
            if (!between) return;
            Matrix4x4.Invert(FighterWorld(prev.Position, prev.Facing, owner._height(prev.Position.X, prev.Position.Z)), out var backPrev);
            _carryPrev = backPrev * view.World;
            _turnPrev = Quaternion.CreateFromAxisAngle(Vector3.UnitY, view.Facing - prev.Facing);
        }

        public ShownCapsule Body(in BodySnapshot body, int segment)
        {
            var now = Carry(body, segment, _carry, _turn);
            if (!_between) return now;
            var before = Carry(body with { Position = body.StartPosition, Rotation = body.StartRotation }, segment, _carryPrev, _turnPrev);
            return now with
            {
                Center = Vector3.Lerp(before.Center, now.Center, _alpha),
                Rotation = Quaternion.Normalize(Quaternion.Slerp(before.Rotation, now.Rotation, _alpha)),
            };
        }
    }

    /// <summary>Тела сочленения в <see cref="_snapshots"/> (сегменты и куски, не больше <see cref="MaxBodies"/>); вернёт, сколько.</summary>
    private int ReadBody(IBodyView bodies, BodyRef body)
    {
        int n = Math.Min(bodies.BodyCount(body), _snapshots.Length);
        for (int i = 0; i < n; i++) _snapshots[i] = bodies.Body(body, i);
        return n;
    }

    /// <summary>Поза бойца кадра — модельные матрицы последнего тика; у трупа — мировые (привязка × тело); пусто — позы нет.</summary>
    public ReadOnlySpan<Matrix4x4> Pose(in FighterView fighter) => _poses.AsSpan(fighter.PoseStart, fighter.PoseCount);

    /// <summary>Капсулы тела бойца кадра — как показан боец; пусто — тела нет.</summary>
    public ReadOnlySpan<ShownCapsule> Body(in FighterView fighter) => _bodies.AsSpan(fighter.BodyStart, fighter.BodyCount);

    /// <summary>Суставы тела бойца кадра (оверлей <c>joints</c>); пусто — не снимались или суставов нет.</summary>
    public ReadOnlySpan<ShownJoint> Joints(in FighterView fighter) => _joints.AsSpan(fighter.BodyJointStart, fighter.BodyJointCount);

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

    /// <summary>
    /// Поза между тиками: модельные матрицы — поэлементно (за тик сустав поворачивается на градусы, укорочение оси при таком
    /// смешивании — доли процента, а кватернион на сустав дороже).
    /// </summary>
    public static void LerpPose(ReadOnlySpan<Matrix4x4> previous, ReadOnlySpan<Matrix4x4> current, float alpha, Span<Matrix4x4> into)
    {
        for (int j = 0; j < into.Length; j++) into[j] = Matrix4x4.Lerp(previous[j], current[j], alpha);
    }

    /// <summary>Интерполяция корня между прошлым и последним тиком.</summary>
    public static FighterView Interpolate(Entity e, bool isPlayer, in PrevTransform prev, in Transform cur, float alpha,
        Func<float, float, float> height)
    {
        var root = Vector3.Lerp(prev.Position, cur.Position, alpha);
        float facing = prev.Facing + MathF.IEEERemainder(cur.Facing - prev.Facing, MathF.Tau) * alpha;
        return new FighterView(e, isPlayer, root, facing, height(root.X, root.Z));
    }

    /// <summary>Мировая система скелета: поворот на курс, перенос в корень на высоте.</summary>
    public static Matrix4x4 FighterWorld(Vector3 root, float facing, float height)
    {
        var m = Matrix4x4.CreateFromQuaternion(Quaternion.CreateFromAxisAngle(Vector3.UnitY, facing));
        m.Translation = new Vector3(root.X, height, root.Z);
        return m;
    }

    /// <summary>Система тела (вектор слева): поворот, затем перенос в центр — как <see cref="BodySnapshot.World"/>.</summary>
    public static Matrix4x4 BodySystem(Vector3 center, Quaternion rotation)
    {
        var m = Matrix4x4.CreateFromQuaternion(rotation);
        m.Translation = center;
        return m;
    }

    /// <summary>
    /// Капсула живого бойца, как показан боец: постановка тика переносится из мира на тике в показанный —
    /// <paramref name="carry"/> = обратный мир на тике · показанный мир; так капсулы стоят на меше при любой доле тика.
    /// </summary>
    public static ShownCapsule Carry(in BodySnapshot body, int segment, in Matrix4x4 carry, Quaternion turn) =>
        new(segment, Vector3.Transform(body.Position, carry), Quaternion.Normalize(Quaternion.Concatenate(body.Rotation, turn)),
            body.Radius, body.HalfLength, body.Simulated, body.Grounded);

    /// <summary>
    /// Тело между прошлой и нынешней постановкой по доле тика (центр — линейно, поворот — <c>Slerp</c>): фокус камеры.
    /// Капсулы живого рисует <c>Carrier</c> (интерполяция постановок в мире отстаёт от меша), тела трупа — <see cref="Shown"/>.
    /// </summary>
    public static (Vector3 Center, Quaternion Rotation) Lerp(in BodySnapshot body, float alpha) =>
        (Vector3.Lerp(body.PrevPosition, body.Position, alpha), Quaternion.Slerp(body.PrevRotation, body.Rotation, alpha));

    /// <summary>
    /// Тело трупа, как его показывает кадр: на физике — между позой начала тика и позой тика по доле тика (прошлую
    /// постановку перезаписывает каждый подшаг); кинематическое и спящее — поза тика, иначе интерполяция качала бы
    /// застывший труп.
    /// </summary>
    public static (Vector3 Center, Quaternion Rotation) Shown(in BodySnapshot body, float alpha) => body.Simulated && !body.Asleep
        ? (Vector3.Lerp(body.StartPosition, body.Position, alpha), Quaternion.Normalize(Quaternion.Slerp(body.StartRotation, body.Rotation, alpha)))
        : (body.Position, body.Rotation);
}
