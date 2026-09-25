using Sword.Engine.Diagnostics;
using Sword.Engine.Platform;
using Sword.Engine.Platform.Win32;
using Sword.Engine.Render;

namespace Sword.Engine.Render.Tests;

/// <summary>Тесты с устройством D3D12 идут по одному: окно, устройство и журнал на процесс общие.</summary>
[CollectionDefinition("gpu", DisableParallelization = true)]
public sealed class GpuCollection;

/// <summary>
/// Рендер-граф v0 на настоящем устройстве (скрытое окно, отладочный слой D3D12 с GPU-валидацией): временные текстуры и
/// буферы из пула,
/// вычислительные проходы на graphics-очереди, переходы и барьеры UAV, инварианты. Результат GPU читается обратно через
/// хранилище; любое предупреждение или ошибка отладочного слоя — провал.
/// </summary>
[Collection("gpu")]
[Trait("Category", "Gpu")]
public sealed class RenderGraphGpuTests : IDisposable, ILogSink
{
    private const string Shader = "graph_tests.hlsl";
    private readonly Win32Window _window;
    private readonly Renderer _renderer;
    private readonly List<string> _d3d12 = [];

    public RenderGraphGpuTests()
    {
        Log.AddSink(this);
        _window = new Win32Window(new WindowOptions("Sword.Engine.Render.Tests", 64, 64, Activate: false));
        var contract = new ShaderContract();
        Renderer.Contribute(contract);
        // GPU-валидация: шейдеры берут ресурсы из bindless-кучи, и только она видит ресурс не в том состоянии.
        _renderer = new Renderer(new RendererOptions(_window.NativeHandle, _window.Width, _window.Height, DebugLayer: true,
            ShaderRoot: Path.Combine(AppContext.BaseDirectory, "Shaders"), GpuValidation: true), contract);
    }

    void ILogSink.Write(in LogEntry entry)
    {
        // Сведения слоя (например, «GPU-валидация включена») — не проблема; предупреждение и ошибка — провал.
        if (entry.Category == "d3d12" && entry.Level >= LogLevel.Warning) _d3d12.Add(entry.Message);
    }

    public void Dispose()
    {
        _renderer.Dispose();
        _window.Dispose();
        Log.RemoveSink(this);
    }

    private void Frame(Action<RenderFrame> build)
    {
        _window.PumpMessages();
        _renderer.Render(build, vsync: false);
    }

    private void AssertNoDebugMessages() =>
        Assert.True(_d3d12.Count == 0, "сообщения отладочного слоя D3D12:\n" + string.Join("\n", _d3d12));

    [Fact]
    public void ComputeGraphicsCompute_ChainThroughTransientResources()
    {
        const int n = 16;
        using var result = _renderer.CreateStorage<float>("test.result", n * n);
        Frame(f =>
        {
            var g = f.Graph;
            var a = g.CreateTexture("test.a", new TextureDesc(TextureFormat.R32Float, TextureUsage.Storage, n, n));
            var b = g.CreateTexture("test.b", new TextureDesc(TextureFormat.R32Float, TextureUsage.ColorTarget, n, n));
            var s = g.Import(result);
            g.AddComputePass("test.fill").Write(a).Execute(ctx =>
            {
                ctx.SetPipeline(new ComputePipelineDesc(Shader, "CsFill"));
                ctx.SetConstant(0, n);
                ctx.SetConstant(1, g.UavOf(a));
                ctx.Dispatch(n / 8, n / 8);
            });
            g.AddPass("test.double").Color(b).Read(a).Execute(ctx =>
            {
                ctx.SetPipeline(new GraphicsPipelineDesc(Shader, "VsFull", "PsDouble"));
                ctx.SetConstant(2, g.SrvOf(a));
                ctx.Draw(3);
            });
            g.AddComputePass("test.copy").Read(b).Write(s).Execute(ctx =>
            {
                ctx.SetPipeline(new ComputePipelineDesc(Shader, "CsCopy"));
                ctx.SetConstant(0, n);
                ctx.SetConstant(3, g.SrvOf(b));
                ctx.SetConstant(4, g.UavOf(s));
                ctx.Dispatch(n / 8, n / 8);
            });
        });

        var values = new float[n * n];
        _renderer.ReadStorage(result, values.AsSpan());
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                Assert.Equal((x + 100f * y) * 2, values[y * n + x]);

        var passes = _renderer.Graph.LastFrame;
        Assert.Equal(["test.fill", "test.double", "test.copy"], passes.Select(p => p.Name));
        Assert.Equal([PassKind.Compute, PassKind.Graphics, PassKind.Compute], passes.Select(p => p.Kind));
        Assert.Equal(1, passes[0].Dispatches);
        Assert.Equal(1, passes[1].Draws);
        Assert.Equal(["test.a"], passes[1].Reads);
        AssertNoDebugMessages();
    }

