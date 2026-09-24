using System.Numerics;
using System.Runtime.InteropServices;
using Sword.Engine.Client;
using Sword.Engine.Diagnostics;
using Sword.Engine.Ecs;
using Sword.Engine.Geometry;
using Sword.Engine.Render;
using Sword.Engine.Ui;
using Sword.Game.Arena;
using Sword.Game.Client.Scene;
using Sword.Game.Contracts;

namespace Sword.Game.Client;

/// <summary>
/// Клиент игры: арена — пол, рельеф из <see cref="HeightField"/>, колонны — и бойцы: Manny в позе последнего тика
/// (аниматор, этап 2) в корне бойца с его курсом на высоте рельефа (<c>DuelArena.KinWorld</c> SWORD). Один непрозрачный
/// проход «scene.opaque» пишет цвет,
/// нормали и глубину (порядок отрисовки — <c>SceneRenderer.Record</c> SWORD 2eab13b: пол, рельеф, колонны, скин).
///
/// Кадр — порядок <c>Program.cs</c> SWORD: <see cref="Input"/> — камера (<see cref="RtsCamera"/>, следует за игроком,
/// каким его показал прошлый кадр) и команда игрока из WASD по этой камере и курсора на земле; <see cref="BeforeTick"/> —
/// та же команда в каждый тик кадра (удерживаемое, раздел 7); <see cref="Extract"/> — мир представления
/// (<see cref="ArenaView"/>, интерполяция по доле тика); <see cref="BuildFrame"/> рисует только его, поверх — HUD
/// (<see cref="Hud"/>: FPS и числа кадра, справка) UI-проходом рендера.
///
/// 5в: камера — за тазом игрока (<see cref="ArenaView.PlayerFocus"/>), тень и кольцо — таз (<see cref="ArenaView.PlayerMarker"/>);
/// капсулы тел поверх персонажей — F7 (<see cref="ShowBodies"/>, отладочное рисование рендера в фазе Debug); в кадре снимка —
/// по заказу ID-проход «scene.ids» (код бойца и сегмента на пиксель) и слой капсул <see cref="CapsulesOverlay"/>.
///
/// 6б: меч в руке каждого бойца — меш оружия таблицы (<see cref="ICombatView"/>) жёстким вложением в кисть позы, как показан
/// боец (<see cref="FighterView.WeaponWorld"/>; <c>SkinnedCharacters.Submit</c> SWORD); в ID-буфере пиксели меча — сегмент 11
/// «клинок»; клинки (отрезок тика и прошлого тика) — F7 вместе с капсулами, <c>--overlay blades</c> и слой снимка
/// <see cref="BladesOverlay"/>.
///
/// 6е: HUD боя (<see cref="CombatHud"/> — снимок мира последнего тика: здоровье и смерть игрока, живые враги, статистика арены
/// <see cref="ArenaStats"/>, меч; кольцо игрока — цветом по здоровью), инвентарь (<see cref="Inventory"/> — панель клиента: I
/// открывает, 1–9 и щелчок — команда арены «взять меч» после команд PageUp, PageDown, R, T тика), ручки «Бой» (<see cref="TuningGroups"/>
/// над привязкой <see cref="Tuning"/> — панель F3 подключает exe; 7и, часть 2 — и «Рэгдолл», <see cref="RagdollTuning"/>). Симуляцию
/// клиент не пишет: только читает мир и ставит команды.
///
/// 7и: труп — по телам (<see cref="ArenaView"/>: скин = привязка × тело, меч — на теле клинка, рамка — по телам), темнее живого
/// (<see cref="DeadShade"/>), в ID-буфере — «голова трупа врага 3»; оверлеи <see cref="JointsOverlay"/> (J) и <see cref="ContactsOverlay"/>
/// (K; трасса физики — диагностический режим <c>physics.trace</c>, клиент включает его между тиками, пока контакты показаны).
/// </summary>
public sealed class ArenaClient : IClientGame, IClientObservable
{
    /// <summary>Рамка бойца для наблюдения: полуширина и рост от корня (капсула Manny SWORD: радиус 0,35, рост ~1,8 м).</summary>
    public const float FighterHalfWidth = 0.45f, FighterHeight = 1.85f;

    /// <summary>Меш персонажа в контенте (<c>CharacterRig.MeshFile</c> SWORD).</summary>
    public const string MannyMesh = "characters/manny/mesh.swmesh";

    /// <summary>Цвет игрока (<c>SkinnedCharacters.Colour</c> SWORD).</summary>
    public static readonly Vector3 PlayerColour = new(0.3f, 0.4f, 0.65f);

    /// <summary>Цвет меча (<c>SkinnedCharacters.Submit</c> SWORD).</summary>
    public static readonly Vector3 WeaponColour = new(0.8f, 0.82f, 0.86f);

    /// <summary>Цвет врага на месте <paramref name="slot"/> (<c>SkinnedCharacters.EnemyColour</c> SWORD): оттенок по золотому сечению.</summary>
    public static Vector3 EnemyColour(int slot)
    {
        float h = slot * 0.618f % 1;
        return new Vector3(0.45f + 0.25f * MathF.Cos(MathF.Tau * h), 0.3f + 0.1f * MathF.Cos(MathF.Tau * (h + 0.33f)), 0.25f);
    }

    /// <summary>Мёртвый темнее живого (<c>SkinnedCharacters.Colour</c> SWORD: × 0,75; 7и — труп на экране).</summary>
    public const float DeadShade = 0.75f;

    /// <summary>Цвет бойца кадра: игрок — свой, враг — по месту; мёртвый — темнее (<see cref="DeadShade"/>).</summary>
    public static Vector3 Colour(in FighterView f) => (f.IsPlayer ? PlayerColour : EnemyColour(f.Slot)) * (f.Dead ? DeadShade : 1);

    /// <summary>Бойцов, отданных скиннингу в последнем кадре (итог Sword.exe: рендер рисует всех).</summary>
    public int SkinnedFighters { get; private set; }

    /// <summary>Цвет очистки кадра (как <c>SceneRenderer.Record</c> SWORD).</summary>
    public static readonly Vector4 ClearColour = new(0.33f, 0.29f, 0.27f, 1f);

    public const int SlotTerrain = 8, SlotMesh = 9;

    private const string SceneShader = "scene.hlsl";
    private static readonly GraphicsPipelineDesc Ground = new(SceneShader, "VsGround", "PsGround", Depth: DepthMode.TestWrite);
    private static readonly GraphicsPipelineDesc Terrain = new(SceneShader, "VsTerrain", "PsGround", Depth: DepthMode.TestWrite);
    private static readonly GraphicsPipelineDesc Pillar = new(SceneShader, "VsPillar", "PsPillar", Depth: DepthMode.TestWrite);
    private static readonly GraphicsPipelineDesc Skin = new(SceneShader, "VsSkin", "PsSkin", Depth: DepthMode.TestWrite);

