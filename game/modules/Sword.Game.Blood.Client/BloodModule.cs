using System.Numerics;
using Sword.Engine.Client;
using Sword.Engine.Diagnostics;
using Sword.Engine.Fluid2D;
using Sword.Engine.Render;
using Sword.Game.Contracts;

namespace Sword.Game.Blood;

/// <summary>
/// Модуль Blood (<c>blood</c>, решение 20): кровь из ран по телу и на полу — только представление, вне симуляции, хеша и реплея; своей
/// сборки симуляции нет (Id объявлен симуляции как клиентский). Игру читает только через сервисы клиента: события боя тика
/// (<see cref="IBloodFeed"/>), позы бойцов тика (<see cref="IFighterPoses"/>), меш персонажа (<see cref="ISkinMesh"/>), рельеф
/// (<see cref="ITerrainView"/>), солнце (<see cref="ISceneSky"/>). Такт — по <c>TickId</c> наблюдателя тиков. Раны — якорь на коже по
/// попаданию в позе его тика (<see cref="BloodWounds"/>); потёки по коже — шаг 60 Гц по позам тика (<see cref="BloodDrips"/>). Оверлеи
/// <see cref="OverlayName"/>, <see cref="WoundsOverlayName"/>, <see cref="DripsOverlayName"/>, вкладка F3 «Кровь», разделы итога
/// <c>--json</c> — <c>blood.feed</c>, <c>blood.wounds</c>, <c>blood.drips</c>.
/// </summary>
public sealed class BloodModule : IClientModule
{
    public const string ModuleId = "blood";

    /// <summary>Оверлей: точки попаданий последнего кадра крестами (убившее — крупнее).</summary>
    public const string OverlayName = "blood";

    /// <summary>Оверлей ран: якоря на коже по позе последнего тика, линии ран; сильные — другим цветом.</summary>
    public const string WoundsOverlayName = "blood.wounds";

    /// <summary>Оверлей потёков: ломаные следов, головки, точки отрыва.</summary>
    public const string DripsOverlayName = "blood.drips";

    /// <summary>Цвета ран и потёков: рана — оранжевая, сильная — жёлтая, след — красный, головка — белая, отрыв — голубой.</summary>
    public const uint WoundColour = 0xFF0080FF, StrongColour = 0xFF00FFFF, TrailColour = 0xFF2020E0, HeadColour = 0xFFFFFFFF,
        DropColour = 0xFFFFC040;

    /// <summary>Шагов потёков в секунду (как огонь): при 120 тиках/с — каждый второй тик.</summary>
    public const int StepRate = 60;

    /// <summary>Цвета оверлея (<c>0xAABBGGRR</c>): попадание — красный, убившее — пурпурный, по трупу — тёмно-красный.</summary>
    public const uint HitColour = 0xFF2020FF, KillColour = 0xFFFF40FF, CorpseColour = 0xFF101090;

    /// <summary>Оверлей пола: рамка окна — зелёная, приземление — жёлтое.</summary>
    public const uint WindowColour = 0xFF40FF40, LandingColour = 0xFF00E0FF;

    /// <summary>Крест попадания на оверлее, м (полуразмер); убившего — вдвое больше.</summary>
    public const float Cross = 0.04f;

    private IBloodFeed? _feed;
    private IFighterPoses? _poses;
    private ISkinMesh? _mesh;
    private ITerrainView? _terrain;
    private ISceneSky? _sky;
    private ISkinBloodLayer? _layer;
    private ISceneFog? _fog;
    private BloodFloor? _floor;
    private BloodFloorGpu? _floorGpu;
    private SimRequests? _floorRequests;
    private BloodSkin? _skin;
    private BloodSkinGpu? _skinGpu;
    private readonly BloodOoze _ooze = new();
    private readonly Func<ulong, bool> _dead;
    private readonly Func<ulong, int> _srvOf;
    private float _clock;
    private long _steps;
    private float _tickDt = 1f / 120;
    private (Vector3 Point, uint Colour, float Size)[] _shown = [];
    private int _shownCount, _frameHits, _frameDeaths, _frameTicks;
    private ulong _lastTick;
    private int _terrainSrv = -1;
    private bool _ticked;
    private SkinSurface? _surface;
    private readonly BloodWounds _wounds = new();
    private BloodDrips? _drips;
    private int _tickRate = 120;
    private float _stepDt = 1f / StepRate;
    private long _stepTicks;
    private long _stepsTimed;

    public BloodModule()
    {
        _dead = fighter => _feed is { } feed && feed.TryBleed(fighter, out _, out bool dead) && dead;
        _srvOf = fighter => _skin is { } skin && _skinGpu is { } gpu ? gpu.Srv(skin.SlotOf(fighter)) : -1;
    }

    public string Id => ModuleId;

    /// <summary>Кровь на коже по вершинам (после загрузки меша).</summary>
    public BloodSkin? Skin => _skin;

    /// <summary>Кровь на коже на GPU (после инициализации с рендером).</summary>
    public BloodSkinGpu? SkinGpu => _skinGpu;

    /// <summary>Сочение сильных ран.</summary>
    public BloodOoze Ooze => _ooze;

    /// <summary>Часы крови, с: шагов × шаг (пауза — стоят); возраст крови на коже — от них.</summary>
    public float Clock => _clock;

    /// <summary>Радиус пятна у раны по урону, м.</summary>
    public static float SpotRadius(float damage, BloodTuning t) => Math.Clamp(t.SpotRadiusMin + damage * t.SpotRadiusPerDamage, t.SpotRadiusMin, t.SpotRadiusMax);

