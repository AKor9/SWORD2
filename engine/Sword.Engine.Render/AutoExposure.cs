using System.Diagnostics;
using System.Numerics;

namespace Sword.Engine.Render;

/// <summary>
/// Автоэкспозиция (<see cref="Renderer.AutoExposure"/>): средний log2 яркости HDR-сцены по сетке через 4 пикселя (две вычислительные
/// стадии в фазе Post перед bloom), цель — <see cref="Key"/> / средняя яркость × 2^<see cref="Compensation"/> в пределах
/// <see cref="Renderer.Exposure"/> × 2^(<see cref="MinEv"/>…<see cref="MaxEv"/>), подстройка — экспоненциально, светлее — быстрее.
/// Экспозиция живёт в буфере GPU: тонмап и порог bloom читают её оттуда, на CPU не возвращается.
/// </summary>
public sealed class AutoExposure : IDisposable
{
    /// <summary>Шаг сетки замера, пиксели.</summary>
    public const int Stride = 4;

    /// <summary>Потолок плиток замера (группа 16×16 — плитка 64×64 пикселя): до 8K×8K.</summary>
    public const int MaxTiles = 128 * 128;

    /// <summary>Пределы яркости замера (log2 от них): чёрное и пятна солнца не уводят среднее.</summary>
    public const float MinLuminance = 1f / 4096, MaxLuminance = 64f;

    /// <summary>SRV «буфера нет» (<c>EXPOSURE_NONE</c>): экспозиция — корневой константой.</summary>
    public const uint None = uint.MaxValue;

    public const int SlotState = 0, SlotScene = 1, SlotDt = 2, SlotKey = 3, SlotCompensation = 4, SlotMin = 5, SlotMax = 6,
        SlotSpeedUp = 7, SlotSpeedDown = 8, SlotTiles = 9;

    private const string Shader = "common/exposure.hlsl";
    private static readonly ComputePipelineDesc Meter = new(Shader, "CsMeter");
    private static readonly ComputePipelineDesc Adapt = new(Shader, "CsAdapt");

    private readonly GpuStorage _state;
    private readonly Action<RenderPassContext> _recordMeter, _recordAdapt;
    private long _lastTimestamp;
    private float _dt, _base;
    private int _sceneSrv, _stateUav;
    private uint _tilesX, _tilesY;

    internal AutoExposure(Renderer renderer)
    {
        // Элемент 0 — состояние (экспозиция, средний log2 яркости), дальше — плитки замера (сумма log2, число выборок).
        var initial = new Vector4[1 + MaxTiles];
        initial[0] = new Vector4(Renderer.DefaultExposure, 0, 0, 0);
        _state = renderer.CreateStorage<Vector4>("exposure.state", initial);
        _recordMeter = RecordMeter;
        _recordAdapt = RecordAdapt;
    }

    /// <summary>Автоэкспозиция в кадре (только с HDR); по умолчанию выключена — игра включает.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Яркость, которую средняя сцены получает после экспозиции (до тонмапа). По умолчанию — дневная арена выходит с
    /// <see cref="Renderer.DefaultExposure"/>: средняя log-яркость сцены дня (замер этой сеткой) 0.405 × 0.99.
    /// </summary>
    public float Key { get; set; } = 0.405f * Renderer.DefaultExposure;

    /// <summary>Поправка, EV (−4…4).</summary>
    public float Compensation { get; set; }

    /// <summary>Нижний предел, EV от <see cref="Renderer.Exposure"/>: темнее не больше чем на 1 (светлее — до <see cref="MaxEv"/>).</summary>
    public float MinEv { get; set; } = -1f;

    /// <summary>Верхний предел, EV от <see cref="Renderer.Exposure"/>: светлее не больше чем в 4 раза — ночь остаётся ночью.</summary>
    public float MaxEv { get; set; } = 2f;

    /// <summary>Скорость подстройки к светлее, 1/с (глаз сужается быстро).</summary>
    public float SpeedUp { get; set; } = 3f;

    /// <summary>Скорость подстройки к темнее, 1/с.</summary>
    public float SpeedDown { get; set; } = 1f;

    /// <summary>Буфер экспозиции этого кадра для тонмапа и bloom; null — автоэкспозиции в кадре нет.</summary>
    internal RenderBuffer? Buffer { get; private set; }