    /// <summary>
    /// ID-проход (диагностический режим, только в кадре снимка с ID-буфером): те же скинированные меши тем же вершинным
    /// шейдером (та же глубина до бита) с тестом по глубине сцены — пиксель получает код ближайшего бойца и сегмента.
    /// </summary>
    private static readonly GraphicsPipelineDesc SkinId = new(SceneShader, "VsSkin", "PsSkinId", Depth: DepthMode.Test);

    /// <summary>Оверлей капсул тел — имя слоя снимка (<c>capture.bundle</c> overlays, <c>views[].overlays</c> манифеста).</summary>
    public const string CapsulesOverlay = "capsules";

    /// <summary>
    /// Оверлей клинков (6б): отрезок клинка тика (игрок — жёлтый, враги — белый), прошлого тика (серый) и стороны пройденного за
    /// тик (оранжевые); в слое снимка — ещё имя меча у кончика.
    /// </summary>
    public const string BladesOverlay = "blades";

    /// <summary>
    /// Оверлей контактов (7и): контакты последнего подшага последнего шага из трассы физики — точка (шарик), нормаль к телу длиной
    /// <see cref="ContactNormal"/> + <see cref="ContactDepthScale"/> × глубина, цвет — источник (<see cref="ContactColour"/>). Без трассы
    /// (диагностический режим <c>physics.trace</c>) пуст — снимок несёт заметку.
    /// </summary>
    public const string ContactsOverlay = "contacts";

    /// <summary>
    /// Оверлей суставов (7и): точка сустава на ребёнке (шарик), отрезок до точки на родителе (ошибка точки), цвет — в пределах (зелёный),
    /// выход за предел (от оранжевого к красному к <see cref="JointExcessRed"/>), выключен (серый — запястье трупа: меч выпал).
    /// </summary>
    public const string JointsOverlay = "joints";

    /// <summary>Оверлеи, которые клиент арены снимает слоем.</summary>
    public static readonly IReadOnlyList<string> KnownOverlays = [CapsulesOverlay, BladesOverlay, ContactsOverlay, JointsOverlay];

    /// <summary>Нормаль контакта на оверлее: длина, м, и прибавка на метр глубины (1 см глубины — +10 см).</summary>
    public const float ContactNormal = 0.1f, ContactDepthScale = 10f;

    /// <summary>Выход за предел, рад, при котором сустав на оверлее — красный (меньше — от оранжевого); до 0,01 рад — в пределах.</summary>
    public const float JointExcessRed = 0.2f, JointExcessShown = 0.01f;

    /// <summary>Цвет контакта по источнику (<c>0xAABBGGRR</c>): земля — зелёный, стена и колонны — синий, пара трупов — пурпурный, толчок живого телом — оранжевый, клинком — красный, без отдачи — серый.</summary>
    public static uint ContactColour(PhysicsContactSource source) => source switch
    {
        PhysicsContactSource.Ground => 0xFF40FF40,
        PhysicsContactSource.Wall => 0xFFFF8040,
        PhysicsContactSource.Pair => 0xFFFF40FF,
        PhysicsContactSource.PushBody => 0xFF0090FF,
        PhysicsContactSource.PushPass => 0xFF4040FF,
        PhysicsContactSource.PushNoReaction => 0xFFA0A0A0,
        _ => 0xFFFFFFFF,
    };

    /// <summary>Отметка тела на физике на оверлее капсул (7и): крест в центре — белый, касается земли — зелёный.</summary>
    public const uint SimulatedMark = 0xFFFFFFFF, GroundedMark = 0xFF40FF40;

    /// <summary>Цвета оверлея клинков (<c>0xAABBGGRR</c>): клинок тика игрока и врага, прошлого тика, стороны прохода.</summary>
    public const uint PlayerBladeColour = 0xFF40FFFF, EnemyBladeColour = 0xFFFFFFFF, PrevBladeColour = 0xB0A0A0A0, SweepColour = 0xA00090FF;

    /// <summary>
    /// Цвет капсулы сегмента (<c>0xAABBGGRR</c>, альфа — у кромки; середина прозрачнее): таз, торс, голова — тёплые
    /// светлые; левые конечности — голубые, правые — розово-красные; плечо и бедро темнее предплечья и голени, стопа —
    /// светлее. Лево и право видно сразу — по ним проверяют зеркальность постановки.
    /// </summary>
    public static uint CapsuleColour(int segment) => segment switch
    {
        BodySegs.Pelvis => Rgba(255, 200, 60),
        BodySegs.Torso => Rgba(255, 230, 120),
        BodySegs.Head => Rgba(255, 255, 200),
        BodySegs.UArmL => Rgba(60, 200, 255),
        BodySegs.FArmL => Rgba(120, 240, 255),
        BodySegs.ThighL => Rgba(40, 140, 255),
        BodySegs.ShinL => Rgba(90, 180, 255),
        BodySegs.FootL => Rgba(150, 220, 255),
        BodySegs.UArmR => Rgba(255, 90, 200),
        BodySegs.FArmR => Rgba(255, 150, 230),
        BodySegs.ThighR => Rgba(255, 70, 90),
        BodySegs.ShinR => Rgba(255, 120, 120),
        BodySegs.FootR => Rgba(255, 170, 150),
        _ => Rgba(255, 255, 255),
    };

    private static uint Rgba(byte r, byte g, byte b, byte a = 0xC0) => (uint)(r | g << 8 | b << 16 | a << 24);

    /// <summary>
    /// Код владельца экземпляра скиннинга для ID-буфера: номер сущности бойца + 1 (0 — «ничего»). Поколение в код не входит:
    /// в одном кадре номер у живых сущностей один, легенда (<see cref="IClientObservable.DescribeId"/>) ищет бойца кадра. Номер,
    /// который в код не помещается (больше <see cref="SkinnedMeshes.MaxOwner"/> − 1), — 0: пиксели без бойца, а не чужой код.
    /// </summary>
    public static uint OwnerOf(Entity e) => e.Index is >= 0 and < (int)SkinnedMeshes.MaxOwner ? (uint)e.Index + 1 : 0;

    /// <summary>Подписи бойцов на слое капсул снимка — заранее, без выделений в кадре.</summary>
    private static readonly string[] Labels = [.. Enumerable.Range(0, ArenaState.MaxEnemies + 1).Select(i => i == 0 ? "игрок" : $"враг {i}")];

    private readonly RtsCamera _camera = new();
    private readonly HeightField _field = ArenaTerrain.BuildField();
    private readonly ArenaView _view;
    private readonly Action<RenderPassContext> _drawOpaque;
    private readonly Action<RenderPassContext> _drawIds;
    private Renderer? _renderer;
    private GpuData? _heights, _capsule;
    private GpuIndices? _capsuleIndices;
    private int _manny = -1;
    /// <summary>Меши оружия по номеру таблицы оружия и их имена (подписи слоя клинков).</summary>
    private int[] _weapons = [];
    private string[] _weaponNames = [];
    private SimulationLink? _simulation;
    private readonly PlayerCommand? _scripted;
    private readonly bool _playerCommands;
    private readonly bool _fixedCamera;
    private PlayerCommand _frameCommand;
    /// <summary>Жест удара (6в): сдвиг мыши с затуханием, жест последнего клика (система бойца) и замахи рубящих ударов.</summary>
    private Vector2 _gesture, _pressGesture;
    private Vector2[] _swings = [];
    private readonly Hud _hud = new();
    private readonly bool _showHud;

