using System.Numerics;
using Sword.Engine.Fluid2D;
using Sword.Engine.Fluid3D;
using Sword.Engine.Render;

namespace Sword.Game.Fire;

/// <summary>
/// Дым из пламени (README модуля, «Дым из пламени»): 3D-газ получает плотность, температуру и скорость из поля 2D-сима пламени там, где
/// пламя гаснет. Карта источника (<see cref="Fluid3DPlane"/>) — <see cref="FireGasFeedParams.Side"/>² клеток по ячейке газа в осях площадки пламени
/// с центром в шишке; клетка — среднее по <see cref="FireGasFeed.Sub"/>² выборкам поля: вес w = ρ·(1 − видимость), видимость =
/// saturate(k̄/срез), k̄ = DensityGain·<see cref="Noise"/>^FlowDensityPow·ρ^DensityPower (пламя при среднем шуме — как метрика «пламя»).
/// A = Σw/Sub²·зона, зона = saturate((<see cref="Radius"/> − r)/<see cref="ZoneEdge"/>)·saturate((y − <see cref="Floor"/>)/<see cref="ZoneEdge"/> + ½),
/// r — расстояние клетки от шишки на площадке, y — её высота по площадке.
/// Клетка: плотность A·<see cref="Density"/>·dt, температура A·<see cref="Temperature"/>·dt, доля скорости min(A·<see cref="VelocityRate"/>·dt, 1),
/// скорость — средняя по w скорость поля в м/с: x — по оси x площадки (горизонталь), y — по мировой вертикали, делённая на долю вертикали
/// в оси y площадки (не меньше <see cref="MinUp"/>): на экране дым у стыка движется как хвост пламени, а в мире — вверх, без ухода в глубину.
/// </summary>
public sealed record FireGasFeedParams
{
    /// <summary>Доля источника «из пламени»: 1 — только карта из пламени, 0 — только шар у шишки; между — карта × это, шар × (1 − это).
    /// Умолчание 0.6: зона карты режет длинный хвост пламени в движении — доля шара держит дым при вращении и замахах.</summary>
    public float Mix { get; init; } = 0.6f;

    /// <summary>
    /// Клеток карты по стороне (по <see cref="FireGasFeed.CellSize"/>): 34 — ≈1 м, в движении длинный хвост пламени умещается в карту.
    /// Кольцо карт выделяется при старте под сторону этого момента — больше её позже не задать.
    /// </summary>
    public int Side { get; init; } = 34;

    /// <summary>Зона у шишки, м, не больше половины карты: дальше — не впрыскивается (дальний хвост 2D-поля не прилипает к глубине факела
    /// при вращении).</summary>
    public float Radius { get; init; } = 0.5f;

    /// <summary>Ширина спада зоны, м.</summary>
    public float ZoneEdge { get; init; } = 0.08f;

    /// <summary>
    /// Низ зоны над центром шишки по площадке, м: ниже (с краем <see cref="ZoneEdge"/>, на низе — ½) кайма пламени у шишки дыма не даёт —
    /// иначе ореол дыма вокруг пламени.
    /// </summary>
    public float Floor { get; init; } = 0.03f;

    /// <summary>Плотность и температура в секунду при A = 1.</summary>
    public float Density { get; init; } = 4.5f;
    public float Temperature { get; init; } = 6f;

    /// <summary>Как быстро скорость газа подтягивается к скорости поля при A = 1, 1/с.</summary>
    public float VelocityRate { get; init; } = 90f;

    /// <summary>Полутолщина слоя впрыска у площадки и ширина его края, м.</summary>
    public float HalfThickness { get; init; } = 0.045f;
    public float Edge { get; init; } = 0.03f;

    /// <summary>Шум пламени в формуле видимости (шум flow-map в поле не записан — средний, как у метрики «пламя»).</summary>
    public float Noise { get; init; } = 0.5f;

    /// <summary>Доля вертикали в оси y площадки не меньше (взгляд сверху): скорость по вертикали — не больше 1/это скорости поля.</summary>
    public float MinUp { get; init; } = 0.5f;

    public static FireGasFeedParams Torch { get; } = new();

    /// <summary>Половина стороны карты, м — предел <see cref="Radius"/>.</summary>
    public float HalfMap => 0.5f * Side * FireGasFeed.CellSize;

