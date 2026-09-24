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

    /// <summary>Цвет бойца кадра: игрок — свой, враг — по месту (мёртвых ещё нет — этап 6).</summary>
    public static Vector3 Colour(in FighterView f) => f.IsPlayer ? PlayerColour : EnemyColour(f.Slot);

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

    /// <summary>Оверлеи, которые клиент арены снимает слоем.</summary>
    public static readonly IReadOnlyList<string> KnownOverlays = [CapsulesOverlay, BladesOverlay];

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
    private readonly Hud _hud = new();
    private readonly bool _showHud;

    /// <summary>Сводка профайлера для F2 (точка Пв); null — профайлера нет (<see cref="CpuProfiler.Off"/>).</summary>
    private ProfileDigest? _digest;

    /// <summary>Профайлер включён F2 (выключает его F2 только тогда: включённый ключом или агентом остаётся).</summary>
    private bool _profilerByHud;
    private UiDrawList? _ui;
    private FrameStats _stats;
    private IReadOnlyList<string> _help = Hud.HelpLines(new Dictionary<string, string>());

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
    public ArenaClient(CameraPose? camera = null, PlayerCommand? scripted = null, bool hud = true, bool playerCommands = true,
        bool showBodies = false, bool showBlades = false)
    {
        if (!playerCommands && scripted is not null) throw new ArgumentException("заданная команда игрока без команд игрока", nameof(scripted));
        _showHud = hud;
        _playerCommands = playerCommands;
        ShowBodies = showBodies;
        ShowBlades = showBlades;
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

    /// <summary>Клинки поверх персонажей (<c>--overlay blades</c>; F7 — вместе с капсулами).</summary>
    public bool ShowBlades { get; set; }

    /// <summary>Мечей, отданных рендеру в последнем кадре (у каждого бойца с мечом — один), и клинков оверлея (0 — не рисовались).</summary>
    public int DrawnWeapons { get; private set; }
    public int DrawnBlades { get; private set; }

    /// <summary>Меч в руке игрока в последнем кадре (имя таблицы оружия); игрока или меча нет — null.</summary>
    public string? PlayerWeapon => _view.Player is { Weapon: var w } && (uint)w < (uint)_weaponNames.Length ? _weaponNames[w] : null;

    /// <summary>Строки справки HUD.</summary>
    public IReadOnlyList<string> HelpLines => _help;

    /// <summary>Камера показанного кадра — для пакета наблюдения агента.</summary>
    CameraView IClientObservable.Camera =>
        new(_camera.ViewProj, _camera.Eye, _camera.Target, _camera.Yaw, _camera.Pitch, _camera.Distance, _camera.Follow);

    /// <summary>Бойцы кадра в показанном (интерполированном) положении — рамкой вокруг тела.</summary>
    void IClientObservable.Visible(List<VisibleEntity> into)
    {
        foreach (var f in _view.Fighters)
        {
            var base_ = new Vector3(f.Root.X, f.Height, f.Root.Z);
            into.Add(new VisibleEntity(f.Entity, f.IsPlayer ? "player" : f.Slot > 0 ? "enemy" : "fighter",
                base_ - new Vector3(FighterHalfWidth, 0, FighterHalfWidth), base_ + new Vector3(FighterHalfWidth, FighterHeight, FighterHalfWidth)));
        }
    }

    IReadOnlyList<string> IClientObservable.Overlays => KnownOverlays;

    /// <summary>
    /// Код ID-буфера последнего кадра словами: владелец — боец кадра (<see cref="OwnerOf"/>), часть — сегмент тела; название
    /// — «левое предплечье врага 3», «голова игрока»; пиксели меча — сегмент 11: «клинок врага 3», «клинок игрока» (6б).
    /// </summary>
    IdMeaning? IClientObservable.DescribeId(uint id)
    {
        var (owner, part) = SkinnedMeshes.SplitId(id);
        if (owner == 0) return null;
        foreach (var f in _view.Fighters)
        {
            if (OwnerOf(f.Entity) != owner) continue;
            string who = f.IsPlayer ? "игрока" : $"врага {f.Slot}";
            string kind = f.IsPlayer ? "player" : "enemy";
            return part == SkinnedMeshes.NoPart
                ? new IdMeaning(f.Entity, kind, null, who)
                : new IdMeaning(f.Entity, kind, BodySegs.Name((int)part), $"{BodySegs.Title((int)part)} {who}");
        }
        return null;
    }

    /// <summary>Справка — по действующим привязкам (действие → клавиша): композиция отдаёт их после разрешения конфига.</summary>
    public void UseBindings(IReadOnlyDictionary<string, string> keys) => _help = Hud.HelpLines(keys);

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
        // Камера — до тиков, за тазом игрока, каким его показал прошлый кадр (Program.cs SWORD: focus — таз по renderAlpha
        // прошлого кадра).
        if (_fixedCamera) _camera.Rebuild(input.Width, input.Height);
        else _camera.Update(ArenaInput.Camera(input), input.Seconds, _view.PlayerFocus ?? _camera.Target, input.Width, input.Height);

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
    /// Команды тика: команда игрока кадра (удерживаемое — в каждом тике кадра) и команды арены из фронтов PageUp/PageDown/R
    /// (ровно одним тиком — <see cref="TickInput"/>). При воспроизведении реплея (<c>playerCommands: false</c>) — ни тех ни
    /// других: команды — реплея, своя команда того же тика увела бы его в сторону.
    /// </summary>
    public void BeforeTick(SimulationLink simulation, in TickInput input)
    {
        if (!_playerCommands) return;
        simulation.Enqueue(_frameCommand);
        Span<ArenaCommand> arena = stackalloc ArenaCommand[ArenaIntent.Max];
        int n = ArenaIntent.Commands(input, arena);
        for (int i = 0; i < n; i++) simulation.Enqueue(arena[i]);
    }

    public void Extract(in FrameView view)
    {
        // Капсулы всех бойцов — только когда их рисуют (F7, --overlay capsules) или их слой заказан снимком инструмента (заказ
        // известен до Extract — FrameView.Capture); иначе — только таз игрока: камера, тень, кольцо (исправления после Пд). Клинки —
        // так же (F7, --overlay blades, слой blades).
        _view.Extract(_simulation!, view.Alpha, allBodies: ShowBodies || view.Capture?.WantsLayer(CapsulesOverlay) == true,
            blades: ShowBodies || ShowBlades || view.Capture?.WantsLayer(BladesOverlay) == true);
        _stats = view.Stats;
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
            else if (_renderer!.Skinned.AddPosed(_manny, pose, fighter.World, colour, owner)) skinned++;
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
    /// Фаза Debug (после сцены и интерфейса, раздел 6 плана): капсулы тел всех бойцов, как показаны бойцы, — поверх кадра
    /// (F7) и (или) слоем снимка <see cref="CapsulesOverlay"/> с подписями бойцов («игрок», «враг 3»); клинки (6б) — поверх кадра
    /// (F7, <c>--overlay blades</c>) и (или) слоем снимка <see cref="BladesOverlay"/> с именами мечей. У каждого слоя — только своё:
    /// слой капсул снимается до клинков, слой клинков — с отметки (<see cref="DebugDraw.Mark"/>).
    /// </summary>
    private void DrawDebug(RenderFrame frame)
    {
        bool bodiesLayer = frame.WantsLayer(CapsulesOverlay), bladesLayer = frame.WantsLayer(BladesOverlay);
        bool bodies = ShowBodies || bodiesLayer, blades = ShowBodies || ShowBlades || bladesLayer;
        DrawnCapsules = DrawnBlades = 0;
        if (!bodies && !blades) return;
        var debug = _renderer!.Debug;
        var fighters = _view.Fighters;
        if (bodies)
        {
            for (int i = 0; i < fighters.Count; i++)
            {
                var body = _view.Body(fighters[i]);
                for (int k = 0; k < body.Length; k++)
                {
                    ref readonly var c = ref body[k];
                    debug.Capsule(c.Center, c.Rotation, c.Radius, c.HalfLength, CapsuleColour(c.Segment));
                }
                DrawnCapsules += body.Length;
            }
        }
        if (bodiesLayer)
        {
            for (int i = 0; i < fighters.Count; i++)
            {
                var f = fighters[i];
                if (_view.Body(f) is { Length: > 0 } body && BodySegs.Body(BodySegs.Head) is var head && head < body.Length)
                    debug.Text(body[head].EndB + new Vector3(0, 0.3f, 0), Labels[Math.Clamp(f.IsPlayer ? 0 : f.Slot, 0, Labels.Length - 1)],
                        f.IsPlayer ? 0xFF80FFFFu : 0xFFFFFFFFu);
            }
            var target = frame.Graph.CreateTexture("overlay.capsules", new TextureDesc(TextureFormat.Rgba8Unorm, TextureUsage.ColorTarget));
            debug.AddPass(frame, target, _camera.ViewProj, _camera.Eye, "overlay.capsules", clear: Vector4.Zero);
            frame.CaptureLayer(CapsulesOverlay, target);
        }
        var bladesFrom = debug.Mark();
        if (blades)
        {
            for (int i = 0; i < fighters.Count; i++)
            {
                if (fighters[i].Blade is not { } b) continue;
                debug.Line(b.PrevBase, b.PrevTip, PrevBladeColour);
                debug.Line(b.PrevBase, b.Base, SweepColour);
                debug.Line(b.PrevTip, b.Tip, SweepColour);
                debug.Line(b.Base, b.Tip, fighters[i].IsPlayer ? PlayerBladeColour : EnemyBladeColour);
                DrawnBlades++;
            }
        }
        // Поверх кадра — клинки и (F7) капсулы; капсулы, накопленные только для слоя снимка, и подписи слоя — не на экран.
        var screenFrom = new DebugDraw.DebugMark(bladesFrom.LineVertices, ShowBodies ? 0 : debug.CapsuleCount, debug.TextCount);
        if (ShowBodies || ShowBlades) debug.AddPass(frame, frame.Graph.BackBuffer, _camera.ViewProj, _camera.Eye, "debug.bodies", since: screenFrom);
        if (!bladesLayer) return;
        for (int i = 0; i < fighters.Count; i++)
            if (fighters[i].Blade is { } b && (uint)fighters[i].Weapon < (uint)_weaponNames.Length)
                debug.Text(b.Tip + new Vector3(0, 0.1f, 0), _weaponNames[fighters[i].Weapon], fighters[i].IsPlayer ? 0xFF80FFFFu : 0xFFFFFFFFu, 12);
        var layer = frame.Graph.CreateTexture("overlay.blades", new TextureDesc(TextureFormat.Rgba8Unorm, TextureUsage.ColorTarget));
        debug.AddPass(frame, layer, _camera.ViewProj, _camera.Eye, "overlay.blades", clear: Vector4.Zero, since: bladesFrom);
        frame.CaptureLayer(BladesOverlay, layer);
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
        _hud.Draw(_ui, frame.Width, frame.Height, new HudData(_stats, draws, renderer.Ui.LastQuads, renderer.AdapterName, _digest), _help);
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
            PlayerHpFrac = 1f,
        };
    }

    public void Dispose()
    {
        _heights?.Dispose();
        _capsule?.Dispose();
        _capsuleIndices?.Dispose();
    }
}