    /// <summary>Сводка профайлера для F2 (точка Пв); null — профайлера нет (<see cref="CpuProfiler.Off"/>).</summary>
    private ProfileDigest? _digest;

    /// <summary>Профайлер включён F2 (выключает его F2 только тогда: включённый ключом или агентом остаётся).</summary>
    private bool _profilerByHud;
    private UiDrawList? _ui;
    private FrameStats _stats;
    private IReadOnlyList<string> _help = Hud.HelpLines(new Dictionary<string, string>());

    /// <summary>Бой для HUD последнего кадра (6е); null — до первого Extract.</summary>
    private CombatHud? _combat;
    private readonly InventoryPanel _inventory = new();
    private TuningBinding? _tuning;
    private readonly List<TuningGroup> _groups = [];

    /// <param name="camera">Неподвижный ракурс вместо камеры SWORD (проверочные снимки); null — камера за игроком.</param>
    /// <param name="scripted">Команда игрока на каждый тик вместо ввода (проверочные прогоны); null — клавиатура и мышь.</param>
    /// <param name="hud">HUD поверх сцены; false — кадр без интерфейса (снимки, сравнимые между прогонами).</param>
    /// <param name="playerCommands">
    /// Ставить команду игрока каждый тик и команды арены по клавишам; false — не ставить (реплей, <c>Sword.exe --replay</c>:
    /// команды — его, а своя команда клиента того же тика перебила бы их и увела бы реплей в сторону). Камера управляется
    /// в обоих случаях.
    /// </param>
    /// <param name="showBodies">Капсулы тел поверх персонажей с первого кадра (<c>--overlay capsules</c>; F7 — переключить).</param>
    /// <param name="showBlades">Клинки поверх персонажей с первого кадра (<c>--overlay blades</c>; F7 показывает их с капсулами).</param>
    /// <param name="showJoints">Суставы тел с первого кадра (7и: <c>--overlay joints</c>; J — переключить).</param>
    /// <param name="showContacts">Контакты физики с первого кадра и трасса физики (7и: <c>--overlay contacts</c>; K — переключить).</param>
    public ArenaClient(CameraPose? camera = null, PlayerCommand? scripted = null, bool hud = true, bool playerCommands = true,
        bool showBodies = false, bool showBlades = false, bool showJoints = false, bool showContacts = false)
    {
        if (!playerCommands && scripted is not null) throw new ArgumentException("заданная команда игрока без команд игрока", nameof(scripted));
        _showHud = hud;
        _playerCommands = playerCommands;
        ShowBodies = showBodies;
        ShowBlades = showBlades;
        ShowJoints = showJoints;
        ShowContacts = showContacts;
        _drawOpaque = DrawOpaque;
        _drawIds = DrawIds;
        _view = new ArenaView(ArenaTerrain.Height);
        _scripted = scripted;
        if (camera is { } c)
        {
            // Пределы SWORD (--pitch, --zoom: наклон 0,45…1,45, дистанция через SetDistance — 6…110 м).
            _fixedCamera = true;
            _camera.Follow = false;
            _camera.Yaw = c.Yaw;
            _camera.Pitch = Math.Clamp(c.Pitch, RtsCamera.MinPitch, RtsCamera.MaxPitch);
            _camera.SetDistance(c.Distance);
            _camera.Target = new Vector3(c.TargetX, 0, c.TargetZ);
        }
    }

    /// <summary>Поле высот арены — то же, что рисуется.</summary>
    public HeightField Field => _field;

    /// <summary>Мир представления последнего кадра.</summary>
    public ArenaView View => _view;

    /// <summary>Камера (для итога и агента).</summary>
    public RtsCamera Camera => _camera;

    /// <summary>HUD (для итога и агента: показана ли справка).</summary>
    public Hud Hud => _hud;

    /// <summary>HUD рисуется.</summary>
    public bool ShowsHud => _showHud;

    /// <summary>Сводка профайлера F2 (итог Sword.exe, тесты); null — профайлера нет.</summary>
    public ProfileDigest? ProfileDigest => _digest;

    /// <summary>Квадов HUD в последнем кадре (0 — HUD не рисуется).</summary>
    public int HudQuads { get; private set; }

    /// <summary>Капсулы тел поверх персонажей (F7, <c>--overlay capsules</c>).</summary>
    public bool ShowBodies { get; set; }

    /// <summary>Капсул, отданных отладочному рисованию в последнем кадре (0 — капсулы не рисовались).</summary>
    public int DrawnCapsules { get; private set; }

    /// <summary>
    /// Из них с крестом тела на физике (7и: тела трупа — <see cref="SimulatedMark"/>) и из них касающихся земли (<see cref="GroundedMark"/>)
    /// — в последнем кадре (0 — капсулы не рисовались).
    /// </summary>
    public int DrawnMarks { get; private set; }
    public int DrawnGroundedMarks { get; private set; }

    /// <summary>Клинки поверх персонажей (<c>--overlay blades</c>; F7 — вместе с капсулами).</summary>
    public bool ShowBlades { get; set; }

    /// <summary>Мечей, отданных рендеру в последнем кадре (у каждого бойца с мечом — один), и клинков оверлея (0 — не рисовались).</summary>
    public int DrawnWeapons { get; private set; }
    public int DrawnBlades { get; private set; }

    /// <summary>Суставы тел поверх кадра (7и: J, <c>--overlay joints</c>).</summary>
    public bool ShowJoints { get; set; }

    /// <summary>
    /// Контакты физики поверх кадра (7и: K, <c>--overlay contacts</c>): пока показаны, трасса физики включена (клиент включает её в шаге
    /// ввода кадра, между тиками; выключает — только если включил сам).
    /// </summary>
    public bool ShowContacts { get; set; }

    /// <summary>Суставов и контактов, отданных отладочному рисованию в последнем кадре (7и; 0 — не рисовались).</summary>
    public int DrawnJoints { get; private set; }
    public int DrawnContacts { get; private set; }

    /// <summary>Трассу физики включил клиент (K, <c>--overlay contacts</c>) — выключает её тоже он; включённую агентом не трогает.</summary>
    private bool _traceByClient;

    /// <summary>Меч в руке игрока в последнем кадре (имя таблицы оружия); игрока или меча нет — null.</summary>
    public string? PlayerWeapon => _view.Player is { Weapon: var w } && (uint)w < (uint)_weaponNames.Length ? _weaponNames[w] : null;

    /// <summary>Строки справки HUD.</summary>
    public IReadOnlyList<string> HelpLines => _help;

    /// <summary>Бой в HUD последнего кадра (6е; итог Sword.exe, тесты); null — кадра ещё не было.</summary>
    public CombatHud? Combat => _combat;

    /// <summary>
    /// Панель инвентаря (6е) — панель клиента: exe подключает её к клиенту движка (<c>ClientHost.AddPanel</c>); открывает её I, выбор
    /// меча уходит командой арены из <see cref="BeforeTick"/>.
    /// </summary>
    public InventoryPanel Inventory => _inventory;

