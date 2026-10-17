using System.Numerics;
using System.Runtime.InteropServices;
using Sword.Engine.Rhi;

namespace Sword.Engine.Render;

/// <summary>Константы проходов GTAO (<c>common/gtao.hlsl</c>, <c>AoConstants</c>): строки по 16 байт.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct AoConstants
{
    public Matrix4x4 InvViewProj;
    public Matrix4x4 ViewProj;
    public Vector3 Eye; public float Radius;
    public Vector3 Right; public float Intensity;
    public Vector3 Up; public float Pad0;
    public uint Width; public uint Height; public uint Pad1; public uint Pad2;
}

/// <summary>
/// Затенение рассеянного света по глубине и нормалям сцены (GTAO, Jimenez и др., 2016; <see cref="Renderer.Ao"/>). Кадр:
/// <see cref="Prepare"/> — цели (SRV итога — до констант кадра), проход глубины и нормалей сцены рисует вызывающий, <see cref="AddPasses"/> —
/// вычислительные <c>ao.gtao</c> (две плоскости через пиксель, по 6 шагов в каждую сторону, шум 4×4) и <c>ao.blur</c> (среднее 4×4 с
/// весом по глубине — снимает шум) в фазе Opaque; непрозрачный проход читает <see cref="Map"/> (r — видимость 0…1) и гасит ею только
/// рассеянный свет. Своё: горизонты — в мировых координатах по глубине сцены (не в осях вида), радиус — в метрах.
/// </summary>
public sealed class AmbientOcclusion : IDisposable
{
    /// <summary>SRV «карты нет» для шейдера (<c>AO_NONE</c>): видимость 1.</summary>
    public const uint None = uint.MaxValue;

    /// <summary>Плоскостей через пиксель и шагов поиска горизонта в каждую сторону.</summary>
    public const int Slices = 2, Steps = 6;

    /// <summary>Радиус поиска на экране не больше стольких пикселей (камера вплотную).</summary>
    public const float MaxPixels = 160f;

    /// <summary>Доля радиуса у его края, где заслонение сходит на нет.</summary>
    public const float Falloff = 0.4f;

    public const int SlotConstants = 0, SlotDepth = 1, SlotNormal = 2, SlotSource = 3, SlotTarget = 4;

    private const string Shader = "common/gtao.hlsl";
    private static readonly ComputePipelineDesc Gtao = new(Shader, "CsGtao");
    private static readonly ComputePipelineDesc Blur = new(Shader, "CsBlur");

    private readonly FrameData<AoConstants> _constants;
    private readonly SceneTargets _scene;
    private readonly Action<RenderPassContext> _recordGtao, _recordBlur;
    private float _radius = 0.8f, _intensity = 1.5f;
    private RenderTexture _raw;
    private int _rawSrv, _rawUav, _mapUav, _depthSrv, _normalSrv;
    private uint _width, _height;

    internal AmbientOcclusion(GraphicsDevice device, SceneTargets scene)
    {
        _constants = new FrameData<AoConstants>(device, "ao.constants", 1);
        _scene = scene;
        _recordGtao = RecordGtao;
        _recordBlur = RecordBlur;
    }

    /// <summary>Затенение в кадре (со следующего кадра); по умолчанию выключено — игра включает.</summary>
    public bool Enabled { get; set; }

