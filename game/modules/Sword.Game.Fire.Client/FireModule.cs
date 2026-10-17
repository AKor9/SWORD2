using System.Numerics;
using System.Runtime.InteropServices;
using Sword.Engine.Client;
using Sword.Engine.Diagnostics;
using Sword.Engine.Fluid2D;
using Sword.Engine.Fluid3D;
using Sword.Engine.Render;
using Sword.Game.Contracts;

namespace Sword.Game.Fire;

/// <summary>
/// Модуль Fire (<c>fire</c>, решение 18): огонь на горящем оружии игрока — только представление, вне симуляции, хеша и реплея; своей
/// сборки симуляции нет (Id объявлен симуляции как клиентский). Секция <c>fire</c> (<see cref="FireSettings"/>); огни по тикам
/// (<see cref="FireTracker"/>) → экземпляры сима (<see cref="FireSlots"/>, 60 Гц по <c>TickId</c>) → GPU-симуляции <c>fire.N</c>
/// (<see cref="FireSim"/>: <see cref="Fluid2DSim"/> 368² факела и кисть в центре шишки — пламя). Дым — 3D-газ в мире: экземпляры
/// <see cref="FireGasSlots"/> → GPU-симуляции <c>fire.gas.N</c> (<see cref="FireGasSim"/>: <see cref="Fluid3DSim"/>, окно за шишкой) → рэймарч
/// по плотности газа (<see cref="FireSmoke"/>, формула HV <c>MI_HVOL_SIM_TorchSmoke</c>) в слой ½, слой и оверлей <see cref="SmokeOverlayName"/>;
/// рамка окна газа, цель и центроид — оверлей <see cref="GasOverlayName"/>. Отладка: оверлей <see cref="OverlayName"/> (головки по тикам,
/// рамка площадки, центр, точка кисти), вид поля <see cref="FieldOverlayName"/> (ρ тепловой шкалой), вкладка F3 «Огонь», раздел итога
/// <c>--json</c> — <c>fire</c>, поля — в снимок кадра (<c>--shot</c>). Пламя (<see cref="FireFlame"/>, фаза Transparent) —
/// площадка к камере по формуле <c>MI_Torch4</c>, слой и оверлей <see cref="FlameOverlayName"/>. Свет (<see cref="FireLight"/>) — точечный
/// источник LIVE-2 с light function у каждого огня, в кадр до проходов игры (оверлей <c>lights</c> игры его видит).
/// </summary>
public sealed class FireModule : IClientModule
{
    public const string ModuleId = "fire";

    /// <summary>Оверлей: головки огней по тикам последнего кадра с тиками, клинок, площадка, центр, точка кисти.</summary>
    public const string OverlayName = "fire";

    /// <summary>Вид поля: площадка с плотностью ρ тепловой шкалой — на экран.</summary>
    public const string FieldOverlayName = "fire.field";

    /// <summary>Вклад пламени: оверлей (цвет x/(1 + x) поверх кадра).</summary>
    public const string FlameOverlayName = "fire.flame";

    /// <summary>Вклад дыма (слой ½): оверлей (цвет x/(1 + x) поверх кадра).</summary>
    public const string SmokeOverlayName = "fire.smoke";

    /// <summary>Окно 3D-дыма: рамка окна газа, цель, шишка, центроид ρ — на экран.</summary>
    public const string GasOverlayName = "fire.gas";

    /// <summary>Цвета рамки окна газа (<c>0xAABBGGRR</c>): дым огня — голубой, погасшего — серо-голубой; цель — белая, центроид — пурпурный.</summary>
    public const uint GasColour = 0xFFFFC040, GasFadingColour = 0xFFC0A080, GasTargetColour = 0xFFFFFFFF, GasCentroidColour = 0xFFFF40FF;

    /// <summary>Слой профиля столба дыма, ячеек газа (6 см).</summary>
    public const int ProfileCells = 2;

    /// <summary>Окно метрики «дым стоит в мире», шагов (1 с).</summary>
    public const int DriftWindow = 60;

    /// <summary>Шумы сима и кисти в паке (FSR §3.11, BR §4.1) и шум flow-map пламени (<c>T_Crumple_MultiLevel_Inv</c>, канал r, sRGB).</summary>
    public const string SimNoisePath = "textures/fire/sim_noise.swtex", BrushNoisePath = "textures/fire/noise_template2.swtex",
        CrumplePath = "textures/fire/crumple.swtex";

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
    private readonly List<FireGasSim> _gasSims = [];

    public string Id => ModuleId;

    /// <summary>Экземпляры 3D-дыма (CPU: чей огонь, состояние, окно, шаги).</summary>
    public FireGasSlots? GasSlots { get; private set; }

    /// <summary>Ручки 3D-дыма (параметры газа и источника).</summary>
    public FireGasTuning? GasTuning { get; private set; }

    public ClientOverlay? GasOverlay { get; private set; }

    /// <summary>GPU-симуляции 3D-дыма (пусто — горящего оружия нет в паке).</summary>
    internal IReadOnlyList<FireGasSim> GasSims => _gasSims;

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

    /// <summary>Дым рисуется (ручка F3 «дым»; по умолчанию — да). Газ шагает и без него (ручка «3D-дым»).</summary>
    public bool SmokeShown { get; set; } = true;

    /// <summary>Числа пламени (ручка F3 «срез хвоста пламени» меняет <see cref="FlameParams.GlowCut"/>).</summary>
    public FlameParams FlameParams { get; set; } = FlameParams.Torch4;

    /// <summary>Числа дыма (ручка F3 «плотность дыма» меняет <see cref="SmokeParams.Density"/>).</summary>
    public SmokeParams SmokeParams { get; set; } = SmokeParams.Torch;

    public ClientOverlay? SmokeOverlay { get; private set; }

    /// <summary>Свет огней (null — горящего оружия нет в паке).</summary>
    public FireLight? Light => _light;

    /// <summary>Пламя кадра (null — сима или шума flow-map нет).</summary>
    internal FireFlame? Flame => _flame;

