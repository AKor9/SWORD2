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
/// (аниматор) в корне бойца с его курсом на высоте рельефа (<c>DuelArena.KinWorld</c> SWORD). Кадр — в HDR: один непрозрачный
/// проход «scene.opaque» пишет линейный цвет в <see cref="RenderFrame.SceneColor"/>, нормали и глубину (порядок отрисовки —
/// <c>SceneRenderer.Record</c> SWORD 2eab13b: пол, рельеф, колонны, скин); на экран его выводит тонмап рендера (фаза Post).
///
/// Кадр — порядок <c>Program.cs</c> SWORD: <see cref="Input"/> — камера (<see cref="RtsCamera"/>, следует за игроком,
/// каким его показал прошлый кадр) и команда игрока из WASD по этой камере и курсора на земле; <see cref="BeforeTick"/> —
/// та же команда в каждый тик кадра (удерживаемое, раздел 7); <see cref="Extract"/> — мир представления
/// (<see cref="ArenaView"/>, интерполяция по доле тика); <see cref="BuildFrame"/> рисует только его, поверх — HUD
/// (<see cref="Hud"/>: FPS и числа кадра, справка) UI-проходом рендера.
///
/// Камера — за тазом игрока (<see cref="ArenaView.PlayerFocus"/>), тень и кольцо — таз (<see cref="ArenaView.PlayerMarker"/>);
/// капсулы тел поверх персонажей — F7 (<see cref="ShowBodies"/>, отладочное рисование рендера в фазе Debug).
///
/// Меч в руке каждого бойца — меш оружия таблицы (<see cref="ICombatView"/>) жёстким вложением в кисть позы, как показан
/// боец (<see cref="FighterView.WeaponWorld"/>; <c>SkinnedCharacters.Submit</c> SWORD); клинки (отрезок тика и прошлого тика) — F7
/// вместе с капсулами и <c>--overlay blades</c> (<see cref="BladesOverlay"/>).
///
/// HUD боя (<see cref="CombatHud"/> — снимок мира последнего тика: здоровье и смерть игрока, живые враги, статистика арены
/// <see cref="ArenaStats"/>, меч; кольцо игрока — цветом по здоровью), инвентарь (<see cref="Inventory"/> — панель клиента: I
/// открывает, 1–9 и щелчок — команда арены «взять меч» после команд PageUp, PageDown, R, T тика), ручки «Бой» (<see cref="TuningGroups"/>
/// над привязкой <see cref="Tuning"/> — панель F3 подключает exe; и «Рэгдолл», <see cref="RagdollTuning"/>; «Свет» — ручки рендера,
/// <see cref="LightTuning"/>: ночь — окружение сцены <see cref="SceneEnvironment"/> в константах кадра и фон проходом). Симуляцию
/// клиент не пишет: только читает мир и ставит команды.
///
/// Труп — по телам (<see cref="ArenaView"/>: скин = привязка × тело, меч — на теле клинка, рамка — по телам), темнее живого
/// (<see cref="DeadShade"/>); оверлеи <see cref="JointsOverlay"/> (J) и <see cref="ContactsOverlay"/>
/// (K; трасса физики — диагностический режим <c>physics.trace</c>, клиент включает его между тиками, пока контакты показаны).
/// </summary>
public sealed class ArenaClient : IClientGame, IClientObservable, ISceneFog, ISceneSky
{
    /// <summary>Меш персонажа в контенте (<c>CharacterRig.MeshFile</c> SWORD).</summary>
    public const string MannyMesh = Sword.Game.Animation.CharacterAssets.MeshFile;

    /// <summary>Цвет игрока (<c>SkinnedCharacters.Colour</c> SWORD).</summary>
    public static readonly Vector3 PlayerColour = new(0.3f, 0.4f, 0.65f);

    /// <summary>Цвет меча (<c>SkinnedCharacters.Submit</c> SWORD) — и части оружия, материала которой нет в <see cref="WeaponMaterialColour"/>.</summary>
    public static readonly Vector3 WeaponColour = new(0.8f, 0.82f, 0.86f);

    /// <summary>
    /// Цвет части оружия по имени материала секции меша (конвертер weapon пекарни с <c>sections</c>): факел — палка «wood» (дерево),
    /// шишка «charred» (обугленная); другое имя и оружие без секций — <see cref="WeaponColour"/>.
    /// </summary>
    public static Vector3 WeaponMaterialColour(string material) => material.ToLowerInvariant() switch
    {
        "wood" => new(0.36f, 0.22f, 0.11f),
        "charred" => new(0.04f, 0.035f, 0.03f),
        _ => WeaponColour,
    };

    /// <summary>Цвет врага на месте <paramref name="slot"/> (<c>SkinnedCharacters.EnemyColour</c> SWORD): оттенок по золотому сечению.</summary>
    public static Vector3 EnemyColour(int slot)
    {
        float h = slot * 0.618f % 1;
        return new Vector3(0.45f + 0.25f * MathF.Cos(MathF.Tau * h), 0.3f + 0.1f * MathF.Cos(MathF.Tau * (h + 0.33f)), 0.25f);
    }

    /// <summary>Мёртвый темнее живого (<c>SkinnedCharacters.Colour</c> SWORD: × 0,75).</summary>
    public const float DeadShade = 0.75f;

    /// <summary>Цвет бойца кадра: игрок — свой, враг — по месту; мёртвый — темнее (<see cref="DeadShade"/>).</summary>
    public static Vector3 Colour(in FighterView f) => (f.IsPlayer ? PlayerColour : EnemyColour(f.Slot)) * (f.Dead ? DeadShade : 1);

    /// <summary>Бойцов, отданных скиннингу в последнем кадре (итог Sword.exe: рендер рисует всех).</summary>
    public int SkinnedFighters { get; private set; }

    /// <summary>Цвет фона на экране (sRGB-кодированный, как <c>SceneRenderer.Record</c> SWORD).</summary>
    public static readonly Vector3 BackgroundDisplay = new(0.33f, 0.29f, 0.27f);