    [Fact]
    public void SequentialUavWrites_AreOrderedByBarriers()
    {
        const int n = 256;
        var initial = Enumerable.Range(0, n).Select(i => (uint)i).ToArray();
        using var storage = _renderer.CreateStorage<uint>("test.counter", initial);
        Frame(f =>
        {
            var g = f.Graph;
            var s = g.Import(storage);
            for (int k = 0; k < 3; k++)
                g.AddComputePass($"test.increment{k}").Write(s).Execute(ctx =>
                {
                    ctx.SetPipeline(new ComputePipelineDesc(Shader, "CsIncrement"));
                    ctx.SetConstant(0, n);
                    ctx.SetConstant(4, g.UavOf(s));
                    ctx.Dispatch(n / 64);
                });
        });

        var values = new uint[n];
        _renderer.ReadStorage(storage, values.AsSpan());
        Assert.Equal(initial.Select(v => v + 3), values);
        // Первый проход — переход в UAV, каждый следующий — барьер UAV после записи предыдущего.
        Assert.All(_renderer.Graph.LastFrame, p => Assert.Equal(1, p.Barriers));
        AssertNoDebugMessages();
    }

    [Fact]
    public void TransientPool_ReusesEntriesAndReleasesStaleOnes()
    {
        void ClearTwo(RenderFrame f, int size)
        {
            var desc = new TextureDesc(TextureFormat.Rgba8Unorm, TextureUsage.ColorTarget, size, size, ClearColor: System.Numerics.Vector4.One);
            var first = f.Graph.CreateTexture("test.first", desc);
            var second = f.Graph.CreateTexture("test.second", desc);
            f.Graph.AddPass("test.clear").Color(first, System.Numerics.Vector4.One).Color(second, System.Numerics.Vector4.One);
        }

        for (int i = 0; i < 4; i++) Frame(f => ClearTwo(f, 32));
        // Две текстуры одного вида в кадре — две записи; из кадра в кадр — те же.
        Assert.Equal(2, _renderer.Graph.PooledResources);

        for (int i = 0; i < 12; i++) Frame(f => ClearTwo(f, 16));
        // Записи 32×32 не выдавались дольше срока хранения — освобождены, остались 16×16.
        Assert.Equal(2, _renderer.Graph.PooledResources);
        AssertNoDebugMessages();
    }