    /// <summary>Дым кадра (null — газа нет).</summary>
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
        var gasSlots = GasSlots = new FireGasSlots(settings, slots, b.TickDt);
        var tuning = Tuning = new FireTuning();
        var gasTuning = GasTuning = new FireGasTuning();
        var overlay = Overlay = b.Overlay(OverlayName);
        var field = FieldOverlay = b.Overlay(FieldOverlayName);
        var flameOverlay = FlameOverlay = b.Overlay(FlameOverlayName);
        var smokeOverlay = SmokeOverlay = b.Overlay(SmokeOverlayName);
        var gasOverlay = GasOverlay = b.Overlay(GasOverlayName);
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
            gasSlots.Observe(tick);
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
                WantsField = frame => FlameWanted(frame) || field.Shown,
                BrushScale = FireArea.BrushScale(settings),
                Closing = Closing,
                Gas = gasTuning,
                Flame = () => FlameParams,
            };
            view = new FireFieldView();
            b.Shaders(Fluid2DSim.Contribute);
            b.Shaders(FireFieldView.Contribute);
            b.Shaders(FireFlame.Contribute);
            b.Shaders(FireSmoke.Contribute);
            b.Shaders(Fluid3DSim.Contribute);
            b.Shaders(FireGasFeed.Contribute);
            for (int i = 0; i < settings.MaxFires; i++)
            {
                var sim = new FireSim(slots, i, gpu);
                _sims.Add(sim);
                slots.Bind(i, b.GpuSimulation(sim));
            }
            // Газ — экземпляр на огонь, ресурсы (≈42 МБ) — при старте, как у сима пламени: в кадре ни выделений, ни загрузок.
            for (int i = 0; i < settings.MaxFires; i++)
            {
                var gas = new FireGasSim(gasSlots, i, gasTuning) { Closing = GasClosing, Feeds = slot => _sims[slot].Feed };
                _gasSims.Add(gas);
                gasSlots.Bind(i, b.GpuSimulation(gas));
            }
        }
        b.Initialize(c =>
        {
            context = c;
            var template2 = gpu is not null ? LoadNoises(c, gpu) : null;
            if (gpu is { Ready: true }) LoadFlame(c, settings);
            if (_gasSims.Count > 0)
            {
                var grid = gasTuning.Params;
                _smoke = c.Own(new FireSmoke(c.Renderer, settings.MaxFires, settings.MaxFires, (grid.Nx, grid.Ny, grid.Nz)));
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
        AddTuning(b, tuning, overlay, field, flameOverlay, smokeOverlay, settings, gasSlots, gasTuning, gasOverlay);
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
        var camera = context?.Camera ?? default;
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
            _displayUv[i] = s.Steps > 0 ? FireArea.ToUv(FireArea.Plane(camera.Rotation, camera.Eye, center), center - s.StepCenter, new Vector3(extents.X, extents.Y, extents.Z)) : Vector2.Zero;
        }
    }

    // ── рисование ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Дым (фаза Transparent, до пламени): объём на живой экземпляр газа с шагами — его окно в мире (содержимое стоит в мире: бокс — окно
    /// целыми ячейками, дробный остаток цели в показ не идёт); рассеяние — свет огней кадра (если он есть и включён, без LF) и солнце сцены,
    /// ambient — небо сцены. Слой ½ начинается и смешивается здесь же — только если есть объём.
    /// </summary>
    private void DrawSmoke(RenderFrame frame, ClientModuleContext context)
    {
        var smoke = _smoke;
        if (smoke is not null && SmokeWanted(frame))
        {
            smoke.Params = SmokeParams;
            int count = 0;
            foreach (var gas in _gasSims)
                if (gas.Slot.State != FireGasState.Free && gas.Sim is { Window: { } window } sim && gas.GpuSteps > 0)
                    _volumes[count++] = new SmokeVolume
                    {
                        Slot = gas.Slot.Index, Scalars = sim.Scalars, Velocity = sim.Velocity, Window = window, Version = (ulong)gas.GpuSteps,
                        Time = (sim.LastStep ?? 0) * (double)sim.Params.StepSeconds,
                    };
            if (count > 0)
            {
                int lights = 0;
                if (_light is { Emits: true } light)
                    for (int i = 0; i < _smokeLights.Length; i++)
                        if (light.Light(i) is { } l) _smokeLights[lights++] = SmokeLight.From(l);
                var camera = context.Camera;
                var forward = -Vector3.Transform(Vector3.UnitZ, FireArea.Plane(camera.Rotation));
                var half = frame.HalfRes.Begin(FlameMath.ViewDepthParams(camera.ViewProj, camera.Eye, forward));
                _smokeView = new SmokeView(camera.ViewProj, camera.Eye, forward, _fog?.Fog ?? SceneFog.None, _sky?.Sky ?? SceneSky.None);
                smoke.Add(frame, half, _smokeView, _volumes.AsSpan(0, count), _smokeLights.AsSpan(0, lights));
                frame.HalfRes.Composite();
                return;
            }
        }
        smoke?.Skip(frame);
    }

    /// <summary>
    /// Рамка окна газа, цель, шишка и центроид ρ (фаза Debug): на экран с оверлеем <see cref="GasOverlayName"/>.
    /// </summary>
    private void DrawGasFrames(RenderFrame frame, ClientModuleContext context)
    {
        if (!GasOverlay!.Shown) return;
        var debug = context.Renderer.Debug;
        var camera = context.Camera;
        var screen = debug.Mark();
        DrawGasLines(debug);
        debug.AddPass(frame, frame.Graph.BackBuffer, camera.ViewProj, camera.Eye, "debug.fire.gas", since: screen);
    }

    /// <summary>Рамка окна (12 рёбер), крест цели, шишки и центроида, упреждение — отрезок шишка → цель по горизонтали. Вернёт число окон.</summary>
    private int DrawGasLines(DebugDraw debug)
    {
        int drawn = 0;
        foreach (var gas in _gasSims)
        {
            var s = gas.Slot;
            if (s.State == FireGasState.Free || gas.Sim is not { Window: { } w }) continue;
            uint colour = s.State == FireGasState.Fading ? GasFadingColour : GasColour;
            var lo = w.Min;
            var hi = w.Max;
            for (int e = 0; e < 4; e++)
            {
                // Рёбра вдоль x, y, z: четыре на ось.
                float a = (e & 1) == 0 ? lo.Y : hi.Y, b = (e & 2) == 0 ? lo.Z : hi.Z;
                debug.Line(new Vector3(lo.X, a, b), new Vector3(hi.X, a, b), colour);
                float c = (e & 1) == 0 ? lo.X : hi.X, d = (e & 2) == 0 ? lo.Z : hi.Z;
                debug.Line(new Vector3(c, lo.Y, d), new Vector3(c, hi.Y, d), colour);
                float f = (e & 1) == 0 ? lo.X : hi.X, g = (e & 2) == 0 ? lo.Y : hi.Y;
                debug.Line(new Vector3(f, g, lo.Z), new Vector3(f, g, hi.Z), colour);
            }
            CrossAt(debug, s.Target, 0.05f, GasTargetColour);
            CrossAt(debug, s.Knob, 0.04f, colour);
            debug.Line(s.Knob, new Vector3(s.Target.X, s.Knob.Y, s.Target.Z), GasTargetColour);
            if (gas.Stats is { DensitySum: > 0 } st) CrossAt(debug, st.Centroid, 0.06f, GasCentroidColour);
            drawn++;
        }
        return drawn;
    }

    /// <summary>
    /// Пламя (фаза Transparent, после дыма): площадки экземпляров с выходом поля этого кадра в показанном центре огня, оси — камеры кадра.
    /// </summary>
    private void DrawTransparent(RenderFrame frame, ClientModuleContext context)
    {
        _quadCount = 0;
        if (!FlameWanted(frame)) return;
        var camera = context.Camera;
        var extents = Tuning!.Params.Extents;
        foreach (var sim in _sims)
        {
            if (sim.Slot.State == FireState.Free || !sim.HasOutput(frame)) continue;
            var center = _display[sim.Slot.Index];
            var plane = FireArea.Plane(camera.Rotation, camera.Eye, center);
            _quads[_quadCount++] = new FlameQuad
            {
                Center = center, U = Vector3.Transform(Vector3.UnitX, plane) * (0.5f * extents.X),
                V = Vector3.Transform(Vector3.UnitY, plane) * (0.5f * extents.Y), WorldOffset = sim.WorldOffset, Field = sim.Output,
            };
        }
        var forward = -Vector3.Transform(Vector3.UnitZ, FireArea.Plane(camera.Rotation));
        var flame = _flame!;
        flame.Params = FlameParams;
        _flameView = new FlameView(camera.ViewProj, camera.Eye, FlameMath.ViewDepthParams(camera.ViewProj, camera.Eye, forward),
            _fog?.Fog ?? SceneFog.None, FlameMath.Phase(_seconds, flame.Params.FlowSpeed));
        _width = frame.Width;
        _height = frame.Height;
        flame.Add(frame, _flameView, _quads.AsSpan(0, _quadCount));
    }

    private void Draw(RenderFrame frame, ClientModuleContext context, FireFieldView? view)
    {
        var overlay = Overlay!;
        var field = FieldOverlay!;
        var fires = Fires!;
        var camera = context.Camera;
        var debug = context.Renderer.Debug;
        var extents = Tuning!.Params.Extents;
        var size = new Vector2(extents.X, extents.Y);
        DrawnPoints = DrawnFields = 0;

        if (frame.Capture?.ColorPath is { } shot && _sims.Count > 0)
        {
            // Поля — между кадрами (чтение с ожиданием GPU): в начале следующего кадра или при выходе, если кадр последний.
            _snapshotPrefix = Path.ChangeExtension(shot, null);
        }

        if (view is not null && field.Shown)
            foreach (var sim in _sims)
            {
                if (sim.Slot.State == FireState.Free || !sim.HasOutput(frame)) continue;
                var center = _display[sim.Slot.Index];
                var plane = FireArea.Plane(camera.Rotation, camera.Eye, center);
                view.Add(frame, frame.Graph.BackBuffer, null, camera.ViewProj, center, plane, size, sim.Output, "debug.fire.field");
                DrawnFields++;
            }

        if (FlameOverlay!.Shown && _flame is not null)
            _flame.AddLayer(frame, frame.Graph.BackBuffer, FramePhase.Debug, "debug.fire.flame", null);
        if (SmokeOverlay!.Shown && _smoke is not null && frame.HalfRes.Current is { } half)
            _smoke.AddLayer(frame, half, frame.Graph.BackBuffer, FramePhase.Debug, "debug.fire.smoke", null);
        DrawGasFrames(frame, context);

        if (!overlay.Shown) return;
        var screen = debug.Mark();
        DrawnPoints = DrawFires(debug, fires, camera, size);
        debug.AddPass(frame, frame.Graph.BackBuffer, camera.ViewProj, camera.Eye, "debug.fire", since: screen);
    }

    /// <summary>
    /// Головки последнего кадра с тиками (крест на каждой, отрезки между соседними), клинок последнего тика; у экземпляров сима — рамка
    /// площадки в показанном центре, крест центра площадки шага и точка кисти шага. Вернёт число головок.
    /// </summary>
    private int DrawFires(DebugDraw debug, FireTracker fires, CameraView camera, Vector2 size)
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
        }
        foreach (var sim in _sims)
        {
            var s = sim.Slot;
            if (s.State == FireState.Free) continue;
            uint colour = s.State == FireState.Dying ? DyingColour : AreaColour;
            var c = _display[s.Index];
            var plane = FireArea.Plane(camera.Rotation, camera.Eye, c);
            var u = Vector3.Transform(Vector3.UnitX, plane) * (0.5f * size.X);
            var v = Vector3.Transform(Vector3.UnitY, plane) * (0.5f * size.Y);
            debug.Line(c - u - v, c + u - v, colour);
            debug.Line(c + u - v, c + u + v, colour);
            debug.Line(c + u + v, c - u + v, colour);
            debug.Line(c - u + v, c - u - v, colour);
            CrossAt(debug, s.StepCenter, 0.5f * Cross, colour);
            if (sim.Brush is { } brush)
            {
                // Точка кисти в мире: UV шага (x — вправо, v — вниз) от центра площадки шага, в осях площадки шага.
                var su = Vector3.Transform(Vector3.UnitX, sim.Rotation) * (0.5f * size.X);
                var sv = Vector3.Transform(Vector3.UnitY, sim.Rotation) * (0.5f * size.Y);
                foreach (var p in brush.Points)
                {
                    var at = s.StepCenter + (p.Unit.X - 0.5f) * 2 * su - (p.Unit.Y - 0.5f) * 2 * sv;
                    CrossAt(debug, at, 0.01f + p.SigmaCm * 0.01f, BrushColour);
                }
            }
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
        // Срезы газа — через центроид ρ (дыма нет — через шишку): XY — столб в плоскости x, y; XZ — горизонталь на его высоте.
        foreach (var gas in _gasSims)
        {
            if (gas.Slot.State == FireGasState.Free || gas.Sim is not { } fluid || gas.GpuSteps == 0) continue;
            string name = $"{prefix}.fire.gas{(gas.Slot.Index == 0 ? "" : gas.Slot.Index.ToString(System.Globalization.CultureInfo.InvariantCulture))}";
            var snapshot = fluid.Snapshot();
            var at = snapshot.Stats.DensitySum > 0 ? snapshot.Stats.Centroid : gas.Slot.Knob;
            var files = snapshot.SaveSlices(name, at);
            _snapshots.Add(files[0]);
        }
    }

    /// <summary>Выход: поля последнего кадра с заказом снимка и хеши полей (до освобождения GPU).</summary>
    internal void Closing(FireSim sim)
    {
        BeginClosing();
        if (sim.Sim is not { } fluid || sim.GpuSteps == 0) return;
        var fields = fluid.ReadFields();
        var brush = fluid.ReadBrush();
        sim.FieldHash = Hash(MemoryMarshal.AsBytes(fields.Composite.AsSpan()), Hash(MemoryMarshal.AsBytes(brush.AsSpan()), 14695981039346656037UL));
        sim.FinalStats = Fluid2DStats.Compute(fields, fluid.LastStep ?? 0);
        var extents = Tuning!.Params.Extents;
        sim.FinalMetrics = FireMetrics.Compute(fields.Composite, fields.N, new Vector2(extents.X, extents.Y), FlameParams,
            FireMetricsParams.Default);
        if (GasGridOf(sim.Slot.Index) is { } grid)
            sim.FinalJoint = FireMetrics.Joint(fields.Composite, fields.N, new Vector2(extents.X, extents.Y), FlameParams, FireMetricsParams.Default,
                grid, sim.Center, sim.Rotation, SmokeParams);
    }

    /// <summary>Первый освобождаемый экземпляр (газ — раньше пламени, обратный порядок владения): снимок прошлого кадра, пока всё живо.</summary>
    private void BeginClosing()
    {
        if (_closing) return;
        _closing = true;
        SaveSnapshots();
    }

    /// <summary>Выход: хеш полей газа (скорость, ρ и T — биты half), статистика и столб дыма по полю (до освобождения GPU).</summary>
    internal void GasClosing(FireGasSim gas)
    {
        BeginClosing();
        if (gas.Sim is not { } fluid || gas.GpuSteps == 0) return;
        var fields = fluid.ReadFields();
        var window = fluid.Window ?? Fluid3DWindow.Around(Vector3.Zero, fluid.Params);
        gas.FieldHash = GasHash(fields);
        gas.FinalStats = Fluid3DStats.Compute(fields, window, fluid.LastStep ?? 0);
        var grid = SmokeGrid.FromScalars(fields.Scalars, window);
        gas.FinalColumn = FireMetrics.SmokeColumn(grid, gas.Slot.Knob, SmokeParams, FireMetricsParams.Default.SmokeTau);
        gas.FinalProfile = FireMetrics.SmokeProfile(grid, gas.Slot.Knob, SmokeParams, ProfileCells);
        gas.FinalGrid = grid;
    }

    /// <summary>Плотность газа огня экземпляра <paramref name="fireSlot"/> на выходе (горящий раньше гаснущего); нет — null.</summary>
    private SmokeGrid? GasGridOf(int fireSlot)
    {
        SmokeGrid? fading = null;
        foreach (var g in _gasSims)
        {
            if (g.Slot.State == FireGasState.Free || g.Slot.FireSlot != fireSlot || g.FinalGrid is not { } grid) continue;
            if (g.Slot.State == FireGasState.Emitting) return grid;
            fading ??= grid;
        }
        return fading;
    }

    /// <summary>Хеш полей газа: скорость и ρ, T (биты half) — FNV-1a 64.</summary>
    internal static ulong GasHash(Fluid3DFields fields) =>
        Hash(MemoryMarshal.AsBytes(fields.Scalars.AsSpan()), Hash(MemoryMarshal.AsBytes(fields.Velocity.AsSpan()), 14695981039346656037UL));

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
        ClientOverlay smokeOverlay, FireSettings settings, FireGasSlots gasSlots, FireGasTuning gas, ClientOverlay gasOverlay)
    {
        var torch = FireTuning.DefaultParams;
        var smoke = FireGasTuning.DefaultParams;
        var source = FireGasTuning.DefaultEmitter;
        var feed = FireGasFeedParams.Torch;
        b.Tuning(_group = new TuningGroup(ModuleId, "Огонь")
            .Header("ВИД")
            .Bool("flame", "пламя", () => FlameShown, v => FlameShown = v,
                "плоскость пламени MI_Torch4 LIVE-2 поверх сцены (выкл. — только сим и отладочные виды)", defaultValue: true)
            .Bool("flameLayer", "вклад пламени", () => flameOverlay.Shown, v => flameOverlay.Shown = v,
                "вклад пламени поверх кадра: цвет x/(1 + x), альфа (как --overlay fire.flame)", defaultValue: false)
            .Float("flameCut", "срез хвоста пламени", () => FlameParams.GlowCut, v => FlameParams = FlameParams with { GlowCut = v }, 0f, 1f,
                hint: "альфа пламени × saturate(k/это): слабый хвост поля (вуаль Color2, «второй дым») гаснет, дым — 3D-газ; 0 — выкл. (MI_Torch4)",
                defaultValue: FlameParams.Torch4.GlowCut)
            .Bool("smoke", "дым", () => SmokeShown, v => SmokeShown = v,
                "дым огня: рэймарч по плотности 3D-газа в мире (HV LIVE-2 MI_HVOL_SIM_TorchSmoke) в слое 1/2; выкл. — газ шагает, не рисуется",
                defaultValue: true)
            .Bool("smokeLayer", "вклад дыма", () => smokeOverlay.Shown, v => smokeOverlay.Shown = v,
                "вклад дыма (слой 1/2) поверх кадра: цвет x/(1 + x), альфа (как --overlay fire.smoke)", defaultValue: false)
            .Float("smokeDensity", "плотность дыма", () => SmokeParams.Density, v => SmokeParams = SmokeParams with { Density = v }, 0f, 8f,
                hint: "1/Scale объёма HV: множитель σ и эмиссии (единицы LIVE-2 не установлены); σ на метр = 10·это·ρ^0.35", defaultValue: SmokeParams.Torch.Density)
            .Float("smokeCut", "срез плотности дыма", () => SmokeParams.DensityCut, v => SmokeParams = SmokeParams with { DensityCut = v }, 0f, 0.1f,
                hint: "ρ не больше — σ = 0: у ρ^0.35 видна и ничтожная плотность; срез делает столб чётче и пустоту — пустой (её марш пропускает)",
                defaultValue: SmokeParams.Torch.DensityCut)
            .Float("smokeDetail", "шум дыма", () => SmokeParams.Detail, v => SmokeParams = SmokeParams with { Detail = v }, 0f, 4f,
                hint: "детализирующий шум при рендере: ρ·(1 − это·(½ + n)) — рваные края и прожилки мельче ячейки газа; 0 — выкл.",
                defaultValue: SmokeParams.Torch.Detail)
            .Float("smokeDetailScale", "размер шума дыма", () => SmokeParams.DetailScale, v => SmokeParams = SmokeParams with { DetailScale = v },
                0.01f, 0.3f, hint: "шаг решётки детализирующего шума, м", defaultValue: SmokeParams.Torch.DetailScale)
            .Float("smokeFlowSpeed", "фаз шума в секунду", () => SmokeParams.FlowSpeed, v => SmokeParams = SmokeParams with { FlowSpeed = v },
                0.25f, 10f, hint: "flow-map: шум уносится скоростью газа и сбрасывается двумя фазами за период 1/это с (больше — меньше растяжения)",
                defaultValue: SmokeParams.Torch.FlowSpeed)
            .Float("smokeFlowStrength", "унос шума газом", () => SmokeParams.FlowStrength, v => SmokeParams = SmokeParams with { FlowStrength = v },
                0f, 2f, hint: "доля скорости газа, с которой шум уносится (1 — вместе с газом, 0 — шум стоит в мире)",
                defaultValue: SmokeParams.Torch.FlowStrength)
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
            .Float("accumulation", "накопление плотности", () => tuning.Params.DensityAccumulation,
                v => tuning.Params = tuning.Params with { DensityAccumulation = v }, 0.5f, 1f,
                hint: "DensityAccumulation: ρ ×(0.5 + 0.5·DA·…) за шаг; меньше — гаснет быстрее (факел — 0.9: ×0.94)", defaultValue: torch.DensityAccumulation)
            .Float("brushStrength", "сила кисти", () => tuning.Params.BrushStrength, v => tuning.Params = tuning.Params with { BrushStrength = v },
                0f, 4f, hint: "BrushStrength: плотность мазка (факел — 1)", defaultValue: torch.BrushStrength)
            .Float("brushSize", "размер кисти", () => tuning.Params.BrushSize, v => tuning.Params = tuning.Params with { BrushSize = v },
                0.25f, 4f, hint: $"GlobalBrushSize: σ = 9.496·s·размер см (s шишки {FireArea.BrushScale(settings):0.###})", defaultValue: torch.BrushSize)
            .Float("kMin", "k: нижняя граница", () => tuning.KMin, v => tuning.KMin = v, 0f, 1f,
                hint: "доля сдвига буферов при движении: 1 — пламя стоит в мире (лучше в движении персонажа); 0.35 — едет с огнём на 65 % пути (L2)",
                defaultValue: FireTuning.DefaultKMin)
            .Float("windX", "ветер x", () => tuning.Wind.X, v => SetWind(tuning, gas, tuning.Wind with { X = v }), -3f, 3f,
                hint: "ветер по мировой x, м/с: газ дыма — однородная скорость воздуха, пламя — боковая скорость 2D-сима (0 — пламя горит вверх)",
                defaultValue: 0f)
            .Float("windZ", "ветер z", () => tuning.Wind.Z, v => SetWind(tuning, gas, tuning.Wind with { Z = v }), -3f, 3f,
                hint: "ветер по мировой z, м/с (как ветер x)", defaultValue: 0f)
            .Info("шаги", dest => Status(dest), "шагов сима огня (экземпляр 0), k и Σρ последнего чтения")
            .Header("3D-ДЫМ (клиент, не реплей)")
            .Bool("gas", "3D-дым", () => gasSlots.Enabled, v => gasSlots.Enabled = v,
                "дым — 3D-газ в мире (96×128×96 по 3 см, окно за шишкой целыми ячейками); выкл. — экземпляры свободны, шагов нет", defaultValue: true)
            .Bool("gasView", "окно 3D-дыма", () => gasOverlay.Shown, v => gasOverlay.Shown = v,
                "рамка окна газа, цель окна, шишка, центроид ρ поверх кадра (как --overlay fire.gas)", defaultValue: false)
            .Float("gasFeed", "дым из пламени", () => gas.Feed.Mix, v => gas.Feed = gas.Feed with { Mix = v }, 0f, 1f,
                hint: "доля источника из поля пламени (где пламя гаснет, у шишки) против шара у шишки: 1 — дым продолжает пламя, 0 — прежний шар",
                defaultValue: feed.Mix)
            .Float("gasFeedRadius", "зона дыма из пламени", () => gas.Feed.Radius, v => gas.Feed = gas.Feed with { Radius = v }, 0.1f,
                feed.HalfMap, hint: "поле пламени даёт дым не дальше этого от шишки, м, до половины карты (дальний хвост 2D не прилипает к глубине факела при вращении)",
                defaultValue: feed.Radius)
            .Float("gasFeedDensity", "выброс из пламени", () => gas.Feed.Density, v => gas.Feed = gas.Feed with { Density = v }, 0f, 40f,
                hint: "ρ в секунду на клетку при A = 1 (A — ρ·(1 − видимость пламени) поля); температура — вдвое", defaultValue: feed.Density)
            .Float("gasFeedTemperature", "температура из пламени", () => gas.Feed.Temperature, v => gas.Feed = gas.Feed with { Temperature = v },
                0f, 80f, hint: "T в секунду на клетку при A = 1: плавучесть дыма у стыка", defaultValue: feed.Temperature)
            .Float("gasFeedVelocity", "скорость из пламени", () => gas.Feed.VelocityRate, v => gas.Feed = gas.Feed with { VelocityRate = v },
                0f, 300f, hint: "как быстро скорость газа у площадки тянется к скорости поля пламени, 1/с при A = 1 (0 — газ движется сам)",
                defaultValue: feed.VelocityRate)
            .Float("gasRadius", "радиус источника", () => gas.Emitter.Radius, v => gas.Emitter = gas.Emitter with { Radius = v }, 0.01f, 0.15f,
                hint: "радиус шара источника у центра шишки, м (толщина столба у огня)", defaultValue: source.Radius)
            .Float("gasDensity", "выброс плотности", () => gas.Emitter.Density, v => gas.Emitter = gas.Emitter with { Density = v }, 0f, 30f,
                hint: "ρ в секунду в шаре источника у шишки", defaultValue: source.Density)
            .Float("gasTemperature", "выброс температуры", () => gas.Emitter.Temperature, v => gas.Emitter = gas.Emitter with { Temperature = v }, 0f, 60f,
                hint: "T в секунду в шаре источника: плавучесть — β·T", defaultValue: source.Temperature)
            .Float("gasBuoyancy", "плавучесть", () => gas.Params.TemperatureBuoyancy, v => gas.Params = gas.Params with { TemperatureBuoyancy = v },
                0f, 3f, hint: "β: сила вверх β·T·g", defaultValue: smoke.TemperatureBuoyancy)
            .Float("gasVorticity", "вихри", () => gas.Params.VorticityConfinement, v => gas.Params = gas.Params with { VorticityConfinement = v },
                0f, 10f, hint: "ε вихревого удержания (0 — выкл.)", defaultValue: smoke.VorticityConfinement)
            .Float("gasNoise", "вихревой шум", () => gas.Params.CurlNoise, v => gas.Params = gas.Params with { CurlNoise = v }, 0f, 20f,
                hint: "бездивергентный curl-шум в мире, м/с² в среднем (0 — выкл.): изгибает столб в S-завитки", defaultValue: smoke.CurlNoise)
            .Float("gasNoiseScale", "размер вихрей", () => gas.Params.CurlNoiseScale, v => gas.Params = gas.Params with { CurlNoiseScale = v },
                0.06f, 1.5f, hint: "шаг решётки вихревого шума, м (вихри ≈ половина шага; мельче 12 см сетка 3 см не разрешает)",
                defaultValue: smoke.CurlNoiseScale)
            .Float("gasNoiseRise", "подъём вихрей", () => gas.Params.CurlNoiseRise, v => gas.Params = gas.Params with { CurlNoiseRise = v },
                0f, 3f, hint: "узор вихревого шума поднимается в мире со скоростью, м/с — вихри уходят вверх с дымом, столб живёт", defaultValue: smoke.CurlNoiseRise)
            .Float("gasNoiseDensity", "маска вихрей", () => gas.Params.CurlNoiseDensity, v => gas.Params = gas.Params with { CurlNoiseDensity = v },
                0f, 1f, hint: "ρ полной силы шума: сила × min(ρ/это, 1) — шум мешает только дым; 0 — весь воздух окна", defaultValue: smoke.CurlNoiseDensity)
            .Float("gasDecay", "затухание", () => gas.Params.DensityDecay, v => gas.Params = gas.Params with { DensityDecay = v }, 0f, 5f,
                hint: "k плотности: ρ/(1 + k·dt) за шаг — дым тает за ≈1/k с", defaultValue: smoke.DensityDecay)
            .Float("gasCooling", "остывание", () => gas.Params.TemperatureDecay, v => gas.Params = gas.Params with { TemperatureDecay = v }, 0f, 10f,
                hint: "k температуры: T/(1 + k·dt) за шаг — плавучесть гаснет за ≈1/k с (столб выше или ниже)", defaultValue: smoke.TemperatureDecay)
            .Float("gasDissipation", "рассеяние", () => gas.Params.DensityDissipation, v => gas.Params = gas.Params with { DensityDissipation = v },
                0f, 1f, hint: "c плотности: −c·dt за шаг — слабый дым исчезает до нуля, столб не расплывается туманом", defaultValue: smoke.DensityDissipation)
            .Float("gasSway", "раскачка", () => gas.Emitter.Sway, v => gas.Emitter = gas.Emitter with { Sway = v }, 0f, 2f,
                hint: "горизонтальная скорость газа у источника ±это, м/с, шумом по шагам: волна вверх по столбу — S-завитки", defaultValue: source.Sway)
            .Int("gasSwaySteps", "период раскачки", () => gas.Emitter.SwaySteps, v => gas.Emitter = gas.Emitter with { SwaySteps = v }, 1, 60,
                hint: "шагов между узлами шума раскачки (60 шагов — 1 с)", defaultValue: source.SwaySteps)
            .Float("gasTrail", "выброс на отрезке", () => gas.Emitter.TrailConservation, v => gas.Emitter = gas.Emitter with { TrailConservation = v },
                0f, 1f, hint: "1 — выброс в секунду не зависит от скорости шишки (след на бегу тоньше); 0 — заметённое получает полный впрыск (след гуще)",
                defaultValue: source.TrailConservation)
            .Float("gasEntrainment", "унос", () => gas.Emitter.VelocityScale, v => gas.Emitter = gas.Emitter with { VelocityScale = v }, 0f, 1.5f,
                hint: "скорость газа в источнике тянется к скорости шишки × это (взмах бросает дым)", defaultValue: source.VelocityScale)
            .Int("gasPressure", "полупроходы давления", () => gas.Params.PressureIterations, v => gas.Params = gas.Params with { PressureIterations = v },
                0, 64, hint: "red-black SOR за шаг; вес SOR зажимается в устойчивое: (ω − 1)^(N/2) < 1/3", defaultValue: smoke.PressureIterations)
            .Float("gasSor", "вес SOR", () => gas.Params.SorWeightValue, v => gas.Params = gas.Params with { SorWeight = v }, 1f, 1.99f,
                hint: "вес SOR; больше устойчивого при этих полупроходах — зажим (WithStablePressure)", defaultValue: smoke.SorWeightValue)
            .Info("дым", dest => GasStatus(dest), "экземпляр газа 0: состояние, шагов, Σρ последнего чтения, отношение путей центроида и шишки за 1 с"));
    }

    /// <summary>Один ветер на оба сима: 2D — боковая постоянная скорость, газ — однородная скорость воздуха.</summary>
    private static void SetWind(FireTuning tuning, FireGasTuning gas, Vector3 wind)
    {
        tuning.Wind = wind;
        gas.Params = gas.Params with { Wind = tuning.Wind };
    }

    private int GasStatus(Span<char> dest)
    {
        if (_gasSims.Count == 0) return Write(dest, 0, "газа нет");
        var gas = _gasSims[0];
        int n = Write(dest, 0, gas.Slot.State switch { FireGasState.Emitting => "горит ", FireGasState.Fading => "гаснет ", _ => "свободен " });
        n = Write(dest, n, "шагов ");
        if (gas.Slot.Steps.TryFormat(dest[n..], out int w)) n += w;
        if (gas.Stats is { } s)
        {
            n = Write(dest, n, ", Σρ ");
            if (s.DensitySum.TryFormat(dest[n..], out w, "0.0")) n += w;
        }
        if (gas.Drift.Compute(DriftWindow) is { KnobPath: > 0.05f } d)
        {
            n = Write(dest, n, ", центроид/шишка ");
            if (d.Ratio.TryFormat(dest[n..], out w, "0.00")) n += w;
        }
        return n;
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
        var gas = GasReport();
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
            gas = gas.Report,
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
                gasFinite = gas.Finite,
                gasBounded = gas.Bounded,
                gasInside = gas.Inside,
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
    /// Раздел <c>fire.gas</c>: 3D-дым — включён ли, вид, настройки окна и угасания, параметры газа и источника, счётчики экземпляров и по
    /// экземпляру: огонь, шишка, сглаженная скорость, цель и окно (центр, размер, место позади и впереди шишки по ходу), статистика шага
    /// (Σρ, ΣT, max|v|, NaN, центроид ρ в мире, остаток дивергенции; на выходе — по полю), метрика «дым стоит в мире» за 1 с, хеш полей.
    /// Проверки: поле конечно, |v| в пределе, горящая шишка внутри окна не ближе 10 см к краю.
    /// </summary>
    private (object Report, bool Finite, bool Bounded, bool Inside) GasReport()
    {
        var slots = GasSlots!;
        var t = GasTuning!;
        var settings = slots.Settings;
        bool finite = true, bounded = true, inside = true;
        var sims = _gasSims.Select(g =>
        {
            var s = g.Slot;
            bool active = s.State != FireGasState.Free;
            var stats = g.FinalStats ?? g.Stats;
            var window = active ? g.Sim?.Window : null;
            var drift = active ? g.Drift.Compute(DriftWindow) : null;
            var dir = new Vector3(s.Velocity.X, 0, s.Velocity.Z);
            float speed = dir.Length();
            bool moving = speed > 0.3f;
            if (moving) dir /= speed;
            float? behind = null, ahead = null, smokeBehind = null;
            if (window is { } w && moving)
            {
                behind = Room(w, s.Knob, -dir);
                ahead = Room(w, s.Knob, dir);
            }
            if (active && moving && stats is { DensitySum: > 0 } cs) smokeBehind = Vector3.Dot(s.Knob - cs.Centroid, dir);
            if (stats is { } st)
            {
                finite &= st.NonFinite == 0;
                bounded &= st.MaxSpeed <= t.Params.MaxSpeed * 1.01f;
            }
            float margin = window is { } ww && s.State == FireGasState.Emitting ? Margin(ww, s.Knob) : float.NaN;
            if (float.IsFinite(margin)) inside &= margin >= 0.1f;
            return new
            {
                name = g.Name,
                state = s.State.ToString().ToLowerInvariant(),
                owner = active ? FireTracker.Owner(s.Owner) : null,
                weapon = active ? s.WeaponName : null,
                key = s.Key,
                firstTick = s.FirstTick,
                fireSlot = s.FireSlot,
                knob = Vec(s.Knob),
                velocity = Vec(s.Velocity),
                speed,
                follow = new[] { s.Follow.X, s.Follow.Z },
                target = Vec(s.Target),
                window = window is { } box ? new
                {
                    center = Vec(box.Center), min = Vec(box.Min), size = Vec(box.Max - box.Min), cells = new[] { box.X, box.Y, box.Z },
                    offset = Vec(box.Offset),
                    // Шишка над низом окна и под верхом, м; до ближайшей боковой грани; место позади и впереди по ходу (бег).
                    knobAboveBottom = s.Knob.Y - box.Min.Y, knobBelowTop = box.Max.Y - s.Knob.Y, knobMargin = float.IsFinite(margin) ? margin : (float?)null,
                    behindM = behind, aheadM = ahead,
                } : null,
                step = s.Step,
                steps = s.Steps,
                fadeSteps = s.FadeSteps,
                gpuSteps = g.GpuSteps,
                lastFrameSteps = g.LastFrameSteps,
                emitter = g.Emitters.Stats,
                fedSteps = g.FedSteps,
                stats = stats is { } x ? new
                {
                    final = g.FinalStats is not null,
                    step = x.Step, densitySum = x.DensitySum, temperatureSum = x.TemperatureSum, maxSpeed = x.MaxSpeed, maxDensity = x.MaxDensity,
                    maxTemperature = x.MaxTemperature, nonFinite = x.NonFinite, centroid = Vec(x.Centroid),
                    // Центроид от шишки, м: вверх и по горизонтали; позади шишки по ходу (бег).
                    centroidAboveKnob = x.Centroid.Y - s.Knob.Y, centroidFromKnobH = Horizontal(x.Centroid - s.Knob), smokeBehindM = smokeBehind,
                    divergenceBefore = x.DivergenceBefore, divergenceAfter = x.DivergenceAfter, divergenceRatio = x.DivergenceRatio, enstrophy = x.Enstrophy,
                } : null,
                drift = drift is { } d ? new
                {
                    fromStep = d.FromStep, toStep = d.ToStep, seconds = d.Seconds, samples = d.Samples,
                    knobPathM = d.KnobPath, knobShiftM = d.KnobShift, centroidPathM = d.CentroidPath, centroidShiftM = d.CentroidShift,
                    knobSpeed = d.KnobSpeed, centroidSpeed = d.CentroidSpeed, ratio = d.Ratio,
                } : null,
                // Столб дыма по полю на выходе (τ вдоль мировой z ≥ порога), см от шишки.
                column = g.FinalColumn is { } col ? Region(col) : null,
                // Профиль столба по высоте (слои по 6 см): Σσ, max ρ, центр σ от шишки по x и z, радиус σ — толщина, наклон, спад.
                profile = g.FinalProfile,
                fieldHash = g.FieldHash is { } h ? h.ToString("X16") : null,
            };
        }).ToArray();
        var p = t.Params;
        var e = t.Emitter;
        object report = new
        {
            enabled = slots.Enabled,
            view = GasOverlay!.Shown,
            emitting = slots.Slots.ToArray().Count(s => s.State == FireGasState.Emitting),
            alive = slots.Slots.ToArray().Count(s => s.State != FireGasState.Free),
            overLimit = slots.OverLimit,
            stolen = slots.Stolen,
            released = new { byDensity = slots.ReleasedByDensity, byTime = slots.ReleasedByTime, bySwitch = slots.ReleasedBySwitch },
            stepRequests = slots.StepRequests,
            fadeSteps = slots.FadeSteps,
            settings = new
            {
                up = settings.GasUp, followSeconds = settings.GasFollowSeconds, maxLead = settings.GasMaxLead,
                fadeSeconds = settings.GasFadeSeconds, fadeDensity = settings.GasFadeDensity, driftWindowSteps = DriftWindow,
            },
            grid = new { nx = p.Nx, ny = p.Ny, nz = p.Nz, cellCm = p.CellSize * 100, size = Vec(p.Size) },
            @params = new
            {
                densityBuoyancy = p.DensityBuoyancy, temperatureBuoyancy = p.TemperatureBuoyancy, vorticity = p.VorticityConfinement,
                pressureIterations = p.PressureIterations, sorWeight = p.SorWeightValue, warmStart = p.PressureWarmStart,
                densityDecay = p.DensityDecay, densityDissipation = p.DensityDissipation, temperatureDecay = p.TemperatureDecay,
                temperatureDissipation = p.TemperatureDissipation, maxSpeed = p.MaxSpeed, advection = p.ScalarAdvection.ToString(),
                curlNoise = p.CurlNoise, curlNoiseScaleCm = p.CurlNoiseScale * 100, curlNoiseRise = p.CurlNoiseRise, curlNoiseDensity = p.CurlNoiseDensity,
                wind = Vec(p.Wind),
            },
            // Дым из пламени: доля карты против шара, зона, выброс, слой (FireGasFeedParams).
            feed = GasTuning?.Feed,
            emitter = new
            {
                radius = e.Radius, density = e.Density, temperature = e.Temperature, velocity = Vec(e.Velocity), entrainment = e.VelocityScale,
                velocityRate = e.VelocityRate, flicker = e.Flicker, flickerSteps = e.FlickerSteps, trailConservation = e.TrailConservation, sway = e.Sway,
                swaySteps = e.SwaySteps,
            },
            sims,
        };
        return (report, finite, bounded, inside);
    }

    /// <summary>Горизонтальное расстояние от <paramref name="from"/> по <paramref name="dir"/> (единичный, горизонтальный) до грани окна, м.</summary>
    private static float Room(Fluid3DWindow w, Vector3 from, Vector3 dir)
    {
        var lo = w.Min;
        var hi = w.Max;
        float t = float.PositiveInfinity;
        if (dir.X > 1e-6f) t = MathF.Min(t, (hi.X - from.X) / dir.X);
        else if (dir.X < -1e-6f) t = MathF.Min(t, (lo.X - from.X) / dir.X);
        if (dir.Z > 1e-6f) t = MathF.Min(t, (hi.Z - from.Z) / dir.Z);
        else if (dir.Z < -1e-6f) t = MathF.Min(t, (lo.Z - from.Z) / dir.Z);
        return t;
    }

    /// <summary>До ближайшей грани окна от точки, м (снаружи — отрицательно).</summary>
    private static float Margin(Fluid3DWindow w, Vector3 p)
    {
        var lo = p - w.Min;
        var hi = w.Max - p;
        return MathF.Min(MathF.Min(MathF.Min(lo.X, lo.Y), MathF.Min(lo.Z, hi.X)), MathF.Min(hi.Y, hi.Z));
    }

    private static float Horizontal(Vector3 d) => MathF.Sqrt(d.X * d.X + d.Z * d.Z);

    /// <summary>
    /// Раздел <c>fire.metrics</c>: размеры огня каждого экземпляра с шагами по полю на выходе (<see cref="FireMetrics"/>) — в осях площадки от
    /// центра шишки, см: пламя (k̄ ≥ порога при среднем шуме), ядро и его доля по высоте; столб дыма — по 3D-полю газа этого огня (τ вдоль мировой
    /// z ≥ порога; x — мировая x, y — вверх, от шишки) и его верх над пламенем.
    /// </summary>
    private object MetricsReport()
    {
        var t = FireMetricsParams.Default;
        return _sims.Where(s => s.FinalMetrics is not null).Select(s =>
        {
            var m = s.FinalMetrics!.Value with { Smoke = GasColumnOf(s.Slot.Index) };
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
                joint = s.FinalJoint is { } j ? Joint(j) : null,
                thresholds = new { noise = t.Noise, flameK = t.FlameK, coreK = t.CoreK, flameRho = m.FlameRho, coreRho = m.CoreRho, smokeTau = t.SmokeTau },
            };
        }).ToArray();

    }

    /// <summary>Столб дыма газа огня экземпляра <paramref name="fireSlot"/> (горящий раньше гаснущего); нет — пусто.</summary>
    private FireRegion GasColumnOf(int fireSlot)
    {
        FireRegion? fading = null;
        foreach (var g in _gasSims)
        {
            if (g.Slot.State == FireGasState.Free || g.Slot.FireSlot != fireSlot || g.FinalColumn is not { } column) continue;
            if (g.Slot.State == FireGasState.Emitting) return column;
            fading ??= column;
        }
        return fading ?? default;
    }

    /// <summary>Стык пламени и дыма (<see cref="FireJoint"/>): см и градусы в осях площадки, как на экране.</summary>
    private static object Joint(FireJoint j) => new
    {
        valid = j.Valid,
        flameTopCm = MathF.Round(j.FlameTopCm, 1),
        flameTopXCm = MathF.Round(j.FlameTopXCm, 1),
        flameAngleDeg = MathF.Round(j.FlameAngleDeg, 1),
        smokeStartCm = MathF.Round(j.SmokeStartCm, 1),
        gapCm = MathF.Round(j.GapCm, 1),
        offset10Cm = Finite(j.Offset10Cm),
        offset30Cm = Finite(j.Offset30Cm),
        smokeAngleDeg = Finite(j.SmokeAngleDeg),
        angleDifferenceDeg = Finite(j.AngleDifferenceDeg),
        tauNearAxis = j.TauNearAxis,
    };

    private static float? Finite(float x) => float.IsFinite(x) ? MathF.Round(x, 1) : null;

    private static object Region(FireRegion r) => new
    {
        texels = r.Texels, heightCm = MathF.Round(r.HeightCm, 1), widthCm = MathF.Round(r.WidthCm, 1), topCm = MathF.Round(r.TopCm, 1),
        bottomCm = MathF.Round(r.BottomCm, 1), leftCm = MathF.Round(r.LeftCm, 1), rightCm = MathF.Round(r.RightCm, 1),
        centroidCm = new[] { MathF.Round(r.CentroidCm.X, 1), MathF.Round(r.CentroidCm.Y, 1) },
    };

    /// <summary>Раздел <c>fire.flame</c>: рисуется ли пламя, площадок и их площадь на экране в последнем кадре, фаза, туман, числа формулы.</summary>
    private object FlameReport()
    {
        var p = FlameParams;
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
            depthFadeCm = p.DepthFadeDistance * 100, edgeMask = p.EdgeMask, glowCut = p.GlowCut,
        };
    }

    /// <summary>
    /// Раздел <c>fire.smoke</c>: рисуется ли дым, объёмов (окон газа; из них с детализирующим шумом — <c>detailed</c>) и источников рассеяния
    /// в последнем кадре, сеток занятости, построенных в нём, числа формулы, шума и единиц, солнце и небо, статистика марша по сетке лучей (шагов на луч в окне — средн./макс., выборок с σ &gt; 0 —
    /// средн./макс., покрытие — доля лучей с α ≥ 1/255, средняя и наибольшая α; у первого окна — непустых кирпичей из всех и их рамка).
    /// </summary>
    private object SmokeReport()
    {
        var p = SmokeParams;
        var s = _smoke?.Stats;
        float cell = GasTuning!.Params.CellSize;
        return new
        {
            shown = SmokeShown,
            state = _smoke is not null ? "ok" : "нет газа",
            overlay = SmokeOverlay!.Shown,
            volumes = _smoke?.LastVolumes ?? 0,
            detailed = _smoke?.LastDetailed ?? 0,
            lights = _smoke?.LastLights ?? 0,
            built = _smoke?.LastBuilt ?? 0,
            density = p.Density,
            unitsPerMetre = p.UnitsPerMetre,
            densityMult = p.DensityMult,
            densityPow = p.DensityPow,
            densityCut = p.DensityCut,
            stepCm = p.StepCells * cell * 100,
            maxSteps = p.MaxSteps,
            minTransmittance = p.MinTransmittance,
            skipEmpty = p.SkipEmpty,
            brickCells = SmokeMath.Brick,
            albedo = p.Albedo,
            ambient = new { mult = p.AmbientMult, pow = p.AmbientPow, aoPow = p.AmbientAoPow, skyColour = p.SkyColour },
            detail = new
            {
                amount = p.Detail, scaleCm = p.DetailScale * 100, flowSpeed = p.FlowSpeed, flowStrength = p.FlowStrength,
                minPixels = SmokeMath.MinDetailPixels,
            },
            sun = Vec(_smoke?.LastSun ?? default),
            sky = Vec(_smoke?.LastAmbient ?? default),
            stats = s is { } st ? new
            {
                frame = st.Frame, rays = st.Rays, hit = st.Hit, meanSteps = st.MeanSteps, maxSteps = st.MaxSteps, meanSamples = st.MeanSamples,
                maxSamples = st.MaxSamples, coverage = st.Coverage, meanAlpha = st.MeanAlpha, maxAlpha = st.MaxAlpha, bricks = st.Bricks,
                totalBricks = st.TotalBricks, brickMin = new[] { st.BrickMin.X, st.BrickMin.Y, st.BrickMin.Z },
                brickMax = new[] { st.BrickMax.X, st.BrickMax.Y, st.BrickMax.Z },
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