    /// <summary>
    /// Цвет очистки HDR-сцены — линейный: тот, что тонмап с экспозицией по умолчанию выводит цветом <see cref="BackgroundDisplay"/>
    /// (фон на экране тот же, что без HDR).
    /// </summary>
    public static readonly Vector4 ClearColour = new(FilmicTonemap.SceneFor(BackgroundDisplay, Renderer.DefaultExposure), 1f);

    public const int SlotTerrain = 8, SlotMesh = 9;

    private const string SceneShader = "scene.hlsl";
    private static readonly GraphicsPipelineDesc Ground = new(SceneShader, "VsGround", "PsGround", Depth: DepthMode.TestWrite);
    private static readonly GraphicsPipelineDesc Terrain = new(SceneShader, "VsTerrain", "PsGround", Depth: DepthMode.TestWrite);
    private static readonly GraphicsPipelineDesc Pillar = new(SceneShader, "VsPillar", "PsPillar", Depth: DepthMode.TestWrite);
    private static readonly GraphicsPipelineDesc Skin = new(SceneShader, "VsSkin", "PsSkin", Depth: DepthMode.TestWrite);
    // Заслоняющие в карту теней солнца (scene.shadow): колонны, рельеф, бойцы и мечи; ровный пол тени не отбрасывает.
    private static readonly GraphicsPipelineDesc PillarShadow = new(SceneShader, "VsPillarShadow", "PsShadow", Depth: DepthMode.TestWrite);
    private static readonly GraphicsPipelineDesc TerrainShadow = new(SceneShader, "VsTerrainShadow", "PsShadow", Depth: DepthMode.TestWrite);
    private static readonly GraphicsPipelineDesc SkinShadow = new(SceneShader, "VsSkinShadow", "PsShadow", Depth: DepthMode.TestWrite);
    // Заслоняющие в грани кубических карт точечных источников (scene.pointShadow.*): рельеф, колонны, бойцы и мечи.
    private static readonly GraphicsPipelineDesc PillarPointShadow = new(SceneShader, "VsPillarPointShadow", "PsShadow", Depth: DepthMode.TestWrite);
    private static readonly GraphicsPipelineDesc TerrainPointShadow = new(SceneShader, "VsTerrainPointShadow", "PsShadow", Depth: DepthMode.TestWrite);
    private static readonly GraphicsPipelineDesc SkinPointShadow = new(SceneShader, "VsSkinPointShadow", "PsShadow", Depth: DepthMode.TestWrite);
    // Предпроход глубины и нормалей для GTAO (scene.prepass): те же вершинные шейдеры, что у непрозрачного, — та же глубина.
    private static readonly GraphicsPipelineDesc GroundPre = new(SceneShader, "VsGround", "PsGroundPre", Depth: DepthMode.TestWrite);
    private static readonly GraphicsPipelineDesc TerrainPre = new(SceneShader, "VsTerrain", "PsGroundPre", Depth: DepthMode.TestWrite);
    private static readonly GraphicsPipelineDesc PillarPre = new(SceneShader, "VsPillar", "PsPillarPre", Depth: DepthMode.TestWrite);
    private static readonly GraphicsPipelineDesc SkinPre = new(SceneShader, "VsSkin", "PsSkinPre", Depth: DepthMode.TestWrite);

    /// <summary>Фон окружения, когда он не цвет очистки (ночь): экранный треугольник на дальней глубине — только пустые пиксели.</summary>
    private static readonly GraphicsPipelineDesc Background = new(SceneShader, "VsBackground", "PsBackground", Depth: DepthMode.Test);

    /// <summary>Оверлей капсул тел (ключ <c>--overlay</c>).</summary>
    public const string CapsulesOverlay = "capsules";

    /// <summary>
    /// Оверлей клинков: отрезок клинка тика (игрок — жёлтый, враги — белый), прошлого тика (серый) и стороны пройденного за
    /// тик (оранжевые).
    /// </summary>
    public const string BladesOverlay = "blades";

    /// <summary>
    /// Оверлей контактов: контакты последнего подшага последнего шага из трассы физики — точка (шарик), нормаль к телу длиной
    /// <see cref="ContactNormal"/> + <see cref="ContactDepthScale"/> × глубина, цвет — источник (<see cref="ContactColour"/>). Без трассы
    /// (диагностический режим <c>physics.trace</c>) пуст.
    /// </summary>
    public const string ContactsOverlay = "contacts";

    /// <summary>
    /// Оверлей суставов: точка сустава на ребёнке (шарик), отрезок до точки на родителе (ошибка точки), цвет — в пределах (зелёный),
    /// выход за предел (от оранжевого к красному к <see cref="JointExcessRed"/>), выключен (серый — запястье трупа: меч выпал).
    /// </summary>
    public const string JointsOverlay = "joints";

    /// <summary>
    /// Оверлей Motion Matching игрока (M, <see cref="MotionMatchOverlay"/>): желаемая траектория (запрос последнего поиска), траектория
    /// найденного кадра, история корня, суставы контакта стоп на земле. Игрок не на MM — пуст.
    /// </summary>
    public const string MotionOverlay = "mm";

    /// <summary>
    /// Оверлей точечных источников кадра (<see cref="PointLights.DrawOverlay"/>): крест в позиции, круги радиуса R, горизонтальный круг
    /// половины затухания (видимое пятно).
    /// </summary>
    public const string LightsOverlay = "lights";

    /// <summary>Оверлеи клиента арены (ключи <c>Sword.exe --overlay</c>).</summary>
    public static readonly IReadOnlyList<string> KnownOverlays =
        [CapsulesOverlay, BladesOverlay, ContactsOverlay, JointsOverlay, MotionOverlay, LightsOverlay];

    /// <summary>Тестовый источник от корня игрока на земле: на высоте 1.4 м и чуть в сторону — примерно там, где факел в руке.</summary>
    public static readonly Vector3 TestLightOffset = new(0.45f, 1.4f, 0.3f);

    /// <summary>Нормаль контакта на оверлее: длина, м, и прибавка на метр глубины (1 см глубины — +10 см).</summary>
    public const float ContactNormal = 0.1f, ContactDepthScale = 10f;