    /// <summary>Привязка ручек симуляции для панели F3 (6е); null — до <see cref="Initialize"/>.</summary>
    public TuningBinding? Tuning => _tuning;

    /// <summary>Группы ручек игры (вкладки панели F3: «Бой», «Рэгдолл») — заводятся в <see cref="Initialize"/>.</summary>
    public IReadOnlyList<TuningGroup> TuningGroups => _groups;

    /// <summary>Нижний край панели производительности HUD в последнем кадре, пиксели (под ней — панель ручек, как в SWORD); без HUD — 0.</summary>
    public float HudBottom => _showHud ? _hud.PerfBottom : 0;

    /// <summary>Камера показанного кадра — для пакета наблюдения агента.</summary>
    CameraView IClientObservable.Camera =>
        new(_camera.ViewProj, _camera.Eye, _camera.Target, _camera.Yaw, _camera.Pitch, _camera.Distance, _camera.Follow);

    /// <summary>
    /// Бойцы кадра в показанном (интерполированном) положении — рамкой вокруг тела; у трупа (7и) — рамка его показанных тел (с мечом на
    /// теле клинка): лежащий труп не влезает в рамку стоящего.
    /// </summary>
    void IClientObservable.Visible(List<VisibleEntity> into)
    {
        foreach (var f in _view.Fighters)
        {
            string kind = f.IsPlayer ? "player" : f.Slot > 0 ? "enemy" : "fighter";
            if (f.Corpse)
            {
                into.Add(new VisibleEntity(f.Entity, kind, f.BoundsMin, f.BoundsMax));
                continue;
            }
            var base_ = new Vector3(f.Root.X, f.Height, f.Root.Z);
            into.Add(new VisibleEntity(f.Entity, kind,
                base_ - new Vector3(FighterHalfWidth, 0, FighterHalfWidth), base_ + new Vector3(FighterHalfWidth, FighterHeight, FighterHalfWidth)));
        }
    }

    IReadOnlyList<string> IClientObservable.Overlays => KnownOverlays;

    /// <summary>
    /// Код ID-буфера последнего кадра словами: владелец — боец кадра (<see cref="OwnerOf"/>), часть — сегмент тела; название
    /// — «левое предплечье врага 3», «голова игрока»; пиксели меча — сегмент 11: «клинок врага 3», «клинок игрока» (6б); у мёртвого
    /// (7и) — «таз трупа врага 3», «клинок трупа игрока» (меч трупа лежит на теле клинка — пиксели его, сегмент 11).
    /// </summary>
    IdMeaning? IClientObservable.DescribeId(uint id)
    {
        var (owner, part) = SkinnedMeshes.SplitId(id);
        if (owner == 0) return null;
        foreach (var f in _view.Fighters)
        {
            if (OwnerOf(f.Entity) != owner) continue;
            string who = (f.Dead ? "трупа " : "") + (f.IsPlayer ? "игрока" : $"врага {f.Slot}");
            string kind = f.IsPlayer ? "player" : "enemy";
            return part == SkinnedMeshes.NoPart
                ? new IdMeaning(f.Entity, kind, null, who)
                : new IdMeaning(f.Entity, kind, BodySegs.Name((int)part), $"{BodySegs.Title((int)part)} {who}");
        }
        return null;
    }

    /// <summary>
    /// Справка, подписи HUD и подсказка инвентаря — по действующим привязкам (действие → клавиша): композиция отдаёт их после
    /// разрешения конфига.
    /// </summary>
    public void UseBindings(IReadOnlyDictionary<string, string> keys)
    {
        _help = Hud.HelpLines(keys);
        string K(InputAction a) => Hud.Show(keys.TryGetValue(a.Name, out var k) ? k : a.DefaultKey);
        _hud.InventoryKey = K(ArenaInput.Inventory);
        _hud.ResetKey = K(ArenaInput.ArenaReset);
        _inventory.UseKeys(K(ArenaInput.Weapons[0]), K(ArenaInput.Weapons[^1]), K(ArenaInput.Inventory));
        _traceOffNote = TraceOffNote(K(ArenaInput.DebugContacts));
    }

    /// <summary>Корень игрока в симуляции на последнем тике кадра (итог Sword.exe, сверка с Headless); нет игрока — null.</summary>
    public Transform? LastPlayer => _view.PlayerAtTick;

    /// <summary>Позиция тени и кольца игрока, когда игрока в кадре нет: за пределами арены — не рисуются.</summary>
    private static readonly Vector3 NoPlayer = new(0, 0, 1e6f);

    public IReadOnlyList<InputAction> Actions => ArenaInput.All;

    public void ContributeShaders(ShaderContract c)
    {
        c.Section("арена (Sword.Game.Client)");
        c.Add("TERRAIN_CELLS", (uint)_field.Cells, "ячеек сетки рельефа на сторону");
        c.Add("TERRAIN_HALF", _field.Half, "полуразмер сетки рельефа, м");
        c.Add("PILLAR_COUNT", ArenaLayout.PillarCount, "колонн в константах кадра");
        c.AddSlot("R_TERRAIN", SlotTerrain, "высоты узлов рельефа (HeightField, по строкам z)");
        c.AddSlot("R_MESH", SlotMesh, "вершины единичной капсулы");
        c.AddSize<FrameConstants>("FRAME_CONSTANTS_SIZE");
        c.AddSize<CapsuleVertex>("CAPSULE_VERTEX_SIZE");
    }

