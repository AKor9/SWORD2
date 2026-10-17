using System.Numerics;
using System.Runtime.InteropServices;
using Sword.Engine.Rhi;
using Vortice.Direct3D12;
using Vortice.Mathematics;

namespace Sword.Engine.Render;

/// <summary>Константы TAA и размытия движения (<c>common/taa.hlsl</c>, <c>TaaConstants</c>): строки по 16 байт.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct TaaConstants
{
    public Matrix4x4 InvViewProj;
    public Matrix4x4 ViewProj;
    public Matrix4x4 PrevViewProj;
    public Vector2 JitterUv; public float Blend; public uint Valid;
    public Vector2 Size; public float BlurScale; public float BlurMax;
    public uint Velocity; public uint TilesX; public uint TilesY; public uint Pad;
}

/// <summary>
/// Временное сглаживание и размытие движения (<see cref="Renderer.Taa"/>), только с HDR, в фазе Post первыми (до автоэкспозиции и
/// bloom). Камера дрожит на долю пикселя (<see cref="Jitter(in Matrix4x4, int, int)"/>, Halton 2/3 по 8 кадрам). Скорость пикселя —
/// цель скоростей сцены (<see cref="SceneTargets.Velocity"/>, если вызывающий записал её — <see cref="SetView"/>), без неё и в пустоте —
/// по глубине и матрицам (только камера; персонажи и оружие — признак «не приёмник декалей» — тогда стоят на месте экрана).
/// <c>post.taa</c> смешивает кадр с историей там, откуда пиксель пришёл, история зажата в окрестность 3×3 текущего кадра (YCoCg,
/// среднее ± 1.25σ); размытие — по плиткам 16×16: <c>post.motionBlur.tiles</c> (наибольшая скорость плитки), <c>post.motionBlur.neighbor</c>
/// (из 3×3 плиток), <c>post.motionBlur</c> (выборки вдоль неё с мягким сравнением глубины: движущееся ближнее ложится на дальнее).
/// </summary>
public sealed class TemporalAa : IDisposable
{
    /// <summary>Кадров в последовательности дрожания.</summary>
    public const int JitterPhases = 8;

    /// <summary>Отсечение истории: среднее окрестности ± столько σ.</summary>
    public const float ClipGamma = 1.25f;

    /// <summary>Выборок размытия движения вдоль скорости.</summary>
    public const int BlurSamples = 12;

    /// <summary>Сторона плитки размытия, пиксели (группа 16×16 — одна плитка).</summary>
    public const int Tile = 16;

    /// <summary>Длиннее этого размытие не бывает, пиксели (покрывают соседние плитки).</summary>
    public const float BlurMaxPixels = 32;

    /// <summary>SRV «скоростей нет» (<c>TAA_NONE</c>).</summary>
    public const uint None = uint.MaxValue;

    public const int SlotConstants = 0, SlotScene = 1, SlotDepth = 2, SlotNormal = 3, SlotHistory = 4, SlotTiles = 5, SlotNeighbor = 6,
        SlotTarget = 7;

    private const string Shader = "common/taa.hlsl";
    private static readonly GraphicsPipelineDesc Resolve = new(Shader, "VsFullscreen", "PsResolve");
    private static readonly GraphicsPipelineDesc Blur = new(Shader, "VsFullscreen", "PsMotionBlur");
    private static readonly ComputePipelineDesc TileMax = new(Shader, "CsTileMax");
    private static readonly ComputePipelineDesc NeighborMax = new(Shader, "CsNeighborMax");

    private readonly GraphicsDevice _gd;
    private readonly SceneTargets _scene;
    private readonly FrameData<TaaConstants> _constants;
    private readonly ID3D12DescriptorHeap _rtvHeap;
    private readonly GraphResource[] _history = [new("taa.history0", isBuffer: false), new("taa.history1", isBuffer: false)];
    private readonly RenderTexture[] _historyTextures = new RenderTexture[2];
    private readonly Action<RenderPassContext> _recordResolve, _recordBlur, _recordTiles, _recordNeighbor;
    private Matrix4x4 _viewProj, _jittered, _prevViewProj;
    private bool _viewSet, _historyValid, _velocity;
    private int _tilesUav, _tilesSrv, _neighborUav, _neighborSrv;
    private uint _tilesX, _tilesY;
    private int _frame, _current, _sceneSrv, _blurSrv;
    private float _blend = 0.1f, _shutter = 0.5f;