    public void Validate()
    {
        if (!(Mix is >= 0 and <= 1)) throw new ArgumentOutOfRangeException(nameof(Mix), Mix, "доля 0…1");
        if (Side is < 1 or > FireGasFeed.MaxSide) throw new ArgumentOutOfRangeException(nameof(Side), Side, $"клеток по стороне 1…{FireGasFeed.MaxSide}");
        if (!(Radius > 0) || !(ZoneEdge > 0) || !(Radius <= HalfMap))
            throw new ArgumentOutOfRangeException(nameof(Radius), Radius, $"зона > 0, не больше половины карты ({HalfMap} м)");
        if (!(Density >= 0) || !(Temperature >= 0) || !(VelocityRate >= 0) || !(HalfThickness >= 0) || !(Edge >= 0) || !(Noise >= 0) ||
            !(MinUp is > 0 and <= 1))
            throw new ArgumentOutOfRangeException(nameof(Density), "выброс, толщина, шум ≥ 0; доля вертикали (0, 1]");
    }
}

/// <summary>
/// Числа прохода карты (корневые константы <c>fire_gas_feed.hlsl</c>; CPU-эталон — <see cref="FireGasFeed.ReferenceTexel"/>).
/// </summary>
public struct FireFeedConstants
{
    public int N, Side, Sub, Offset;
    public float InvExtX, InvExtY, TexelSize, InvSub;
    public float Radius, InvZoneEdge, Floor, Gain, Power;
    public float InvCut, RateDensity, RateTemperature, RateBlend;
    public float ScaleX, ScaleUp;
    public Vector3 AxisX;

    /// <summary>
    /// Числа шага: поле N², площадка <paramref name="extents"/> (м) с поворотом <paramref name="rotation"/>, сим <paramref name="sim"/>, пламя
    /// <paramref name="flame"/>, карта с клетки <paramref name="offset"/>. Скорость поля (ед. — SimSpeed текселей за шаг 1/60 с) в м/с:
    /// v·SimSpeed·E/N·60.
    /// </summary>
    public static FireFeedConstants Build(Fluid2DParams sim, Vector3 extents, Quaternion rotation, FlameParams flame, FireGasFeedParams feed,
        float stepSeconds, float metricsFlameK, int offset)
    {
        var ax = Vector3.Transform(Vector3.UnitX, rotation);
        var ay = Vector3.Transform(Vector3.UnitY, rotation);
        float up = MathF.Max(ay.Y, feed.MinUp);
        float cut = flame.GlowCut > 0 ? flame.GlowCut : metricsFlameK;
        double perUnit = sim.SimSpeed / (double)sim.Resolution / stepSeconds;
        return new FireFeedConstants
        {
            N = sim.Resolution,
            Side = feed.Side,
            Sub = FireGasFeed.Sub,
            Offset = offset,
            InvExtX = 1f / extents.X,
            InvExtY = 1f / extents.Y,
            TexelSize = FireGasFeed.CellSize,
            InvSub = 1f / FireGasFeed.Sub,
            Radius = feed.Radius,
            InvZoneEdge = 1f / feed.ZoneEdge,
            Floor = feed.Floor,
            Gain = flame.DensityGain * MathF.Pow(feed.Noise, flame.FlowDensityPow),
            Power = flame.DensityPower,
            InvCut = 1f / cut,
            RateDensity = feed.Mix * feed.Density * stepSeconds,
            RateTemperature = feed.Mix * feed.Temperature * stepSeconds,
            RateBlend = feed.VelocityRate * stepSeconds,
            ScaleX = (float)(perUnit * extents.X),
            ScaleUp = (float)(perUnit * extents.Y / up),
            AxisX = ax,
        };
    }
}

/// <summary>
/// Проход карты дыма из пламени (<c>fire.gas.feed</c>, фаза Simulation) после шага 2D-сима: поле C шага → клетки карты в кольце кадра
/// (<see cref="Ring"/> карт: шагов за кадр не больше <see cref="Fluid2DSim.MaxStepsPerFrame"/>, газ того же шага читает свою карту позже в кадре).
/// Кадр — 0 Б: делегат прохода один, аргументы — массив по порядку регистрации.
/// </summary>
public sealed class FireGasFeed : IDisposable
{
    public const string ShaderFile = "fire/fire_gas_feed.hlsl";