    /// <summary>Настройки после настройки модуля; null — модуль не настроен (выключен или не собран).</summary>
    public BloodSettings? Settings { get; private set; }

    public ClientOverlay? Overlay { get; private set; }

    public ClientOverlay? WoundsOverlay { get; private set; }

    public ClientOverlay? DripsOverlay { get; private set; }

    /// <summary>Ручки крови на теле (F3 «Кровь»).</summary>
    public BloodTuning Tuning { get; } = new();

    /// <summary>Кожа персонажа (после загрузки меша; без меша или с выключенной кровью на теле — null).</summary>
    public SkinSurface? Surface => _surface;

    /// <summary>Раны бойцов (якоря на коже).</summary>
    public BloodWounds Wounds => _wounds;

    /// <summary>Кровь на полу на CPU (капли, окно, штампы); пол выключен — null.</summary>
    public BloodFloor? Floor => _floor;

    /// <summary>Кровь на полу на GPU (сим окна, карта пятен, проходы); пол выключен — null.</summary>
    public BloodFloorGpu? FloorGpu => _floorGpu;

    /// <summary>Потёки по коже (после загрузки меша).</summary>
    public BloodDrips? Drips => _drips;

    /// <summary>Время последнего шага потёков, мс, и среднее по шагам с потёками.</summary>
    public double LastStepMs { get; private set; }
    public double MeanStepMs => _stepsTimed > 0 ? _stepTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / _stepsTimed : 0;

    /// <summary>Тиков, увиденных наблюдателем, и событий в них (попадания, смерти, постройки, сбросы арены) — с начала прогона.</summary>
    public long ObservedTicks { get; private set; }
    public long Hits { get; private set; }
    public long Deaths { get; private set; }
    public long Built { get; private set; }
    public long Resets { get; private set; }

    /// <summary>Тиков, попаданий и смертей в последнем кадре (кадр без тиков — нули).</summary>
    public int LastFrameTicks { get; private set; }
    public int LastFrameHits { get; private set; }
    public int LastFrameDeaths { get; private set; }

    /// <summary>Крестов, отданных отладочному рисованию в последнем кадре (0 — оверлей не показан).</summary>
    public int DrawnHits { get; private set; }