    public void Initialize(Renderer renderer, SimulationLink? simulation)
    {
        _renderer = renderer;
        // Профайлер CPU кадра (точка Па) — тот же, что у рендера: зоны арены заводятся здесь, не в кадре.
        var profiler = renderer.Profiler;
        _view.Profiler = profiler;
        _zones = (profiler.Zone("arena.build.skin"), profiler.Zone("arena.build.hud"), profiler.Zone("arena.build.bodies"));
        _digest = profiler.IsOff ? null : new ProfileDigest(profiler);
        // Игрок и его команда — ядро игры: симуляция у арены есть всегда.
        _simulation = simulation ?? throw new ArgumentNullException(nameof(simulation), "клиенту арены нужна симуляция");
        _heights = renderer.CreateData<float>("Arena.Heights", _field.Heights);

        var verts = new List<CapsuleVertex>();
        var idx = new List<uint>();
        CapsuleMesh.Build(verts, idx);
        _capsule = renderer.CreateData<CapsuleVertex>("Mesh.CapsuleVertices", CollectionsMarshal.AsSpan(verts));
        _capsuleIndices = renderer.CreateIndices("Mesh.CapsuleIndices", CollectionsMarshal.AsSpan(idx));

        _manny = renderer.Skinned.LoadMesh("Manny", MannyMesh);
        // ID-буфер: пиксель Manny — сегмент тела сустава вершины (CharacterRig.JointSegment SWORD, вид тел симуляции).
        var bodies = _simulation.World.View<IBodyView>();
        int joints = renderer.Skinned.JointCount(_manny);
        if (joints != bodies.JointCount)
            throw new InvalidDataException($"{MannyMesh}: суставов меша {joints}, скелета бойца {bodies.JointCount}");
        var parts = new uint[joints];
        for (int j = 0; j < joints; j++) parts[j] = (uint)bodies.JointSegment(j);
        renderer.Skinned.SetJointParts(_manny, parts);
        // Мечи (6б): меш оружия таблицы — статический, в ID-буфере весь — клинок (сегмент 11).
        var weapons = _simulation.World.View<ICombatView>().Weapons;
        _weapons = new int[weapons.Length];
        _weaponNames = new string[weapons.Length];
        ReadOnlySpan<uint> blade = [(uint)BodySegs.Weapon];
        for (int w = 0; w < weapons.Length; w++)
        {
            _weapons[w] = renderer.Skinned.LoadMesh("Weapon." + weapons[w].Name, weapons[w].Mesh);
            renderer.Skinned.SetJointParts(_weapons[w], blade);
            _weaponNames[w] = weapons[w].Name;
        }
        // Жест удара (6в): направления замаха рубящих ударов таблицы — в системе бойца.
        var swings = new List<Vector2>();
        foreach (var a in _simulation.World.View<ICombatView>().Attacks)
            if (!a.Thrust) swings.Add(a.SwingDir);
        _swings = [.. swings];
        // 6е: инвентарь — имена мечей таблицы; ручки «Бой» — привязка к ручкам симуляции (панель F3 подключает exe). 7и, часть 2 — ручки
        // «Рэгдолл» (секция ragdoll) и трупы кадра.
        _inventory.Attach(_simulation, _weaponNames);
        _tuning = new TuningBinding(_simulation);
        _groups.Clear();
        _groups.Add(CombatTuning.Build(_tuning, _simulation));
        _groups.Add(RagdollTuning.Build(_tuning, _view));
        if (_showHud)
        {
            _ui = renderer.Ui.CreateDrawList();
            Log.Info("hud", renderer.Ui.Report());
        }
        var (min, max) = _field.Range();
        Log.Info("arena", $"рельеф {_field.Cells}×{_field.Cells} ячеек ({_field.Nodes}×{_field.Nodes} узлов), шаг {_field.Step} м, " +
            $"высоты {min:0.###}…{max:0.###} м");
    }

    public void Input(in FrameInput input)
    {
        if (input.WasPressed(ArenaInput.HudHelp)) _hud.ShowHelp = !_hud.ShowHelp;
        if (input.WasPressed(ArenaInput.HudProfiler)) ToggleProfiler();
        if (input.WasPressed(ArenaInput.DebugBodies)) ShowBodies = !ShowBodies;
        if (input.WasPressed(ArenaInput.DebugJoints)) ShowJoints = !ShowJoints;
        if (input.WasPressed(ArenaInput.DebugContacts)) ShowContacts = !ShowContacts;
        SyncTrace();
        // Инвентарь (6е; SWORD: I, если не набирается текст) — панель видна со следующего кадра панелей.
        if (input.WasPressed(ArenaInput.Inventory)) _inventory.Open = !_inventory.Open;
        // Камера — до тиков, за тазом игрока, каким его показал прошлый кадр (Program.cs SWORD: focus — таз по renderAlpha
        // прошлого кадра).
        if (_fixedCamera) _camera.Rebuild(input.Width, input.Height);
        else _camera.Update(ArenaInput.Camera(input), input.Seconds, _view.PlayerFocus ?? _camera.Target, input.Width, input.Height);

        // Жест удара (6в): сдвиг мыши за последние кадры; на клике — удар, чья траектория на экране ближе к жесту (камера кадра,
        // игрок, каким его показал прошлый кадр), в систему бойца.
        _gesture = StrikeGesture.Accumulate(_gesture, input.MouseDeltaX, input.MouseDeltaY, input.Seconds);
        if (input.WasPressed(ArenaInput.Attack))
            _pressGesture = _view.Player is { } shown
                ? StrikeGesture.Pick(_gesture, _swings, _camera.ViewProj, input.Width, input.Height, shown.Root with { Y = shown.Height }, shown.Facing)
                : Vector2.Zero;

        if (_scripted is { } scripted)
        {
            _frameCommand = scripted;
            return;
        }
        // Курсор — после камеры этого кадра; точка на плоскости земли, только когда курсор над окном (SWORD).
        bool hit = _camera.ScreenToGround(input.MouseX, input.MouseY, input.Width, input.Height, out var ground);
        _frameCommand = PlayerIntent.Command(ArenaInput.Player(input), _camera, hit && input.MouseInside, ground);
    }

    /// <summary>
    /// Трасса физики под оверлей контактов (7и): шаг ввода кадра — до тиков, между тиками — там диагностический режим и меняется. Контакты
    /// показаны, а трасса выключена — включить (и запомнить, что включил клиент); скрыты — выключить, только если включил клиент
    /// (включённую агентом оставить; как F2 с профайлером). И при воспроизведении реплея: режим не меняет ни мир, ни хеш, ни реплей.
    /// </summary>
    private void SyncTrace()
    {
        if (_simulation is not { } link || !link.HasDiagnostic(IPhysicsDebugView.TraceMode)) return;
        bool on = link.DiagnosticEnabled(IPhysicsDebugView.TraceMode);
        if (ShowContacts && !on)
        {
            link.SetDiagnostic(IPhysicsDebugView.TraceMode, true);
            _traceByClient = true;
        }
        else if (!ShowContacts && _traceByClient)
        {
            if (on) link.SetDiagnostic(IPhysicsDebugView.TraceMode, false);
            _traceByClient = false;
        }
    }

    /// <summary>
    /// F2 (точка Пв): секция профайлера в HUD; профайлер выключен — F2 его включает (со следующего кадра), и выключает, только если
    /// включил сам. Представление: симуляцию не трогает, в реплей не идёт.
    /// </summary>
    private void ToggleProfiler()
    {
        _hud.ShowProfiler = !_hud.ShowProfiler;
        if (_renderer?.Profiler is not { IsOff: false } profiler) return;
        if (_hud.ShowProfiler && !profiler.Requested)
        {
            profiler.SetEnabled(true);
            _profilerByHud = true;
        }
        else if (!_hud.ShowProfiler && _profilerByHud)
        {
            profiler.SetEnabled(false);
            _profilerByHud = false;
        }
    }

