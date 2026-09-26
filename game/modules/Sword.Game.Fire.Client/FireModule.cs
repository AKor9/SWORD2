using System.Numerics;
using System.Runtime.InteropServices;
using Sword.Engine.Client;
using Sword.Engine.Diagnostics;
using Sword.Engine.Fluid2D;
using Sword.Engine.Render;
using Sword.Game.Contracts;

namespace Sword.Game.Fire;

/// <summary>
/// Модуль Fire (<c>fire</c>, решение 18): огонь на горящем оружии игрока — только представление, вне симуляции, хеша и реплея; своей
/// сборки симуляции нет (Id объявлен симуляции как клиентский). Секция <c>fire</c> (<see cref="FireSettings"/>); огни по тикам
/// (<see cref="FireTracker"/>) → экземпляры сима (<see cref="FireSlots"/>, 60 Гц по <c>TickId</c>) → GPU-симуляции <c>fire.N</c>
/// (<see cref="FireSim"/>: <see cref="Fluid2DSim"/> 368² факела и кисть в центре шишки). Отладка: оверлей <see cref="OverlayName"/> (головки по
/// тикам, рамка площадки, центр, точка кисти), вид поля <see cref="FieldOverlayName"/> (ρ тепловой шкалой), вкладка F3 «Огонь», раздел итога
/// <c>--json</c> — <c>fire</c>, поля — в снимок кадра (<c>--shot</c>, <c>capture.bundle</c>). Пламя (<see cref="FireFlame"/>, фаза Transparent) —
/// площадка к камере по формуле <c>MI_Torch4</c>, слой и оверлей <see cref="FlameOverlayName"/>. Свет (<see cref="FireLight"/>) — точечный
/// источник LIVE-2 с light function у каждого огня, в кадр до проходов игры (оверлей <c>lights</c> игры его видит). Дым (<see cref="FireSmoke"/>,
/// фаза Transparent до пламени) — рэймарч объёма вокруг площадки по формуле HV <c>MI_HVOL_SIM_TorchSmoke</c> в слой ½, слой и оверлей
/// <see cref="SmokeOverlayName"/>.
/// </summary>
public sealed class FireModule : IClientModule
{
    public const string ModuleId = "fire";

    /// <summary>Оверлей и слой снимка: головки огней по тикам последнего кадра с тиками, клинок, площадка, центр, точка кисти.</summary>
    public const string OverlayName = "fire";

    /// <summary>Вид поля: площадка с плотностью ρ тепловой шкалой — на экран и слоем снимка.</summary>
    public const string FieldOverlayName = "fire.field";

    /// <summary>Вклад пламени: слой снимка и оверлей (цвет x/(1 + x) поверх кадра).</summary>
    public const string FlameOverlayName = "fire.flame";

    /// <summary>Вклад дыма (слой ½): слой снимка и оверлей (цвет x/(1 + x) поверх кадра).</summary>
    public const string SmokeOverlayName = "fire.smoke";

    /// <summary>Шумы сима и кисти в паке (FSR §3.11, BR §4.1) и шум flow-map пламени (<c>T_Crumple_MultiLevel_Inv</c>, канал r, sRGB).</summary>
    public const string SimNoisePath = "effects/fire/sim_noise.swtex", BrushNoisePath = "effects/fire/noise_template2.swtex",
        CrumplePath = "effects/fire/crumple.swtex";

    /// <summary>Цвета оверлея (<c>0xAABBGGRR</c>): горит — оранжевый, гаснет — серо-голубой, клинок — жёлтый полупрозрачный, площадка — белая.</summary>
    public const uint BurningColour = 0xFF0090FF, DyingColour = 0xFFC0A080, BladeColour = 0x9040FFFF, AreaColour = 0xA0FFFFFF,
        BrushColour = 0xFF40FF40;

    /// <summary>Крест головки на оверлее, м (полуразмер).</summary>
    public const float Cross = 0.02f;

    private readonly List<FireSim> _sims = [];
    private Vector3[] _display = [];
    private Vector2[] _displayUv = [];
    private string? _snapshotPrefix;
    private readonly List<string> _snapshots = [];
    private string? _gpuMissing;
    private bool _closing;
    private TuningGroup? _group;
    private FireFlame? _flame;
    private string? _flameMissing;
    private FlameQuad[] _quads = [];
    private int _quadCount;
    private ISceneFog? _fog;
    private ulong _tick;
    private bool _ticked;
    private float _tickDt;
    private double _seconds;
    private FlameView _flameView;
    private int _width, _height;
    private FireLight? _light;
    private FireSmoke? _smoke;
    private ISceneSky? _sky;
    private SmokeVolume[] _volumes = [];
    private SmokeLight[] _smokeLights = [];
    private SmokeView _smokeView;

    public string Id => ModuleId;

    /// <summary>Огни модуля после настройки (тесты, итог); null — модуль не настроен (выключен или не собран).</summary>
    public FireTracker? Fires { get; private set; }

    /// <summary>Экземпляры сима огня (CPU: чей огонь, состояние, шаги).</summary>
    public FireSlots? Slots { get; private set; }

    /// <summary>Ручки огня (параметры сима и кисти, k).</summary>
    public FireTuning? Tuning { get; private set; }

    /// <summary>Оверлеи модуля после настройки.</summary>
    public ClientOverlay? Overlay { get; private set; }
    public ClientOverlay? FieldOverlay { get; private set; }
    public ClientOverlay? FlameOverlay { get; private set; }

    /// <summary>Пламя рисуется (ручка F3 «пламя»; по умолчанию — да). Отладочный вид поля — отдельно.</summary>
    public bool FlameShown { get; set; } = true;

    /// <summary>Дым рисуется (ручка F3 «дым»; по умолчанию — да).</summary>
    public bool SmokeShown { get; set; } = true;

    /// <summary>Числа дыма (ручка F3 «плотность дыма» меняет <see cref="SmokeParams.Density"/>).</summary>
    public SmokeParams SmokeParams { get; set; } = SmokeParams.Torch;