    public void ConfigureClient(ClientBuilder b)
    {
        var settings = Settings = b.Config<BloodSettings>();
        settings.Validate();
        _feed = b.Service<IBloodFeed>();
        _poses = b.Service<IFighterPoses>();
        _mesh = b.Service<ISkinMesh>();
        _terrain = b.Service<ITerrainView>();
        _sky = b.Service<ISceneSky>();
        _layer = b.Service<ISkinBloodLayer>();
        _fog = b.Service<ISceneFog>();
        if (_feed is null) Log.Warning(ModuleId, "событий боя нет (клиент игры не дал IBloodFeed) — крови не будет");
        _shown = new (Vector3, uint, float)[settings.ShownHits];
        _tickRate = Math.Max(1, (int)Math.Round(1.0 / b.TickDt));
        _tickDt = b.TickDt;
        _stepDt = _tickRate <= StepRate ? b.TickDt : 1f / StepRate;
        var overlay = Overlay = b.Overlay(OverlayName);
        var woundsOverlay = WoundsOverlay = b.Overlay(WoundsOverlayName);
        var dripsOverlay = DripsOverlay = b.Overlay(DripsOverlayName);
        b.AfterTick(Observe);
        b.Extract((in FrameView view) =>
        {
            EndFrame();
            _floorGpu?.SetDisplayTick((double)_lastTick - 1 + view.Alpha);   // double: без перехода через 0 у ulong и потери доли тика
        });
        ClientModuleContext? context = null;
        BloodFloorView? floorView = null;
        if (settings.Floor)
        {
            var floor = _floor = new BloodFloor(Tuning);
            floorView = new BloodFloorView { Tuning = Tuning, Sky = _sky, Fog = _fog, Terrain = _terrain };
            var gpu = _floorGpu = new BloodFloorGpu(floor, floorView);
            // Ядро Fluid2D может уже объявить огонь (имена контракта общие).
            b.Shaders(c =>
            {
                if (!c.Names.Contains("FLUID2D_STAMP_WORDS")) Fluid2DSim.Contribute(c);
            });
            b.Shaders(BloodFloorGpu.Contribute);
            _floorRequests = b.GpuSimulation(gpu);
            b.Pass(FramePhase.Opaque, frame => gpu.DrawDrops(frame, _tickDt));
            b.Pass(FramePhase.Composite, gpu.DrawFilm);
        }
        b.Initialize(c =>
        {
            context = c;
            if (floorView is not null) floorView.Camera = () => c.Camera;
            _terrainSrv = _terrain?.HeightsSrv ?? -1;   // клиент арены инициализирован раньше модулей
            EnsureSurface();
            // Кровь на коже на GPU — если кожа есть и клиент игры даёт слой; иначе кровь на коже только на CPU (тесты, без рендера).
            if (_skin is not null && _layer is not null)
            {
                _skinGpu = c.Own(new BloodSkinGpu(c.Renderer, _skin));
                _layer.Attach(_srvOf);
            }
        });
        if (settings.Body)
        {
            b.Shaders(BloodSkinGpu.Contribute);
            b.Pass(FramePhase.Simulation, frame => _skinGpu?.Build(frame, _clock, _steps, Tuning.Look));
        }
        b.Pass(FramePhase.Debug, frame =>
        {
            DrawnHits = 0;
            if (context is null || !(overlay.Shown || woundsOverlay.Shown || dripsOverlay.Shown)) return;
            Draw(frame, context, overlay.Shown, woundsOverlay.Shown, dripsOverlay.Shown);
        });
        var t = Tuning;
        b.Tuning(new TuningGroup(ModuleId, "Кровь")
            .Info("события", Status, "тиков наблюдателя, попаданий и смертей последнего кадра и всего, сбросов арены (R)")
            .Info("потёки", DripStatus, "ран сейчас, потёков живых, оторвалось капель, шаг потёков, мкс")
            .Float("volumePerDamage", "крови на урон, мл", () => t.VolumePerDamage, v => t.VolumePerDamage = v, 0f, 0.5f, format: "0.###")
            .Float("viscosity", "вязкость k, 1/с", () => t.Viscosity, v => t.Viscosity = v, 1f, 1000f, log: true, format: "0.#")
            .Float("detachVolume", "порог отрыва, мл", () => t.DetachVolume, v => t.DetachVolume = v, 0.01f, 5f, log: true, format: "0.###")
            .Float("detachAngle", "отрыв: угол от «вниз», °", () => t.DetachAngle, v => t.DetachAngle = v, 0f, 90f, format: "0")
            .Float("strongDamage", "сильная рана — урон от", () => t.StrongDamage, v => t.StrongDamage = v, 0f, 200f, format: "0")
            .Float("spotPerDamage", "пятно у раны: крови на урон, мл", () => t.SpotPerDamage, v => t.SpotPerDamage = v, 0f, 0.1f, format: "0.####")
            .Float("oozeRate", "сочение: темп на урон, мл/с", () => t.OozeRate, v => t.OozeRate = v, 0f, 0.05f, format: "0.####")
            .Float("oozeSeconds", "сочение гаснет за, с", () => t.OozeSeconds, v => t.OozeSeconds = v, 1f, 120f, format: "0")
            .Float("oozeCorpseShare", "сочение у трупа: доля темпа", () => t.OozeCorpseShare, v => t.OozeCorpseShare = v, 0f, 1f, format: "0.##")
            .Float("trailRadius", "след потёка на коже: радиус, м", () => t.TrailRadius, v => t.TrailRadius = v, 0f, 0.1f, format: "0.###")
            .Float("trailCost", "след потёка: мл на метр", () => t.TrailCost, v => t.TrailCost = v, 0.1f, 20f, log: true, format: "0.##")
            .Float("corpsePoolMl", "лужа трупа, мл", () => t.CorpsePoolMl, v => t.CorpsePoolMl = v, 0f, 1000f, format: "0")
            .Float("corpsePoolSeconds", "лужа трупа: τ, с", () => t.CorpsePoolSeconds, v => t.CorpsePoolSeconds = v, 0.1f, 60f, log: true, format: "0.#")
            .Info("кожа", SkinStatus, "слотов кожи занято, сочащихся ран, темп сочения мкл/с, выпущено мкл")
            .Float("glossStart", "блеск уходит с, с", () => t.Look.GlossStart, v => t.Look.GlossStart = v, 0f, 300f, format: "0")
            .Float("glossEnd", "блеск ушёл к, с", () => t.Look.GlossEnd, v => t.Look.GlossEnd = v, 1f, 600f, format: "0")
            .Float("darkStart", "темнеет с, с", () => t.Look.DarkStart, v => t.Look.DarkStart = v, 0f, 600f, format: "0")
            .Float("darkEnd", "засохла к, с", () => t.Look.DarkEnd, v => t.Look.DarkEnd = v, 1f, 1200f, format: "0")
            .Float("wetRoughness", "шероховатость свежей", () => t.Look.WetRoughness, v => t.Look.WetRoughness = v, 0.03f, 1f, format: "0.##")
            .Float("dryRoughness", "шероховатость засохшей", () => t.Look.DryRoughness, v => t.Look.DryRoughness = v, 0.03f, 1f, format: "0.##")
            .Float("thickMm", "густая с толщины, мм", () => t.Look.ThickMm, v => t.Look.ThickMm = v, 0.01f, 5f, log: true, format: "0.###")
            .Float("edgeMm", "мягкий край плёнки, мм", () => t.Look.EdgeMm, v => t.Look.EdgeMm = v, 0.001f, 1f, log: true, format: "0.####")
            .Float("absorbR", "поглощение красного, 1/мм", () => t.Look.Absorb.X, v => t.Look.Absorb.X = v, 0.1f, 100f, log: true, format: "0.##")
            .Float("absorbG", "поглощение зелёного, 1/мм", () => t.Look.Absorb.Y, v => t.Look.Absorb.Y = v, 1f, 500f, log: true, format: "0.#")
            .Float("absorbB", "поглощение синего, 1/мм", () => t.Look.Absorb.Z, v => t.Look.Absorb.Z = v, 1f, 500f, log: true, format: "0.#")
            .Float("dryAbsorb", "засохшая: поглощение +, 1/мм", () => t.Look.DryAbsorb, v => t.Look.DryAbsorb = v, 0f, 200f, format: "0.#")
            .Info("пол", FloorStatus, "капель в полёте, приземлилось и в окно, мкл, шагов пола, сдвигов окна")
            .Button("лужа под игроком", () => _floor?.RequestPuddle(), "штамп отладочной лужи у ног игрока (подгонка вида)")
            .Bool("puddle", "лужа под игроком (флаг)", () => _floor?.PuddlePending ?? false, v => { if (v) _floor?.RequestPuddle(); }, defaultValue: false)
            .Float("puddleMl", "отладочная лужа, мл", () => t.PuddleMl, v => t.PuddleMl = v, 1f, 2000f, log: true, format: "0")
            .Float("dropDrag", "капля: сопротивление k, 1/с", () => t.DropDrag, v => t.DropDrag = v, 0.01f, 50f, log: true, format: "0.##")
            .Float("dropSpread", "пятно капли: размер ×", () => t.DropSpread, v => t.DropSpread = v, 0.25f, 8f, format: "0.##")
            .Float("dropRadius", "капля в полёте: радиус ×", () => t.DropRadiusScale, v => t.DropRadiusScale = v, 0.25f, 8f, format: "0.##")
            .Float("floorNormal", "плёнка: усиление нормали", () => t.FloorNormalScale, v => t.FloorNormalScale = v, 0f, 50f, format: "0.#")
            .Float("floorMeniscus", "плёнка: мениск до толщины, мм", () => t.FloorMeniscusMm, v => t.FloorMeniscusMm = v, 0.01f, 10f, log: true, format: "0.##")
            .Float("floorF0", "плёнка: F0 блика", () => t.FloorF0, v => t.FloorF0 = v, 0f, 0.2f, format: "0.###")
            .Float("floorSky", "плёнка: сила неба", () => t.FloorSkyGain, v => t.FloorSkyGain = v, 0f, 4f, format: "0.##")
            .Float("floorWet", "мокрый пол: множитель", () => t.FloorWetDarken, v => t.FloorWetDarken = v, 0.1f, 1f, format: "0.##")
            .Float("floorHalo", "мокрый пол: ореол, текселей", () => t.FloorHalo, v => t.FloorHalo = v, 0f, 8f, format: "0.#")
            .Float("floorRipple", "плёнка: рябь, уклон", () => t.FloorRipple, v => t.FloorRipple = v, 0f, 0.3f, format: "0.###")
            .Float("floorRippleSize", "плёнка: рябь, размер, м", () => t.FloorRippleSize, v => t.FloorRippleSize = v, 0.005f, 0.5f, log: true, format: "0.###")
            .Float("floorNoise", "плёнка: неровность толщины ±", () => t.FloorNoise, v => t.FloorNoise = v, 0f, 1f, format: "0.##")
            .Float("floorNoiseSize", "плёнка: неровность, размер, м", () => t.FloorNoiseSize, v => t.FloorNoiseSize = v, 0.01f, 1f, log: true, format: "0.###")
            .Float("floorErode", "плёнка: рваный край, мм", () => t.FloorErodeMm, v => t.FloorErodeMm = v, 0f, 1f, format: "0.###")
            .Float("floorDryAge", "засохшее — в карту с возраста, с", () => t.FloorDryAge, v => t.FloorDryAge = v, 10f, 3600f, log: true, format: "0"));
        b.Report(Report);
    }