    /// <summary>Выход за предел, рад, при котором сустав на оверлее — красный (меньше — от оранжевого); до 0,01 рад — в пределах.</summary>
    public const float JointExcessRed = 0.2f, JointExcessShown = 0.01f;

    /// <summary>Цвет контакта по источнику (<c>0xAABBGGRR</c>): земля — зелёный, стена и колонны — синий, пара трупов — пурпурный, части одного трупа (самоколлизия) — жёлтый, толчок живого телом — оранжевый, клинком — красный, без отдачи — серый.</summary>
    public static uint ContactColour(PhysicsContactSource source) => source switch
    {
        PhysicsContactSource.Ground => 0xFF40FF40,
        PhysicsContactSource.Wall => 0xFFFF8040,
        PhysicsContactSource.Pair => 0xFFFF40FF,
        PhysicsContactSource.Self => 0xFF40FFFF,
        PhysicsContactSource.PushBody => 0xFF0090FF,
        PhysicsContactSource.PushPass => 0xFF4040FF,
        PhysicsContactSource.PushNoReaction => 0xFFA0A0A0,
        _ => 0xFFFFFFFF,
    };

    /// <summary>
    /// Отметка тела на физике на оверлее капсул: крест в центре — белый, касается земли — зелёный; труп спит (физика его не
    /// считает) — синий.
    /// </summary>
    public const uint SimulatedMark = 0xFFFFFFFF, GroundedMark = 0xFF40FF40, AsleepMark = 0xFFFF7030;

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

    private readonly RtsCamera _camera = new();
    private readonly HeightField _field = ArenaTerrain.BuildField();
    private readonly ArenaView _view;
    private readonly Action<RenderPassContext> _drawOpaque, _drawShadow, _drawPrepass, _drawPointShadow;
    private Renderer? _renderer;
    private GpuData? _heights, _capsule;
    private GpuIndices? _capsuleIndices;
    private int _manny = -1;
    /// <summary>Части мешей оружия (меш и цвет) по номеру таблицы оружия и имена оружия (подписи слоя клинков).</summary>
    private (int Mesh, Vector3 Colour, uint Material)[][] _weapons = [];
    private string[] _weaponNames = [];
    private SimulationLink? _simulation;
    private readonly PlayerCommand? _scripted;
    private readonly bool _playerCommands;
    private readonly bool _fixedCamera;
    private PlayerCommand _frameCommand;
    /// <summary>Жест удара: сдвиг мыши с затуханием, жест последнего клика (система бойца) и замахи рубящих ударов.</summary>
    private Vector2 _gesture, _pressGesture;
    private Vector2[] _swings = [];
    private readonly Hud _hud = new();
    private readonly bool _showHud;

    /// <summary>Сводка профайлера для F2; null — профайлера нет (<see cref="CpuProfiler.Off"/>).</summary>
    private ProfileDigest? _digest;

    /// <summary>Профайлер включён F2 (выключает его F2 только тогда: включённый ключом остаётся).</summary>
    private bool _profilerByHud;
    private UiDrawList? _ui;
    private FrameStats _stats;
    private IReadOnlyList<string> _help = Hud.HelpLines(new Dictionary<string, string>());