    /// <summary>
    /// Команды тика: команда игрока кадра (удерживаемое — в каждом тике кадра; удар, укол, рывок — фронты, 6в) и команды арены из
    /// фронтов PageUp/PageDown/R/T (ровно одним тиком — <see cref="TickInput"/>). При воспроизведении реплея (<c>playerCommands: false</c>) — ни тех ни
    /// других: команды — реплея, своя команда того же тика увела бы его в сторону.
    /// </summary>
    public void BeforeTick(SimulationLink simulation, in TickInput input)
    {
        if (!_playerCommands)
        {
            _inventory.TakeClicked();   // реплей: выбор меча не ставится (увёл бы реплей в сторону)
            return;
        }
        // Удар, укол, рывок (6в) — фронты: ровно в одном тике, первом после кадра нажатия; жест — клика.
        var command = _frameCommand;
        if (input.WasPressed(ArenaInput.Attack))
        {
            command.Attack = true;
            command.Gesture = _pressGesture;
        }
        command.Stab = input.WasPressed(ArenaInput.Stab);
        command.Dash = input.WasPressed(ArenaInput.Dash);
        simulation.Enqueue(command);
        Span<ArenaCommand> arena = stackalloc ArenaCommand[ArenaIntent.Max];
        int n = ArenaIntent.Commands(input, arena);
        for (int i = 0; i < n; i++) simulation.Enqueue(arena[i]);
        // Инвентарь (6е; Program.cs SWORD — после PageUp, PageDown, R, T): щелчок по строке и 1–9 при открытой панели — «взять меч»
        // (k-й по порядку получения, Owned[k] SWORD); меча с таким номером нет — ничего.
        int clicked = _inventory.TakeClicked();
        if (clicked >= 0) simulation.Enqueue(new ArenaCommand { Action = ArenaAction.EquipWeapon, Weapon = clicked });
        if (!_inventory.Open) return;
        var weapons = ArenaInput.Weapons;
        for (int k = 0; k < weapons.Count; k++)
            if (input.WasPressed(weapons[k]) && simulation.World.Resource<PlayerInventory>().OwnedAt(k) is >= 0 and var weapon)
                simulation.Enqueue(new ArenaCommand { Action = ArenaAction.EquipWeapon, Weapon = weapon });
    }

    public void Extract(in FrameView view)
    {
        // Капсулы всех бойцов — только когда их рисуют (F7, --overlay capsules) или их слой заказан снимком инструмента (заказ
        // известен до Extract — FrameView.Capture); иначе — только таз игрока: камера, тень, кольцо (исправления после Пд). Клинки —
        // так же (F7, --overlay blades, слой blades).
        // 7и: суставы (J, --overlay joints, слой joints) — с капсулами, на которых стоят; контакты (K, --overlay contacts, слой contacts) —
        // копия контактов трассы.
        _view.Extract(_simulation!, view.Alpha, allBodies: ShowBodies || view.Capture?.WantsLayer(CapsulesOverlay) == true,
            blades: ShowBodies || ShowBlades || view.Capture?.WantsLayer(BladesOverlay) == true,
            joints: ShowJoints || view.Capture?.WantsLayer(JointsOverlay) == true,
            contacts: ShowContacts || view.Capture?.WantsLayer(ContactsOverlay) == true);
        _stats = view.Stats;
        // Бой для HUD (6е): мир последнего тика кадра — только чтение.
        var world = _simulation!.World;
        var stats = world.Resource<ArenaStats>();
        _combat = new CombatHud(_view.PlayerHp, _view.PlayerDead, _view.AliveEnemies, world.Resource<ArenaState>().Enemies, stats.Kills,
            stats.Hits, stats.LastPart, stats.LastDamage, PlayerWeapon, _inventory.Open);
    }

    public void BuildFrame(RenderFrame frame)
    {
        _camera.Rebuild(frame.Width, frame.Height);   // кадр из перетаскивания рамки — без Input, со своим размером
        frame.SetFrameConstants(Constants());

        // Manny в позе тика (аниматор); позы нет — поза привязки: палитра InverseBind·Component·World = World.
        // Владелец экземпляра — код бойца для ID-буфера снимка (OwnerOf).
        var profiler = _renderer!.Profiler;
        profiler.Begin(_zones.Skin);
        int skinned = 0;
        var fighters = _view.Fighters;
        for (int i = 0; i < fighters.Count; i++)
        {
            var fighter = fighters[i];
            var pose = _view.Pose(fighter);
            var colour = Colour(fighter);
            uint owner = OwnerOf(fighter.Entity);
            if (pose.IsEmpty)
            {
                var palette = _renderer!.Skinned.Add(_manny, colour, owner);
                palette.Fill(GpuMat34.From(fighter.World));
                if (!palette.IsEmpty) skinned++;
            }
            // 7и: у трупа поза — уже мировые матрицы суставов (привязка × тело), мир экземпляра — единичный.
            else if (_renderer!.Skinned.AddPosed(_manny, pose, fighter.Corpse ? Matrix4x4.Identity : fighter.World, colour, owner)) skinned++;
        }
        SkinnedFighters = skinned;
        // Мечи — после всех тел (SkinnedCharacters SWORD: меч — сразу за телом бойца; кончится место — пропадут мечи, а не бойцы).
        int armed = 0;
        for (int i = 0; i < fighters.Count; i++)
        {
            var fighter = fighters[i];
            if ((uint)fighter.Weapon < (uint)_weapons.Length
                && _renderer!.Skinned.AddRigid(_weapons[fighter.Weapon], fighter.WeaponWorld, WeaponColour, OwnerOf(fighter.Entity)))
                armed++;
        }
        DrawnWeapons = armed;
        profiler.End(_zones.Skin);

        frame.Graph.AddPass("scene.opaque")
            .Color(frame.Graph.BackBuffer, ClearColour)
            .Color(frame.Scene.Normal, SceneTargets.NormalClear)
            .Depth(frame.Scene.Depth, DepthAccess.Write, clear: 0f)
            .Execute(_drawOpaque);

        // ID-буфер — только в кадре снимка, который его заказал (диагностический режим, раздел 8 плана).
        if (frame.Capture?.IdsPath is not null)
        {
            var ids = frame.Graph.CreateTexture("scene.ids", new TextureDesc(TextureFormat.R32Uint, TextureUsage.ColorTarget));
            frame.Graph.AddPass("scene.ids").Color(ids, Vector4.Zero).Depth(frame.Scene.Depth, DepthAccess.Read).Execute(_drawIds);
            frame.CaptureIds(ids);
        }

        if (_ui is not null)
        {
            profiler.Begin(_zones.Hud);
            DrawHud(frame);
            profiler.End(_zones.Hud);
        }
        profiler.Begin(_zones.Bodies);
        DrawDebug(frame);
        profiler.End(_zones.Bodies);
    }

    /// <summary>Зоны профайлера CPU в построении кадра арены (<see cref="Initialize"/>).</summary>
    private (int Skin, int Hud, int Bodies) _zones;