    /// <summary>Наблюдатель тиков: события ровно этого тика (сервис отдаёт последний сыгранный — он и есть этот).</summary>
    private void Observe(ulong tick)
    {
        _lastTick = tick;
        _ticked = true;
        ObservedTicks++;
        if (++_frameTicks == 1)
        {
            _shownCount = 0;   // первый тик кадра: оверлей — попадания этого кадра
            _floor?.BeginFrame();
        }
        if (_feed is not { } feed) return;
        var hits = feed.Hits;
        _frameHits += hits.Length;
        Hits += hits.Length;
        for (int i = 0; i < hits.Length; i++) Remember(hits[i]);
        int deaths = feed.Deaths.Length;
        _frameDeaths += deaths;
        Deaths += deaths;
        Built += feed.Built.Length;
        if (feed.ArenaReset) Resets++;
        if (_poses is not { } poses) return;
        if (ObservedTicks == 1) _ = poses.Count;   // первый снимок поз (буферы сервиса) — в служебном кадре первого тика
        if (feed.ArenaReset) _floor?.Reset();
        foreach (ulong f in feed.Deaths) RememberDeath(f, tick, poses);
        if (Settings!.Body && (_surface is not null || EnsureSurface()))
        {
            if (feed.ArenaReset)
            {
                _wounds.ClearAll();
                _drips!.ClearAll();
                _skin?.ReleaseAll();
                _ooze.ClearAll();
            }
            Forget(feed.Removed);
            Forget(feed.Built);
            for (int i = 0; i < hits.Length; i++) Wound(hits[i], poses);
            if (IsStep(tick)) StepDrips(tick, poses);
        }
        if (_floor is not null && IsStep(tick)) StepFloor(tick, poses);
    }

    /// <summary>Место смерти (таз тика) для итога: первые <see cref="DeathPoints"/>.</summary>
    private void RememberDeath(ulong fighter, ulong tick, IFighterPoses poses)
    {
        if (_deathCount >= _deathsAt.Length) return;
        int index = poses.Find(fighter);
        if (index < 0) return;
        var segments = poses.Segments(index);
        if (segments.Length <= BodySegs.Pelvis) return;
        var p = segments[BodySegs.Pelvis].Position;
        _deathsAt[_deathCount++] = (tick, p.X, p.Z);
    }

    /// <summary>Мест смерти в итоге не больше.</summary>
    public const int DeathPoints = 16;

    private readonly (ulong Tick, float X, float Z)[] _deathsAt = new (ulong, float, float)[DeathPoints];
    private int _deathCount;