    /// <summary>Бой для HUD последнего кадра; null — до первого Extract.</summary>
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
    /// <param name="showJoints">Суставы тел с первого кадра (<c>--overlay joints</c>; J — переключить).</param>
    /// <param name="showContacts">Контакты физики с первого кадра и трасса физики (<c>--overlay contacts</c>; K — переключить).</param>
    /// <param name="showMotion">Траектории Motion Matching игрока с первого кадра (<c>--overlay mm</c>; M — переключить).</param>
    /// <param name="showLights">Оверлей точечных источников (<c>--overlay lights</c>).</param>
    /// <param name="testLights">Тестовых источников «факел» у игрока (<c>--test-light [N]</c>, <see cref="TestLights"/>); 0 — нет.</param>
    public ArenaClient(CameraPose? camera = null, PlayerCommand? scripted = null, bool hud = true, bool playerCommands = true,
        bool showBodies = false, bool showBlades = false, bool showJoints = false, bool showContacts = false, bool showMotion = false,
        bool showLights = false, int testLights = 0)
    {
        if (!playerCommands && scripted is not null) throw new ArgumentException("заданная команда игрока без команд игрока", nameof(scripted));
        _showHud = hud;
        _playerCommands = playerCommands;
        ShowBodies = showBodies;
        ShowBlades = showBlades;
        ShowJoints = showJoints;
        ShowContacts = showContacts;
        ShowMotion = showMotion;
        ShowLights = showLights;
        TestLights = testLights;
        _drawOpaque = DrawOpaque;
        _drawShadow = DrawShadow;
        _drawPrepass = DrawPrepass;
        _drawPointShadow = DrawPointShadow;
        _view = new ArenaView(ArenaTerrain.Height);
        _motion = new MotionMatchOverlay(ArenaTerrain.Height);
        _scripted = scripted;
        if (camera is { } c)
        {
            // Пределы камеры: наклон 0,45…1,45, дистанция через SetDistance — 3…110 м (как у зума колесом).
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

    /// <summary>Камера (для итога).</summary>
    public RtsCamera Camera => _camera;

    /// <summary>HUD (для итога: показана ли справка).</summary>
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
    /// Из них с крестом тела на физике (тела трупа — <see cref="SimulatedMark"/>) и из них касающихся земли (<see cref="GroundedMark"/>)
    /// и спящих (<see cref="AsleepMark"/>) — в последнем кадре (0 — капсулы не рисовались).
    /// </summary>
    public int DrawnMarks { get; private set; }
    public int DrawnGroundedMarks { get; private set; }
    public int DrawnAsleepMarks { get; private set; }

    /// <summary>Клинки поверх персонажей (<c>--overlay blades</c>; F7 — вместе с капсулами).</summary>
    public bool ShowBlades { get; set; }

    /// <summary>Мечей, отданных рендеру в последнем кадре (у каждого бойца с мечом — один), и клинков оверлея (0 — не рисовались).</summary>
    public int DrawnWeapons { get; private set; }
    public int DrawnBlades { get; private set; }

    /// <summary>Суставы тел поверх кадра (J, <c>--overlay joints</c>).</summary>
    public bool ShowJoints { get; set; }

    /// <summary>
    /// Контакты физики поверх кадра (K, <c>--overlay contacts</c>): пока показаны, трасса физики включена (клиент включает её в шаге
    /// ввода кадра, между тиками; выключает — только если включил сам).
    /// </summary>
    public bool ShowContacts { get; set; }

    /// <summary>Суставов и контактов, отданных отладочному рисованию в последнем кадре (0 — не рисовались).</summary>
    public int DrawnJoints { get; private set; }
    public int DrawnContacts { get; private set; }

    /// <summary>Траектории Motion Matching игрока поверх кадра (M, <c>--overlay mm</c>).</summary>
    public bool ShowMotion { get; set; }

    /// <summary>Примитивов оверлея <c>mm</c>, отданных отладочному рисованию в последнем кадре (0 — не рисовался или игрок не на MM).</summary>
    public int DrawnMotion { get; private set; }

    /// <summary>Оверлей Motion Matching игрока, снятый последним кадром (итог Sword.exe, тесты).</summary>
    public MotionMatchOverlay Motion => _motion;

    /// <summary>Точечные источники поверх кадра (<c>--overlay lights</c>).</summary>
    public bool ShowLights { get; set; }

    /// <summary>Источников в оверлее последнего кадра (0 — не рисовался или источников нет).</summary>
    public int DrawnLights { get; private set; }

    /// <summary>
    /// Тестовые источники «факел LIVE-2» (<see cref="PointLight.Torch"/>) у игрока — свет без огня для проверки глазами, снимков и замера
    /// (<c>Sword.exe --test-light [N]</c>): первый — в <see cref="TestLightOffset"/> от корня, остальные — по кругу 3 м вокруг него на той
    /// же высоте; 0 — нет; нет игрока — нет и источников. Только представление: симуляция и реплей их не видят.
    /// </summary>
    public int TestLights { get; set; }

    /// <summary>Радиус круга тестовых источников после первого, м.</summary>
    public const float TestLightRing = 3f;

    /// <summary>
    /// Ночь (ручка группы «Свет», <c>--tune light.night=1</c>): окружение сцены <see cref="SceneEnvironment.Night"/> вместо
    /// <see cref="SceneEnvironment.Day"/> со следующего кадра — константы кадра и проход фона, ресурсы те же. Только представление.
    /// </summary>
    public bool Night { get; set; }

    /// <summary>Окружение сцены следующего кадра (<see cref="Night"/>).</summary>
    public SceneEnvironment Environment => Night ? SceneEnvironment.Night : SceneEnvironment.Day;

    /// <summary>Туман сцены следующего кадра — для прозрачного клиентских модулей (пламя): цвет окружения, начало и длина как у <c>Shade</c>.</summary>
    public SceneFog Fog => new(Environment.Fog, SceneEnvironment.FogStart, SceneEnvironment.FogRange);

    /// <summary>
    /// Солнце и небо следующего кадра — для объёмов и бликов клиентских модулей (дым, кровь): солнце окружения, небо вертикальной поверхности,
    /// направление на солнце (обратное <see cref="SunLight"/>).
    /// </summary>
    public SceneSky Sky => new(Environment.Sun, (Environment.SkyLow + Environment.SkyHigh) * 0.5f, -SunLight);

    /// <summary>Блики и металл материалов (<see cref="SceneMaterial"/>): выключены — только рассеянный свет; со следующего кадра.</summary>
    public bool Specular { get; set; } = true;

    /// <summary>Строки ручек показа арены во вкладку «Графика» панели F3 (её строит DevTools над клиентом движка).</summary>
    public void AddGraphicsTuning(TuningGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.Header("МАТЕРИАЛЫ");
        group.Bool("specular", "блики и металл", () => Specular, v => Specular = v,
            "блик солнца GGX и отражение неба: сталь клинков — металл, тела и камень — с шероховатостью; выключено — только рассеянный свет",
            defaultValue: true);
    }

    /// <summary>Направление света солнца (от солнца к сцене, единичное) — <c>lightDir</c> констант кадра.</summary>
    public static readonly Vector3 SunLight = Vector3.Normalize(new Vector3(-0.45f, -1f, -0.3f));

    /// <summary>Рельеф арены для клиентских модулей; буфер высот на GPU — после <see cref="Initialize"/>.</summary>
    public ArenaTerrainView TerrainView { get; } = new();

    /// <summary>Меш персонажа на CPU для клиентских модулей — источник после <see cref="Initialize"/>, загрузка — при первом обращении.</summary>
    public ArenaSkinMesh SkinMesh { get; } = new();

    /// <summary>Слой крови на коже для модуля крови: SRV вида по вершинам бойца — в <c>owner</c> экземпляра скина.</summary>
    public ArenaSkinBloodLayer SkinBlood { get; } = new();

    /// <summary>Группа ручек «Свет» (<see cref="LightTuning"/>); null — до <see cref="Initialize"/>.</summary>
    public TuningGroup? Light => _light;

    private TuningGroup? _light;

    /// <summary>Фон окружения этого кадра рисуется проходом (не равен цвету очистки).</summary>
    private bool _drawBackground;

    private readonly MotionMatchOverlay _motion;

    /// <summary>Трассу физики включил клиент (K, <c>--overlay contacts</c>) — выключает её тоже он; включённую не им не трогает.</summary>
    private bool _traceByClient;

    /// <summary>Меч в руке игрока в последнем кадре (имя таблицы оружия); игрока или меча нет — null.</summary>
    public string? PlayerWeapon => _view.Player is { Weapon: var w } && (uint)w < (uint)_weaponNames.Length ? _weaponNames[w] : null;

    /// <summary>Строки справки HUD.</summary>
    public IReadOnlyList<string> HelpLines => _help;

    /// <summary>Бой в HUD последнего кадра (итог Sword.exe, тесты); null — кадра ещё не было.</summary>
    public CombatHud? Combat => _combat;

    /// <summary>
    /// Панель инвентаря — панель клиента: exe подключает её к клиенту движка (<c>ClientHost.AddPanel</c>); открывает её I, выбор
    /// меча уходит командой арены из <see cref="BeforeTick"/>.
    /// </summary>
    public InventoryPanel Inventory => _inventory;

    /// <summary>Привязка ручек симуляции для панели F3; null — до <see cref="Initialize"/>.</summary>
    public TuningBinding? Tuning => _tuning;

    /// <summary>
    /// Группы ручек игры (вкладки панели F3: «Бой», «Рэгдолл», «Локомоция» — ручки симуляции; «Свет» — клиента, <see cref="Light"/>) —
    /// заводятся в <see cref="Initialize"/>.
    /// </summary>
    public IReadOnlyList<TuningGroup> TuningGroups => _groups;

    /// <summary>Нижний край панели производительности HUD в последнем кадре, пиксели (под ней — панель ручек, как в SWORD); без HUD — 0.</summary>
    public float HudBottom => _showHud ? _hud.PerfBottom : 0;

    /// <summary>Камера показанного кадра — для проходов клиентских модулей.</summary>
    CameraView IClientObservable.Camera =>
        new(_frameViewProj, _camera.Eye, _camera.Target, _camera.Yaw, _camera.Pitch, _camera.Distance, _camera.Follow) { Rotation = _camera.Rotation };

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
        c.Add("SCENE_MATERIAL_METAL", SceneMaterial.Metal, "бит металла материала (SceneMaterial)");
        c.Add("SCENE_MATERIAL_GROUND", SceneMaterial.Ground, "материал пола и рельефа");
        c.Add("SCENE_MATERIAL_PILLAR", SceneMaterial.Pillar, "материал колонн");
        c.AddSlot("R_TERRAIN", SlotTerrain, "высоты узлов рельефа (HeightField, по строкам z)");
        c.AddSlot("R_MESH", SlotMesh, "вершины единичной капсулы");
        c.AddSize<FrameConstants>("FRAME_CONSTANTS_SIZE");
        c.AddSize<CapsuleVertex>("CAPSULE_VERTEX_SIZE");
    }

    public void Initialize(Renderer renderer, SimulationLink? simulation)
    {
        _renderer = renderer;
        // Сцена — в HDR: линейный цвет в SceneColor, в задний буфер его переводит тонмап рендера (фаза Post).
        renderer.Hdr = true;
        // Сглаживание — TAA (с размытием движения камеры); SMAA при нём не нужен, но остаётся ручкой вкладки «Графика» панели F3.
        renderer.Taa.Enabled = true;
        renderer.Taa.MotionBlur = true;
        renderer.Smaa = false;
        renderer.Shadow.Enabled = true;   // тени солнца; выключатель — там же
        renderer.Ao.Enabled = true;   // затенение рассеянного света (GTAO); выключатель — там же
        renderer.AutoExposure.Enabled = true;   // автоэкспозиция; выключатель — там же
        renderer.Lights.Shadows = true;   // тени точечных источников с флагом (факел); выключатель — там же
        renderer.Post.Vignette = 0.3f;   // постобработка: углы чуть темнее, лёгкая резкость; ручки — там же
        renderer.Post.Sharpen = 0.3f;
        renderer.SceneColorClear = ClearColour;
        // Профайлер CPU кадра — тот же, что у рендера: зоны арены заводятся здесь, не в кадре.
        var profiler = renderer.Profiler;
        _view.Profiler = profiler;
        _zones = (profiler.Zone("arena.build.skin"), profiler.Zone("arena.build.hud"), profiler.Zone("arena.build.bodies"));
        _digest = profiler.IsOff ? null : new ProfileDigest(profiler);
        // Игрок и его команда — ядро игры: симуляция у арены есть всегда.
        _simulation = simulation ?? throw new ArgumentNullException(nameof(simulation), "клиенту арены нужна симуляция");
        _heights = renderer.CreateData<float>("Arena.Heights", _field.Heights);
        TerrainView.HeightsSrv = _heights.Srv;

        var verts = new List<CapsuleVertex>();
        var idx = new List<uint>();
        CapsuleMesh.Build(verts, idx);
        _capsule = renderer.CreateData<CapsuleVertex>("Mesh.CapsuleVertices", CollectionsMarshal.AsSpan(verts));
        _capsuleIndices = renderer.CreateIndices("Mesh.CapsuleIndices", CollectionsMarshal.AsSpan(idx));

        _manny = renderer.Skinned.LoadMesh("Manny", MannyMesh);
        // Палитра Manny — позы скелета бойца (вид тел симуляции): суставов столько же.
        int joints = renderer.Skinned.JointCount(_manny), skeleton = _simulation.World.View<IBodyView>().JointCount;
        if (joints != skeleton)
            throw new InvalidDataException($"{MannyMesh}: суставов меша {joints}, скелета бойца {skeleton}");
        SkinMesh.Attach(() => renderer.Skinned.ReadCpu(MannyMesh), _simulation.World.View<IBodyView>());
        // Мечи: меш оружия таблицы — статический, по части на секцию материала (цвет части — по имени материала).
        var weapons = _simulation.World.View<ICombatView>().Weapons;
        _weapons = new (int, Vector3, uint)[weapons.Length][];
        _weaponNames = new string[weapons.Length];
        for (int w = 0; w < weapons.Length; w++)
        {
            var sections = renderer.Skinned.LoadMeshSections("Weapon." + weapons[w].Name, weapons[w].Mesh);
            _weapons[w] = [.. sections.Select(p => (p.Mesh, WeaponMaterialColour(p.Material), SceneMaterial.OfWeaponPart(p.Material)))];
            _weaponNames[w] = weapons[w].Name;
        }
        // Жест удара: направления замаха рубящих ударов таблицы — в системе бойца.
        var swings = new List<Vector2>();
        foreach (var a in _simulation.World.View<ICombatView>().Attacks)
            if (!a.Thrust) swings.Add(a.SwingDir);
        _swings = [.. swings];
        // Инвентарь — имена мечей таблицы; ручки «Бой» — привязка к ручкам симуляции (панель F3 подключает exe). Ручки
        // «Рэгдолл» (секция ragdoll) и трупы кадра.
        _inventory.Attach(_simulation, _weaponNames);
        _tuning = new TuningBinding(_simulation);
        _groups.Clear();
        _groups.Add(CombatTuning.Build(_tuning, _simulation));
        _groups.Add(RagdollTuning.Build(_tuning, _view));
        if (LocomotionTuning.Build(_tuning, _simulation, _view) is { } locomotion) _groups.Add(locomotion);   // игрок на MM
        // «Свет» — ручки клиента (рендер, окружение): значения здесь и в рендере, команд симуляции нет.
        _light = LightTuning.Build(this, renderer);
        _groups.Add(_light);
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
        if (input.WasPressed(ArenaInput.DebugMotion)) ShowMotion = !ShowMotion;
        SyncTrace();
        // Инвентарь (SWORD: I, если не набирается текст) — панель видна со следующего кадра панелей.
        if (input.WasPressed(ArenaInput.Inventory)) _inventory.Open = !_inventory.Open;
        // Камера — до тиков, за тазом игрока, каким его показал прошлый кадр (Program.cs SWORD: focus — таз по renderAlpha
        // прошлого кадра).
        if (_fixedCamera) _camera.Rebuild(input.Width, input.Height);
        else _camera.Update(ArenaInput.Camera(input), input.Seconds, _view.PlayerFocus ?? _camera.Target, input.Width, input.Height);

        // Жест удара: сдвиг мыши за последние кадры; на клике — удар, чья траектория на экране ближе к жесту (камера кадра,
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
    /// Трасса физики под оверлей контактов: шаг ввода кадра — до тиков, между тиками — там диагностический режим и меняется. Контакты
    /// показаны, а трасса выключена — включить (и запомнить, что включил клиент); скрыты — выключить, только если включил клиент
    /// (включённую не им оставить; как F2 с профайлером). И при воспроизведении реплея: режим не меняет ни мир, ни хеш, ни реплей.
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
    /// F2: секция профайлера в HUD; профайлер выключен — F2 его включает (со следующего кадра), и выключает, только если
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
    /// Команды тика: команда игрока кадра (удерживаемое — в каждом тике кадра; удар, укол, рывок — фронты) и команды арены из
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
        // Удар, укол, рывок — фронты: ровно в одном тике, первом после кадра нажатия; жест — клика.
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
        // Инвентарь (Program.cs SWORD — после PageUp, PageDown, R, T): щелчок по строке и 1–9 при открытой панели — «взять меч»
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
        // Капсулы всех бойцов — только когда их рисуют (F7, --overlay capsules); иначе — только таз игрока: камера, тень, кольцо.
        // Клинки — так же (F7, --overlay blades). Суставы (J, --overlay joints) — с капсулами, на которых стоят; контакты (K,
        // --overlay contacts) — копия контактов трассы.
        _view.Extract(_simulation!, view.Alpha, allBodies: ShowBodies, blades: ShowBodies || ShowBlades, joints: ShowJoints,
            contacts: ShowContacts);
        // Траектории Motion Matching игрока (M, --overlay mm) — после позы игрока кадра (стопы — в показанной позе).
        if (ShowMotion) _motion.Extract(_simulation!.World, _view);
        else _motion.Clear();
        _stats = view.Stats;
        // Бой для HUD: мир последнего тика кадра — только чтение.
        var world = _simulation!.World;
        var stats = world.Resource<ArenaStats>();
        _combat = new CombatHud(_view.PlayerHp, _view.PlayerDead, _view.AliveEnemies, world.Resource<ArenaState>().Enemies, stats.Kills,
            stats.Hits, stats.LastPart, stats.LastDamage, PlayerWeapon, _inventory.Open);
    }

    /// <summary>Матрица, которой рисуется сцена кадра: камера со сдвигом TAA (без TAA — камера); оверлеи и выбор мышью — без сдвига.</summary>
    private Matrix4x4 _frameViewProj = Matrix4x4.Identity;

    public void BuildFrame(RenderFrame frame)
    {
        _camera.Rebuild(frame.Width, frame.Height);   // кадр из перетаскивания рамки — без Input, со своим размером
        var taa = _renderer!.Taa;
        _frameViewProj = taa.Jitter(_camera.ViewProj, frame.Width, frame.Height);
        taa.SetView(_camera.ViewProj, _frameViewProj);
        var environment = Environment;
        _drawBackground = environment.Background != new Vector3(ClearColour.X, ClearColour.Y, ClearColour.Z);
        // Карта теней — над видимой частью земли; матрица нужна константам кадра раньше проходов.
        var shadow = frame.Shadow;
        var focus = ShadowFocus.Of(_camera.ViewProj);
        shadow.Prepare(frame, SunLight, focus.NearCenter, focus.NearRadius, focus.FarCenter, focus.FarRadius);
        var ao = frame.Ao;
        ao.Prepare(frame);
        frame.SetFrameConstants(Constants(frame.Lights, environment, shadow, ao));
        frame.Lights.ViewOrigin = _camera.Eye;
        if (TestLights > 0 && _view.Player is { } player) AddTestLights(frame.Lights, new Vector3(player.Root.X, player.Height, player.Root.Z));

        // Manny в позе тика (аниматор); позы нет — поза привязки: палитра InverseBind·Component·World = World.
        var profiler = _renderer!.Profiler;
        profiler.Begin(_zones.Skin);
        int skinned = 0;
        var fighters = _view.Fighters;
        for (int i = 0; i < fighters.Count; i++)
        {
            var fighter = fighters[i];
            var pose = _view.Pose(fighter);
            var colour = Colour(fighter);
            uint owner = SkinBlood.OwnerOf(FighterKey.Of(fighter.Entity));
            if (pose.IsEmpty)
            {
                var palette = _renderer!.Skinned.Add(_manny, colour, owner, flags: SceneMaterial.Body);
                palette.Fill(GpuMat34.From(fighter.World));
                if (!palette.IsEmpty) skinned++;
            }
            // У трупа поза — уже мировые матрицы суставов (привязка × тело), мир экземпляра — единичный.
            else if (_renderer!.Skinned.AddPosed(_manny, pose, fighter.Corpse ? Matrix4x4.Identity : fighter.World, colour, owner,
                         flags: SceneMaterial.Body)) skinned++;
        }
        SkinnedFighters = skinned;
        // Мечи — после всех тел (SkinnedCharacters SWORD: меч — сразу за телом бойца; кончится место — пропадут мечи, а не бойцы).
        int armed = 0;
        for (int i = 0; i < fighters.Count; i++)
        {
            var fighter = fighters[i];
            if ((uint)fighter.Weapon < (uint)_weapons.Length && AddWeapon(_weapons[fighter.Weapon], fighter.WeaponWorld))
                armed++;
        }
        DrawnWeapons = armed;
        profiler.End(_zones.Skin);

        if (shadow.Active) shadow.AddPass(frame, _drawShadow);
        frame.Lights.AddShadowPasses(frame, _drawPointShadow);
        RenderGraph.RenderPassBuilder opaque;
        if (ao.Active)
        {
            // GTAO читает глубину и нормали до непрозрачного прохода: предпроход пишет их, непрозрачный — поверх тем же (без очистки).
            frame.Graph.AddPass("scene.prepass")
                .Color(frame.Scene.Normal, SceneTargets.NormalClear)
                .Depth(frame.Scene.Depth, DepthAccess.Write, clear: 0f)
                .Execute(_drawPrepass);
            var view = _camera.View;
            ao.AddPasses(frame, _frameViewProj, _camera.Eye, new Vector3(view.M11, view.M21, view.M31), new Vector3(view.M12, view.M22, view.M32));
            opaque = frame.Graph.AddPass("scene.opaque")
                .Color(frame.SceneColor, ClearColour)
                .Color(frame.Scene.Normal)
                .Depth(frame.Scene.Depth, DepthAccess.Write)
                .Read(ao.Map);
        }
        else
            opaque = frame.Graph.AddPass("scene.opaque")
                .Color(frame.SceneColor, ClearColour)
                .Color(frame.Scene.Normal, SceneTargets.NormalClear)
                .Depth(frame.Scene.Depth, DepthAccess.Write, clear: 0f);
        if (shadow.Active) opaque.Read(shadow.Map(0)).Read(shadow.Map(1));
        frame.Lights.ReadShadows(opaque);
        opaque.Execute(_drawOpaque);

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

    /// <summary>Оружие бойца — все части меша своими цветами в одном мире; false — места в кадре не хватило хоть одной части.</summary>
    private bool AddWeapon((int Mesh, Vector3 Colour, uint Material)[] parts, in Matrix4x4 world)
    {
        bool all = true;
        foreach (var (mesh, colour, material) in parts) all &= _renderer!.Skinned.AddRigid(mesh, world, colour, flags: material);
        return all;
    }

    /// <summary>Зоны профайлера CPU в построении кадра арены (<see cref="Initialize"/>).</summary>
    private (int Skin, int Hud, int Bodies) _zones;

    /// <summary>
    /// Фаза Debug (после сцены и интерфейса, раздел 6 плана): оверлеи — капсулы тел (F7; у трупа — его тела, на физике — с отметкой
    /// <see cref="SimulatedMark"/>), клинки (F7, <c>--overlay blades</c>), суставы (J, <c>--overlay joints</c>), контакты (K,
    /// <c>--overlay contacts</c>), Motion Matching (M) и точечные источники — поверх кадра одним проходом.
    /// </summary>
    private void DrawDebug(RenderFrame frame)
    {
        bool screenBlades = ShowBodies || ShowBlades, screen = screenBlades || ShowJoints || ShowContacts || ShowMotion || ShowLights;
        DrawnCapsules = DrawnBlades = DrawnJoints = DrawnContacts = DrawnMarks = DrawnGroundedMarks = DrawnMotion = DrawnLights = 0;
        if (!screen) return;
        var debug = _renderer!.Debug;
        var from = debug.Mark();
        if (ShowBodies) DrawnCapsules = DrawCapsules(debug);
        if (screenBlades) DrawnBlades = DrawBlades(debug);
        if (ShowJoints) DrawnJoints = DrawJoints(debug);
        if (ShowContacts) DrawnContacts = DrawContacts(debug);
        if (ShowMotion) DrawnMotion = _motion.Draw(debug);
        // Источники, добавленные в этот кадр до оверлея (тестовый — в начале кадра).
        if (ShowLights) DrawnLights = frame.Lights.DrawOverlay(debug);
        debug.AddPass(frame, frame.Graph.BackBuffer, _camera.ViewProj, _camera.Eye, "debug.bodies", since: from);
    }

    /// <summary>
    /// Капсулы тел всех бойцов кадра (цвет — сегмент); тела на физике (труп) — с крестом в центре (<see cref="DrawnMarks"/>). Вернёт,
    /// сколько капсул.
    /// </summary>
    private int DrawCapsules(DebugDraw debug)
    {
        int drawn = 0;
        DrawnMarks = DrawnGroundedMarks = DrawnAsleepMarks = 0;
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
            if (c.Asleep) DrawnAsleepMarks++;
            uint mark = c.Asleep ? AsleepMark : c.Grounded ? GroundedMark : SimulatedMark;
            float s = MathF.Max(c.Radius, 0.04f);
            debug.Line(c.Center - new Vector3(s, 0, 0), c.Center + new Vector3(s, 0, 0), mark);
            debug.Line(c.Center - new Vector3(0, s, 0), c.Center + new Vector3(0, s, 0), mark);
            debug.Line(c.Center - new Vector3(0, 0, s), c.Center + new Vector3(0, 0, s), mark);
        }
        return body.Length;
    }

    /// <summary>Клинки: отрезок тика, прошлого тика и стороны пройденного за тик. Вернёт, сколько клинков.</summary>
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

    /// <summary>
    /// Суставы тел: шарик в точке сустава на ребёнке (в пределах — зелёный, выход за предел — от оранжевого к красному и крупнее,
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
    /// Контакты последнего подшага: шарик в точке, нормаль к телу длиной <see cref="ContactNormal"/> + <see cref="ContactDepthScale"/>
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

    /// <summary>HUD поверх сцены: числа кадра — прошлого (этот ещё пишется), вызовы и квады — тоже.</summary>
    private void DrawHud(RenderFrame frame)
    {
        var renderer = _renderer!;
        int draws = renderer.Graph.LastFrameDraws;   // без отчёта прохода: он строится только по запросу (0 Б/кадр)
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
        if (_drawBackground)
        {
            ctx.SetPipeline(Background);
            ctx.Draw(3);
        }
    }

    /// <summary>Заслоняющие точечный источник в грань его кубической карты: рельеф, колонны, бойцы и мечи.</summary>
    private void DrawPointShadow(RenderPassContext ctx)
    {
        ctx.SetConstant(SlotTerrain, _heights!.Srv);
        ctx.SetConstant(SlotMesh, _capsule!.Srv);
        ctx.SetPipeline(TerrainPointShadow);
        ctx.Draw((uint)(_field.Cells * _field.Cells * 6));
        ctx.SetPipeline(PillarPointShadow);
        ctx.DrawIndexed(_capsuleIndices!, ArenaLayout.PillarCount);
        _renderer!.Skinned.Record(ctx, SkinPointShadow);
    }

    /// <summary>Предпроход глубины и нормалей под GTAO: всё непрозрачное, кроме фона.</summary>
    private void DrawPrepass(RenderPassContext ctx)
    {
        ctx.SetConstant(SlotTerrain, _heights!.Srv);
        ctx.SetConstant(SlotMesh, _capsule!.Srv);
        ctx.SetPipeline(GroundPre);
        ctx.Draw(6);
        ctx.SetPipeline(TerrainPre);
        ctx.Draw((uint)(_field.Cells * _field.Cells * 6));
        ctx.SetPipeline(PillarPre);
        ctx.DrawIndexed(_capsuleIndices!, ArenaLayout.PillarCount);
        _renderer!.Skinned.Record(ctx, SkinPre);
    }

    /// <summary>Заслоняющие солнце в карту теней: рельеф, колонны, бойцы и мечи (те же экземпляры скина, что у сцены).</summary>
    private void DrawShadow(RenderPassContext ctx)
    {
        ctx.SetConstant(SlotTerrain, _heights!.Srv);
        ctx.SetConstant(SlotMesh, _capsule!.Srv);
        ctx.SetPipeline(TerrainShadow);
        ctx.Draw((uint)(_field.Cells * _field.Cells * 6));
        ctx.SetPipeline(PillarShadow);
        ctx.DrawIndexed(_capsuleIndices!, ArenaLayout.PillarCount);
        _renderer!.Skinned.Record(ctx, SkinShadow);
    }

    /// <summary>Тестовые источники <see cref="TestLights"/>: первый — в <see cref="TestLightOffset"/> от корня, остальные — по кругу.</summary>
    private void AddTestLights(PointLights lights, Vector3 root)
    {
        var first = root + TestLightOffset;
        lights.Add(PointLight.Torch(first));
        for (int i = 1; i < TestLights; i++)
        {
            float a = i * (2 * MathF.PI / (TestLights - 1));
            lights.Add(PointLight.Torch(first + new Vector3(MathF.Cos(a), 0, MathF.Sin(a)) * TestLightRing));
        }
    }

    /// <summary>
    /// Константы кадра (<c>Program.Fill</c> SWORD): шаг и время тика, номер последнего тика, доля тика. Тень и кольцо
    /// игрока — таз тела, как в SWORD, в показанной точке, как и Manny (<see cref="ArenaView.PlayerMarker"/>).
    /// Намеренное отличие: в SWORD — таз на последнем тике без интерполяции, и кольцо дрожит относительно плавно идущего
    /// бойца. SRV точечных источников и окружение (день или ночь) — клиента, симуляция их не видит.
    /// </summary>
    private FrameConstants Constants(PointLights lights, in SceneEnvironment environment, SunShadow shadow, AmbientOcclusion ao)
    {
        var pl = ArenaLayout.Pillars;
        return new FrameConstants
        {
            Dt = _view.TickDt, Time = _view.Time, Tick = (uint)_view.LastTick, Alpha = _view.Alpha,
            ViewProj = _frameViewProj,
            InvViewProj = Matrix4x4.Invert(_frameViewProj, out var inv) ? inv : Matrix4x4.Identity,
            CameraPos = _camera.Eye, ArenaRadius = ArenaLayout.ArenaRadius,
            Pillar0 = pl[0], Pillar1 = pl[1], Pillar2 = pl[2], Pillar3 = pl[3],
            Pillar4 = pl[4], Pillar5 = pl[5], Pillar6 = pl[6], Pillar7 = pl[7],
            LightDir = SunLight,
            PlayerPos = _view.PlayerMarker ?? NoPlayer,
            PlayerHpFrac = PlayerHpFrac(_view.PlayerHp),
            PointLights = (uint)lights.Srv,
            Environment = environment,
            ShadowViewProj0 = shadow.ViewProj(0), ShadowViewProj1 = shadow.ViewProj(1),
            ShadowMap0 = shadow.Srv(0), ShadowMap1 = shadow.Srv(1), ShadowTexel0 = shadow.Texel(0), ShadowTexel1 = shadow.Texel(1),
            ShadowBias0 = shadow.Bias(0), ShadowBias1 = shadow.Bias(1), ShadowSoftness = shadow.Softness,
            AoMap = ao.Srv, Specular = Specular ? 1u : 0u,
        };
    }

    /// <summary>Цвет кольца игрока (<c>Program.Fill</c> SWORD): доля здоровья <c>clamp(Hp / 100, 0, 1)</c> — от красного к зелёному.</summary>
    public static float PlayerHpFrac(float hp) => Math.Clamp(hp / Health.Full, 0, 1);

    public void Dispose()
    {
        _heights?.Dispose();
        TerrainView.HeightsSrv = -1;
        _capsule?.Dispose();
        _capsuleIndices?.Dispose();
    }
}
