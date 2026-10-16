using System.Diagnostics;
using Sword.Engine.Diagnostics;
using Sword.Engine.Platform;
using Sword.Engine.Platform.Win32;
using Xunit.Abstractions;

namespace Sword.Engine.Render.Tests;

/// <summary>
/// GPU-время проходов: таймстемпы вокруг каждого прохода, разбор кадра N − 3 без стойла, сверка кадров по номеру, 0 байт на
/// кадр. Любое предупреждение отладочного слоя — провал.
/// </summary>
[Collection("gpu")]
[Trait("Category", "Gpu")]
public sealed class GpuTimesGpuTests : IDisposable, ILogSink
{
    private const string Shader = "graph_tests.hlsl";
    private readonly Win32Window _window;
    private readonly Renderer _renderer;
    private readonly List<string> _d3d12 = [];
    private readonly ITestOutputHelper _output;

    public GpuTimesGpuTests(ITestOutputHelper output)
    {
        _output = output;
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

    private void AssertNoDebugMessages() =>
        Assert.True(_d3d12.Count == 0, "сообщения отладочного слоя D3D12:\n" + string.Join("\n", _d3d12));

    /// <summary>Вычислительный проход <paramref name="name"/>: <paramref name="steps"/> шагов зависимых вычислений на поток.</summary>
    private static void Spin(RenderFrame f, GpuStorage storage, string name, uint steps)
    {
        var g = f.Graph;
        var s = g.Import(storage);
        g.AddComputePass(name).Write(s).Execute(ctx =>
        {
            ctx.SetPipeline(new ComputePipelineDesc(Shader, "CsSpin"));
            ctx.SetConstant(0, steps);
            ctx.SetConstant(4, g.UavOf(s));
            ctx.Dispatch(1);
        });
    }

    [Fact]
    public void LongPass_HasGpuTime_AndPassesFitInFrame()
    {
        using var storage = _renderer.CreateStorage<uint>("test.spin", 64);
        void Build(RenderFrame f)
        {
            Spin(f, storage, "test.empty", 1);
            Spin(f, storage, "test.long", 200_000);
            f.Graph.AddPass("test.clear").Color(f.Graph.BackBuffer, System.Numerics.Vector4.Zero);
        }

        // Установившийся режим: в кадре N видны времена кадра N − 3 (кадры в полёте), CPU их не ждал.
        for (int i = 0; i < 6; i++)
        {
            _window.PumpMessages();
            _renderer.Render(Build, vsync: false);
        }
        var gpu = _renderer.Gpu;
        Assert.True(gpu.Valid);
        Assert.Equal(3, gpu.Lag);
        Assert.Equal(_renderer.FrameNumber - 4, gpu.Frame);

        // Ожидание простоя — опубликован последний отправленный кадр.
        _renderer.WaitForIdle();
        Assert.Equal(0, gpu.Lag);
        Assert.Equal(_renderer.FrameNumber - 1, gpu.Frame);
        Assert.Equal(["test.empty", "test.long", "test.clear"], Enumerable.Range(0, gpu.Count).Select(gpu.NameOf));
        double empty = gpu.MillisecondsOf(0), spin = gpu.MillisecondsOf(1), clear = gpu.MillisecondsOf(2);
        _output.WriteLine($"кадр {gpu.FrameMs:F4} мс, проходы {gpu.PassesMs:F4}: empty {empty:F4}, long {spin:F4}, clear {clear:F4}; " +
            $"частота {gpu.TimestampFrequency:N0} Гц");
        Assert.True(spin > 0.05, $"долгий проход {spin} мс — ждали заметно больше нуля");
        Assert.True(spin > 10 * empty, $"долгий проход {spin} мс, пустой {empty} мс — ждали больше в 10 раз");
        Assert.True(gpu.PassesMs <= gpu.FrameMs + 1e-6, $"сумма проходов {gpu.PassesMs} мс больше кадра {gpu.FrameMs} мс");
        Assert.True(gpu.FrameMs >= spin);
        Assert.Equal(spin, gpu.MillisecondsOf("test.long"));
        Assert.Null(gpu.MillisecondsOf("test.none"));

        // Итоги: проходы графа с GPU-временем того же кадра (по имени).
        var joined = gpu.Join(_renderer.Graph.LastFrame);
        Assert.Equal(Math.Round(spin, 5), joined[1].GpuMs);
        Assert.Equal(0, gpu.Report()!.Lag);
        AssertNoDebugMessages();
    }

    [Fact]
    public void AbandonedFrame_Resize_WaitForIdle_NeverMixFrames()
    {
        // У каждого кадра — проход с номером кадра рендера в имени: опубликованные времена сверяются с номером кадра.
        var names = new Dictionary<ulong, string>();
        void Build(RenderFrame f)
        {
            if (!names.TryGetValue(f.Number, out var name)) names[f.Number] = name = $"test.f{f.Number}";
            f.Graph.AddPass(name).Color(f.Graph.BackBuffer, System.Numerics.Vector4.Zero);
        }
        void Bad(RenderFrame f)
        {
            f.Graph.AddPass($"test.bad{f.Number}").Color(f.Graph.BackBuffer, System.Numerics.Vector4.Zero);
            f.Graph.AddPass("test.empty");   // нет целей — кадр бросается до отправки
        }
        long sequence = 0;
        ulong last = 0;
        int checks = 0;
        void Check()
        {
            var gpu = _renderer.Gpu;
            if (!gpu.Valid) return;
            Assert.Equal(1, gpu.Count);
            Assert.Equal($"test.f{gpu.Frame}", gpu.NameOf(0));   // не брошенный и не чужой кадр
            Assert.True(gpu.Frame < _renderer.FrameNumber);
            if (gpu.Sequence != sequence)
            {
                Assert.True(sequence == 0 || gpu.Frame > last, $"номер опубликованного кадра не растёт: {last} → {gpu.Frame}");
                sequence = gpu.Sequence;
                last = gpu.Frame;
            }
            Assert.True(gpu.PassesMs <= gpu.FrameMs + 1e-6);
            checks++;
        }
        void Frames(int n)
        {
            for (int i = 0; i < n; i++)
            {
                _window.PumpMessages();
                _renderer.Render(Build, vsync: false);
                Check();
            }
        }
        void Abandon()
        {
            Assert.Throws<InvalidOperationException>(() => _renderer.Render(Bad, vsync: false));
            Check();
        }

        Frames(5);
        Abandon();
        Frames(2);
        _renderer.Resize(96, 80);   // ждёт простоя: опубликован последний отправленный, индекс заднего буфера сброшен
        Check();
        Assert.Equal(0, _renderer.Gpu.Lag);
        Frames(3);
        Abandon();
        Abandon();
        _renderer.Resize(64, 64);
        Check();
        Frames(5);
        _renderer.WaitForIdle();
        Check();
        Assert.Equal(_renderer.FrameNumber - 1, _renderer.Gpu.Frame);
        Assert.True(checks >= 15);
        AssertNoDebugMessages();
    }

    /// <summary>
    /// Проходов больше <c>MaxScopes − 1</c> — лишние не замеряются (<c>Dropped</c>), замеренные — свои; кадр
    /// без проходов — кадр есть, проходов нет; порядок проходов меняется от кадра к кадру — время остаётся у своего имени.
    /// </summary>
    [Fact]
    public void Overflow_EmptyFrame_Reorder_TimesStayWithTheirPass()
    {
        const int many = Sword.Engine.Rhi.GpuTimer.MaxScopes + 44;   // 300 проходов: 255 замерены, 45 — нет
        void Many(RenderFrame f)
        {
            for (int i = 0; i < many; i++) f.Graph.AddPass($"test.p{i}").Color(f.Graph.BackBuffer, System.Numerics.Vector4.Zero);
        }
        _renderer.Render(Many, vsync: false);
        _renderer.WaitForIdle();
        var gpu = _renderer.Gpu;
        Assert.Equal(Sword.Engine.Rhi.GpuTimer.MaxScopes - 1, gpu.Count);
        Assert.Equal(many - gpu.Count, gpu.Dropped);
        Assert.Equal("test.p254", gpu.NameOf(gpu.Count - 1));
        var joined = gpu.Join(_renderer.Graph.LastFrame);
        Assert.Equal(many, joined.Count);
        Assert.NotNull(joined[254].GpuMs);
        Assert.Null(joined[255].GpuMs);   // не замерен — null, а не время чужого прохода
        Assert.True(gpu.PassesMs <= gpu.FrameMs + 1e-6);

        _renderer.Render(_ => { }, vsync: false);   // кадр без проходов: только кадр целиком
        _renderer.WaitForIdle();
        Assert.Equal((0, 0), (gpu.Count, gpu.Dropped));
        Assert.Equal(0, gpu.PassesMs);
        Assert.True(gpu.FrameMs >= 0);

        using var storage = _renderer.CreateStorage<uint>("test.order", 64);
        bool swap = false;
        void Swapped(RenderFrame f)
        {
            if (swap) Spin(f, storage, "test.short", 1);
            Spin(f, storage, "test.long", 200_000);
            if (!swap) Spin(f, storage, "test.short", 1);
            f.Graph.AddPass("test.clear").Color(f.Graph.BackBuffer, System.Numerics.Vector4.Zero);
        }
        for (int i = 0; i < 7; i++)
        {
            swap = i % 2 == 1;
            _renderer.Render(Swapped, vsync: false);
            if (!gpu.Valid || gpu.Count != 3) continue;
            double shortMs = gpu.MillisecondsOf("test.short")!.Value, longMs = gpu.MillisecondsOf("test.long")!.Value;
            Assert.True(longMs > 10 * shortMs, $"кадр {gpu.Frame}: long {longMs} мс, short {shortMs} мс — времена перепутаны порядком");
        }
        _renderer.WaitForIdle();
        var last = gpu.Join(_renderer.Graph.LastFrame);
        Assert.Equal(["test.long", "test.short", "test.clear"], last.Select(p => p.Name));   // последний кадр — без перестановки
        Assert.True(last[0].GpuMs > 10 * last[1].GpuMs);
        AssertNoDebugMessages();
    }

    /// <remarks>
    /// Кадр графа — 0 Б (отчёт прохода — по запросу, барьеры и дескрипторы — без массивов на проход), поэтому
    /// здесь не «столько же, сколько без таймстемпов», а 0: работа проходов — готовые делегаты (замыкание на кадр выделяло бы само).
    /// </remarks>
    [Fact]
    public void Timestamps_AddNoBytesPerFrame()
    {
        using var storage = _renderer.CreateStorage<uint>("test.bytes", 64);
        RenderGraph g = null!;
        RenderBuffer s = default;
        Action<RenderPassContext> spin = ctx =>
        {
            ctx.SetPipeline(new ComputePipelineDesc(Shader, "CsSpin"));
            ctx.SetConstant(0, 16u);
            ctx.SetConstant(4, g.UavOf(s));
            ctx.Dispatch(1);
        };
        Action<RenderFrame> build = f =>
        {
            g = f.Graph;
            s = g.Import(storage);
            g.AddComputePass("test.a").Write(s).Execute(spin);
            g.AddComputePass("test.b").Write(s).Execute(spin);
            g.AddPass("test.clear").Color(g.BackBuffer, System.Numerics.Vector4.Zero);
        };
        (long Bytes, double RecordMs) Measure(int frames)
        {
            for (int i = 0; i < 20; i++) _renderer.Render(build, vsync: false);   // прогрев: пул, конвейеры, JIT
            long before = GC.GetAllocatedBytesForCurrentThread();
            double record = 0;
            for (int i = 0; i < frames; i++)
            {
                _renderer.Render(build, vsync: false);
                record += _renderer.LastRecordMs;
            }
            return ((GC.GetAllocatedBytesForCurrentThread() - before) / frames, record / frames);
        }

        const int frames = 200;
        var on = Measure(frames);
        long sequence = _renderer.Gpu.Sequence;
        _renderer.Gpu.Enabled = false;
        var off = Measure(frames);
        Assert.Equal(sequence + 3, _renderer.Gpu.Sequence);   // выключенные таймстемпы — новых времён нет (кроме трёх в полёте)
        _renderer.Gpu.Enabled = true;
        var again = Measure(frames);
        _output.WriteLine($"байт на кадр: с таймстемпами {on.Bytes}, без {off.Bytes}, снова с ними {again.Bytes}; запись кадра на CPU " +
            $"(отладочный слой, GPU-валидация): {on.RecordMs:F4} / {off.RecordMs:F4} / {again.RecordMs:F4} мс");
        Assert.Equal(0, off.Bytes);
        Assert.Equal(0, on.Bytes);
        Assert.Equal(0, again.Bytes);
        Assert.True(_renderer.Gpu.Sequence > sequence + 3);
        AssertNoDebugMessages();
    }
}
