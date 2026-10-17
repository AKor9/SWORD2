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
    private IWeaponTips? _tips;
    private IWeaponBloodLayer? _weaponLayer;
    private ISeverFeed? _sever;
    private IPiecePoses? _pieces;
    private BloodStumps? _stumps;
    private (ulong Fighter, int Segment)[] _severed = new (ulong, int)[8];
    private int _severedCount;
    private long _stumpTicks, _stumpMax;
    private ulong _stumpMaxTick;
    private BloodBlade? _bladeBlood;
    private BloodBladeGpu? _bladeGpu;
    private WeaponTip[] _tipBuf = new WeaponTip[64];
    private int _tipCount;
    private ulong _tipsTick = ulong.MaxValue;
    private readonly Func<ulong, int, int> _bladeSrvOf;
    private BloodFloor? _floor;
    private BloodFloorGpu? _floorGpu;
    private SimRequests? _floorRequests;
    private BloodSkin? _skin;
    private BloodSkinGpu? _skinGpu;
    private readonly BloodOoze _ooze = new();
    private readonly BloodSpray _spray = new();
    private BloodBladeContact[] _blade = new BloodBladeContact[16];
    private int _bladeCount;
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
        _bladeSrvOf = BladeSrv;
    }

    /// <summary>SRV крови лезвия оружия <paramref name="weapon"/> бойца для кадра; крови нет — −1.</summary>
    private int BladeSrv(ulong fighter, int weapon)
    {
        if (_bladeBlood is not { } blade || _bladeGpu is not { } gpu) return -1;
        int slot = blade.SlotOf(fighter);
        return slot >= 0 && blade.Weapon(slot) == weapon ? gpu.Srv(slot, _clock, _steps, Tuning.Look, Tuning.BladeCoverMm) : -1;
    }

    /// <summary>Кровь на клинках (сервисы оружия есть и <c>blood.blade</c>); иначе null.</summary>
    public BloodBlade? BladeBlood => _bladeBlood;

    /// <summary>Кровь на клинках для шейдера оружия (после инициализации с рендером).</summary>
    public BloodBladeGpu? BladeGpu => _bladeGpu;

    public string Id => ModuleId;

    /// <summary>Кровь на коже по вершинам (после загрузки меша).</summary>
    public BloodSkin? Skin => _skin;

    /// <summary>Кровь на коже на GPU (после инициализации с рендером).</summary>
    public BloodSkinGpu? SkinGpu => _skinGpu;

    /// <summary>Сочение сильных ран.</summary>
    public BloodOoze Ooze => _ooze;

    /// <summary>Кровь из обрубков (есть сервис отделений и пол); иначе null.</summary>
    public BloodStumps? Stumps => _stumps;

    /// <summary>Брызги из раны при ударе (счёт вылетевшего).</summary>
    public BloodSpray Spray => _spray;

    /// <summary>
    /// Касания клинков последнего тика (наблюдатель тиков): по одному на попадание — кто бил, жертва, точка, место на клинке (система тела
    /// клинка атакующего в позе тика, доля от хвата к кончику). Для крови на оружии.
    /// </summary>
    public ReadOnlySpan<BloodBladeContact> BladeContacts => _blade.AsSpan(0, _bladeCount);

    /// <summary>Объём брызг, легших на кожу бойцов, мл, и капель без кожи под касанием (нет слота, сегмент без кожи).</summary>
    public double SprayOnSkinMl { get; private set; }
    public long SprayMissedSkin { get; private set; }

    /// <summary>Часы крови, с: шагов × шаг (пауза — стоят); возраст крови на коже — от них.</summary>
    public float Clock => _clock;

    /// <summary>
    /// Часы после <paramref name="steps"/> шагов по <paramref name="stepDt"/> с — произведением в double, а не суммой float: сумма теряет шаг
    /// (шаг 1/60 с: с ~5 ч часы шли на 5–6 % вбок, с ~73 ч — почти вдвое быстрее, с ~6 суток шаг меньше полушага float — часы
    /// вставали и кровь переставала сохнуть).
    /// </summary>
    public static float ClockAt(long steps, float stepDt) => (float)(steps * (double)stepDt);

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
        _tips = b.Service<IWeaponTips>();
        _weaponLayer = b.Service<IWeaponBloodLayer>();
        _sever = b.Service<ISeverFeed>();
        _pieces = b.Service<IPiecePoses>();
        if (settings.Blade && _tips is not null && _weaponLayer is not null) _bladeBlood = new BloodBlade(settings.BladeSlots, Tuning);
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
            _bladeGpu?.BeginFrame();
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
            if (_bladeBlood is not null && _weaponLayer is not null)
            {
                _bladeGpu = c.Own(new BloodBladeGpu(c.Renderer, _bladeBlood));
                _weaponLayer.Attach(_bladeSrvOf);
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
            .Float("volumePerDamage", "крови на урон, мл", () => t.VolumePerDamage, v => t.VolumePerDamage = v, 0f, 5f, format: "0.###")
            .Float("sprayPerDamage", "брызги: крови на урон, мл", () => t.SprayPerDamage, v => t.SprayPerDamage = v, 0f, 1f, format: "0.###")
            .Float("sprayMedianMm", "брызги: медиана капли, мм", () => t.SprayMedianMm, v => t.SprayMedianMm = v, 0.5f, 6f, format: "0.#")
            .Float("sprayFan", "брызги: полуугол веера, рад", () => t.SprayFan, v => t.SprayFan = v, 0f, 1.5f, format: "0.##")
            .Float("sprayOutward", "брызги: наружу по нормали", () => t.SprayOutward, v => t.SprayOutward = v, 0f, 2f, format: "0.##")
            .Float("spraySpeedMax", "брызги: скорость до, × кончика", () => t.SpraySpeedMax, v => t.SpraySpeedMax = v, 0.05f, 1.5f, format: "0.##")
            .Float("sprayBackShare", "брызги: доля к атакующему", () => t.SprayBackShare, v => t.SprayBackShare = v, 0f, 1f, format: "0.##")
            .Info("обрубки", StumpStatus, "источников на срезах, родилось, выпущено мл: каплями, на кожу у края, потёками")
            .Float("stumpMl", "культя: струя, мл", () => t.StumpMl, v => t.StumpMl = v, 0f, 2000f, format: "0")
            .Float("stumpSeconds", "культя: струя гаснет, τ с", () => t.StumpSeconds, v => t.StumpSeconds = v, 0.1f, 20f, log: true, format: "0.##")
            .Float("stumpCorpseShare", "культя у трупа: доля τ", () => t.StumpCorpseShare, v => t.StumpCorpseShare = v, 0.05f, 1f, format: "0.##")
            .Float("stumpPulseHz", "культя: пульс, Гц", () => t.StumpPulseHz, v => t.StumpPulseHz = v, 0.2f, 4f, format: "0.##")
            .Float("stumpSpeed", "культя: скорость струи, м/с", () => t.StumpSpeed, v => t.StumpSpeed = v, 0.1f, 10f, format: "0.#")
            .Float("stumpSpread", "культя: разброс струи, рад", () => t.StumpSpread, v => t.StumpSpread = v, 0f, 1f, format: "0.##")
            .Float("pieceMl", "кусок: выплеск, мл", () => t.PieceMl, v => t.PieceMl = v, 0f, 500f, format: "0")
            .Float("stumpDripMl", "культя: капание, мл", () => t.StumpDripMl, v => t.StumpDripMl = v, 0f, 500f, format: "0")
            .Float("pieceDripMl", "кусок: капание, мл", () => t.PieceDripMl, v => t.PieceDripMl = v, 0f, 500f, format: "0")
            .Float("stumpRimMl", "край среза: кровь на кожу, мл", () => t.StumpRimMl, v => t.StumpRimMl = v, 0f, 100f, format: "0.#")
            .Float("severSprayShare", "отруб: брызги из касания, доля", () => t.SeverSprayShare, v => t.SeverSprayShare = v, 0f, 1f, format: "0.##")
            .Float("bladePerDamage", "клинок: крови на урон, мл", () => t.BladePerDamage, v => t.BladePerDamage = v, 0f, 1f, format: "0.###")
            .Float("bladeViscosity", "клинок: вязкость k, 1/с", () => t.BladeViscosity, v => t.BladeViscosity = v, 1f, 1000f, log: true, format: "0.#")
            .Float("bladeDetachMl", "клинок: капля с конца, мл", () => t.BladeDetachMl, v => t.BladeDetachMl = v, 0.005f, 2f, log: true, format: "0.###")
            .Button("кровь на мече игрока", RequestSmear, "кровь на лезвие меча игрока (подгонка вида и стекания)")
            .Bool("bladeSmear", "кровь на мече игрока (флаг)", () => _smear, v => { if (v) RequestSmear(); }, defaultValue: false)
            .Float("bladeSmearMl", "кровь на мече игрока, мл", () => t.SmearMl, v => t.SmearMl = v, 0.1f, 50f, log: true, format: "0.#")
            .Float("bladeShakeAccel", "клинок: стряхивает с, м/с²", () => t.BladeShakeAccel, v => t.BladeShakeAccel = v, 10f, 1000f, log: true, format: "0")
            .Float("viscosity", "вязкость k, 1/с", () => t.Viscosity, v => t.Viscosity = v, 1f, 1000f, log: true, format: "0.#")
            .Float("detachVolume", "порог отрыва, мл", () => t.DetachVolume, v => t.DetachVolume = v, 0.01f, 5f, log: true, format: "0.###")
            .Float("detachAngle", "отрыв: угол от «вниз», °", () => t.DetachAngle, v => t.DetachAngle = v, 0f, 90f, format: "0")
            .Float("strongDamage", "сильная рана — урон от", () => t.StrongDamage, v => t.StrongDamage = v, 0f, 200f, format: "0")
            .Float("spotPerDamage", "пятно у раны: крови на урон, мл", () => t.SpotPerDamage, v => t.SpotPerDamage = v, 0f, 1f, format: "0.####")
            .Float("oozeRate", "сочение: темп на урон, мл/с", () => t.OozeRate, v => t.OozeRate = v, 0f, 0.5f, format: "0.####")
            .Float("oozeSeconds", "сочение гаснет за, с", () => t.OozeSeconds, v => t.OozeSeconds = v, 1f, 120f, format: "0")
            .Float("oozeCorpseShare", "сочение у трупа: доля темпа", () => t.OozeCorpseShare, v => t.OozeCorpseShare = v, 0f, 1f, format: "0.##")
            .Float("trailRadius", "след потёка на коже: радиус, м", () => t.TrailRadius, v => t.TrailRadius = v, 0f, 0.1f, format: "0.###")
            .Float("trailCost", "след потёка: мл на метр", () => t.TrailCost, v => t.TrailCost = v, 0.1f, 20f, log: true, format: "0.##")
            .Float("corpsePoolMl", "лужа трупа, мл", () => t.CorpsePoolMl, v => t.CorpsePoolMl = v, 0f, 3000f, format: "0")
            .Float("corpsePoolSeconds", "лужа трупа: τ, с", () => t.CorpsePoolSeconds, v => t.CorpsePoolSeconds = v, 0.1f, 60f, log: true, format: "0.#")
            .Float("corpsePoolDelay", "лужа трупа: задержка, с", () => t.CorpsePoolDelay, v => t.CorpsePoolDelay = v, 0f, 5f, format: "0.#")
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
            .Float("floorNormal", "плёнка: усиление нормали", () => t.FloorNormalScale, v => t.FloorNormalScale = v, 0f, 200f, format: "0.#")
            .Float("floorMeniscus", "плёнка: мениск до толщины, мм", () => t.FloorMeniscusMm, v => t.FloorMeniscusMm = v, 0.01f, 10f, log: true, format: "0.##")
            .Float("floorF0", "плёнка: F0 блика", () => t.FloorF0, v => t.FloorF0 = v, 0f, 0.2f, format: "0.###")
            .Float("floorSky", "плёнка: сила неба", () => t.FloorSkyGain, v => t.FloorSkyGain = v, 0f, 4f, format: "0.##")
            .Float("floorHorizon", "плёнка: горизонт в отражении ×", () => t.FloorHorizon, v => t.FloorHorizon = v, 0f, 4f, format: "0.##")
            .Float("floorGlint", "плёнка: широкий блик, вес", () => t.FloorGlint, v => t.FloorGlint = v, 0f, 2f, format: "0.##")
            .Float("floorGlintRough", "плёнка: широкий блик, + шероховатость", () => t.FloorGlintRoughness, v => t.FloorGlintRoughness = v, 0f, 1f, format: "0.##")
            .Float("floorMaxTilt", "плёнка: наклон нормали до, °", () => t.FloorMaxTilt, v => t.FloorMaxTilt = v, 0f, 80f, format: "0")
            .Float("floorWet", "мокрый пол: множитель", () => t.FloorWetDarken, v => t.FloorWetDarken = v, 0.1f, 1f, format: "0.##")
            .Float("floorHalo", "мокрый пол: ореол, текселей", () => t.FloorHalo, v => t.FloorHalo = v, 0f, 8f, format: "0.#")
            .Float("floorEdgeWarp", "плёнка: сдвиг края шумом, текселей", () => t.FloorEdgeWarp, v => t.FloorEdgeWarp = v, 0f, 2f, format: "0.##")
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
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        ObserveTick(tick);
        long spent = System.Diagnostics.Stopwatch.GetTimestamp() - started;
        _observeTicks += spent;
        if (ObservedTicks <= WarmTicks) return;
        Slow(tick, spent);
        _tickHistogram[Math.Min((int)(Ms(spent) / HistogramStepMs), _tickHistogram.Length - 1)]++;
    }

    /// <summary>Гистограмма тиков наблюдателя после прогрева: корзина 10 мкс, последняя — 10 мс и дольше (p99 в итоге).</summary>
    private const double HistogramStepMs = 0.01;
    private readonly long[] _tickHistogram = new long[1000];

    /// <summary>Наблюдатель тиков после прогрева: 99-й процентиль, мс (верх корзины 10 мкс).</summary>
    public double P99ObserveMs
    {
        get
        {
            long total = 0, seen = 0;
            foreach (long n in _tickHistogram) total += n;
            for (int i = 0; i < _tickHistogram.Length; i++)
                if ((seen += _tickHistogram[i]) * 100 >= total * 99) return (i + 1) * HistogramStepMs;
            return 0;
        }
    }

    private long _observeTicks, _observeMax, _emitTicks, _collideTicks;

    /// <summary>Первые тики (прогрев: JIT, первые снимки) в самые долгие не идут.</summary>
    private const int WarmTicks = 240;

    /// <summary>Наблюдатель тиков: самый долгий тик после прогрева, мс (всплеск тика удара).</summary>
    public double MaxObserveMs => Ms(_observeMax);

    /// <summary>Самые долгие тики после прогрева (тик, время) — по убыванию.</summary>
    public const int SlowTicks = 8;
    private readonly (ulong Tick, long Spent)[] _slow = new (ulong, long)[SlowTicks];

    private void Slow(ulong tick, long spent)
    {
        if (spent > _observeMax) _observeMax = spent;
        int at = SlowTicks - 1;
        if (spent <= _slow[at].Spent) return;
        while (at > 0 && _slow[at - 1].Spent < spent)
        {
            _slow[at] = _slow[at - 1];
            at--;
        }
        _slow[at] = (tick, spent);
    }

    /// <summary>Выброс брызг удара, мс — первые <see cref="EmitLog"/>.</summary>
    public const int EmitLog = 16;
    private readonly double[] _emitLog = new double[EmitLog];
    private int _emits;

    private static double Ms(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>Наблюдатель тиков: среднее время на тик, мс (раны, брызги, потёки, кожа, пол на CPU).</summary>
    public double MeanObserveMs => ObservedTicks > 0 ? Ms(_observeTicks) / ObservedTicks : 0;

    private void ObserveTick(ulong tick)
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
        _bladeCount = 0;
        _frameHits += hits.Length;
        Hits += hits.Length;
        for (int i = 0; i < hits.Length; i++) Remember(hits[i]);
        int deaths = feed.Deaths.Length;
        _frameDeaths += deaths;
        Deaths += deaths;
        Built += feed.Built.Length;
        if (feed.ArenaReset) Resets++;
        if (_poses is not { } poses) return;
        if (ObservedTicks == 1)
        {
            _ = poses.Count;   // первый снимок поз (буферы сервиса) — в служебном кадре первого тика
            if (_surface is not null) WarmAnchor(_surface, poses);
        }
        if (feed.ArenaReset) _floor?.Reset();
        foreach (ulong f in feed.Deaths) RememberDeath(f, tick, poses);
        for (int i = 0; i < hits.Length; i++) Blade(hits[i], poses);
        bool step = IsStep(tick);
        if (step)
        {
            _steps++;
            _clock = ClockAt(_steps, _stepDt);
        }
        if (_bladeBlood is { } blades)
        {
            if (feed.ArenaReset) blades.ReleaseAll();
            foreach (ulong f in feed.Removed) blades.Release(f);
            foreach (ulong f in feed.Built) blades.Release(f);
            if (_bladeCount > 0) DepositBlades(blades, tick);
            if (_smear) SmearPlayer(blades, tick, poses);
            if (step && blades.UsedSlots > 0) StepBlades(blades, tick, poses);
        }
        RememberSevered();
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
            if (step) StepDrips(tick, poses);
        }
        if (_floor is { } stumpFloor) StepStumps(stumpFloor, feed, tick, step, poses);
        if (_floor is not null && step) StepFloor(tick, poses);
    }

    /// <summary>Отделения тика: (боец, сегмент) — удар, отрубивший часть, брызжет из касания меньше.</summary>
    private void RememberSevered()
    {
        _severedCount = 0;
        if (_sever is not { } sever) return;
        foreach (var r in sever.Detached)
        {
            if (_severedCount == _severed.Length) Array.Resize(ref _severed, 2 * _severed.Length);
            _severed[_severedCount++] = (r.Fighter, r.Event.Segment);
        }
    }

    private bool Severed(ulong fighter, int segment)
    {
        for (int i = 0; i < _severedCount; i++)
            if (_severed[i].Fighter == fighter && _severed[i].Segment == segment) return true;
        return false;
    }

    /// <summary>
    /// Обрубки: источники на срезах по отделениям тика (поза тика), шаг струй и капания по позам тика, капли — в полёт до шага пола (липнут
    /// к телам, ложатся на пол).
    /// </summary>
    private void StepStumps(BloodFloor floor, IBloodFeed feed, ulong tick, bool step, IFighterPoses poses)
    {
        if (_sever is not { } sever) return;
        if (_stumps is null)
        {
            if (Settings!.Body && _surface is null) return;
            _stumps = new BloodStumps(_surface);
        }
        var stumps = _stumps;
        if (ObservedTicks == 1)
        {
            stumps.Warm(poses, Tuning);
            if (poses.Count > 0 && _pieces is { } warmPieces) _ = warmPieces.Pieces(poses.Fighter(0).Fighter).Length;   // первый снимок кусков (запрос мира) — здесь, а не в тике отруба
        }
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (feed.ArenaReset) stumps.ClearAll();
        foreach (ulong f in feed.Removed) stumps.Clear(f);
        foreach (ulong f in feed.Built) stumps.Clear(f);
        var detached = sever.Detached;
        for (int i = 0; i < detached.Length; i++) stumps.Sever(detached[i].Fighter, detached[i].Event, tick, poses, _pieces, _skin, _clock, Tuning);
        if (step && stumps.Alive > 0)
        {
            stumps.Step(tick, _stepDt, poses, _pieces, _dead, _skin, _drips, _clock, Tuning);
            if (stumps.Drops.Length > 0) floor.DetachSticky(stumps.Drops, _terrain, Tuning.DropDrag, _tickDt, 0x57u);
        }
        long spent = System.Diagnostics.Stopwatch.GetTimestamp() - started;
        _stumpTicks += spent;
        if (ObservedTicks > WarmTicks && spent > _stumpMax) (_stumpMax, _stumpMaxTick) = (spent, tick);
    }

    /// <summary>Оружие бойцов тика (раз в тик, когда нужно).</summary>
    private void ReadTips(ulong tick)
    {
        if (_tipsTick == tick || _tips is not { } tips) return;
        _tipsTick = tick;
        int n = tips.Read(_tipBuf, playerOnly: false);
        if (n > _tipBuf.Length)
        {
            _tipBuf = new WeaponTip[Math.Max(2 * _tipBuf.Length, n)];
            n = tips.Read(_tipBuf, playerOnly: false);
        }
        _tipCount = Math.Min(n, _tipBuf.Length);
    }

    /// <summary>Номер оружия бойца в тике; нет — −1.</summary>
    private int WeaponOf(ulong fighter)
    {
        for (int i = 0; i < _tipCount; i++)
            if (_tipBuf[i].Key == fighter) return _tipBuf[i].Weapon;
        return -1;
    }

    /// <summary>Касания клинков тика — кровь на лезвие атакующего около места касания (∝ урону; рукоять и гарда — нет).</summary>
    private void DepositBlades(BloodBlade blades, ulong tick)
    {
        ReadTips(tick);
        for (int i = 0; i < _bladeCount; i++) DepositBlade(blades, _blade[i], i);
    }

    private void DepositBlade(BloodBlade blades, in BloodBladeContact c, int index)
    {
        if (!c.HasBlade || c.Attacker == c.Victim) return;
        BladeContactsSeen++;
        int weapon = WeaponOf(c.Attacker);
        if (weapon < 0 || !_weaponLayer!.TryBlade(weapon, out var zone))
        {
            BladeNoZone++;
            return;
        }
        float along = zone.Along(Vector3.Transform(c.BladeLocal, zone.BodyToMesh));
        _recentAlong[(int)((BladeContactsSeen - 1) % RecentAlong)] = (c.Along, along);
        if (along > 1 && along <= 1 + TipMargin) along = 1;   // касание кончиком: точка жертвы чуть дальше острия
        if (!(along >= 0 && along <= 1)) BladeOffBlade++;
        blades.Deposit(c.Attacker, weapon, zone, along, c.Damage * Tuning.BladePerDamage, _clock,
            BloodHash.Combine(BloodHash.Seed(c.Attacker, c.Tick, 0xB1ADu), (uint)index));
    }

    /// <summary>Отладка вида: кровь на меч игрока в следующем тике (<see cref="BloodTuning.SmearMl"/>: две трети — у середины лезвия, треть — у кончика).</summary>
    public void RequestSmear() => _smear = true;

    private bool _smear;

    private void SmearPlayer(BloodBlade blades, ulong tick, IFighterPoses poses)
    {
        _smear = false;
        ReadTips(tick);
        for (int i = 0; i < poses.Count; i++)
        {
            var f = poses.Fighter(i);
            if (!f.IsPlayer) continue;
            int weapon = WeaponOf(f.Fighter);
            if (weapon < 0 || !_weaponLayer!.TryBlade(weapon, out var zone)) return;
            uint seed = BloodHash.Seed(f.Fighter, tick, 0x5E4Eu);
            blades.Deposit(f.Fighter, weapon, zone, 0.45f, 2f / 3 * Tuning.SmearMl, _clock, seed);
            blades.Deposit(f.Fighter, weapon, zone, 0.85f, 1f / 3 * Tuning.SmearMl, _clock, seed + 1);
            return;
        }
    }

    /// <summary>Касание дальше острия не больше этой доли лезвия — кровь на кончик.</summary>
    private const float TipMargin = 0.25f;

    /// <summary>Касаний клинков с атакующим (не самоудар), без зоны лезвия у оружия, мимо лезвия (рукоять, гарда).</summary>
    public long BladeContactsSeen { get; private set; }
    public long BladeNoZone { get; private set; }
    public long BladeOffBlade { get; private set; }

    /// <summary>Последних касаний в итоге: доля длины тела клинка и доля лезвия.</summary>
    private const int RecentAlong = 8;
    private readonly (float Body, float Blade)[] _recentAlong = new (float, float)[RecentAlong];

    /// <summary>Шаг крови на клинках: по телу клинка тика; капли — в полёт (липнут к телам, ложатся на пол).</summary>
    private void StepBlades(BloodBlade blades, ulong tick, IFighterPoses poses)
    {
        ReadTips(tick);
        for (int s = 0; s < blades.SlotCount; s++) StepBlade(blades, s, tick, poses);
        if (blades.Drops.Length > 0) _floor?.DetachSticky(blades.Drops, _terrain, Tuning.DropDrag, _tickDt, 0xB1ADu);
        blades.TakeDrops();
    }

    private void StepBlade(BloodBlade blades, int slot, ulong tick, IFighterPoses poses)
    {
        if (!blades.Used(slot)) return;
        ulong key = blades.Key(slot);
        int index = WeaponOf(key) == blades.Weapon(slot) ? poses.Find(key) : -1;
        var segments = index >= 0 ? poses.Segments(index) : default;
        if (segments.Length <= BodySegs.Weapon || !Matrix4x4.Invert(blades.Zone(slot).BodyToMesh, out var meshToBody))
        {
            blades.Release(slot);   // оружие сменилось, боец снят или клинка в позах нет
            return;
        }
        blades.Step(slot, meshToBody * segments[BodySegs.Weapon].World, tick, _stepDt, _clock, Tuning.Look);
    }

    /// <summary>Касание клинка попадания: точка в системе тела клинка атакующего тика.</summary>
    private void Blade(in BloodHit hit, IFighterPoses poses)
    {
        if (_bladeCount == _blade.Length) Array.Resize(ref _blade, 2 * _blade.Length);
        int index = poses.Find(hit.Attacker);
        var segments = index >= 0 ? poses.Segments(index) : default;
        if (segments.Length > BodySegs.Weapon && Matrix4x4.Invert(segments[BodySegs.Weapon].World, out var inverse))
        {
            var body = segments[BodySegs.Weapon];
            var local = Vector3.Transform(hit.Point, inverse);
            float along = body.HalfLength > 1e-4f ? Math.Clamp((local.Y + body.HalfLength) / (2 * body.HalfLength), 0, 1) : 0;
            _blade[_bladeCount++] = new BloodBladeContact(hit.Tick, hit.Attacker, hit.Fighter, hit.Point, local, along, hit.Damage, true);
        }
        else _blade[_bladeCount++] = new BloodBladeContact(hit.Tick, hit.Attacker, hit.Fighter, hit.Point, default, 0, hit.Damage, false);
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
        if (_skin is { } skin && _surface is not null) StickSprays(floor, skin, tick, poses);
        if (_drips is { } drips)
        {
            floor.Detach(drips.Drops, _terrain, Tuning.DropDrag, _tickDt);
            drips.TakeDrops();
        }
        floor.Step(tick, StepOf(tick), PlayerXz(poses), Tuning.DropSpread, Tuning.PuddleMl, _floorRequests);
    }

    /// <summary>Брызги, коснувшиеся тел бойцов этого тика, — на кожу ближайшего треугольника сегмента (пятно от объёма капли).</summary>
    private void StickSprays(BloodFloor floor, BloodSkin skin, ulong tick, IFighterPoses poses)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        var sticks = floor.Collide(tick, poses, Tuning.DropDrag, _tickDt, Tuning.SprayOwnSkip);
        for (int i = 0; i < sticks.Length; i++) Stick(sticks[i], skin, poses);
        _collideTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
    }

    private void Stick(in BloodStick stick, BloodSkin skin, IFighterPoses poses)
    {
        int index = poses.Find(stick.Fighter);
        var joints = index >= 0 ? poses.Joints(index) : default;
        Vector3 bary = default;
        // Без уточнения по скинированной коже: оно в разы дороже, а пятно брызг — не рана.
        int triangle = joints.IsEmpty ? -1 : BloodWounds.Anchor(_surface!, stick.Segment, stick.Point, joints, out bary, refine: false);
        if (triangle < 0)
        {
            SprayMissedSkin++;
            return;
        }
        _recentSticks[(int)(_stuckCount++ % RecentSticks)] = (stick.Fighter, stick.Segment, stick.Point,
            Vector3.Distance(_surface!.SkinPoint(triangle, bary, joints), stick.Point));
        double before = skin.Deposited;
        skin.Stamp(stick.Fighter, triangle, bary, stick.Volume, _clock, StickRadius(stick.Volume, Tuning));
        if (skin.Deposited > before) SprayOnSkinMl += skin.Deposited - before;
        else SprayMissedSkin++;
    }

    /// <summary>Последних касаний тел в итоге (боец, сегмент, точка, до кожи под ней, м).</summary>
    public const int RecentSticks = 16;

    private readonly (ulong Fighter, int Segment, Vector3 Point, float Gap)[] _recentSticks = new (ulong, int, Vector3, float)[RecentSticks];
    private long _stuckCount;

    /// <summary>Радиус пятна прилипшей капли, м: диск толщины <see cref="BloodTuning.SprayStickMm"/> с её объёмом, 0.8…3 см.</summary>
    public static float StickRadius(float ml, BloodTuning t) =>
        Math.Clamp(MathF.Sqrt(ml * 1e-6f / (MathF.PI * MathF.Max(t.SprayStickMm, 1e-3f) * 1e-3f)), 0.008f, 0.03f);

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

    /// <summary>
    /// Якорь раны один раз в первом тике: поиск ближайшей кожи собирается JIT здесь, а не в тике первого удара (было ≈ 8 мс в нём).
    /// </summary>
    private static void WarmAnchor(SkinSurface surface, IFighterPoses poses)
    {
        if (poses.Count == 0) return;
        var joints = poses.Joints(0);
        int pelvis = surface.ReferenceJoint(BodySegs.Pelvis, default, joints);
        if (pelvis < 0 || pelvis >= joints.Length) return;
        BloodWounds.Anchor(surface, BodySegs.Pelvis, joints[pelvis].Translation, joints, out _);
        BloodWounds.Anchor(surface, BodySegs.Pelvis, joints[pelvis].Translation, joints, out _, refine: false);
    }

    /// <summary>Шаг потёков на тике <paramref name="tick"/>: ⌊tick·60/T⌋ сменился (при T ≤ 60 — каждый тик).</summary>
    private bool IsStep(ulong tick) => tick == 0 || StepOf(tick) != StepOf(tick - 1);

    private ulong StepOf(ulong tick) => _tickRate <= StepRate ? tick : tick * StepRate / (ulong)_tickRate;

    private void Forget(ReadOnlySpan<ulong> fighters)
    {
        foreach (ulong f in fighters)
        {
            _ooze.Clear(_wounds.Find(f));
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
        if (_floor is { } floor) EmitSpray(floor, hit, w, poses.Joints(index), poses);
    }

    /// <summary>Брызги из раны в тике удара — в полёт (липнут к телам бойцов).</summary>
    private void EmitSpray(BloodFloor floor, in BloodHit hit, in BloodWound w, ReadOnlySpan<Matrix4x4> joints, IFighterPoses poses)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        int attacker = hit.Attacker != hit.Fighter ? poses.Find(hit.Attacker) : -1;
        float share = Severed(hit.Fighter, hit.Segment) ? Tuning.SeverSprayShare : 1;
        var drops = _spray.Emit(hit, w, joints, attacker >= 0 ? poses.Segments(attacker) : default, _surface!, Tuning, share);
        floor.DetachSticky(drops, _terrain, Tuning.DropDrag, _tickDt, w.Seed | 1);
        long spent = System.Diagnostics.Stopwatch.GetTimestamp() - started;
        _emitTicks += spent;
        if (_emits < EmitLog) _emitLog[_emits++] = Ms(spent);
    }

    private void StepDrips(ulong tick, IFighterPoses poses)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        bool any = _drips!.Alive > 0;
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
        return new Line(d).S("тик ").I((long)_lastTick).S(", кадр: попаданий ").I(LastFrameHits).S(", смертей ").I(LastFrameDeaths)
            .S("; всего: попаданий ").I(Hits).S(", смертей ").I(Deaths).S(", сбросов ").I(Resets).Length;
    }

    private int DripStatus(Span<char> d)
    {
        return new Line(d).S("ран ").I(_wounds.Count).S(", потёков ").I(_drips?.Alive ?? 0).S(", капель ").I(_drips?.Detached ?? 0)
            .S(", шаг мкс ").I((long)(LastStepMs * 1000)).Length;
    }

    private int FloorStatus(Span<char> d)
    {
        var f = _floor;
        return new Line(d).S("в полёте ").I(f?.Flying.Length ?? 0).S(", приземлилось мкл ").I((long)((f?.LandedMl ?? 0) * 1000))
            .S(", в окно мкл ").I((long)((f?.StampedMl ?? 0) * 1000)).S(", шагов ").I(f?.Steps ?? 0).S(", сдвигов ").I(f?.Shifts ?? 0).Length;
    }

    private int StumpStatus(Span<char> d)
    {
        var s = _stumps;
        return new Line(d).S("источников ").I(s?.Alive ?? 0).S(", родилось ").I(s?.Born ?? 0).S(", мл: каплями ").I((long)(s?.DropsMl ?? 0))
            .S(", на край ").I((long)(s?.RimMl ?? 0)).S(", потёками ").I((long)(s?.RunMl ?? 0)).Length;
    }

    private int SkinStatus(Span<char> d)
    {
        return new Line(d).S("слотов ").I(_skin?.UsedSlots ?? 0).S(", сочится ").I(_ooze.Active).S(", темп мкл/с ").I((long)(_ooze.Rate * 1000))
            .S(", выпущено мкл ").I((long)(_ooze.Released * 1000)).Length;
    }

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
            meanTickMs = Math.Round(MeanObserveMs, 4),
            p99TickMs = Math.Round(P99ObserveMs, 3),
            maxTickMs = Math.Round(MaxObserveMs, 3),
            slowTicks = _slow.Where(x => x.Spent > 0).Select(x => new { tick = x.Tick, ms = Math.Round(Ms(x.Spent), 3) }).ToArray(),
        },
        services = new
        {
            feed = _feed is not null,
            poses = _poses is not null,
            skinMesh = _mesh is not null,
            terrain = _terrain is not null,
            terrainSrv = _terrainSrv >= 0,
            sever = _sever is not null,
            pieces = _pieces is not null,
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
        blade = _bladeBlood is { } bb ? new
        {
            slots = bb.SlotCount,
            used = bb.UsedSlots,
            slotMisses = bb.SlotMisses,
            depositedMl = Math.Round(bb.Deposited, 4),
            onBladesMl = Math.Round(bb.OnBlades(), 4),
            droppedMl = Math.Round(bb.DroppedMl, 4),
            drops = bb.Dropped,
            shakenMl = Math.Round(bb.ShakenMl, 4),
            contacts = BladeContactsSeen,
            noZone = BladeNoZone,
            offBlade = BladeOffBlade,
            recentAlong = _recentAlong.Take((int)Math.Min(BladeContactsSeen, RecentAlong))
                .Select(a => new { body = Math.Round(a.Body, 3), blade = Math.Round(a.Blade, 3) }).ToArray(),
            blades = Enumerable.Range(0, bb.SlotCount).Where(bb.Used).Select(k => new
            {
                fighter = bb.Key(k), weapon = bb.Weapon(k), lengthM = Math.Round(bb.Zone(k).Length, 3), halfM = Math.Round(bb.Zone(k).Half, 4),
                ml = Math.Round(bb.OnBlade(k), 4), centre = Math.Round(bb.Centre(k), 3), tipBeadMl = Math.Round(bb.Bead(k, 1), 4),
                guardBeadMl = Math.Round(bb.Bead(k, 0), 4),
            }).ToArray(),
            gpu = _bladeGpu is not null,
            lastPacks = _bladeGpu?.Packs ?? 0,
            lastServed = _bladeGpu?.Served ?? 0,
        } : null,
        stumps = _stumps is { } st ? new
        {
            alive = st.Alive,
            born = st.Born,
            dropsMl = Math.Round(st.DropsMl, 3),
            rimMl = Math.Round(st.RimMl, 3),
            runMl = Math.Round(st.RunMl, 3),
            drops = st.DropCount,
            ms = Math.Round(Ms(_stumpTicks), 3),
            maxTickMs = Math.Round(Ms(_stumpMax), 3),
            maxTick = _stumpMaxTick,
            sources = Enumerable.Range(0, BloodStumps.Capacity).Where(i => st.Source(i).Alive).Select(i => st.Source(i)).Select(x => new
            {
                fighter = x.Fighter, segment = BodySegs.Name(x.Segment), mode = x.Mode.ToString(), side = x.Side.ToString(), born = x.Born,
                time = Math.Round(x.Time, 2), envelope = Math.Round(x.Envelope, 4), outMl = Math.Round(x.Out, 3), rim = x.RimCount, runs = x.RunCount,
                x = Math.Round(x.Point.X, 3), y = Math.Round(x.Point.Y, 3), z = Math.Round(x.Point.Z, 3),
            }).ToArray(),
        } : null,
        spray = new
        {
            drops = _spray.Emitted,
            ml = Math.Round(_spray.EmittedMl, 4),
            onSkinMl = Math.Round(SprayOnSkinMl, 4),
            missedSkin = SprayMissedSkin,
            bladeContacts = _bladeCount,
            emitMs = Math.Round(Ms(_emitTicks), 3),
            emitEachMs = _emitLog.Take(_emits).Select(x => Math.Round(x, 3)).ToArray(),
            collideMs = Math.Round(Ms(_collideTicks), 3),
            recentSticks = _recentSticks.Take((int)Math.Min(_stuckCount, RecentSticks)).Select(r => new
            {
                fighter = r.Fighter, segment = BodySegs.Name(r.Segment), x = Math.Round(r.Point.X, 3), y = Math.Round(r.Point.Y, 3),
                z = Math.Round(r.Point.Z, 3), gapCm = Math.Round(r.Gap * 100, 1),
            }).ToArray(),
        },
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
            stuck = f.Stuck,
            stuckMl = Math.Round(f.StuckMl, 4),
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