    /// <summary>Радиус поиска заслоняющих, м (0.1…5).</summary>
    public float Radius
    {
        get => _radius;
        set => _radius = value is >= 0.1f and <= 5f ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "радиус 0.1…5 м");
    }

    /// <summary>Сила: видимость в этой степени (0.2…4; 1 — как посчитано, по умолчанию 1.5 — днём рассеянного света мало).</summary>
    public float Intensity
    {
        get => _intensity;
        set => _intensity = value is >= 0.2f and <= 4f ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "сила 0.2…4");
    }

    /// <summary>Карта в этом кадре (<see cref="Prepare"/> при <see cref="Enabled"/>).</summary>
    public bool Active { get; private set; }

    /// <summary>Итог кадра (<c>Texture2D&lt;float2&gt;</c>, r — видимость) — читать в непрозрачном проходе.</summary>
    public RenderTexture Map { get; private set; }

    /// <summary>SRV итога для констант кадра или <see cref="None"/>.</summary>
    public uint Srv { get; private set; } = None;

    internal void BeginFrame()
    {
        Active = false;
        Srv = None;
    }

    /// <summary>Цели кадра; выключено — false. Дальше: проход глубины и нормалей сцены, затем <see cref="AddPasses"/>.</summary>
    public bool Prepare(RenderFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!Enabled) return false;
        var g = frame.Graph;
        var desc = new TextureDesc(TextureFormat.Rg16Float, TextureUsage.Storage, g.Width, g.Height);
        _raw = g.CreateTexture("ao.raw", desc);
        Map = g.CreateTexture("ao.map", desc);
        _rawSrv = g.SrvOf(_raw);
        _rawUav = g.UavOf(_raw);
        _mapUav = g.UavOf(Map);
        Srv = (uint)g.SrvOf(Map);
        Active = true;
        return true;
    }

    /// <summary>
    /// Проходы GTAO по глубине и нормалям, записанным в этом кадре до них: <paramref name="viewProj"/> — матрица, которой они
    /// нарисованы; <paramref name="eye"/>, <paramref name="right"/>, <paramref name="up"/> — глаз и оси камеры в мире.
    /// </summary>
    public void AddPasses(RenderFrame frame, in Matrix4x4 viewProj, Vector3 eye, Vector3 right, Vector3 up)
    {
        if (!Active) throw new InvalidOperationException("AO: AddPasses без Prepare в этом кадре");
        var g = frame.Graph;
        _width = (uint)g.Width;
        _height = (uint)g.Height;
        _constants.Slot[0] = new AoConstants
        {
            InvViewProj = Matrix4x4.Invert(viewProj, out var inv) ? inv : Matrix4x4.Identity,
            ViewProj = viewProj,
            Eye = eye, Radius = _radius,
            Right = right, Intensity = _intensity,
            Up = up,
            Width = _width, Height = _height,
        };
        _depthSrv = _scene.DepthSrv;
        _normalSrv = g.SrvOf(_scene.Normal);
        g.AddComputePass("ao.gtao", FramePhase.Opaque).Read(_scene.Depth).Read(_scene.Normal).Write(_raw).Execute(_recordGtao);
        g.AddComputePass("ao.blur", FramePhase.Opaque).Read(_raw).Write(Map).Execute(_recordBlur);
    }

    private void RecordGtao(RenderPassContext ctx)
    {
        ctx.SetPipeline(Gtao);
        ctx.SetConstant(SlotConstants, _constants.Srv);
        ctx.SetConstant(SlotDepth, _depthSrv);
        ctx.SetConstant(SlotNormal, _normalSrv);
        ctx.SetConstant(SlotTarget, _rawUav);
        ctx.Dispatch((_width + 7) / 8, (_height + 7) / 8);
    }

    private void RecordBlur(RenderPassContext ctx)
    {
        ctx.SetPipeline(Blur);
        ctx.SetConstant(SlotConstants, _constants.Srv);
        ctx.SetConstant(SlotSource, _rawSrv);
        ctx.SetConstant(SlotTarget, _mapUav);
        ctx.Dispatch((_width + 7) / 8, (_height + 7) / 8);
    }

    internal static void Contribute(ShaderContract c)
    {
        c.Section("затенение рассеянного света (AmbientOcclusion)");
        c.Add("AO_NONE", None, "карты нет - видимость 1");
        c.Add("AO_SLICES", Slices);
        c.Add("AO_STEPS", Steps);
        c.Add("AO_MAX_PIXELS", MaxPixels);
        c.Add("AO_FALLOFF", Falloff);
        c.AddSize<AoConstants>("AO_CONSTANTS_SIZE");
        c.AddSlot("AO_CONSTANTS", SlotConstants, "SRV констант (StructuredBuffer<AoConstants>)");
        c.AddSlot("AO_DEPTH", SlotDepth, "SRV глубины сцены");
        c.AddSlot("AO_NORMAL", SlotNormal, "SRV нормалей сцены");
        c.AddSlot("AO_SOURCE", SlotSource, "SRV сырой видимости (размытие)");
        c.AddSlot("AO_TARGET", SlotTarget, "UAV выхода");
    }

    public void Dispose() => _constants.Dispose();
}