    [Fact]
    public void Invariants_RejectFrameBeforeAnyCommand_AndRendererRecovers()
    {
        var desc = new TextureDesc(TextureFormat.R32Float, TextureUsage.ColorTarget | TextureUsage.Storage, 8, 8);

        var unwritten = Assert.Throws<InvalidOperationException>(() => Frame(f =>
        {
            var t = f.Graph.CreateTexture("test.unwritten", desc);
            var target = f.Graph.CreateTexture("test.target", desc);
            f.Graph.AddPass("test.reader").Color(target).Read(t);
        }));
        Assert.Contains("test.unwritten", unwritten.Message);
        Assert.Contains("никто не записал", unwritten.Message);

        var both = Assert.Throws<InvalidOperationException>(() => Frame(f =>
        {
            var t = f.Graph.CreateTexture("test.both", desc);
            f.Graph.AddComputePass("test.self").Write(t).Read(t).Execute(_ => { });
        }));
        Assert.Contains("пишет (UAV) и читает", both.Message);

        var colorInCompute = Assert.Throws<InvalidOperationException>(() => Frame(f =>
            f.Graph.AddComputePass("test.compute").Color(f.Graph.CreateTexture("test.c", desc))));
        Assert.Contains("у вычислительного прохода", colorInCompute.Message);

        var noTargets = Assert.Throws<InvalidOperationException>(() => Frame(f => f.Graph.AddPass("test.empty")));
        Assert.Contains("нет целей", noTargets.Message);

        var wrongClear = Assert.Throws<InvalidOperationException>(() => Frame(f =>
            f.Graph.AddPass("test.clear").Color(f.Graph.CreateTexture("test.cleared", desc), System.Numerics.Vector4.One)));
        Assert.Contains("значением очистки", wrongClear.Message);

        // Брошенные кадры не сломали устройство: следующий кадр собирается и исполняется.
        Frame(f => f.Graph.AddPass("test.ok").Color(f.Graph.BackBuffer, System.Numerics.Vector4.Zero));
        Assert.Equal("test.ok", Assert.Single(_renderer.Graph.LastFrame).Name);
        AssertNoDebugMessages();
    }

    /// <summary>
    /// Кадр графа (docs/history/profiler.md) — compute → графика с очисткой →
    /// compute через временные текстуры, переходы и барьеры UAV, проверка инвариантов, привязка целей — 0 байт на кадр (было
    /// ~290 Б на такой кадр: отчёт прохода, списки имён, <c>bool[]</c>, массивы барьеров и дескрипторов). Отчёт
    /// <see cref="RenderGraph.LastFrame"/> строится по запросу — тот же, что строился каждый кадр, один на кадр;
    /// <see cref="RenderGraph.LastFrameDraws"/> — без отчёта.
    /// </summary>
    [Fact]
    public void SteadyFrame_AllocatesNothing_ReportOnRequest()
    {
        const int n = 16;
        using var result = _renderer.CreateStorage<float>("test.result", n * n);
        RenderGraph g = null!;
        RenderTexture a = default, b = default;
        RenderBuffer s = default;
        // Работа проходов — готовые делегаты (замыкание на кадр выделяло бы само по себе): ресурсы кадра — в локальных выше.
        Action<RenderPassContext> fill = ctx =>
        {
            ctx.SetPipeline(new ComputePipelineDesc(Shader, "CsFill"));
            ctx.SetConstant(0, n);
            ctx.SetConstant(1, g.UavOf(a));
            ctx.Dispatch(n / 8, n / 8);
        };
        Action<RenderPassContext> twice = ctx =>
        {
            ctx.SetPipeline(new GraphicsPipelineDesc(Shader, "VsFull", "PsDouble"));
            ctx.SetConstant(2, g.SrvOf(a));
            ctx.Draw(3);
        };
        Action<RenderPassContext> copy = ctx =>
        {
            ctx.SetPipeline(new ComputePipelineDesc(Shader, "CsCopy"));
            ctx.SetConstant(0, n);
            ctx.SetConstant(3, g.SrvOf(b));
            ctx.SetConstant(4, g.UavOf(s));
            ctx.Dispatch(n / 8, n / 8);
        };
        Action<RenderFrame> build = f =>
        {
            g = f.Graph;
            a = g.CreateTexture("test.a", new TextureDesc(TextureFormat.R32Float, TextureUsage.Storage, n, n));
            b = g.CreateTexture("test.b", new TextureDesc(TextureFormat.R32Float, TextureUsage.ColorTarget, n, n));
            s = g.Import(result);
            g.AddComputePass("test.fill").Write(a).Execute(fill);
            g.AddPass("test.double").Color(b, System.Numerics.Vector4.Zero).Read(a).Execute(twice);
            g.AddComputePass("test.copy").Read(b).Write(s).Execute(copy);
        };

        for (int i = 0; i < 30; i++) Frame(build);   // прогрев: пул, конвейеры, JIT
        const int frames = 200;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < frames; i++) _renderer.Render(build, vsync: false);
        long bytes = (GC.GetAllocatedBytesForCurrentThread() - before) / frames;
        Assert.True(bytes == 0, $"кадр графа выделил {bytes} Б");