    /// <summary>
    /// Фаза Debug (после сцены и интерфейса, раздел 6 плана): оверлеи — капсулы тел (F7; у трупа — его тела, на физике — с отметкой
    /// <see cref="SimulatedMark"/>, 7и), клинки (F7, <c>--overlay blades</c>), суставы (J, <c>--overlay joints</c>) и контакты (K,
    /// <c>--overlay contacts</c>) — поверх кадра одним проходом и (или) каждый своим слоем снимка (капсулы — с подписями бойцов, клинки —
    /// с именами мечей). У каждого слоя — только своё: он рисует накопленное со своей отметки (<see cref="DebugDraw.Mark"/>); экран — со
    /// своей, после слоёв, без подписей (оверлей, заказанный и слоем, и на экран, копится дважды — это кадр снимка). Слой контактов без
    /// трассы и слой суставов без суставов — пустые, с заметкой снимка (<see cref="RenderFrame.CaptureNote"/>).
    /// </summary>
    private void DrawDebug(RenderFrame frame)
    {
        bool capsulesLayer = frame.WantsLayer(CapsulesOverlay), bladesLayer = frame.WantsLayer(BladesOverlay);
        bool contactsLayer = frame.WantsLayer(ContactsOverlay), jointsLayer = frame.WantsLayer(JointsOverlay);
        bool screenBlades = ShowBodies || ShowBlades, screen = screenBlades || ShowJoints || ShowContacts;
        DrawnCapsules = DrawnBlades = DrawnJoints = DrawnContacts = DrawnMarks = DrawnGroundedMarks = 0;
        if (!screen && !capsulesLayer && !bladesLayer && !contactsLayer && !jointsLayer) return;
        var debug = _renderer!.Debug;
        if (capsulesLayer)
        {
            var from = debug.Mark();
            DrawnCapsules = DrawCapsules(debug);
            CapsuleLabels(debug);
            Layer(frame, debug, CapsulesOverlay, "overlay.capsules", from);
        }
        if (bladesLayer)
        {
            var from = debug.Mark();
            DrawnBlades = DrawBlades(debug);
            BladeLabels(debug);
            Layer(frame, debug, BladesOverlay, "overlay.blades", from);
        }
        if (contactsLayer)
        {
            var from = debug.Mark();
            DrawnContacts = DrawContacts(debug);
            if (!_view.TraceOn) frame.CaptureNote(_traceOffNote);
            else if (_view.LostContacts > 0) frame.CaptureNote($"{ContactsOverlay}: не поместилось в трассу контактов последнего подшага — {_view.LostContacts}");
            Layer(frame, debug, ContactsOverlay, "overlay.contacts", from);
        }
        if (jointsLayer)
        {
            var from = debug.Mark();
            DrawnJoints = DrawJoints(debug);
            if (_view.JointTotal == 0) frame.CaptureNote($"{JointsOverlay}: суставов нет ни у одного бойца кадра (суставы — у трупа на физике)");
            Layer(frame, debug, JointsOverlay, "overlay.joints", from);
        }
        if (!screen) return;
        var screenFrom = debug.Mark();
        if (ShowBodies) DrawnCapsules = DrawCapsules(debug);
        if (screenBlades) DrawnBlades = DrawBlades(debug);
        if (ShowJoints) DrawnJoints = DrawJoints(debug);
        if (ShowContacts) DrawnContacts = DrawContacts(debug);
        debug.AddPass(frame, frame.Graph.BackBuffer, _camera.ViewProj, _camera.Eye, "debug.bodies", since: screenFrom);
    }

    /// <summary>Заметка снимка: слой контактов заказан, а трасса выключена (строка — заранее: клавиша из привязок).</summary>
    private string _traceOffNote = TraceOffNote("K");

    private static string TraceOffNote(string key) =>
        $"{ContactsOverlay}: трасса физики выключена — контактов нет (включить: physics.bodies {{\"trace\":\"on\"}}, клавиша {key} или " +
        $"--overlay {ContactsOverlay}; контакты — со следующего тика)";

    /// <summary>Слой снимка <paramref name="name"/>: накопленное с отметки <paramref name="from"/> — в свою прозрачную цель.</summary>
    private void Layer(RenderFrame frame, DebugDraw debug, string name, string pass, DebugDraw.DebugMark from)
    {
        var target = frame.Graph.CreateTexture(pass, new TextureDesc(TextureFormat.Rgba8Unorm, TextureUsage.ColorTarget));
        debug.AddPass(frame, target, _camera.ViewProj, _camera.Eye, pass, clear: Vector4.Zero, since: from);
        frame.CaptureLayer(name, target);
    }

    /// <summary>
    /// Капсулы тел всех бойцов кадра (цвет — сегмент); тела на физике (труп, 7и) — с крестом в центре (<see cref="DrawnMarks"/>). Вернёт,
    /// сколько капсул.
    /// </summary>
    private int DrawCapsules(DebugDraw debug)
    {
        int drawn = 0;
        DrawnMarks = DrawnGroundedMarks = 0;
        var fighters = _view.Fighters;
        for (int i = 0; i < fighters.Count; i++) drawn += DrawBody(debug, _view.Body(fighters[i]));
        return drawn;
    }

    /// <summary>Капсулы одного тела (тело цикла по бойцам — свой метод).</summary>
    private int DrawBody(DebugDraw debug, ReadOnlySpan<ShownCapsule> body)
    {
        for (int k = 0; k < body.Length; k++)
        {
            ref readonly var c = ref body[k];
            debug.Capsule(c.Center, c.Rotation, c.Radius, c.HalfLength, CapsuleColour(c.Segment));
            if (!c.Simulated) continue;
            DrawnMarks++;
            if (c.Grounded) DrawnGroundedMarks++;
            uint mark = c.Grounded ? GroundedMark : SimulatedMark;
            float s = MathF.Max(c.Radius, 0.04f);
            debug.Line(c.Center - new Vector3(s, 0, 0), c.Center + new Vector3(s, 0, 0), mark);
            debug.Line(c.Center - new Vector3(0, s, 0), c.Center + new Vector3(0, s, 0), mark);
            debug.Line(c.Center - new Vector3(0, 0, s), c.Center + new Vector3(0, 0, s), mark);
        }
        return body.Length;
    }

    /// <summary>Подписи бойцов слоя капсул («игрок», «враг 3») над головой.</summary>
    private void CapsuleLabels(DebugDraw debug)
    {
        var fighters = _view.Fighters;
        for (int i = 0; i < fighters.Count; i++)
        {
            var f = fighters[i];
            if (_view.Body(f) is { Length: > 0 } body && BodySegs.Body(BodySegs.Head) is var head && head < body.Length)
                debug.Text(body[head].EndB + new Vector3(0, 0.3f, 0), Labels[Math.Clamp(f.IsPlayer ? 0 : f.Slot, 0, Labels.Length - 1)],
                    f.IsPlayer ? 0xFF80FFFFu : 0xFFFFFFFFu);
        }
    }

    /// <summary>Клинки (6б): отрезок тика, прошлого тика и стороны пройденного за тик. Вернёт, сколько клинков.</summary>
    private int DrawBlades(DebugDraw debug)
    {
        int drawn = 0;
        var fighters = _view.Fighters;
        for (int i = 0; i < fighters.Count; i++)
        {
            if (fighters[i].Blade is not { } b) continue;
            debug.Line(b.PrevBase, b.PrevTip, PrevBladeColour);
            debug.Line(b.PrevBase, b.Base, SweepColour);
            debug.Line(b.PrevTip, b.Tip, SweepColour);
            debug.Line(b.Base, b.Tip, fighters[i].IsPlayer ? PlayerBladeColour : EnemyBladeColour);
            drawn++;
        }
        return drawn;
    }