    internal TemporalAa(GraphicsDevice gd, RenderGraph graph, SceneTargets scene)
    {
        _gd = gd;
        _scene = scene;
        _constants = new FrameData<TaaConstants>(gd, "taa.constants", 1);
        _rtvHeap = gd.Device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, 2));
        int increment = (int)gd.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
        for (int i = 0; i < 2; i++)
        {
            _history[i].TargetFormat = HdrScene.ColorFormat;
            _history[i].Persistent = true;   // история прошлого кадра читается до записи в этом
            _history[i].Rtv = _rtvHeap.GetCPUDescriptorHandleForHeapStart() + i * increment;
            _historyTextures[i] = graph.ImportPersistent(_history[i]);
        }
        _recordResolve = RecordResolve;
        _recordBlur = RecordBlur;
        _recordTiles = RecordTiles;
        _recordNeighbor = RecordNeighbor;
    }

    /// <summary>TAA в кадре (со следующего кадра); по умолчанию выключено — игра включает.</summary>
    public bool Enabled { get; set; }

    /// <summary>Размытие движения камеры (нужен <see cref="Enabled"/>).</summary>
    public bool MotionBlur { get; set; }

    /// <summary>Доля текущего кадра в смеси (0.02…0.5): меньше — глаже и дольше след.</summary>
    public float Blend
    {
        get => _blend;
        set => _blend = value is >= 0.02f and <= 0.5f ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "доля 0.02…0.5");
    }

    /// <summary>Выдержка размытия: доля перемещения за кадр (0…1).</summary>
    public float Shutter
    {
        get => _shutter;
        set => _shutter = value is >= 0 and <= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "выдержка 0…1");
    }

    /// <summary>Сдвиг кадра, пиксели (−0.5…0.5): Halton 2/3; выключено — 0.</summary>
    public Vector2 JitterPixels => Enabled ? new Vector2(Halton(_frame % JitterPhases + 1, 2) - 0.5f, Halton(_frame % JitterPhases + 1, 3) - 0.5f) : Vector2.Zero;

    /// <summary>
    /// Матрица кадра со сдвигом <see cref="JitterPixels"/> для экрана <paramref name="width"/>×<paramref name="height"/> (вектор-строка): ею
    /// рисуется сцена; без сдвига — для скоростей (<see cref="SetView"/>).
    /// </summary>
    public Matrix4x4 Jitter(in Matrix4x4 viewProj, int width, int height)
    {
        var j = JitterPixels;
        if (j == Vector2.Zero) return viewProj;
        // Вектор-строка: clip' = clip · S, где строка w матрицы S сдвигает x и y на w · сдвиг NDC (y экрана — вниз).
        var shift = Matrix4x4.Identity;
        shift.M41 = j.X * 2 / Math.Max(width, 1);
        shift.M42 = -j.Y * 2 / Math.Max(height, 1);
        return viewProj * shift;
    }

    /// <summary>
    /// Вид кадра: <paramref name="viewProj"/> без сдвига и <paramref name="jittered"/> — та, которой нарисованы глубина и цвет;
    /// <paramref name="velocity"/> — вызывающий записал в этом кадре <see cref="SceneTargets.Velocity"/> (без сдвига: этот кадр минус прошлый).
    /// </summary>
    public void SetView(in Matrix4x4 viewProj, in Matrix4x4 jittered, bool velocity = false)
    {
        _viewProj = viewProj;
        _jittered = jittered;
        _velocity = velocity;
        _viewSet = true;
    }

    /// <summary>Сбросить историю (смена сцены, телепорт камеры).</summary>
    public void Reset() => _historyValid = false;

    internal static float Halton(int index, int b)
    {
        float f = 1, r = 0;
        for (int i = index; i > 0; i /= b)
        {
            f /= b;
            r += f * (i % b);
        }
        return r;
    }

    /// <summary>Цели истории под размер кадра; выключено — освободить.</summary>
    internal void Ensure(bool enabled)
    {
        if (!enabled)
        {
            Release();
            return;
        }
        int w = _gd.BackBufferWidth, h = _gd.BackBufferHeight;
        if (_history[0].Handle is not null && _history[0].Width == w && _history[0].Height == h) return;
        Release();
        for (int i = 0; i < 2; i++)
        {
            var r = _history[i];
            var res = _gd.Device.CreateCommittedResource(HeapType.Default, HeapFlags.None,
                ResourceDescription.Texture2D(HdrScene.ColorFormat, (uint)w, (uint)h, 1, 1, flags: ResourceFlags.AllowRenderTarget),
                ResourceStates.PixelShaderResource, new ClearValue(HdrScene.ColorFormat, new Color4(0, 0, 0, 1)));
            res.Name = r.Name;
            _gd.Device.CreateRenderTargetView(res, null, r.Rtv!.Value);
            r.Srv = _gd.Bindless.Allocate();
            _gd.Device.CreateShaderResourceView(res, null, _gd.Bindless.Cpu(r.Srv));
            r.Handle = res;
            r.State = ResourceStates.PixelShaderResource;
            r.Width = w;
            r.Height = h;
            r.ClearColor = new Vector4(0, 0, 0, 1);
        }
        _historyValid = false;
    }

    private void Release()
    {
        foreach (var r in _history)
        {
            if (r.Handle is null) continue;
            _gd.DeferRelease(r.Handle, r.Srv);
            r.Handle = null;
            r.Srv = -1;
        }
        _historyValid = false;
    }

    /// <summary>
    /// Проходы кадра над цветом сцены <paramref name="scene"/> — в фазу Post; вернёт текстуру, которую дальше читает постобработка
    /// (история этого кадра или размытая копия). Вид не задан в этом кадре (<see cref="SetView"/>) — без TAA (вернёт сцену).
    /// </summary>
    internal (RenderTexture Output, int Srv) Add(RenderGraph graph, RenderTexture scene, int sceneSrv)
    {
        if (!_viewSet || _history[0].Handle is null)
        {
            _historyValid = false;
            _viewSet = false;
            return (scene, sceneSrv);
        }
        _viewSet = false;
        int previous = _current;
        _current ^= 1;
        _sceneSrv = sceneSrv;
        _tilesX = (uint)((graph.Width + Tile - 1) / Tile);
        _tilesY = (uint)((graph.Height + Tile - 1) / Tile);
        _constants.Slot[0] = new TaaConstants
        {
            InvViewProj = Matrix4x4.Invert(_jittered, out var inv) ? inv : Matrix4x4.Identity,
            ViewProj = _viewProj,
            PrevViewProj = _historyValid ? _prevViewProj : _viewProj,
            JitterUv = JitterPixels / new Vector2(graph.Width, graph.Height),
            Blend = _blend,
            Valid = _historyValid ? 1u : 0u,
            Size = new Vector2(graph.Width, graph.Height),
            BlurScale = _shutter,
            BlurMax = BlurMaxPixels,
            Velocity = _velocity ? (uint)graph.SrvOf(_scene.Velocity) : None,
            TilesX = _tilesX, TilesY = _tilesY,
        };
        _historySrv = _history[previous].Srv;
        var output = _historyTextures[_current];
        var resolve = graph.AddPass("post.taa", FramePhase.Post).Read(scene).Read(_scene.Depth).Read(_scene.Normal).Read(_historyTextures[previous]);
        if (_velocity) resolve.Read(_scene.Velocity);
        resolve.Color(output).Execute(_recordResolve);
        _prevViewProj = _viewProj;
        _historyValid = true;
        _frame++;
        _normalSrv = graph.SrvOf(_scene.Normal);
        if (!MotionBlur || _shutter <= 0) return (output, _history[_current].Srv);
        var tileDesc = new TextureDesc(TextureFormat.Rg16Float, TextureUsage.Storage, (int)_tilesX, (int)_tilesY);
        var tiles = graph.CreateTexture("taa.tiles", tileDesc);
        var neighbor = graph.CreateTexture("taa.neighbor", tileDesc);
        (_tilesUav, _tilesSrv, _neighborUav, _neighborSrv) = (graph.UavOf(tiles), graph.SrvOf(tiles), graph.UavOf(neighbor), graph.SrvOf(neighbor));
        var tilePass = graph.AddComputePass("post.motionBlur.tiles", FramePhase.Post).Read(_scene.Depth).Read(_scene.Normal);
        if (_velocity) tilePass.Read(_scene.Velocity);
        tilePass.Write(tiles).Execute(_recordTiles);
        graph.AddComputePass("post.motionBlur.neighbor", FramePhase.Post).Read(tiles).Write(neighbor).Execute(_recordNeighbor);
        var blurred = graph.CreateTexture("taa.blur", new TextureDesc(TextureFormat.Rgba16Float, TextureUsage.ColorTarget, graph.Width, graph.Height));
        _blurSrv = _history[_current].Srv;
        var blur = graph.AddPass("post.motionBlur", FramePhase.Post).Read(output).Read(_scene.Depth).Read(_scene.Normal).Read(neighbor);
        if (_velocity) blur.Read(_scene.Velocity);
        blur.Color(blurred).Execute(_recordBlur);
        return (blurred, graph.SrvOf(blurred));
    }

    private int _historySrv, _normalSrv;

    private void RecordResolve(RenderPassContext ctx)
    {
        ctx.SetPipeline(Resolve);
        ctx.SetConstant(SlotConstants, _constants.Srv);
        ctx.SetConstant(SlotScene, _sceneSrv);
        ctx.SetConstant(SlotDepth, _scene.DepthSrv);
        ctx.SetConstant(SlotNormal, _normalSrv);
        ctx.SetConstant(SlotHistory, _historySrv);
        ctx.Draw(3);
    }

    private void RecordBlur(RenderPassContext ctx)
    {
        ctx.SetPipeline(Blur);
        ctx.SetConstant(SlotConstants, _constants.Srv);
        ctx.SetConstant(SlotScene, _blurSrv);
        ctx.SetConstant(SlotDepth, _scene.DepthSrv);
        ctx.SetConstant(SlotNormal, _normalSrv);
        ctx.SetConstant(SlotNeighbor, _neighborSrv);
        ctx.Draw(3);
    }

    private void RecordTiles(RenderPassContext ctx)
    {
        ctx.SetPipeline(TileMax);
        ctx.SetConstant(SlotConstants, _constants.Srv);
        ctx.SetConstant(SlotDepth, _scene.DepthSrv);
        ctx.SetConstant(SlotNormal, _normalSrv);
        ctx.SetConstant(SlotTarget, _tilesUav);
        ctx.Dispatch(_tilesX, _tilesY);
    }

    private void RecordNeighbor(RenderPassContext ctx)
    {
        ctx.SetPipeline(NeighborMax);
        ctx.SetConstant(SlotConstants, _constants.Srv);
        ctx.SetConstant(SlotTiles, _tilesSrv);
        ctx.SetConstant(SlotTarget, _neighborUav);
        ctx.Dispatch((_tilesX + 7) / 8, (_tilesY + 7) / 8);
    }

    internal static void Contribute(ShaderContract c)
    {
        c.Section("временное сглаживание (TemporalAa)");
        c.Add("TAA_CLIP_GAMMA", ClipGamma, "отсечение истории: среднее +- столько сигм");
        c.Add("TAA_BLUR_SAMPLES", BlurSamples, "выборок размытия движения");
        c.Add("TAA_TILE", Tile, "сторона плитки размытия, пиксели");
        c.Add("TAA_NONE", None, "скоростей нет");
        c.AddSize<TaaConstants>("TAA_CONSTANTS_SIZE");
        c.AddSlot("TAA_CONSTANTS", SlotConstants, "SRV констант (StructuredBuffer<TaaConstants>)");
        c.AddSlot("TAA_SCENE", SlotScene, "SRV цвета (сцена или итог TAA для размытия)");
        c.AddSlot("TAA_DEPTH", SlotDepth, "SRV глубины сцены");
        c.AddSlot("TAA_NORMAL", SlotNormal, "SRV нормалей сцены (a - статичный мир)");
        c.AddSlot("TAA_HISTORY", SlotHistory, "SRV истории прошлого кадра");
        c.AddSlot("TAA_TILES", SlotTiles, "SRV наибольших скоростей плиток");
        c.AddSlot("TAA_NEIGHBOR", SlotNeighbor, "SRV наибольших скоростей 3x3 плиток");
        c.AddSlot("TAA_TARGET", SlotTarget, "UAV выхода проходов плиток");
    }

    public void Dispose()
    {
        Release();
        _constants.Dispose();
        _rtvHeap.Dispose();
    }
}