    /// <summary>Выборок поля на клетку по стороне, сторона клетки (= ячейка газа), предел клеток карты по стороне.</summary>
    public const int Sub = 4, MaxSide = 64;
    public const float CellSize = 0.03f;

    /// <summary>Карт в кольце — шагов за кадр не больше.</summary>
    public const int Ring = Fluid2DSim.MaxStepsPerFrame;

    private const int Group = 8;
    private const int SlotN = 0, SlotSide = 1, SlotSub = 2, SlotOffset = 3, SlotInvExtX = 4, SlotInvExtY = 5, SlotTexel = 6, SlotInvSub = 7,
        SlotRadius = 8, SlotInvZoneEdge = 9, SlotGain = 10, SlotPower = 11, SlotInvCut = 12, SlotRateDensity = 13, SlotRateTemperature = 14,
        SlotRateBlend = 15, SlotScaleX = 16, SlotScaleUp = 17, SlotAxisX = 18, SlotAxisY = 19, SlotAxisZ = 20, SlotField = 21, SlotMap = 22,
        SlotFloor = 23;

    private struct Args
    {
        public FireFeedConstants K;
        public int Field, Map;
    }

    /// <summary>Карта шага в кольце: номер шага, плоскость, первая клетка; кадр, в котором она записана.</summary>
    public readonly record struct Entry(ulong Frame, ulong Step, Fluid3DPlane Plane, int Offset);

    private readonly GpuStorage _map;
    private readonly int _side;
    private readonly Entry[] _entries = new Entry[Ring];
    private Args[] _args = new Args[4];
    private int _count, _next, _used;
    private ulong _frame = ulong.MaxValue;
    private readonly Action<RenderPassContext> _execute;

    /// <summary>Кольцо карт до <paramref name="side"/>² клеток (<see cref="FireGasFeedParams.Side"/> при старте).</summary>
    public FireGasFeed(Renderer renderer, string name, int side)
    {
        if (side is < 1 or > MaxSide) throw new ArgumentOutOfRangeException(nameof(side), side, $"клеток по стороне 1…{MaxSide}");
        _side = side;
        _map = renderer.CreateStorage<Fluid3DMapTexel>($"{name}.feed", (uint)(Ring * side * side));
        _execute = Execute;
    }

    /// <summary>Клеток по стороне, под которые выделено кольцо (шаг карты в буфере — это²).</summary>
    public int Capacity => _side;

    /// <summary>Буфер карт (кольцо).</summary>
    public GpuStorage Map => _map;

    /// <summary>Слоты шейдера карты в контракт.</summary>
    public static void Contribute(ShaderContract c) => c
        .Section("Fire: smoke from the flame (Sword.Game.Fire.Client)")
        .Add("FIRE_FEED_GROUP", Group, "numthreads of the map pass (8x8)")
        .AddSlot("gFeedN", SlotN).AddSlot("gFeedSide", SlotSide).AddSlot("gFeedSub", SlotSub).AddSlot("gFeedOffset", SlotOffset)
        .AddSlot("gFeedInvExtX", SlotInvExtX).AddSlot("gFeedInvExtY", SlotInvExtY).AddSlot("gFeedTexel", SlotTexel).AddSlot("gFeedInvSub", SlotInvSub)
        .AddSlot("gFeedRadius", SlotRadius).AddSlot("gFeedInvZoneEdge", SlotInvZoneEdge).AddSlot("gFeedGain", SlotGain).AddSlot("gFeedPower", SlotPower)
        .AddSlot("gFeedInvCut", SlotInvCut).AddSlot("gFeedRateDensity", SlotRateDensity).AddSlot("gFeedRateTemperature", SlotRateTemperature)
        .AddSlot("gFeedRateBlend", SlotRateBlend).AddSlot("gFeedScaleX", SlotScaleX).AddSlot("gFeedScaleUp", SlotScaleUp)
        .AddSlot("gFeedAxisX", SlotAxisX).AddSlot("gFeedAxisY", SlotAxisY).AddSlot("gFeedAxisZ", SlotAxisZ)
        .AddSlot("gFeedField", SlotField).AddSlot("gFeedMap", SlotMap).AddSlot("gFeedFloor", SlotFloor);