    /// <summary>Имена мечей у кончиков клинков (слой клинков).</summary>
    private void BladeLabels(DebugDraw debug)
    {
        var fighters = _view.Fighters;
        for (int i = 0; i < fighters.Count; i++)
            if (fighters[i].Blade is { } b && (uint)fighters[i].Weapon < (uint)_weaponNames.Length)
                debug.Text(b.Tip + new Vector3(0, 0.1f, 0), _weaponNames[fighters[i].Weapon], fighters[i].IsPlayer ? 0xFF80FFFFu : 0xFFFFFFFFu, 12);
    }

    /// <summary>
    /// Суставы тел (7и): шарик в точке сустава на ребёнке (в пределах — зелёный, выход за предел — от оранжевого к красному и крупнее,
    /// выключен — серый), отрезок до точки на родителе (ошибка точки) — красный. Вернёт, сколько суставов.
    /// </summary>
    private int DrawJoints(DebugDraw debug)
    {
        int drawn = 0;
        var fighters = _view.Fighters;
        for (int i = 0; i < fighters.Count; i++) drawn += DrawJointsOf(debug, _view.Joints(fighters[i]));
        return drawn;
    }

    private static int DrawJointsOf(DebugDraw debug, ReadOnlySpan<ShownJoint> joints)
    {
        for (int k = 0; k < joints.Length; k++)
        {
            ref readonly var j = ref joints[k];
            debug.Capsule(j.Point, Quaternion.Identity, j.Excess > JointExcessShown ? 0.035f : 0.022f, 0, JointColour(j.Enabled, j.Excess));
            if (Vector3.DistanceSquared(j.Point, j.ParentPoint) > 1e-6f) debug.Line(j.Point, j.ParentPoint, JointGapColour);
        }
        return joints.Length;
    }

    /// <summary>Отрезок от точки сустава на ребёнке к точке на родителе (ошибка точки больше 1 мм) — красный.</summary>
    public const uint JointGapColour = 0xFF2020FF;

    /// <summary>Цвет сустава на оверлее (<c>0xAABBGGRR</c>): выключен — серый; в пределах — зелёный; выход — от оранжевого к красному.</summary>
    public static uint JointColour(bool enabled, float excess)
    {
        if (!enabled) return 0xE0909090;
        if (!(excess > JointExcessShown)) return 0xE040E040;
        float t = Math.Clamp(excess / JointExcessRed, 0, 1);
        return 0xF00000FF | (uint)(160 * (1 - t)) << 8;
    }

    /// <summary>
    /// Контакты последнего подшага (7и): шарик в точке, нормаль к телу длиной <see cref="ContactNormal"/> + <see cref="ContactDepthScale"/>
    /// × глубина, цвет — источник (<see cref="ContactColour"/>). Точки — конца последнего подшага (не интерполированы по доле тика).
    /// Вернёт, сколько контактов.
    /// </summary>
    private int DrawContacts(DebugDraw debug)
    {
        var contacts = _view.Contacts;
        for (int i = 0; i < contacts.Length; i++)
        {
            ref readonly var c = ref contacts[i];
            uint colour = ContactColour(c.Source);
            debug.Capsule(c.Point, Quaternion.Identity, 0.015f, 0, colour);
            debug.Line(c.Point, c.Point + c.Normal * (ContactNormal + ContactDepthScale * MathF.Max(c.Depth, 0)), colour);
        }
        return contacts.Length;
    }

    /// <summary>ID-проход: скинированные меши по глубине сцены (только проходит равная — ближайшая поверхность).</summary>
    private void DrawIds(RenderPassContext ctx) => _renderer!.Skinned.Record(ctx, SkinId);

    /// <summary>HUD поверх сцены: числа кадра — прошлого (этот ещё пишется), вызовы и квады — тоже.</summary>
    private void DrawHud(RenderFrame frame)
    {
        var renderer = _renderer!;
        int draws = renderer.Graph.LastFrameDraws;   // без отчёта прохода: он строится только по запросу (0 Б/кадр — исправления после Пд)
        _ui!.Clear();
        // Сводка F2 — раз в 0,25 с из итогов профайлера (в остальных кадрах — сравнение времени), только когда секция видна.
        if (_hud.ShowProfiler) _digest?.Update(System.Diagnostics.Stopwatch.GetTimestamp());
        _hud.Draw(_ui, frame.Width, frame.Height, new HudData(_stats, draws, renderer.Ui.LastQuads, renderer.AdapterName, _digest, _combat), _help);
        HudQuads = _ui.Count;
        renderer.Ui.Add(frame, _ui, "hud");
    }

    private void DrawOpaque(RenderPassContext ctx)
    {
        ctx.SetConstant(SlotTerrain, _heights!.Srv);
        ctx.SetConstant(SlotMesh, _capsule!.Srv);

        ctx.SetPipeline(Ground);
        ctx.Draw(6);
        // Рельеф тестовых участков — сетка без вершинного буфера поверх ровного пола.
        ctx.SetPipeline(Terrain);
        ctx.Draw((uint)(_field.Cells * _field.Cells * 6));
        ctx.SetPipeline(Pillar);
        ctx.DrawIndexed(_capsuleIndices!, ArenaLayout.PillarCount);

        _renderer!.Skinned.Record(ctx, Skin);
    }

    /// <summary>
    /// Константы кадра (<c>Program.Fill</c> SWORD): шаг и время тика, номер последнего тика, доля тика. Тень и кольцо
    /// игрока — таз тела, как в SWORD (5в), в показанной точке, как и Manny (<see cref="ArenaView.PlayerMarker"/>).
    /// Намеренное отличие: в SWORD — таз на последнем тике без интерполяции, и кольцо дрожит относительно плавно идущего
    /// бойца.
    /// </summary>
    private FrameConstants Constants()
    {
        var pl = ArenaLayout.Pillars;
        return new FrameConstants
        {
            Dt = _view.TickDt, Time = _view.Time, Tick = (uint)_view.LastTick, Alpha = _view.Alpha,
            ViewProj = _camera.ViewProj,
            InvViewProj = Matrix4x4.Invert(_camera.ViewProj, out var inv) ? inv : Matrix4x4.Identity,
            CameraPos = _camera.Eye, ArenaRadius = ArenaLayout.ArenaRadius,
            Pillar0 = pl[0], Pillar1 = pl[1], Pillar2 = pl[2], Pillar3 = pl[3],
            Pillar4 = pl[4], Pillar5 = pl[5], Pillar6 = pl[6], Pillar7 = pl[7],
            LightDir = Vector3.Normalize(new Vector3(-0.45f, -1f, -0.3f)),
            PlayerPos = _view.PlayerMarker ?? NoPlayer,
            PlayerHpFrac = PlayerHpFrac(_view.PlayerHp),
        };
    }

    /// <summary>Цвет кольца игрока (6е; <c>Program.Fill</c> SWORD): доля здоровья <c>clamp(Hp / 100, 0, 1)</c> — от красного к зелёному.</summary>
    public static float PlayerHpFrac(float hp) => Math.Clamp(hp / Health.Full, 0, 1);

    public void Dispose()
    {
        _heights?.Dispose();
        _capsule?.Dispose();
        _capsuleIndices?.Dispose();
    }
}