    /// <summary>Шаг пола: отрывы шага потёков — в полёт (приземление по рельефу), окно у игрока, заявка шага сима.</summary>
    private void StepFloor(ulong tick, IFighterPoses poses)
    {
        var floor = _floor!;
        if (_drips is { } drips)
        {
            floor.Detach(drips.Drops, _terrain, Tuning.DropDrag, _tickDt);
            drips.TakeDrops();
        }
        floor.Step(tick, StepOf(tick), PlayerXz(poses), Tuning.DropSpread, Tuning.PuddleMl, _floorRequests);
    }

    /// <summary>Игрок (x, z) по телу таза тика; нет — null.</summary>
    private static Vector2? PlayerXz(IFighterPoses poses)
    {
        for (int i = 0; i < poses.Count; i++)
        {
            if (!poses.Fighter(i).IsPlayer) continue;
            var segments = poses.Segments(i);
            if (segments.Length <= BodySegs.Pelvis) return null;
            var p = segments[BodySegs.Pelvis].Position;
            return new Vector2(p.X, p.Z);
        }
        return null;
    }

    /// <summary>Кожа и пулы потёков — при первой возможности (меш грузится вместе с рендером клиента арены).</summary>
    private bool EnsureSurface()
    {
        if (_surface is not null) return true;
        if (Settings is not { Body: true } settings || _mesh is not { Loaded: true } mesh || _poses is null) return false;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        _surface = new SkinSurface(mesh);
        _drips = new BloodDrips(_surface, Tuning, settings.DripsPerFighter, settings.DripsWorld);
        _skin = new BloodSkin(_surface, settings.SkinSlots);
        Log.Info(ModuleId, $"кожа: треугольников {_surface.TriangleCount}, вершин {_surface.VertexCount} (сварено {_surface.WeldCount}), " +
            $"рёбер без пары {_surface.OpenEdges}, {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} мс");
        return true;
    }

    /// <summary>Шаг потёков на тике <paramref name="tick"/>: ⌊tick·60/T⌋ сменился (при T ≤ 60 — каждый тик).</summary>
    private bool IsStep(ulong tick) => tick == 0 || StepOf(tick) != StepOf(tick - 1);

    private ulong StepOf(ulong tick) => _tickRate <= StepRate ? tick : tick * StepRate / (ulong)_tickRate;

    private void Forget(ReadOnlySpan<ulong> fighters)
    {
        foreach (ulong f in fighters)
        {
            _wounds.Clear(f);
            _drips!.Clear(f);
            _skin?.Release(f);
        }
    }

    /// <summary>Рана по попаданию в позе его тика и выброс потёков.</summary>
    private void Wound(in BloodHit hit, IFighterPoses poses)
    {
        int index = poses.Find(hit.Fighter);
        if (index < 0) return;
        int wound = _wounds.Record(hit, poses.Joints(index), _surface!, Tuning, out int row);
        if (wound < 0) return;
        ref readonly var w = ref _wounds.At(row, wound);
        _drips!.SpawnWound(w, wound);
        _skin?.Spot(w.Fighter, w.Triangle, w.BindPoint, w.BindNormal, SpotRadius(w.Damage, Tuning), w.Damage * Tuning.SpotPerDamage, _clock);
    }