    /// <summary>
    /// Карта шага <paramref name="step"/> после его проходов 2D-сима: поле <paramref name="field"/> (C), площадка с центром <paramref name="center"/>
    /// (шишка шага) и поворотом <paramref name="rotation"/>. Возвращает запись кольца (для газа того же шага — <see cref="Find"/>).
    /// </summary>
    public Entry Add(RenderFrame frame, ulong step, GpuStorage field, Vector3 center, Quaternion rotation, in FireFeedConstants numbers,
        FireGasFeedParams feed)
    {
        if (frame.Number != _frame)
        {
            _frame = frame.Number;
            _count = _next = _used = 0;
        }
        if (_used == Ring) throw new InvalidOperationException($"карт дыма из пламени за кадр больше {Ring}");
        if (feed.Side > _side || numbers.Side != feed.Side)
            throw new ArgumentException($"карта {feed.Side}² (числа — {numbers.Side}²) не умещается в кольцо по {_side}²", nameof(feed));
        int slot = _used++;
        int offset = slot * _side * _side;
        var k = numbers;
        k.Offset = offset;
        var g = frame.Graph;
        var c = g.Import(field);
        var m = g.Import(_map);
        g.AddComputePass("fire.gas.feed", FramePhase.Simulation).Read(c).Write(m).Execute(_execute);
        if (_count == _args.Length) Array.Resize(ref _args, _args.Length * 2);
        _args[_count++] = new Args { K = k, Field = g.SrvOf(c), Map = g.UavOf(m) };
        var plane = new Fluid3DPlane(center, Vector3.Transform(Vector3.UnitX, rotation), Vector3.Transform(Vector3.UnitY, rotation), feed.Side,
            CellSize, feed.HalfThickness, feed.Edge);
        return _entries[slot] = new Entry(frame.Number, step, plane, offset);
    }

    /// <summary>Карта шага <paramref name="step"/>, записанная в кадре <paramref name="frame"/>; нет — false.</summary>
    public bool Find(ulong frame, ulong step, out Entry entry)
    {
        if (frame == _frame)
            for (int i = 0; i < _used; i++)
                if (_entries[i].Step == step)
                {
                    entry = _entries[i];
                    return true;
                }
        entry = default;
        return false;
    }

    private void Execute(RenderPassContext ctx)
    {
        ref readonly var a = ref _args[_next++];
        ref readonly var k = ref a.K;
        ctx.SetPipeline(new ComputePipelineDesc(ShaderFile, "CsFeed"));
        ctx.SetConstant(SlotN, k.N);
        ctx.SetConstant(SlotSide, k.Side);
        ctx.SetConstant(SlotSub, k.Sub);
        ctx.SetConstant(SlotOffset, k.Offset);
        ctx.SetConstant(SlotInvExtX, k.InvExtX);
        ctx.SetConstant(SlotInvExtY, k.InvExtY);
        ctx.SetConstant(SlotTexel, k.TexelSize);
        ctx.SetConstant(SlotInvSub, k.InvSub);
        ctx.SetConstant(SlotRadius, k.Radius);
        ctx.SetConstant(SlotInvZoneEdge, k.InvZoneEdge);
        ctx.SetConstant(SlotGain, k.Gain);
        ctx.SetConstant(SlotPower, k.Power);
        ctx.SetConstant(SlotInvCut, k.InvCut);
        ctx.SetConstant(SlotRateDensity, k.RateDensity);
        ctx.SetConstant(SlotRateTemperature, k.RateTemperature);
        ctx.SetConstant(SlotRateBlend, k.RateBlend);
        ctx.SetConstant(SlotScaleX, k.ScaleX);
        ctx.SetConstant(SlotScaleUp, k.ScaleUp);
        ctx.SetConstant(SlotAxisX, k.AxisX.X);
        ctx.SetConstant(SlotAxisY, k.AxisX.Y);
        ctx.SetConstant(SlotAxisZ, k.AxisX.Z);
        ctx.SetConstant(SlotField, a.Field);
        ctx.SetConstant(SlotMap, a.Map);
        ctx.SetConstant(SlotFloor, k.Floor);
        uint groups = (uint)((k.Side + Group - 1) / Group);
        ctx.Dispatch(groups, groups, 1);
    }

    public void Dispose() => _map.Dispose();