    /// <summary>SRV буфера этого кадра или <see cref="None"/>.</summary>
    internal uint Srv { get; private set; } = None;

    /// <summary>Сцена прыгнула (новый уровень, ночь ↔ день): следующий кадр — сразу к цели, без подстройки.</summary>
    public void Reset() => _lastTimestamp = 0;

    /// <summary>Замер и подстройка в фазу Post (до bloom и тонмапа) над <paramref name="scene"/>.</summary>
    internal void Add(RenderGraph graph, RenderTexture scene, int sceneSrv, float baseExposure)
    {
        long now = Stopwatch.GetTimestamp();
        // Первый кадр после паузы или сброса — сразу к цели (dt большой); дальше — время кадра.
        _dt = _lastTimestamp == 0 ? 1000f : (float)Math.Min(Stopwatch.GetElapsedTime(_lastTimestamp, now).TotalSeconds, 0.25);
        _lastTimestamp = now;
        _base = baseExposure;
        _sceneSrv = sceneSrv;
        _tilesX = (uint)((graph.Width / Stride + 15) / 16);
        _tilesY = (uint)((graph.Height / Stride + 15) / 16);
        var buffer = graph.Import(_state);
        _stateUav = graph.UavOf(buffer);
        graph.AddComputePass("post.exposure.meter", FramePhase.Post).Read(scene).Write(buffer).Execute(_recordMeter);
        graph.AddComputePass("post.exposure.adapt", FramePhase.Post).Write(buffer).Execute(_recordAdapt);
        Buffer = buffer;
        Srv = (uint)graph.SrvOf(buffer);
    }

    internal void SkipFrame()
    {
        Buffer = null;
        Srv = None;
        _lastTimestamp = 0;
    }

    private void RecordMeter(RenderPassContext ctx)
    {
        ctx.SetPipeline(Meter);
        ctx.SetConstant(SlotState, _stateUav);
        ctx.SetConstant(SlotScene, _sceneSrv);
        ctx.SetConstant(SlotTiles, _tilesX);
        ctx.Dispatch(_tilesX, _tilesY);
    }

    private void RecordAdapt(RenderPassContext ctx)
    {
        ctx.SetPipeline(Adapt);
        ctx.SetConstant(SlotState, _stateUav);
        ctx.SetConstant(SlotDt, _dt);
        ctx.SetConstant(SlotKey, Key * MathF.Pow(2, Compensation));
        ctx.SetConstant(SlotMin, _base * MathF.Pow(2, MinEv));
        ctx.SetConstant(SlotMax, _base * MathF.Pow(2, MaxEv));
        ctx.SetConstant(SlotSpeedUp, SpeedUp);
        ctx.SetConstant(SlotSpeedDown, SpeedDown);
        ctx.SetConstant(SlotTiles, _tilesX * _tilesY);
        ctx.Dispatch(1);
    }

    internal static void Contribute(ShaderContract c)
    {
        c.Section("автоэкспозиция (AutoExposure)");
        c.Add("EXPOSURE_NONE", None, "буфера нет - экспозиция константой");
        c.Add("EXPOSURE_STRIDE", Stride, "шаг сетки замера, пиксели");
        c.Add("EXPOSURE_MIN_LUMINANCE", MinLuminance);
        c.Add("EXPOSURE_MAX_LUMINANCE", MaxLuminance);
        c.AddSlot("EXPOSURE_STATE", SlotState, "UAV состояния и плиток (float4)");
        c.AddSlot("EXPOSURE_SCENE", SlotScene, "SRV scene.color");
        c.AddSlot("EXPOSURE_DT", SlotDt, "время кадра, с, float");
        c.AddSlot("EXPOSURE_KEY", SlotKey, "цель средней яркости, float");
        c.AddSlot("EXPOSURE_MIN", SlotMin, "нижний предел экспозиции, float");
        c.AddSlot("EXPOSURE_MAX", SlotMax, "верхний предел экспозиции, float");
        c.AddSlot("EXPOSURE_SPEED_UP", SlotSpeedUp, "к светлее, 1/с, float");
        c.AddSlot("EXPOSURE_SPEED_DOWN", SlotSpeedDown, "к темнее, 1/с, float");
        c.AddSlot("EXPOSURE_TILES", SlotTiles, "замер: плиток по x; подстройка: плиток всего");
    }

    public void Dispose() => _state.Dispose();
}
