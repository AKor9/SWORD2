using System.Runtime.InteropServices;
using Sword.Engine.Diagnostics;
using Sword.Engine.Platform;
using Sword.Engine.Platform.Win32;

namespace Sword.Engine.Render.Tests;

/// <summary>
/// Асинхронное чтение хранилища (<see cref="GpuReadback"/>) на настоящем устройстве со слоем отладки и GPU-валидацией: данные кадра N — ровно
/// то, что записал кадр N, приходят в начале кадра N + <see cref="GpuReadback.Latency"/> без ожидания GPU, по порядку кадров, каждый кадр —
/// один раз; снятое чтение больше не приходит. Любое предупреждение отладочного слоя — провал.
/// </summary>
[Collection("gpu")]
[Trait("Category", "Gpu")]
public sealed class ReadbackGpuTests : IDisposable, ILogSink
{
    private const string Shader = "graph_tests.hlsl";
    private const uint N = 64;
    private readonly Win32Window _window;
    private readonly Renderer _renderer;
    private readonly List<string> _d3d12 = [];

    public ReadbackGpuTests()
    {
        Log.AddSink(this);
        _window = new Win32Window(new WindowOptions("Sword.Engine.Render.Tests", 64, 64, Activate: false));
        var contract = new ShaderContract();
        Renderer.Contribute(contract);
        _renderer = new Renderer(new RendererOptions(_window.NativeHandle, _window.Width, _window.Height, DebugLayer: true,
            ShaderRoot: Path.Combine(AppContext.BaseDirectory, "Shaders"), GpuValidation: true), contract);
    }

    void ILogSink.Write(in LogEntry entry)
    {
        if (entry.Category == "d3d12" && entry.Level >= LogLevel.Warning) _d3d12.Add(entry.Message);
    }

    public void Dispose()
    {
        _renderer.Dispose();
        _window.Dispose();
        Log.RemoveSink(this);
    }

    [Fact]
    public void Readback_DeliversEachFramesData_AfterLatency_InOrder()
    {
        using var storage = _renderer.CreateStorage<uint>("test.readback", N);
        // Кадр: +1 ко всем элементам — после кадра k (с 1) в хранилище k.
        void Build(RenderFrame f)
        {
            var g = f.Graph;
            var s = g.Import(storage);
            g.AddComputePass("test.increment", FramePhase.Simulation).Write(s).Execute(ctx =>
            {
                ctx.SetPipeline(new ComputePipelineDesc(Shader, "CsIncrement"));
                ctx.SetConstant(0, N);
                ctx.SetConstant(4, g.UavOf(s));
                ctx.Dispatch(N / 64);
            });
        }

        var frameOf = new Dictionary<ulong, uint>();   // номер кадра рендера → сколько кадров сделано после него
        var got = new List<(ulong Frame, uint First, bool Uniform, ulong DeliveredAt)>();
        var readback = _renderer.CreateReadback(storage, N * sizeof(uint), (data, frame) =>
        {
            var values = MemoryMarshal.Cast<byte, uint>(data);
            bool uniform = true;
            foreach (uint v in values) uniform &= v == values[0];
            got.Add((frame, values[0], uniform, _renderer.FrameNumber));
        });
        const int frames = 10;
        for (uint k = 1; k <= frames; k++)
        {
            _window.PumpMessages();
            frameOf[_renderer.FrameNumber] = k;
            _renderer.Render(Build, vsync: false);
        }

        // Первые Latency кадров — ещё в полёте: пришли данные кадров 1 … frames − Latency, по порядку, каждый раз.
        Assert.Equal(frames - GpuReadback.Latency, got.Count);
        for (int i = 0; i < got.Count; i++)
        {
            var (frame, first, uniform, at) = got[i];
            Assert.Equal((uint)(i + 1), frameOf[frame]);
            Assert.True(uniform, $"кадр {frame}: элементы разные");
            Assert.Equal(frameOf[frame], first);                      // ровно то, что записал этот кадр
            Assert.Equal((ulong)GpuReadback.Latency, at - frame);     // в начале кадра N + Latency
        }
        Assert.Equal(got[^1].Frame, readback.LastFrame);

        // Смена размера (номер слота кадра — с нуля): ждущие данные отданы до неё, дальше — снова по порядку, каждый кадр один раз.
        _window.SetClientSize(96, 80);
        _window.PumpMessages();
        _renderer.Resize(_window.Width, _window.Height);
        for (uint k = frames + 1; k <= frames + 6; k++)
        {
            _window.PumpMessages();
            frameOf[_renderer.FrameNumber] = k;
            _renderer.Render(Build, vsync: false);
        }
        Assert.Equal(Enumerable.Range(1, frames + 6 - GpuReadback.Latency).Select(k => (uint)k), got.Select(g => frameOf[g.Frame]));
        Assert.All(got, g => Assert.Equal(frameOf[g.Frame], g.First));

        // Снятое чтение больше не приходит.
        readback.Dispose();
        int count = got.Count;
        for (int k = 0; k < 4; k++) _renderer.Render(Build, vsync: false);
        Assert.Equal(count, got.Count);
        Assert.True(_d3d12.Count == 0, "сообщения отладочного слоя D3D12:\n" + string.Join("\n", _d3d12));
    }
}