    private void StepDrips(ulong tick, IFighterPoses poses)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        bool any = _drips!.Alive > 0;
        _clock += _stepDt;
        _steps++;
        _drips.Step(tick, _stepDt, poses);
        if (_skin is { } skin) StampSkin(skin, _drips.Stamps);
        _ooze.Step(tick, _tickDt, _clock, _wounds, Tuning, _dead, _skin, _drips);
        if (_floor is { } floor && _ooze.Pours.Length > 0) Pour(floor, tick, poses);
        long spent = System.Diagnostics.Stopwatch.GetTimestamp() - started;
        LastStepMs = spent * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (!any) return;
        _stepTicks += spent;
        _stepsTimed++;
    }

    /// <summary>
    /// Капли лужи трупа шага: из точки раны на коже позы тика, на 1 см наружу, со скоростью 0.1 м/с по нормали и разбросом ±5 см/с из хеша
    /// (зерно раны, номер капли) — в полёт к полу.
    /// </summary>
    private void Pour(BloodFloor floor, ulong tick, IFighterPoses poses)
    {
        var pours = _ooze.Pours;
        if (_pourDrops.Length < pours.Length) _pourDrops = new BloodDrop[Math.Max(2 * _pourDrops.Length, pours.Length)];
        int count = 0;
        for (int i = 0; i < pours.Length; i++)
            if (PourDrop(pours[i], tick, poses, out var drop)) _pourDrops[count++] = drop;
        floor.Detach(_pourDrops.AsSpan(0, count), _terrain, Tuning.DropDrag, _tickDt);
    }

    private BloodDrop[] _pourDrops = new BloodDrop[16];

    private bool PourDrop(in BloodPour pour, ulong tick, IFighterPoses poses, out BloodDrop drop)
    {
        drop = default;
        int index = poses.Find(pour.Fighter);
        if (index < 0) return false;
        var joints = poses.Joints(index);
        if (joints.IsEmpty) return false;
        ref readonly var w = ref _wounds.At(pour.Row, pour.Wound);
        var s = _surface!;
        s.SkinTriangle(w.Triangle, joints, out var w0, out var w1, out var w2);
        var c = Vector3.Cross(w1 - w0, w2 - w0);
        float l = c.Length();
        var n = l > 1e-12f ? c * (s.FaceSign(w.Triangle) / l) : Vector3.UnitY;
        uint seed = BloodHash.Combine(w.Seed, 0x700u + pour.Salt);
        var jitter = new Vector3(BloodHash.Unit(seed, 0) - 0.5f, 0, BloodHash.Unit(seed, 1) - 0.5f) * 0.1f;
        var p = w.Bary.X * w0 + w.Bary.Y * w1 + w.Bary.Z * w2 + n * 0.01f;
        drop = new BloodDrop(pour.Fighter, tick, p, n * 0.1f + jitter, pour.Volume);
        return true;
    }

    /// <summary>Следы потёков шага — в кровь на коже.</summary>
    private void StampSkin(BloodSkin skin, ReadOnlySpan<DripStamp> stamps)
    {
        float radius = Tuning.TrailRadius;
        for (int i = 0; i < stamps.Length; i++) skin.Stamp(stamps[i].Fighter, stamps[i].Triangle, stamps[i].Bary, stamps[i].Volume, _clock, radius);
    }

    /// <summary>Точка попадания для оверлея кадра.</summary>
    private void Remember(in BloodHit hit)
    {
        if (_shownCount >= _shown.Length) return;
        uint colour = hit.Killed ? KillColour : hit.Corpse ? CorpseColour : HitColour;
        _shown[_shownCount++] = (hit.Point, colour, hit.Killed ? 2 * Cross : Cross);
    }

    /// <summary>Конец тиков кадра: счёт кадра — в «последний кадр» (кадр без тиков — пауза: оверлей прежний).</summary>
    private void EndFrame()
    {
        LastFrameTicks = _frameTicks;
        LastFrameHits = _frameHits;
        LastFrameDeaths = _frameDeaths;
        _frameTicks = _frameHits = _frameDeaths = 0;
    }

    private void Draw(RenderFrame frame, ClientModuleContext context, bool hits, bool wounds, bool drips)
    {
        var debug = context.Renderer.Debug;
        var camera = context.Camera;
        var mark = debug.Mark();
        if (hits)
        {
            for (int i = 0; i < _shownCount; i++) DrawCross(debug, _shown[i].Point, _shown[i].Colour, _shown[i].Size);
            DrawnHits = _shownCount;
            if (_floor is { HasWindow: true } floor) DrawFloor(debug, floor);
        }
        if (_surface is not null && _poses is { } poses)
        {
            if (wounds)
                for (int row = 0; row < _wounds.Capacity; row++) DrawWounds(debug, poses, row);
            if (drips && _drips is { } d)
            {
                for (int i = 0; i < d.Capacity; i++) DrawDrip(debug, poses, d, i);
                foreach (var p in d.RecentDropPoints) DrawCross(debug, p, DropColour, 0.015f);
            }
        }
        debug.AddPass(frame, frame.Graph.BackBuffer, camera.ViewProj, camera.Eye, "debug.blood", since: mark);
    }

    /// <summary>Рамка окна пола по рельефу (отрезки по 0.5 м) и точки последних приземлений.</summary>
    private void DrawFloor(DebugDraw debug, BloodFloor floor)
    {
        var c = floor.Center;
        float h = 0.5f * BloodFloor.Size;
        for (int side = 0; side < 4; side++)
            for (int k = 0; k < 64; k++) DrawFrameSegment(debug, c, h, side, k);
        foreach (var p in floor.RecentLandingPoints) DrawCross(debug, p + new Vector3(0, 0.01f, 0), LandingColour, 0.03f);
    }

    private static Vector2 Corner(int i, Vector2 c, float h) => i switch
    {
        0 => c + new Vector2(-h, -h),
        1 => c + new Vector2(h, -h),
        2 => c + new Vector2(h, h),
        _ => c + new Vector2(-h, h),
    };

    private void DrawFrameSegment(DebugDraw debug, Vector2 c, float h, int side, int k)
    {
        var a = Corner(side, c, h);
        var b = Corner((side + 1) % 4, c, h);
        var p0 = Vector2.Lerp(a, b, k / 64f);
        var p1 = Vector2.Lerp(a, b, (k + 1) / 64f);
        float y0 = (_terrain?.Height(p0.X, p0.Y) ?? 0) + 0.05f, y1 = (_terrain?.Height(p1.X, p1.Y) ?? 0) + 0.05f;
        debug.Line(new Vector3(p0.X, y0, p0.Y), new Vector3(p1.X, y1, p1.Y), WindowColour);
    }

    private void DrawWounds(DebugDraw debug, IFighterPoses poses, int row)
    {
        if (!_wounds.Used(row)) return;
        int index = poses.Find(_wounds.Key(row));
        if (index < 0) return;
        var joints = poses.Joints(index);
        if (joints.IsEmpty) return;
        var s = _surface!;
        foreach (ref readonly var w in _wounds.Of(row))
        {
            s.SkinTriangle(w.Triangle, joints, out var w0, out var w1, out var w2);
            var p = w.Bary.X * w0 + w.Bary.Y * w1 + w.Bary.Z * w2;
            var line = BindToWorld(s, w.Triangle, w.BindLine, w0, w1, w2);
            float half = Math.Clamp(0.01f + w.Damage * 0.0006f, 0.01f, 0.05f);
            uint colour = w.Strong ? StrongColour : WoundColour;
            debug.Line(p - line * half, p + line * half, colour);
            DrawCross(debug, p, colour, 0.008f);
        }
    }

    /// <summary>Касательный вектор привязки в треугольнике → мир (через рёбра треугольника), единичный.</summary>
    private static Vector3 BindToWorld(SkinSurface s, int t, Vector3 bind, Vector3 w0, Vector3 w1, Vector3 w2)
    {
        var b0 = s.BindPosition(s.Vertex(t, 0));
        var e1 = s.BindPosition(s.Vertex(t, 1)) - b0;
        var e2 = s.BindPosition(s.Vertex(t, 2)) - b0;
        float g11 = Vector3.Dot(e1, e1), g12 = Vector3.Dot(e1, e2), g22 = Vector3.Dot(e2, e2), det = g11 * g22 - g12 * g12;
        if (!(det > 1e-20f)) return Vector3.Zero;
        float r1 = Vector3.Dot(e1, bind), r2 = Vector3.Dot(e2, bind);
        var world = (g22 * r1 - g12 * r2) / det * (w1 - w0) + (g11 * r2 - g12 * r1) / det * (w2 - w0);
        return world.LengthSquared() > 1e-12f ? Vector3.Normalize(world) : Vector3.Zero;
    }

    private void DrawDrip(DebugDraw debug, IFighterPoses poses, BloodDrips d, int i)
    {
        if (!d.IsAlive(i)) return;
        int index = poses.Find(d.FighterOf(i));
        if (index < 0) return;
        var joints = poses.Joints(index);
        if (joints.IsEmpty) return;
        var s = _surface!;
        Span<DripPoint> trail = stackalloc DripPoint[BloodDrips.TrailCap];
        int n = d.Trail(i, trail);
        var prev = Vector3.Zero;
        for (int k = 0; k < n; k++)
        {
            var p = s.SkinPoint(trail[k].Triangle, trail[k].Bary, joints);
            if (k > 0) debug.Line(prev, p, TrailColour);
            prev = p;
        }
        var head = d.Head(i);
        var h = s.SkinPoint(head.Triangle, head.Bary, joints);
        if (n > 0) debug.Line(prev, h, TrailColour);
        DrawCross(debug, h, HeadColour, 0.004f + 0.01f * MathF.Min(1, d.Volume(i)));
    }

    private static void DrawCross(DebugDraw debug, Vector3 p, uint colour, float s)
    {
        debug.Line(p - new Vector3(s, 0, 0), p + new Vector3(s, 0, 0), colour);
        debug.Line(p - new Vector3(0, s, 0), p + new Vector3(0, s, 0), colour);
        debug.Line(p - new Vector3(0, 0, s), p + new Vector3(0, 0, s), colour);
    }

    /// <summary>Строка F3 — на стек: числа — <c>TryFormat</c> (кадр панели без выделений).</summary>
    private int Status(Span<char> d)
    {
        int n = Write(d, 0, "тик ");
        n = Number(d, n, (long)_lastTick);
        n = Write(d, n, ", кадр: попаданий ");
        n = Number(d, n, LastFrameHits);
        n = Write(d, n, ", смертей ");
        n = Number(d, n, LastFrameDeaths);
        n = Write(d, n, "; всего: попаданий ");
        n = Number(d, n, Hits);
        n = Write(d, n, ", смертей ");
        n = Number(d, n, Deaths);
        n = Write(d, n, ", сбросов ");
        return Number(d, n, Resets);
    }

    private int DripStatus(Span<char> d)
    {
        int n = Write(d, 0, "ран ");
        n = Number(d, n, _wounds.Count);
        n = Write(d, n, ", потёков ");
        n = Number(d, n, _drips?.Alive ?? 0);
        n = Write(d, n, ", капель ");
        n = Number(d, n, _drips?.Detached ?? 0);
        n = Write(d, n, ", шаг мкс ");
        return Number(d, n, (long)(LastStepMs * 1000));
    }

    private int FloorStatus(Span<char> d)
    {
        var f = _floor;
        int n = Write(d, 0, "в полёте ");
        n = Number(d, n, f?.Flying.Length ?? 0);
        n = Write(d, n, ", приземлилось мкл ");
        n = Number(d, n, (long)((f?.LandedMl ?? 0) * 1000));
        n = Write(d, n, ", в окно мкл ");
        n = Number(d, n, (long)((f?.StampedMl ?? 0) * 1000));
        n = Write(d, n, ", шагов ");
        n = Number(d, n, f?.Steps ?? 0);
        n = Write(d, n, ", сдвигов ");
        return Number(d, n, f?.Shifts ?? 0);
    }

    private int SkinStatus(Span<char> d)
    {
        int n = Write(d, 0, "слотов ");
        n = Number(d, n, _skin?.UsedSlots ?? 0);
        n = Write(d, n, ", сочится ");
        n = Number(d, n, _ooze.Active);
        n = Write(d, n, ", темп мкл/с ");
        n = Number(d, n, (long)(_ooze.Rate * 1000));
        n = Write(d, n, ", выпущено мкл ");
        return Number(d, n, (long)(_ooze.Released * 1000));
    }

    private static int Write(Span<char> d, int n, string text)
    {
        int k = Math.Min(text.Length, d.Length - n);
        text.AsSpan(0, k).CopyTo(d[n..]);
        return n + k;
    }

    private static int Number(Span<char> d, int n, long value) =>
        value.TryFormat(d[n..], out int w, provider: System.Globalization.CultureInfo.InvariantCulture) ? n + w : n;

    private object Report() => new
    {
        feed = new
        {
            ticks = ObservedTicks,
            lastTick = _ticked ? _lastTick : (ulong?)null,
            hits = Hits,
            deaths = Deaths,
            built = Built,
            resets = Resets,
            lastFrame = new { ticks = LastFrameTicks, hits = LastFrameHits, deaths = LastFrameDeaths },
            deathsAt = _deathsAt.Take(_deathCount).Select(d => new { tick = d.Tick, x = Math.Round(d.X, 2), z = Math.Round(d.Z, 2) }).ToArray(),
            // Позы — снимок последнего тика при чтении итога (в кадре скелет их не снимает).
            posedFighters = _poses?.Count ?? 0,
        },
        services = new
        {
            feed = _feed is not null,
            poses = _poses is not null,
            skinMesh = _mesh is not null,
            terrain = _terrain is not null,
            terrainSrv = _terrainSrv >= 0,
            sunDirection = _sky is { Sky.SunDirection: var d } && d != Vector3.Zero,
        },
        settings = new { body = Settings!.Body, floor = Settings.Floor },
        wounds = WoundsReport(),
        drips = _drips is { } dr ? new
        {
            alive = dr.Alive,
            spawned = dr.Spawned,
            ended = dr.Ended,
            evicted = dr.Evicted,
            detached = dr.Detached,
            detachedVolume = Math.Round(dr.DetachedVolume, 4),
            queuedDrops = dr.QueuedDrops,
            aliveVolume = Math.Round(dr.AliveVolume, 4),
            steps = dr.Steps,
            lastStepMs = Math.Round(LastStepMs, 4),
            meanStepMs = Math.Round(MeanStepMs, 4),
        } : null,
        skin = _skin is { } sk ? new
        {
            slots = sk.SlotCount,
            used = sk.UsedSlots,
            slotMisses = sk.SlotMisses,
            depositedMl = Math.Round(sk.Deposited, 4),
            clock = Math.Round(_clock, 3),
            gpu = _skinGpu is not null,
            gpuMb = _skinGpu is { } g ? Math.Round(g.Bytes / 1048576.0, 1) : 0,
            lastLooks = _skinGpu?.LastLooks ?? 0,
            lastUploads = _skinGpu?.LastUploads ?? 0,
            layer = _layer is not null,
        } : null,
        floor = FloorReport(),
        surface = _surface is { } s ? new { triangles = s.TriangleCount, vertices = s.VertexCount, welded = s.WeldCount, openEdges = s.OpenEdges } : null,
    };

    private object? FloorReport()
    {
        if (_floor is not { } f) return null;
        var g = _floorGpu;
        var stats = g?.Stats;
        return new
        {
            window = f.HasWindow ? new { x = Math.Round(f.Center.X, 3), z = Math.Round(f.Center.Y, 3) } : null,
            steps = f.Steps,
            gpuSteps = g?.GpuSteps ?? 0,
            shifts = f.Shifts,
            transfers = g?.Transfers ?? 0,
            resets = g?.Resets ?? 0,
            flying = f.Flying.Length,
            detached = f.Detached,
            detachedMl = Math.Round(f.DetachedMl, 4),
            landed = f.Landed,
            landedMl = Math.Round(f.LandedMl, 4),
            stampedMl = Math.Round(f.StampedMl, 4),
            outsideMl = Math.Round(f.OutsideMl, 4),
            offMapMl = Math.Round(f.OffMapMl, 4),
            pouredMl = Math.Round(_ooze.Poured, 4),
            dried = g?.Dries ?? 0,
            lost = f.Lost,
            drawnDrops = g?.DrawnDrops ?? 0,
            sumRho = stats is { } st ? Math.Round(st.DensitySum, 4) : (double?)null,
            windowMl = g?.FinalWindowMl is { } w ? Math.Round(w, 3) : (double?)null,
            mapMl = g?.FinalMapMl is { } m ? Math.Round(m, 3) : (double?)null,
            areaM2 = g?.FinalAreaM2 is { } a ? Math.Round(a, 4) : (double?)null,
            fieldHash = g?.FieldHash is { } h ? h.ToString("X16") : null,
        };
    }

    private object WoundsReport()
    {
        var items = new List<object>();
        var gaps = new List<float>();
        int strong = 0;
        for (int row = 0; row < _wounds.Capacity; row++)
        {
            var list = _wounds.Of(row);
            for (int index = 0; index < list.Length; index++)
            {
                var w = list[index];
                gaps.Add(w.Gap);
                if (w.Strong) strong++;
                if (items.Count < 32)
                    items.Add(new
                    {
                        fighter = w.Fighter, tick = w.Tick, segment = BodySegs.Name(w.Segment), triangle = w.Triangle, damage = w.Damage, strong = w.Strong,
                        probe = w.Probe, corpse = w.Corpse, gapCm = Math.Round(w.Gap * 100, 2),
                        oozeRate = Math.Round(_ooze.RateOf(_wounds, row, index), 5), releasedMl = Math.Round(_ooze.ReleasedBy(_wounds, row, index), 4),
                    });
            }
        }
        gaps.Sort();
        return new
        {
            count = gaps.Count,
            strong,
            recorded = _wounds.Recorded,
            missed = _wounds.Missed,
            gapMedianCm = gaps.Count > 0 ? Math.Round(gaps[gaps.Count / 2] * 100, 2) : (double?)null,
            gapMaxCm = gaps.Count > 0 ? Math.Round(gaps[^1] * 100, 2) : (double?)null,
            ooze = new { active = _ooze.Active, rate = Math.Round(_ooze.Rate, 5), releasedMl = Math.Round(_ooze.Released, 4), feeds = _ooze.Feeds },
            items,
        };
    }
}
