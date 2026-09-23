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
/// </summary>
public sealed class ArenaClient : IClientGame, IClientObservable
{
    /// <summary>Рамка бойца для наблюдения: полуширина и рост от корня (капсула Manny SWORD: радиус 0,35, рост ~1,8 м).</summary>
    public const float FighterHalfWidth = 0.45f, FighterHeight = 1.85f;

    /// <summary>Меш персонажа в контенте (<c>CharacterRig.MeshFile</c> SWORD).</summary>
    public const string MannyMesh = "characters/manny/mesh.swmesh";

    /// <summary>Цвет игрока (<c>SkinnedCharacters.Colour</c> SWORD).</summary>
    public static readonly Vector3 PlayerColour = new(0.3f, 0.4f, 0.65f);

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

    private readonly RtsCamera _camera = new();
    private readonly HeightField _field = ArenaTerrain.BuildField();
    private readonly ArenaView _view;
    private readonly Action<RenderPassContext> _drawOpaque;
    private Renderer? _renderer;
    private GpuData? _heights, _capsule;
    private GpuIndices? _capsuleIndices;
    private int _manny = -1;
    private SimulationLink? _simulation;
    private readonly PlayerCommand? _scripted;
    private readonly bool _playerCommands;
    private readonly bool _fixedCamera;
    private PlayerCommand _frameCommand;
    private readonly Hud _hud = new();
    private readonly bool _showHud;
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
    public ArenaClient(CameraPose? camera = null, PlayerCommand? scripted = null, bool hud = true, bool playerCommands = true)
    {
        if (!playerCommands && scripted is not null) throw new ArgumentException("заданная команда игрока без команд игрока", nameof(scripted));
        _showHud = hud;
        _playerCommands = playerCommands;
        _drawOpaque = DrawOpaque;
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

    /// <summary>Квадов HUD в последнем кадре (0 — HUD не рисуется).</summary>
    public int HudQuads { get; private set; }

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
        // Игрок и его команда — ядро игры: симуляция у арены есть всегда.
        _simulation = simulation ?? throw new ArgumentNullException(nameof(simulation), "клиенту арены нужна симуляция");
        _heights = renderer.CreateData<float>("Arena.Heights", _field.Heights);

        var verts = new List<CapsuleVertex>();
        var idx = new List<uint>();
        CapsuleMesh.Build(verts, idx);
        _capsule = renderer.CreateData<CapsuleVertex>("Mesh.CapsuleVertices", CollectionsMarshal.AsSpan(verts));
        _capsuleIndices = renderer.CreateIndices("Mesh.CapsuleIndices", CollectionsMarshal.AsSpan(idx));

        _manny = renderer.Skinned.LoadMesh("Manny", MannyMesh);
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
        // Камера — до тиков, за игроком, каким его показал прошлый кадр (Program.cs SWORD: focus по renderAlpha прошлого кадра).
        if (_fixedCamera) _camera.Rebuild(input.Width, input.Height);
        else _camera.Update(ArenaInput.Camera(input), input.Seconds, _view.Player is { } p ? p.Root with { Y = 0 } : _camera.Target,
            input.Width, input.Height);

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
        _view.Extract(_simulation!, view.Alpha);
        _stats = view.Stats;
    }

    public void BuildFrame(RenderFrame frame)
    {
        _camera.Rebuild(frame.Width, frame.Height);   // кадр из перетаскивания рамки — без Input, со своим размером
        frame.SetFrameConstants(Constants());

        // Manny в позе тика (аниматор); позы нет — поза привязки: палитра InverseBind·Component·World = World.
        int skinned = 0;
        foreach (var fighter in _view.Fighters)
        {
            var pose = _view.Pose(fighter);
            var colour = Colour(fighter);
            if (pose.IsEmpty)
            {
                var palette = _renderer!.Skinned.Add(_manny, colour);
                palette.Fill(GpuMat34.From(fighter.World));
                if (!palette.IsEmpty) skinned++;
            }
            else if (_renderer!.Skinned.AddPosed(_manny, pose, fighter.World, colour)) skinned++;
        }
        SkinnedFighters = skinned;

        frame.Graph.AddPass("scene.opaque")
            .Color(frame.Graph.BackBuffer, ClearColour)
            .Color(frame.Scene.Normal, SceneTargets.NormalClear)
            .Depth(frame.Scene.Depth, DepthAccess.Write, clear: 0f)
            .Execute(_drawOpaque);

        if (_ui is not null) DrawHud(frame);
    }

    /// <summary>HUD поверх сцены: числа кадра — прошлого (этот ещё пишется), вызовы и квады — тоже.</summary>
    private void DrawHud(RenderFrame frame)
    {
        var renderer = _renderer!;
        int draws = 0;
        var passes = renderer.Graph.LastFrame;
        for (int i = 0; i < passes.Count; i++) draws += passes[i].Draws;
        _ui!.Clear();
        _hud.Draw(_ui, frame.Width, frame.Height, new HudData(_stats, draws, renderer.Ui.LastQuads, renderer.AdapterName), _help);
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
    /// игрока — в показанной (интерполированной) точке, как и Manny. Намеренное отличие: в SWORD — таз тела на последнем
    /// тике, и кольцо дрожит относительно плавно идущего бойца.
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
