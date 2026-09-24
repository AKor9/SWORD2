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
/// 7и: <see cref="Dead"/> — мёртв (метка <see cref="Contracts.Dead"/>); <see cref="Corpse"/> — труп рисуется по телам: поза — уже
/// мировые матрицы суставов (привязка скина × тело, <see cref="ArenaView.Pose"/>), меч — на теле клинка, рамка — по телам
/// (<see cref="BoundsMin"/>, <see cref="BoundsMax"/>); суставы тела (физики) для оверлея — <see cref="BodyJointCount"/> с
/// <see cref="BodyJointStart"/> (<see cref="ArenaView.Joints"/>, только когда их рисуют).
/// </summary>
public readonly record struct FighterView(Entity Entity, bool IsPlayer, Vector3 Root, float Facing, float Height,
    int PoseStart = 0, int PoseCount = 0, int Slot = 0, int BodyStart = 0, int BodyCount = 0, int Weapon = -1,
    Matrix4x4 WeaponWorld = default, ShownBlade? Blade = null, bool Dead = false, bool Corpse = false,
    Vector3 BoundsMin = default, Vector3 BoundsMax = default, int BodyJointStart = 0, int BodyJointCount = 0)
{
    /// <summary>Мировая система скелета (<c>DuelArena.KinWorld</c> SWORD): поворот на курс, перенос в корень на высоте.</summary>
    public Matrix4x4 World => ArenaView.FighterWorld(Root, Facing, Height);
}