    // ── CPU-эталон (те же выражения float и порядок, что CsFeed) ────────────────────────────────────────────────────

    /// <summary>Карта целиком по полю C (<paramref name="composite"/>: N² × 4 half — vx, vy, ρ, wet).</summary>
    public static Fluid3DMapTexel[] ReferenceMap(ReadOnlySpan<Half> composite, in FireFeedConstants k)
    {
        var map = new Fluid3DMapTexel[k.Side * k.Side];
        for (int j = 0; j < k.Side; j++)
            for (int i = 0; i < k.Side; i++) map[i + k.Side * j] = ReferenceTexel(composite, k, i, j);
        return map;
    }

    /// <summary>Клетка (i, j) карты: столбцы — вдоль x площадки, ряды — вверх по площадке.</summary>
    public static Fluid3DMapTexel ReferenceTexel(ReadOnlySpan<Half> composite, in FireFeedConstants k, int i, int j)
    {
        float half = 0.5f * k.Side;
        float xc = ((float)i + 0.5f - half) * k.TexelSize, yc = ((float)j + 0.5f - half) * k.TexelSize;
        float zone = Math.Clamp((k.Radius - MathF.Sqrt(xc * xc + yc * yc)) * k.InvZoneEdge, 0f, 1f) *
            Math.Clamp((yc - k.Floor) * k.InvZoneEdge + 0.5f, 0f, 1f);
        float w = 0, vx = 0, vy = 0;
        if (zone > 0)
            for (int b = 0; b < k.Sub; b++)
                for (int a = 0; a < k.Sub; a++) Sample(composite, k, i, j, a, b, half, ref w, ref vx, ref vy);
        float amount = w * k.InvSub * k.InvSub * zone;
        var velocity = Vector3.Zero;
        if (w > 0)
        {
            float sx = vx / w * k.ScaleX, sy = -(vy / w) * k.ScaleUp;
            velocity = new Vector3(k.AxisX.X * sx, k.AxisX.Y * sx + sy, k.AxisX.Z * sx);
        }
        return Fluid3DMapTexel.Pack(amount * k.RateDensity, amount * k.RateTemperature, MathF.Min(amount * k.RateBlend, 1f), velocity);
    }

    private static void Sample(ReadOnlySpan<Half> c, in FireFeedConstants k, int i, int j, int a, int b, float half, ref float w, ref float vx,
        ref float vy)
    {
        float x = ((float)i + ((float)a + 0.5f) * k.InvSub - half) * k.TexelSize;
        float y = ((float)j + ((float)b + 0.5f) * k.InvSub - half) * k.TexelSize;
        float u = 0.5f + x * k.InvExtX, v = 0.5f - y * k.InvExtY;
        var s = Bilinear(c, k.N, u, v);
        float rho = Math.Clamp(s.Z, 0f, 1f);
        float vis = Math.Clamp(k.Gain * MathF.Pow(rho, k.Power) * k.InvCut, 0f, 1f);
        float weight = rho * (1f - vis);
        w += weight;
        vx += weight * s.X;
        vy += weight * s.Y;
    }

    /// <summary>Билинейная выборка C в uv (центры текселей — (i + ½)/N), за краем — крайний тексель.</summary>
    private static Vector4 Bilinear(ReadOnlySpan<Half> c, int n, float u, float v)
    {
        float px = u * n - 0.5f, py = v * n - 0.5f;
        float fx = MathF.Floor(px), fy = MathF.Floor(py);
        float tx = px - fx, ty = py - fy;
        int x = (int)fx, y = (int)fy;
        var a = Load(c, n, x, y);
        var b = Load(c, n, x + 1, y);
        var d = Load(c, n, x, y + 1);
        var e = Load(c, n, x + 1, y + 1);
        var r0 = a + (b - a) * tx;
        var r1 = d + (e - d) * tx;
        return r0 + (r1 - r0) * ty;
    }

    private static Vector4 Load(ReadOnlySpan<Half> c, int n, int x, int y)
    {
        x = Math.Clamp(x, 0, n - 1);
        y = Math.Clamp(y, 0, n - 1);
        int i = 4 * (y * n + x);
        return new Vector4((float)c[i], (float)c[i + 1], (float)c[i + 2], (float)c[i + 3]);
    }
}