    public ClientOverlay? SmokeOverlay { get; private set; }

    /// <summary>Свет огней (null — горящего оружия нет в паке).</summary>
    public FireLight? Light => _light;

    /// <summary>Пламя кадра (null — сима или шума flow-map нет).</summary>
    internal FireFlame? Flame => _flame;

    /// <summary>Дым кадра (null — сима нет).</summary>
    internal FireSmoke? Smoke => _smoke;

    /// <summary>Время кадра для flow-map, с: (последний тик + доля тика)·шаг тика — тот же кадр реплея даёт то же время.</summary>
    public double FrameSeconds => _seconds;

    /// <summary>Головок, отданных отладочному рисованию на экран в последнем кадре (0 — оверлей не показан или огня нет).</summary>
    public int DrawnPoints { get; private set; }

    /// <summary>Площадок вида поля на экране в последнем кадре.</summary>
    public int DrawnFields { get; private set; }

    /// <summary>GPU-симуляции экземпляров (пусто — горящего оружия нет в паке).</summary>
    internal IReadOnlyList<FireSim> Sims => _sims;

    /// <summary>
    /// Показанный центр огня экземпляра <paramref name="slot"/> в кадре: центр шишки, интерполированный между тиками по доле тика
    /// (оружия нет — последний), и его смещение от центра площадки последнего шага в UV площадки (x — вправо, v — вниз). Шаг 60 Гц,
    /// кадр чаще: плоскость и выборка поля сдвигаются на это смещение, иначе пятно отстаёт от шишки до шага (16 см на замахе 10 м/с).
    /// </summary>
    public (Vector3 Center, Vector2 OffsetUv) Display(int slot) => (_display[slot], _displayUv[slot]);

    public void ConfigureClient(ClientBuilder b)
    {
        var settings = b.Config<FireSettings>();
        var fires = Fires = new FireTracker(settings, b.Service<IWeaponTips>());
        var slots = Slots = new FireSlots(settings, fires, b.TickDt);
        var tuning = Tuning = new FireTuning();
        var overlay = Overlay = b.Overlay(OverlayName);
        var field = FieldOverlay = b.Overlay(FieldOverlayName);
        var flameOverlay = FlameOverlay = b.Overlay(FlameOverlayName);
        var smokeOverlay = SmokeOverlay = b.Overlay(SmokeOverlayName);
        _fog = b.Service<ISceneFog>();
        _sky = b.Service<ISceneSky>();
        _tickDt = b.TickDt;
        _display = new Vector3[settings.MaxFires];
        _displayUv = new Vector2[settings.MaxFires];
        _quads = new FlameQuad[settings.MaxFires];
        _volumes = new SmokeVolume[settings.MaxFires];
        _smokeLights = new SmokeLight[settings.MaxFires];
        b.AfterTick(tick =>
        {
            _tick = tick;
            _ticked = true;
            fires.Observe(tick);
            slots.Observe(tick);
        });
        ClientModuleContext? context = null;
        b.Extract((in FrameView view) =>
        {
            fires.Frame(view.Ticks);
            // Время кадра — по тикам и доле тика, не по часам: реплей с тем же темпом кадров даёт ту же фазу flow-map.
            _seconds = _ticked ? (_tick + (double)view.Alpha) * _tickDt : 0;
            UpdateDisplay(view.Alpha, context);
            // Показ — между двумя последними тиками по доле тика: момент кадра для угасания света.
            _light?.Update(slots, _display, _seconds, _ticked ? _tick + (double)view.Alpha - 1 : 0);
            SaveSnapshots();
        });

        // Сим — только если что-то горит: иначе ни шейдеров сима, ни ресурсов.
        FireGpu? gpu = null;
        FireFieldView? view = null;
        if (fires.Burning.Count > 0)
        {
            var light = _light = new FireLight(settings.MaxFires);
            b.Lights(light.Add);
            gpu = new FireGpu
            {
                Tuning = tuning,
                Camera = () => context?.Camera ?? default,
                WantsField = frame => FlameWanted(frame) || SmokeWanted(frame) || field.Shown || frame.WantsLayer(FieldOverlayName),
                BrushScale = FireArea.BrushScale(settings),
                Closing = Closing,
            };
            view = new FireFieldView();
            b.Shaders(Fluid2DSim.Contribute);
            b.Shaders(FireFieldView.Contribute);
            b.Shaders(FireFlame.Contribute);
            b.Shaders(FireSmoke.Contribute);
            for (int i = 0; i < settings.MaxFires; i++)
            {
                var sim = new FireSim(slots, i, gpu);
                _sims.Add(sim);
                slots.Bind(i, b.GpuSimulation(sim));
            }
        }
        b.Initialize(c =>
        {
            context = c;
            var template2 = gpu is not null ? LoadNoises(c, gpu) : null;
            if (gpu is { Ready: true })
            {
                LoadFlame(c, settings);
                _smoke = c.Own(new FireSmoke(c.Renderer, settings.MaxFires, settings.MaxFires));
            }
            if (_light is not null) LoadLight(c, _light, template2);
        });
        // Прозрачное модуля: дым (слой ½) — раньше пламени, пламя поверх него.
        b.Pass(FramePhase.Transparent, frame =>
        {
            DrawSmoke(frame, context!);
            DrawTransparent(frame, context!);
        });
        b.Pass(FramePhase.Debug, frame => Draw(frame, context!, view));
        AddTuning(b, tuning, overlay, field, flameOverlay, smokeOverlay, settings);
        b.Report(Report);
    }