/// <summary>
/// Капсула тела бойца в кадре (5в): сегмент (<see cref="BodySegs"/>), центр и поворот (ось — локальная Y) — как показан
/// боец (<see cref="ArenaView.Carry"/>; у трупа — тело, <see cref="ArenaView.Shown"/>), радиус и полудлина оси, м; 7и — на физике ли
/// тело (труп) и касается ли земли.
/// </summary>
public readonly record struct ShownCapsule(int Segment, Vector3 Center, Quaternion Rotation, float Radius, float HalfLength,
    bool Simulated = false, bool Grounded = false)
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
/// Сустав тела бойца в кадре (7и, оверлей <c>joints</c>): тела родителя и ребёнка (номера тел = сегменты), точка сустава на ребёнке
/// и на родителе — в мире, как показаны их капсулы (расстояние между ними — ошибка точки), включён ли (выключен — запястье трупа: меч
/// выпал), шарнир ли; замер в позе тика — ошибка точки, м, и выход за предел, рад (0 — в пределах; <see cref="IPhysicsDebugView"/>).
/// </summary>
public readonly record struct ShownJoint(int Parent, int Child, Vector3 Point, Vector3 ParentPoint, bool Enabled, bool Hinge, float PointError,
    float Excess);

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
///
/// 7и — труп на экране (<c>SkinCutPose.LiveJoints</c>, <c>WeaponOnBody</c>, <c>WeaponWorld</c> SWORD — идеи): мёртвый с привязкой скина
/// (<see cref="ISkinView"/>) рисуется по телам — сустав <c>j</c> = привязка[j] × тело его сегмента, как показано тело
/// (<see cref="Shown"/>: тело на физике — интерполяция позы начала тика → поза тика по доле тика; застывшее кинематическое — поза тика);
/// меч — на теле клинка (тело 11: кисть = (тело клинка в кисти)⁻¹ × тело — выпавший лежит там, где лежит тело); капсулы трупа — те же
/// показанные тела (на физике — с отметкой); рамка — по телам. Нет привязки — как раньше (поза пула). Суставы тела (оверлей
/// <c>joints</c>) и контакты последнего подшага (оверлей <c>contacts</c>, трасса физики) — из <see cref="IPhysicsDebugView"/>, только
/// когда их рисуют.
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
    private readonly BodySnapshot[] _snapshots = new BodySnapshot[BodySegs.Bodies];
    /// <summary>Показанные системы тел бойца, которого сейчас снимает кадр (центр и поворот, вектор слева).</summary>
    private readonly Matrix4x4[] _shown = new Matrix4x4[BodySegs.Bodies];
    private readonly Quaternion[] _shownRotation = new Quaternion[BodySegs.Bodies];
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

    /// <summary>
    /// Профайлер CPU кадра (точка Па): копия поз бойцов — зона <c>arena.extract.poses</c> (7и — и скин трупа по телам), чтение и перенос
    /// капсул тел — <c>arena.extract.bodies</c> (по вызову на бойца). Задаёт клиент арены из профайлера рендера.
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
    /// по доле прошлого кадра). У трупа игрока (7и) — таз, как его показывает кадр. Нет игрока или его тела — null.
    /// </summary>
    public Vector3? PlayerFocus { get; private set; }

    /// <summary>Капсул тел в кадре.</summary>
    public int BodyCount { get; private set; }

    /// <summary>Бойцов кадра с мечом в руке и клинков, снятых кадром (6б).</summary>
    public int WeaponCount { get; private set; }
    public int BladeCount { get; private set; }

    /// <summary>Трупов кадра, нарисованных по телам (7и: мёртвые с привязкой скина).</summary>
    public int CorpseCount { get; private set; }

    /// <summary>Суставов тел, снятых кадром для оверлея <c>joints</c> (7и; 0 — не снимались).</summary>
    public int JointTotal { get; private set; }

    /// <summary>Трасса физики включена (7и: оверлей <c>contacts</c> без неё пуст).</summary>
    public bool TraceOn { get; private set; }

    /// <summary>Контакты последнего подшага, снятые кадром (7и; только когда их рисуют и трасса включена), и не поместившиеся в трассу.</summary>
    public ReadOnlySpan<PhysicsContactView> Contacts => _contacts.AsSpan(0, ContactCount);
    public int ContactCount { get; private set; }
    public int LostContacts { get; private set; }

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
    /// <param name="joints">Суставы тел (7и: J, <c>--overlay joints</c>, слой снимка) — и капсулы тел, на которых они стоят.</param>
    /// <param name="contacts">Контакты последнего подшага из трассы физики (7и: K, <c>--overlay contacts</c>, слой снимка).</param>
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
            // Бой (6е, HUD): здоровье и смерть игрока, живые враги — снимок последнего тика.
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

    /// <summary>
    /// Обратная система тела клинка в системе кисти (7ж: центр — середина хват — кончик, ось Y — к кончику, X — опорная ось кисти,
    /// <c>AnimMath.FromBasis</c>): мир кисти = это × система тела клинка — меч рисуется на теле клинка без снимка.
    /// </summary>
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

        // Труп (7и): мёртвый с привязкой скина и годным телом — по телам.
        if (dead && hasBody && _skinView is not null && _skinView.TryGet(bodyRef, out var binding, out _) && binding.Length == _jointBodies.Length
            && ReadBody(_bodyView!, bodyRef) == BodySegs.Bodies)
        {
            _profiler.Begin(_posesZone);
            view = Corpse(view, binding, alpha, weapon);
            _profiler.End(_posesZone);
            _profiler.Begin(_bodiesZone);
            if (allBodies || joints) view = CorpseCapsules(view);
            if (joints) view = Joints(view, bodyRef);
            _profiler.End(_bodiesZone);
            return view;
        }

        // Поза — последнего тика, без интерполяции между тиками (как SkinnedCharacters SWORD): плавно идёт корень.
        _profiler.Begin(_posesZone);
        if (_poseView is not null && world.TryGet<PoseRef>(e, out var poseRef) && _poseView.Model(poseRef) is { Length: > 0 } model)
        {
            if (_poses.Length < _posed + model.Length) Array.Resize(ref _poses, Math.Max(_poses.Length * 2, _posed + model.Length));
            model.CopyTo(_poses.AsSpan(_posed));
            view = view with { PoseStart = _posed, PoseCount = model.Length };
            _posed += model.Length;
        }
        _profiler.End(_posesZone);
        // Меч — жёсткое вложение в кисть позы тика в показанном мире (как меш); позы нет — кисть позы привязки.
        if (_hand >= 0 && weapon >= 0)
        {
            var handModel = view.PoseCount > _hand ? _poses[view.PoseStart + _hand] : _referenceHand;
            view = view with { Weapon = weapon, WeaponWorld = handModel * view.World };
            if (blades && world.TryGet<Blade>(e, out var blade) && blade.Weapon >= 0) view = view with { Blade = CarryBlade(blade, cur, view) };
        }
        // Тело — постановка последнего тика в мире, где кадр показывает бойца (как меш): без отставания от персонажа.
        _profiler.Begin(_bodiesZone);
        if (!allBodies && !joints)
        {
            if (view.IsPlayer && hasBody) PlayerPelvis(_bodyView!, bodyRef, cur, view, alpha);
        }
        else if (hasBody && ReadBody(_bodyView!, bodyRef) is > 0 and var n)
        {
            if (_bodies.Length < _bodied + n) Array.Resize(ref _bodies, Math.Max(_bodies.Length * 2, _bodied + n));
            var tickWorld = FighterWorld(cur.Position, cur.Facing, _height(cur.Position.X, cur.Position.Z));
            Matrix4x4.Invert(tickWorld, out var back);
            var carry = back * view.World;
            var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, view.Facing - cur.Facing);
            for (int i = 0; i < n; i++)
            {
                var c = _bodies[_bodied + i] = Carry(_snapshots[i], BodySegs.Segment(i), carry, turn);
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
    /// Труп по телам (7и): показанные тела (<see cref="Shown"/>), суставы скелета — привязка × тело сегмента (мировые матрицы в снимке
    /// поз), меч — на теле клинка, рамка — по капсулам тел, таз игрока — камере, тени и кольцу.
    /// </summary>
    private FighterView Corpse(FighterView view, ReadOnlySpan<Matrix4x4> binding, float alpha, int weapon)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int k = 0; k < BodySegs.Bodies; k++)
        {
            ref readonly var b = ref _snapshots[k];
            var (center, rotation) = Shown(b, alpha);
            _shown[k] = BodySystem(center, rotation);
            _shownRotation[k] = rotation;
            var axis = Vector3.Transform(Vector3.UnitY, rotation) * b.HalfLength;
            var r = new Vector3(b.Radius);
            min = Vector3.Min(min, Vector3.Min(center - axis, center + axis) - r);
            max = Vector3.Max(max, Vector3.Max(center - axis, center + axis) + r);
        }
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

    /// <summary>Сустав <c>j</c> трупа в мире — привязка[j] × показанное тело его сегмента (<c>SkinCutPose.LiveJoints</c> SWORD).</summary>
    private void SkinJoints(ReadOnlySpan<Matrix4x4> binding, Span<Matrix4x4> joints)
    {
        for (int j = 0; j < joints.Length; j++) joints[j] = binding[j] * _shown[_jointBodies[j]];
    }

    /// <summary>Капсулы трупа — показанные тела (как скин): на физике — с отметкой.</summary>
    private FighterView CorpseCapsules(FighterView view)
    {
        int n = BodySegs.Bodies;
        if (_bodies.Length < _bodied + n) Array.Resize(ref _bodies, Math.Max(_bodies.Length * 2, _bodied + n));
        for (int k = 0; k < n; k++)
        {
            ref readonly var b = ref _snapshots[k];
            _bodies[_bodied + k] = new ShownCapsule(BodySegs.Segment(k), _shown[k].Translation, _shownRotation[k], b.Radius, b.HalfLength,
                b.Simulated, b.Grounded);
        }
        view = view with { BodyStart = _bodied, BodyCount = n };
        _bodied += n;
        return view;
    }

    /// <summary>Суставы тела бойца (7и, оверлей <c>joints</c>): точки на ребёнке и родителе — по показанным телам (<see cref="_shown"/>).</summary>
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

    /// <summary>Контакты последнего подшага (7и): копия из вида (рендер читает только мир представления).</summary>
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

    /// <summary>Тела сочленения в <see cref="_snapshots"/> (не больше 14); вернёт, сколько.</summary>
    private int ReadBody(IBodyView bodies, BodyRef body)
    {
        int n = Math.Min(bodies.BodyCount(body), _snapshots.Length);
        for (int i = 0; i < n; i++) _snapshots[i] = bodies.Body(body, i);
        return n;
    }

    /// <summary>
    /// Поза бойца кадра — модельные матрицы суставов последнего тика; у трупа (<see cref="FighterView.Corpse"/>) — мировые матрицы
    /// суставов (привязка × тело, 7и); пусто — позы нет.
    /// </summary>
    public ReadOnlySpan<Matrix4x4> Pose(in FighterView fighter) => _poses.AsSpan(fighter.PoseStart, fighter.PoseCount);

    /// <summary>Капсулы тела бойца кадра — как показан боец; пусто — тела нет.</summary>
    public ReadOnlySpan<ShownCapsule> Body(in FighterView fighter) => _bodies.AsSpan(fighter.BodyStart, fighter.BodyCount);

    /// <summary>Суставы тела бойца кадра (7и, оверлей <c>joints</c>); пусто — не снимались или суставов нет.</summary>
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

    /// <summary>Система тела (вектор слева): поворот, затем перенос в центр — как <see cref="BodySnapshot.World"/>.</summary>
    public static Matrix4x4 BodySystem(Vector3 center, Quaternion rotation)
    {
        var m = Matrix4x4.CreateFromQuaternion(rotation);
        m.Translation = center;
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
            body.Radius, body.HalfLength, body.Simulated, body.Grounded);

    /// <summary>
    /// Тело между прошлой и нынешней постановкой по доле тика (<c>Fighter.BodyMatrix(b, alpha)</c> SWORD: центр — линейно,
    /// поворот — <c>Slerp</c>; у кинематического тела прошлая постановка — это <c>RenderPos/RenderRot</c> SWORD). Так SWORD
    /// ведёт камеру за тазом живого. Капсулы живого бойца рисуются <see cref="Carry"/>: интерполяция постановок отстаёт от меша на
    /// (1 − доля) изменения позы за тик. Тела трупа — <see cref="Shown"/> (у тела на физике прошлую постановку перезаписывает каждый
    /// подшаг).
    /// </summary>
    public static (Vector3 Center, Quaternion Rotation) Lerp(in BodySnapshot body, float alpha) =>
        (Vector3.Lerp(body.PrevPosition, body.Position, alpha), Quaternion.Slerp(body.PrevRotation, body.Rotation, alpha));

    /// <summary>
    /// Тело трупа, как его показывает кадр (7и; <c>Fighter.SkinMatrix(body, alpha)</c> SWORD — идея): тело на физике — между позой
    /// начала тика и позой тика по доле тика (центр — линейно, поворот — <c>Slerp</c>; прошлую постановку тела на физике перезаписывает
    /// каждый подшаг — от неё нельзя); кинематическое (мёртвый до перехода в труп, 7з, замирает) — поза тика: его поза начала тика —
    /// прошлой постановки живого, и интерполяция качала бы застывший труп на расстояние последнего тика жизни. При доле 1 — поза тика.
    /// </summary>
    public static (Vector3 Center, Quaternion Rotation) Shown(in BodySnapshot body, float alpha) => body.Simulated
        ? (Vector3.Lerp(body.StartPosition, body.Position, alpha), Quaternion.Normalize(Quaternion.Slerp(body.StartRotation, body.Rotation, alpha)))
        : (body.Position, body.Rotation);
}