        Assert.Equal(3, _renderer.Graph.LastFramePasses);
        Assert.Equal(1, _renderer.Graph.LastFrameDraws);
        var passes = _renderer.Graph.LastFrame;
        Assert.Same(passes, _renderer.Graph.LastFrame);   // до следующего кадра — тот же отчёт
        Assert.Equal(["test.fill", "test.double", "test.copy"], passes.Select(p => p.Name));
        Assert.Equal([PassKind.Compute, PassKind.Graphics, PassKind.Compute], passes.Select(p => p.Kind));
        Assert.Equal(["test.a"], passes[0].Writes);
        Assert.Equal(["test.b"], passes[1].Writes);
        Assert.Equal(["test.a"], passes[1].Reads);
        Assert.Equal(["test.result"], passes[2].Writes);
        Assert.Equal(["test.b"], passes[2].Reads);
        Assert.Equal([1, 0], passes.Take(2).Select(p => p.Dispatches));
        Assert.Equal(1, passes[1].Draws);
        Assert.All(passes, p => Assert.True(p.Barriers >= 1, $"{p.Name}: барьеров {p.Barriers}"));

        Frame(f => f.Graph.AddPass("test.clear").Color(f.Graph.BackBuffer, System.Numerics.Vector4.Zero));
        Assert.NotSame(passes, _renderer.Graph.LastFrame);   // новый кадр — новый отчёт
        Assert.Equal(["test.clear"], _renderer.Graph.LastFrame.Select(p => p.Name));
        Assert.Equal(0, _renderer.Graph.LastFrameDraws);
        Assert.Equal(["test.fill", "test.double", "test.copy"], passes.Select(p => p.Name));   // выданный отчёт не портится
        var values = new float[n * n];
        _renderer.ReadStorage(result, values.AsSpan());
        Assert.Equal((3 + 100f * 2) * 2, values[2 * n + 3]);
        AssertNoDebugMessages();
    }

    [Fact]
    public void FrameCapture_ColorAndDepth_UnderGpuValidation_StateRestored()
    {
        // Снимок агента (capture.bundle): цвет и глубина сцены кадра — копии из заднего буфера и из глубины графа;
        // состояние глубины после копии — прежнее, следующий кадр граф ведёт без переходов вслепую.
        string dir = Path.Combine(Path.GetTempPath(), $"sword-capture-{Guid.NewGuid():N}");
        void Scene(RenderFrame f) => f.Graph.AddPass("test.scene")
            .Color(f.Graph.BackBuffer, new System.Numerics.Vector4(0.2f, 0.4f, 0.6f, 1))
            .Depth(f.Scene.Depth, DepthAccess.Write, clear: 0f);
        try
        {
            _window.PumpMessages();
            _renderer.Render(Scene, vsync: false, new FrameCapture(Path.Combine(dir, "c.png"), Path.Combine(dir, "d.png")));
            var capture = _renderer.LastCapture!;
            Assert.True(File.Exists(capture.ColorPath) && File.Exists(capture.DepthPath));
            Assert.Equal((_renderer.Width, _renderer.Height), (capture.Width, capture.Height));
            Assert.Equal(0f, capture.DepthCoverage);            // очищена в 0 (reversed-Z: дальняя плоскость)
            Frame(Scene);                                        // следующий кадр — глубина в своём состоянии
            _renderer.Render(Scene, vsync: false, new FrameCapture(null, Path.Combine(dir, "d2.png")));
            Assert.Null(_renderer.LastCapture!.ColorPath);       // итог — только этого снимка
            AssertNoDebugMessages();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