    /// <summary>Шумы пака; их нет (эталонный пак, пак без огня) — сим не создаётся, огонь только по тикам. Вернёт шум кисти (его берёт и LF).</summary>
    private GpuTexture? LoadNoises(ClientModuleContext c, FireGpu gpu)
    {
        try
        {
            var sim = c.Own(c.Renderer.LoadTexture(SimNoisePath));
            var brush = c.Own(c.Renderer.LoadTexture(BrushNoisePath));
            gpu.Density = Fluid2DNoise.FromLayer(sim, "T_Noise3");
            gpu.Amp = Fluid2DNoise.FromLayer(sim, "T_NoiseTemplate1");
            gpu.BrushNoise = Fluid2DNoise.FromLayer(brush, "T_NoiseTemplate2");
            gpu.Ready = true;
            return brush;
        }
        catch (Exception e) when (e is FileNotFoundException or InvalidDataException or ArgumentException)
        {
            _gpuMissing = e.Message;
            Log.Warning(ModuleId, $"шумов огня нет в паке — сима огня не будет: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Шумы light function (T_Noise3 и T_NoiseTemplate2 — второй уже загружен кистью, если сим есть) и их тексели на CPU для отчёта; их нет —
    /// света нет (пламя и сим работают).
    /// </summary>
    private static void LoadLight(ClientModuleContext c, FireLight light, GpuTexture? template2)
    {
        try
        {
            var a = c.Own(c.Renderer.LoadTexture(FireLight.NoisePathA));
            var b = template2 ?? c.Own(c.Renderer.LoadTexture(FireLight.NoisePathB));
            light.SetTextures(a.Srv, b.Srv, c.Renderer.ReadTexels(FireLight.NoisePathA), c.Renderer.ReadTexels(FireLight.NoisePathB));
        }
        catch (Exception e) when (e is FileNotFoundException or InvalidDataException or ArgumentException)
        {
            light.Missing = e.Message;
            Log.Warning(ModuleId, $"шумов света огня нет в паке — света не будет: {e.Message}");
        }
    }

    /// <summary>Шум flow-map пламени; его нет — пламени нет (сим и вид поля работают).</summary>
    private void LoadFlame(ClientModuleContext c, FireSettings settings)
    {
        try
        {
            var crumple = c.Own(c.Renderer.LoadTexture(CrumplePath));
            var flame = new FireFlame(c.Renderer, crumple.Srv, 0, settings.MaxFires);
            c.Own(flame.Resources);
            _flame = flame;
        }
        catch (Exception e) when (e is FileNotFoundException or InvalidDataException or ArgumentException)
        {
            _flameMissing = e.Message;
            Log.Warning(ModuleId, $"шума пламени нет в паке — пламени не будет: {e.Message}");
        }
    }

    /// <summary>Пламя в этом кадре: есть, включено и кадр в HDR (фаза Transparent есть только с HDR).</summary>
    private bool FlameWanted(RenderFrame frame) => _flame is not null && FlameShown && frame.Hdr;

    /// <summary>Дым в этом кадре: есть, включён и кадр в HDR (слой ½ есть только с HDR).</summary>
    private bool SmokeWanted(RenderFrame frame) => _smoke is not null && SmokeShown && frame.Hdr;

    private void UpdateDisplay(float alpha, ClientModuleContext? context)
    {
        var slots = Slots!;
        var fires = Fires!.Fires;
        var extents = Tuning!.Params.Extents;
        var rotation = FireArea.Plane(context?.Camera.Rotation ?? default);
        for (int i = 0; i < _display.Length; i++)
        {
            var s = slots[i];
            var center = s.Center;
            if (s.HasWeapon)
                foreach (var f in fires)
                    if (f.Key == s.Key && f.Weapon == s.Weapon && f.FirstTick == s.FirstTick && f.PointCount >= 2)
                    {
                        center = Vector3.Lerp(f.CenterAt(1), f.CenterAt(0), alpha);
                        break;
                    }
            _display[i] = center;
            _displayUv[i] = s.Steps > 0 ? FireArea.ToUv(rotation, center - s.StepCenter, new Vector3(extents.X, extents.Y, extents.Z)) : Vector2.Zero;
        }
    }

    // ── рисование ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Дым (фаза Transparent, до пламени): объём на экземпляр с выходом поля этого кадра в показанном центре огня, оси — камеры кадра;
    /// рассеяние — свет огней кадра (если он есть и включён, без LF) и солнце сцены, ambient — небо сцены. Слой ½ начинается и смешивается
    /// здесь же — только если есть объём.
    /// </summary>
    private void DrawSmoke(RenderFrame frame, ClientModuleContext context)
    {
        bool layer = frame.WantsLayer(SmokeOverlayName);
        var smoke = _smoke;
        if (smoke is not null && SmokeWanted(frame))
        {
            smoke.Params = SmokeParams;
            var rotation = FireArea.Plane(context.Camera.Rotation);
            var axisU = Vector3.Transform(Vector3.UnitX, rotation);
            var axisV = Vector3.Transform(Vector3.UnitY, rotation);
            var axisW = Vector3.Transform(Vector3.UnitZ, rotation);
            int count = 0;
            foreach (var sim in _sims)
            {
                if (sim.Slot.State == FireState.Free || !sim.HasOutput(frame)) continue;
                _volumes[count++] = new SmokeVolume { Center = _display[sim.Slot.Index], AxisU = axisU, AxisV = axisV, AxisW = axisW, Field = sim.Output };
            }
            if (count > 0)
            {
                int lights = 0;
                if (_light is { Emits: true } light)
                    for (int i = 0; i < _smokeLights.Length; i++)
                        if (light.Light(i) is { } l) _smokeLights[lights++] = SmokeLight.From(l);
                var camera = context.Camera;
                var viewDepth = FlameMath.ViewDepthParams(camera.ViewProj, camera.Eye, -axisW);
                var half = frame.HalfRes.Begin(viewDepth);
                _smokeView = new SmokeView(camera.ViewProj, camera.Eye, -axisW, _fog?.Fog ?? SceneFog.None, _sky?.Sky ?? SceneSky.None);
                smoke.Add(frame, half, _smokeView, _volumes.AsSpan(0, count), _smokeLights.AsSpan(0, lights));
                if (layer)
                {
                    var target = frame.Graph.CreateTexture("overlay.fire.smoke", new TextureDesc(TextureFormat.Rgba8Unorm, TextureUsage.ColorTarget));
                    smoke.AddLayer(frame, half, target, FramePhase.Transparent, FireSmoke.LayerPassName, Vector4.Zero);
                    frame.CaptureLayer(SmokeOverlayName, target);
                }
                frame.HalfRes.Composite();
                return;
            }
        }
        smoke?.Skip(frame);
        if (!layer) return;
        frame.CaptureNote($"{SmokeOverlayName}: дыма нет — {SmokeAbsence(frame)}");
        var empty = frame.Graph.CreateTexture("overlay.fire.smoke", new TextureDesc(TextureFormat.Rgba8Unorm, TextureUsage.ColorTarget));
        frame.Graph.AddPass(FireSmoke.LayerPassName, FramePhase.Transparent).Color(empty, Vector4.Zero).Execute(static _ => { });
        frame.CaptureLayer(SmokeOverlayName, empty);
    }

    private string SmokeAbsence(RenderFrame frame) =>
        _sims.Count == 0 ? "сима огня нет (горящего оружия или шумов нет в паке)"
        : _smoke is null ? "дым не создан"
        : !SmokeShown ? "выключено ручкой fire.smoke"
        : !frame.Hdr ? "кадр без HDR"
        : "огонь не горит";

    /// <summary>
    /// Пламя (фаза Transparent, после дыма): площадки экземпляров с выходом поля этого кадра в показанном центре огня, оси — камеры кадра.
    /// </summary>
    private void DrawTransparent(RenderFrame frame, ClientModuleContext context)
    {
        _quadCount = 0;
        if (!FlameWanted(frame))
        {
            if (frame.WantsLayer(FlameOverlayName))
                frame.CaptureNote($"{FlameOverlayName}: пламени нет — {FlameAbsence(frame)}");
            return;
        }
        var camera = context.Camera;
        var extents = Tuning!.Params.Extents;
        var rotation = FireArea.Plane(camera.Rotation);
        var u = Vector3.Transform(Vector3.UnitX, rotation) * (0.5f * extents.X);
        var v = Vector3.Transform(Vector3.UnitY, rotation) * (0.5f * extents.Y);
        foreach (var sim in _sims)
        {
            if (sim.Slot.State == FireState.Free || !sim.HasOutput(frame)) continue;
            _quads[_quadCount++] = new FlameQuad
            {
                Center = _display[sim.Slot.Index], U = u, V = v, WorldOffset = sim.WorldOffset, Field = sim.Output,
            };
        }
        var flame = _flame!;
        var forward = -Vector3.Transform(Vector3.UnitZ, rotation);
        _flameView = new FlameView(camera.ViewProj, camera.Eye, FlameMath.ViewDepthParams(camera.ViewProj, camera.Eye, forward),
            _fog?.Fog ?? SceneFog.None, FlameMath.Phase(_seconds, flame.Params.FlowSpeed));
        _width = frame.Width;
        _height = frame.Height;
        flame.Add(frame, _flameView, _quads.AsSpan(0, _quadCount));
        if (frame.WantsLayer(FlameOverlayName))
        {
            var target = frame.Graph.CreateTexture("overlay.fire.flame", new TextureDesc(TextureFormat.Rgba8Unorm, TextureUsage.ColorTarget));
            if (flame.LastQuads == 0)
            {
                frame.CaptureNote($"{FlameOverlayName}: пламени нет — огонь не горит");
                frame.Graph.AddPass(FireFlame.LayerPassName, FramePhase.Transparent).Color(target, Vector4.Zero).Execute(static _ => { });
            }
            else flame.AddLayer(frame, target, FramePhase.Transparent, FireFlame.LayerPassName, Vector4.Zero);
            frame.CaptureLayer(FlameOverlayName, target);
        }
    }

    private string FlameAbsence(RenderFrame frame) =>
        _sims.Count == 0 ? "сима огня нет (горящего оружия или шумов нет в паке)"
        : _flame is null ? $"шума пламени нет ({_flameMissing})"
        : !FlameShown ? "выключено ручкой fire.flame"
        : !frame.Hdr ? "кадр без HDR"
        : "огонь не горит";

    private void Draw(RenderFrame frame, ClientModuleContext context, FireFieldView? view)
    {
        var overlay = Overlay!;
        var field = FieldOverlay!;
        var fires = Fires!;
        var camera = context.Camera;
        var debug = context.Renderer.Debug;
        var extents = Tuning!.Params.Extents;
        var size = new Vector2(extents.X, extents.Y);
        var rotation = FireArea.Plane(camera.Rotation);
        DrawnPoints = DrawnFields = 0;

        if (frame.Capture?.ColorPath is { } shot && _sims.Count > 0)
        {
            // Поля — между кадрами (чтение с ожиданием GPU): в начале следующего кадра или при выходе, если кадр последний.
            _snapshotPrefix = Path.ChangeExtension(shot, null);
        }

        if (view is not null && (field.Shown || frame.WantsLayer(FieldOverlayName)))
        {
            bool layer = frame.WantsLayer(FieldOverlayName);
            var target = layer ? frame.Graph.CreateTexture("overlay.fire.field", new TextureDesc(TextureFormat.Rgba8Unorm, TextureUsage.ColorTarget)) : default;
            bool cleared = false;
            foreach (var sim in _sims)
            {
                if (sim.Slot.State == FireState.Free || !sim.HasOutput(frame)) continue;
                var center = _display[sim.Slot.Index];
                if (layer)
                {
                    view.Add(frame, target, cleared ? null : Vector4.Zero, camera.ViewProj, center, rotation, size, sim.Output, "overlay.fire.field");
                    cleared = true;
                }
                if (field.Shown)
                {
                    view.Add(frame, frame.Graph.BackBuffer, null, camera.ViewProj, center, rotation, size, sim.Output, "debug.fire.field");
                    DrawnFields++;
                }
            }
            if (layer)
            {
                if (!cleared)
                {
                    frame.CaptureNote($"{FieldOverlayName}: поля нет — {(_sims.Count == 0 ? "сима огня нет (горящего оружия или шумов нет в паке)" : "огонь не горит")}");
                    frame.Graph.AddPass("overlay.fire.field", FramePhase.Debug).Color(target, Vector4.Zero).Execute(static _ => { });
                }
                frame.CaptureLayer(FieldOverlayName, target);
            }
        }

        if (FlameOverlay!.Shown && _flame is not null)
            _flame.AddLayer(frame, frame.Graph.BackBuffer, FramePhase.Debug, "debug.fire.flame", null);
        if (SmokeOverlay!.Shown && _smoke is not null && frame.HalfRes.Current is { } half)
            _smoke.AddLayer(frame, half, frame.Graph.BackBuffer, FramePhase.Debug, "debug.fire.smoke", null);

        bool wantsLayer = frame.WantsLayer(OverlayName);
        if (!overlay.Shown && !wantsLayer) return;
        if (wantsLayer)
        {
            var from = debug.Mark();
            DrawFires(debug, fires, rotation, size, labels: true);
            if (fires.Fires.IsEmpty)
                frame.CaptureNote($"{OverlayName}: огня нет — горящего оружия (fire.weapons) в руке игрока нет");
            var target = frame.Graph.CreateTexture("overlay.fire", new TextureDesc(TextureFormat.Rgba8Unorm, TextureUsage.ColorTarget));
            debug.AddPass(frame, target, camera.ViewProj, camera.Eye, "overlay.fire", clear: Vector4.Zero, since: from);
            frame.CaptureLayer(OverlayName, target);
        }
        if (!overlay.Shown) return;
        var screen = debug.Mark();
        DrawnPoints = DrawFires(debug, fires, rotation, size, labels: false);
        debug.AddPass(frame, frame.Graph.BackBuffer, camera.ViewProj, camera.Eye, "debug.fire", since: screen);
    }

    /// <summary>
    /// Головки последнего кадра с тиками (крест на каждой, отрезки между соседними), клинок последнего тика; у экземпляров сима — рамка
    /// площадки в показанном центре, крест центра площадки шага и точка кисти шага. Вернёт число головок.
    /// </summary>
    private int DrawFires(DebugDraw debug, FireTracker fires, Quaternion rotation, Vector2 size, bool labels)
    {
        int drawn = 0;
        foreach (var fire in fires.Fires)
        {
            uint colour = fire.Dying ? DyingColour : BurningColour;
            int points = Math.Min(Math.Max(fires.LastFrameTicks, 1), fire.PointCount);
            Vector3 previous = default;
            for (int k = points - 1; k >= 0; k--)
            {
                var (_, head) = fire.Point(k);
                CrossAt(debug, head, Cross, colour);
                if (k < points - 1) debug.Line(previous, head, colour);
                previous = head;
                drawn++;
            }
            debug.Line(fire.Base, fire.Head, BladeColour);
            if (labels) debug.Text(fire.Head + new Vector3(0, 0.12f, 0), fire.Dying ? fire.DyingLabel : fire.Label, colour);
        }
        var u = Vector3.Transform(Vector3.UnitX, rotation) * (0.5f * size.X);
        var v = Vector3.Transform(Vector3.UnitY, rotation) * (0.5f * size.Y);
        foreach (var sim in _sims)
        {
            var s = sim.Slot;
            if (s.State == FireState.Free) continue;
            uint colour = s.State == FireState.Dying ? DyingColour : AreaColour;
            var c = _display[s.Index];
            debug.Line(c - u - v, c + u - v, colour);
            debug.Line(c + u - v, c + u + v, colour);
            debug.Line(c + u + v, c - u + v, colour);
            debug.Line(c - u + v, c - u - v, colour);
            CrossAt(debug, s.StepCenter, 0.5f * Cross, colour);
            if (sim.Brush is { } brush)
                foreach (var p in brush.Points)
                {
                    // Точка кисти в мире: UV шага (x — вправо, v — вниз) от центра площадки шага.
                    var at = s.StepCenter + (p.Unit.X - 0.5f) * 2 * u - (p.Unit.Y - 0.5f) * 2 * v;
                    CrossAt(debug, at, 0.01f + p.SigmaCm * 0.01f, BrushColour);
                }
            if (labels)
                debug.Text(c + v + new Vector3(0, 0.05f, 0), $"{(s.State == FireState.Dying ? s.DyingLabel : s.Label)}: k {sim.K:0.00}, шагов {s.Steps}",
                    colour);
        }
        return drawn;
    }

    private static void CrossAt(DebugDraw debug, Vector3 p, float r, uint colour)
    {
        debug.Line(p - Vector3.UnitX * r, p + Vector3.UnitX * r, colour);
        debug.Line(p - Vector3.UnitY * r, p + Vector3.UnitY * r, colour);
        debug.Line(p - Vector3.UnitZ * r, p + Vector3.UnitZ * r, colour);
    }

    // ── снимки полей ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Поля экземпляров, заказанные снимком прошлого кадра: <c>&lt;снимок&gt;.fire[N].density.png</c>, <c>.velocity.png</c>, <c>.bin</c>, кисть.</summary>
    private void SaveSnapshots()
    {
        if (_snapshotPrefix is not { } prefix) return;
        _snapshotPrefix = null;
        _snapshots.Clear();
        foreach (var sim in _sims)
        {
            if (sim.Slot.State == FireState.Free || sim.Sim is not { } fluid) continue;
            string name = $"{prefix}.fire{(sim.Slot.Index == 0 ? "" : sim.Slot.Index.ToString(System.Globalization.CultureInfo.InvariantCulture))}";
            var (density, _, _) = fluid.Snapshot().Save(name);
            _snapshots.Add(density);
        }
    }

    /// <summary>Выход: поля последнего кадра с заказом снимка и хеши полей (до освобождения GPU).</summary>
    internal void Closing(FireSim sim)
    {
        if (!_closing)
        {
            _closing = true;
            SaveSnapshots();   // первый из экземпляров: остальные ещё не освобождены
        }
        if (sim.Sim is not { } fluid || sim.GpuSteps == 0) return;
        var fields = fluid.ReadFields();
        var brush = fluid.ReadBrush();
        sim.FieldHash = Hash(MemoryMarshal.AsBytes(fields.Composite.AsSpan()), Hash(MemoryMarshal.AsBytes(brush.AsSpan()), 14695981039346656037UL));
        sim.FinalStats = Fluid2DStats.Compute(fields, fluid.LastStep ?? 0);
        var extents = Tuning!.Params.Extents;
        sim.FinalMetrics = FireMetrics.Compute(fields.Composite, fields.N, new Vector2(extents.X, extents.Y), _flame?.Params ?? FlameParams.Torch4,
            SmokeParams, FireMetricsParams.Default);
    }

    /// <summary>FNV-1a 64 по байтам полей: те же поля — тот же хеш бит в бит.</summary>
    internal static ulong Hash(ReadOnlySpan<byte> data, ulong h)
    {
        foreach (byte b in data)
        {
            h ^= b;
            h *= 1099511628211UL;
        }
        return h;
    }

    // ── ручки и итог ─────────────────────────────────────────────────────────────────────────────────────────────

    private void AddTuning(ClientBuilder b, FireTuning tuning, ClientOverlay overlay, ClientOverlay field, ClientOverlay flameOverlay,
        ClientOverlay smokeOverlay, FireSettings settings)
    {
        var torch = Fluid2DParams.Torch;
        b.Tuning(_group = new TuningGroup(ModuleId, "Огонь")
            .Header("ВИД")
            .Bool("flame", "пламя", () => FlameShown, v => FlameShown = v,
                "плоскость пламени MI_Torch4 LIVE-2 поверх сцены (выкл. — только сим и отладочные виды)", defaultValue: true)
            .Bool("flameLayer", "вклад пламени", () => flameOverlay.Shown, v => flameOverlay.Shown = v,
                "вклад пламени поверх кадра: цвет x/(1 + x), альфа (как --overlay fire.flame)", defaultValue: false)
            .Bool("smoke", "дым", () => SmokeShown, v => SmokeShown = v,
                "объёмный дым HV LIVE-2 (MI_HVOL_SIM_TorchSmoke) в слое 1/2: рассеяние света огня x0.15 без LF и солнца, ambient неба", defaultValue: true)
            .Bool("smokeLayer", "вклад дыма", () => smokeOverlay.Shown, v => smokeOverlay.Shown = v,
                "вклад дыма (слой 1/2) поверх кадра: цвет x/(1 + x), альфа (как --overlay fire.smoke)", defaultValue: false)
            .Float("smokeDensity", "плотность дыма", () => SmokeParams.Density, v => SmokeParams = SmokeParams with { Density = v }, 0f, 4f,
                hint: "1/Scale объёма HV: множитель σ и эмиссии (единицы LIVE-2 не установлены); 1 — σ = 0.1·ρ^0.35 на см", defaultValue: SmokeParams.Torch.Density)
            .Bool("light", "свет", () => _light?.Shown ?? false, v => { if (_light is not null) _light.Shown = v; },
                "точечный свет факела LIVE-2 у огня: (1, 0.038, 0), 100, экспонента 400, R 20 м (источник — --overlay lights)", defaultValue: true)
            .Bool("lightFunction", "мерцание света (LF)", () => _light?.FunctionShown ?? false, v => { if (_light is not null) _light.FunctionShown = v; },
                "light function M_LightFunction1: min(T_Noise3 + T_NoiseTemplate2, 1) по направлению и времени; выкл. — ровно затухание",
                defaultValue: true)
            .Header("ОВЕРЛЕЙ")
            .Bool("overlay", "головки и площадка", () => overlay.Shown, v => overlay.Shown = v,
                "головки горящего оружия игрока по тикам, рамка площадки сима, центр шага, точка кисти (как --overlay fire)", defaultValue: false)
            .Bool("field", "вид поля", () => field.Shown, v => field.Shown = v,
                "площадка сима с плотностью ρ тепловой шкалой поверх кадра (как --overlay fire.field)", defaultValue: false)
            .Header("СИМ (клиент, не реплей)")
            .Float("simSpeed", "скорость сима", () => tuning.Params.SimSpeed, v => tuning.Params = tuning.Params with { SimSpeed = v }, 0.25f, 8f,
                hint: "SimSpeed: шаг соседей и адвекции, текселей (факел — 2)", defaultValue: torch.SimSpeed)
            .Float("accumulation", "затухание (накопление ρ)", () => tuning.Params.DensityAccumulation,
                v => tuning.Params = tuning.Params with { DensityAccumulation = v }, 0.5f, 1f,
                hint: "DensityAccumulation: ρ ×(0.5 + 0.5·DA·…) за шаг; меньше — гаснет быстрее (факел — 0.9: ×0.94)", defaultValue: torch.DensityAccumulation)
            .Float("brushStrength", "сила кисти", () => tuning.Params.BrushStrength, v => tuning.Params = tuning.Params with { BrushStrength = v },
                0f, 4f, hint: "BrushStrength: плотность мазка (факел — 1)", defaultValue: torch.BrushStrength)
            .Float("brushSize", "размер кисти", () => tuning.Params.BrushSize, v => tuning.Params = tuning.Params with { BrushSize = v },
                0.25f, 4f, hint: $"GlobalBrushSize: σ = 9.496·s·размер см (s шишки {FireArea.BrushScale(settings):0.###})", defaultValue: torch.BrushSize)
            .Float("kMin", "k: нижняя граница", () => tuning.KMin, v => tuning.KMin = v, 0f, 1f,
                hint: "доля сдвига буферов при движении: 0.35 — газ едет с огнём на 65 % пути (L2); 1 — стоит в мире", defaultValue: 0.35f)
            .Info("шаги", dest => Status(dest), "шагов сима огня (экземпляр 0), k и Σρ последнего чтения"));
    }

    private int Status(Span<char> dest)
    {
        if (_sims.Count == 0) return Write(dest, 0, "сима нет");
        var sim = _sims[0];
        int n = Write(dest, 0, sim.Slot.State switch { FireState.Burning => "горит ", FireState.Dying => "гаснет ", _ => "свободен " });
        n = Write(dest, n, "шагов ");
        if (sim.Slot.Steps.TryFormat(dest[n..], out int w)) n += w;
        n = Write(dest, n, ", k ");
        if (sim.K.TryFormat(dest[n..], out w, "0.00")) n += w;
        if (sim.Stats is { } s)
        {
            n = Write(dest, n, ", Σρ ");
            if (s.DensitySum.TryFormat(dest[n..], out w, "0.0")) n += w;
        }
        return n;
    }

    private static int Write(Span<char> dest, int at, string text)
    {
        int n = Math.Min(text.Length, dest.Length - at);
        if (n > 0) text.AsSpan(0, n).CopyTo(dest[at..]);
        return at + Math.Max(n, 0);
    }

    /// <summary>Раздел <c>fire</c> итога <c>--json</c>: настройки, найденное оружие, огни последнего тика, экземпляры сима и проверки.</summary>
    private object Report()
    {
        var fires = Fires!;
        var slots = Slots!;
        var p = Tuning!.Params;
        float speedLimit = p.VelocityClamp * MathF.Sqrt(2) + 1e-3f;
        var sims = _sims.Select(sim =>
        {
            var s = sim.Slot;
            var stats = sim.FinalStats ?? sim.Stats;
            var (display, offset) = Display(s.Index);
            bool active = s.State != FireState.Free;
            bool finite = stats is not { } a || a.NonFinite == 0;
            bool bounded = stats is not { } b || (b.MaxDensity <= 1f && b.MaxSpeed <= speedLimit);
            bool centered = !active || (MathF.Abs(offset.X) < 0.5f && MathF.Abs(offset.Y) < 0.5f);
            return new
            {
                name = sim.Name,
                state = s.State.ToString().ToLowerInvariant(),
                owner = active ? FireTracker.Owner(s.Owner) : null,
                weapon = active ? s.WeaponName : null,
                key = s.Key,
                firstTick = s.FirstTick,
                hasWeapon = s.HasWeapon,
                center = Vec(s.Center),
                stepCenter = Vec(s.StepCenter),
                stepTick = s.StepTick,
                step = s.Step,
                display = Vec(display),
                displayUv = new[] { offset.X, offset.Y },
                k = sim.K,
                deltaCm = Vec(sim.Delta * 100f),
                wod = new[] { sim.Wod.X, sim.Wod.Y },
                worldOffset = new[] { sim.WorldOffset.X, sim.WorldOffset.Y },
                steps = s.Steps,
                ageSeconds = s.Steps / (double)FireArea.StepRate,
                fadeSteps = s.FadeSteps,
                gpuSteps = sim.GpuSteps,
                lastFrameSteps = sim.LastFrameSteps,
                brush = sim.Brush is { } br ? new { stats = br.Stats, points = br.Points.ToArray().Select(q => new
                {
                    uv = new[] { q.Unit.X, q.Unit.Y }, uvVelocity = new[] { q.UnitVelocity.X, q.UnitVelocity.Y }, scale = q.Scale, sigmaCm = q.SigmaCm,
                    segment = q.Segment,
                }).ToArray() } : null,
                stats = stats is { } st ? new
                {
                    final = sim.FinalStats is not null,
                    step = st.Step, densitySum = st.DensitySum, maxSpeed = st.MaxSpeed, maxDensity = st.MaxDensity, nonFinite = st.NonFinite,
                    centroid = new[] { st.Centroid.X, st.Centroid.Y },
                    // Центроид ρ от центра площадки в её осях (x — вправо, y — вверх), см.
                    centroidCm = new[] { (st.Centroid.X - 0.5f) * p.Extents.X * 100f, -(st.Centroid.Y - 0.5f) * p.Extents.Y * 100f },
                } : null,
                fieldHash = sim.FieldHash is { } h ? h.ToString("X16") : null,
                checks = new { finite, bounded, centered },
            };
        }).ToArray();
        return new
        {
            weapons = fires.Settings.Weapons,
            burning = fires.Burning,
            missing = fires.Missing,
            gpu = _sims.Count == 0 ? "нет горящего оружия в паке" : _gpuMissing ?? "ok",
            maxFires = fires.Settings.MaxFires,
            overlay = Overlay!.Shown,
            field = FieldOverlay!.Shown,
            metrics = MetricsReport(),
            flame = FlameReport(),
            smoke = SmokeReport(),
            light = _light is { } light ? light.Report(slots) : (object)"нет горящего оружия в паке",
            observedTicks = fires.ObservedTicks,
            overLimit = fires.OverLimit + slots.OverLimit,
            stolen = slots.Stolen,
            released = new { byTime = slots.ReleasedByTime, byDensity = slots.ReleasedByDensity },
            tickRate = slots.TickRate,
            stepRate = FireArea.StepRate,
            stepRequests = slots.StepRequests,
            fadeSteps = slots.FadeSteps,
            brushScale = FireArea.BrushScale(fires.Settings),
            kMin = Tuning.KMin,
            // Ручки «Огонь» (F3, --tune fire.*) в конце прогона — ключ → число (флаг — 1/0).
            tuning = _group?.Values(),
            lastFrameTicks = fires.LastFrameTicks,
            snapshots = _snapshots.ToArray(),
            checks = new
            {
                finite = sims.All(s => s.checks.finite),
                bounded = sims.All(s => s.checks.bounded),
                centered = sims.All(s => s.checks.centered),
            },
            fires = fires.Fires.ToArray().Select(f => new
            {
                owner = FireTracker.Owner(f.Slot),
                key = f.Key,
                weapon = f.WeaponName,
                head = Vec(f.Head),
                center = Vec(f.Center),
                @base = Vec(f.Base),
                dying = f.Dying,
                ticks = f.Ticks,
                firstTick = f.FirstTick,
                lastTick = f.LastTick,
                points = f.PointCount,
            }).ToArray(),
            sims,
        };
    }

    /// <summary>
    /// Раздел <c>fire.metrics</c>: размеры огня каждого экземпляра с шагами по полю на выходе (<see cref="FireMetrics"/>) — в осях площадки от
    /// центра шишки, см: пламя (k̄ ≥ порога при среднем шуме), ядро и его доля по высоте, столб дыма (τ по нормали ≥ порога) и его верх над пламенем.
    /// </summary>
    private object MetricsReport()
    {
        var t = FireMetricsParams.Default;
        return _sims.Where(s => s.FinalMetrics is not null).Select(s =>
        {
            var m = s.FinalMetrics!.Value;
            return new
            {
                sim = s.Name,
                state = s.Slot.State.ToString().ToLowerInvariant(),
                step = s.FinalStats?.Step,
                flame = Region(m.Flame),
                core = Region(m.Core),
                coreShare = MathF.Round(m.CoreShare, 3),
                smoke = Region(m.Smoke),
                smokeAboveFlameCm = MathF.Round(m.SmokeAboveFlameCm, 1),
                thresholds = new { noise = t.Noise, flameK = t.FlameK, coreK = t.CoreK, flameRho = m.FlameRho, coreRho = m.CoreRho, smokeTau = t.SmokeTau },
            };
        }).ToArray();

        static object Region(FireRegion r) => new
        {
            texels = r.Texels, heightCm = MathF.Round(r.HeightCm, 1), widthCm = MathF.Round(r.WidthCm, 1), topCm = MathF.Round(r.TopCm, 1),
            bottomCm = MathF.Round(r.BottomCm, 1), leftCm = MathF.Round(r.LeftCm, 1), rightCm = MathF.Round(r.RightCm, 1),
            centroidCm = new[] { MathF.Round(r.CentroidCm.X, 1), MathF.Round(r.CentroidCm.Y, 1) },
        };
    }

    /// <summary>Раздел <c>fire.flame</c>: рисуется ли пламя, площадок и их площадь на экране в последнем кадре, фаза, туман, числа формулы.</summary>
    private object FlameReport()
    {
        var p = _flame?.Params ?? FlameParams.Torch4;
        double pixels = 0;
        for (int i = 0; i < _quadCount; i++) pixels += ScreenPixels(_quads[i], _flameView.ViewProj, _width, _height);
        return new
        {
            shown = FlameShown,
            state = _flame is not null ? "ok" : _sims.Count == 0 ? "нет сима" : $"нет шума пламени: {_flameMissing}",
            overlay = FlameOverlay!.Shown,
            quads = _flame?.LastQuads ?? 0,
            // Площадь площадок на экране (полная плоскость 3×3 м, не покрытие ρ), пикселей.
            screenPixels = Math.Round(pixels),
            frameSeconds = _seconds,
            phase = _flameView.Phase,
            fog = new { colour = Vec(_flameView.Fog.Colour), start = _flameView.Fog.Start, range = _flameView.Fog.Range, service = _fog is not null },
            viewDepth = new[] { _flameView.ViewDepth.X, _flameView.ViewDepth.Y },
            colour1 = Vec(p.Colour1),
            colour2 = Vec(p.Colour2),
            flowStrength = p.FlowStrength, velocityClamp = p.VelocityClamp, flowSpeed = p.FlowSpeed, flowTile = p.FlowTile,
            densityGain = p.DensityGain, noisePower = p.FlowDensityPow, densityPower = p.DensityPower,
            depthFadeCm = p.DepthFadeDistance * 100, edgeMask = p.EdgeMask,
        };
    }

    /// <summary>
    /// Раздел <c>fire.smoke</c>: рисуется ли дым, объёмов и источников рассеяния в последнем кадре, числа формулы и единиц, солнце и небо,
    /// статистика марша по сетке лучей (шагов на луч с дымом — средн./макс., покрытие — доля лучей с α ≥ 1/255, средняя и наибольшая α).
    /// </summary>
    private object SmokeReport()
    {
        var p = SmokeParams;
        var s = _smoke?.Stats;
        return new
        {
            shown = SmokeShown,
            state = _smoke is not null ? "ok" : _sims.Count == 0 ? "нет сима" : "нет дыма",
            overlay = SmokeOverlay!.Shown,
            volumes = _smoke?.LastVolumes ?? 0,
            lights = _smoke?.LastLights ?? 0,
            density = p.Density,
            unitsPerMetre = p.UnitsPerMetre,
            stepCm = p.StepLength * 100,
            maxSteps = p.MaxSteps,
            slabHalfCm = p.SlabHalfDepth * 100,
            extents = Vec(p.Extents),
            albedo = p.Albedo,
            sun = Vec(_smoke?.LastSun ?? default),
            ambient = Vec(_smoke?.LastAmbient ?? default),
            stats = s is { } st ? new
            {
                frame = st.Frame, rays = st.Rays, hit = st.Hit, meanSteps = st.MeanSteps, maxSteps = st.MaxSteps,
                coverage = st.Coverage, meanAlpha = st.MeanAlpha, maxAlpha = st.MaxAlpha,
            } : null,
        };
    }

    /// <summary>Площадь площадки на экране, пикселей (многоугольник углов; угол за камерой — 0).</summary>
    internal static double ScreenPixels(in FlameQuad q, in Matrix4x4 viewProj, int width, int height)
    {
        Span<Vector2> c = stackalloc Vector2[4];
        ReadOnlySpan<Vector2> corners = [new(-1, -1), new(1, -1), new(1, 1), new(-1, 1)];
        for (int k = 0; k < 4; k++)
        {
            var p = Vector4.Transform(new Vector4(q.Center + corners[k].X * q.U + corners[k].Y * q.V, 1), viewProj);
            if (p.W <= 1e-6f) return 0;
            c[k] = new Vector2((p.X / p.W * 0.5f + 0.5f) * width, (0.5f - p.Y / p.W * 0.5f) * height);
        }
        double area = 0;
        for (int k = 0; k < 4; k++)
        {
            var a = c[k];
            var b = c[(k + 1) % 4];
            area += (double)a.X * b.Y - (double)b.X * a.Y;
        }
        return Math.Abs(area) * 0.5;
    }

    private static float[] Vec(Vector3 v) => [v.X, v.Y, v.Z];
}
