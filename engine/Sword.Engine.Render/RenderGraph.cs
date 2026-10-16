using System.Numerics;
using Sword.Engine.Diagnostics;
using Sword.Engine.Rhi;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Sword.Engine.Render;

/// <summary>Доступ прохода к глубине.</summary>
public enum DepthAccess
{
    /// <summary>Тест и запись.</summary>
    Write,

    /// <summary>Только тест: глубина привязана на чтение, её же можно читать текстурой в том же проходе.</summary>
    Read,
}

/// <summary>Что проход сделал в кадре; строится по запросу из сырых данных кадра (кадр графа — 0 Б).</summary>
/// <param name="Barriers">Переходов состояний и барьеров UAV перед проходом (одной пачкой).</param>
public sealed record RenderPassReport(
    string Name,
    PassKind Kind,
    FramePhase Phase,
    IReadOnlyList<string> Writes,
    IReadOnlyList<string> Reads,
    int Barriers,
    int Draws,
    int Dispatches);

/// <summary>
/// Рендер-граф: проходы объявляют читаемые и записываемые ресурсы, граф ставит переходы состояний и барьеры UAV одной
/// пачкой на проход, привязывает цели и константы. Одна graphics-очередь; порядок — по фазам (<see cref="FramePhase"/>),
/// внутри фазы — регистрации; без отсечения и алиасинга. Инварианты проверяются до первой команды кадра: проход не читает
/// ресурс, не записанный в этом кадре (кроме хранилищ); не пишет и не читает один ресурс; с HDR задний буфер не пишется
/// раньше фазы <see cref="FramePhase.Post"/>.
/// </summary>
public sealed class RenderGraph
{
    internal sealed class Pass
    {
        public string Name = "";
        public PassKind Kind;
        public FramePhase Phase;
        public readonly List<(int Resource, Color4? Clear)> Colors = [];
        public int Depth = -1;
        public DepthAccess DepthAccess;
        public float? DepthClear;
        public readonly List<int> Reads = [];
        public readonly List<int> Writes = [];
        public ulong FrameConstants;
        public Action<RenderPassContext>? Execute;

        public void Reset(string name, PassKind kind, FramePhase phase, ulong frameConstants)
        {
            Name = name;
            Kind = kind;
            Phase = phase;
            Colors.Clear();
            Depth = -1;
            DepthClear = null;
            Reads.Clear();
            Writes.Clear();
            FrameConstants = frameConstants;
            Execute = null;
        }
    }

    private readonly GraphicsDevice _device;
    private readonly PipelineCache _pipelines;
    private readonly TransientPool _pool;
    /// <summary>Ресурсы кадра: сначала постоянные (задний буфер, цели сцены), затем импорт и временные этого кадра.</summary>
    private readonly List<GraphResource> _resources = [];
    private int _persistentCount;
    private readonly List<Pass> _passes = [];
    private readonly List<Pass> _passPool = [];
    private readonly RenderPassContext _context;
    /// <summary>Барьеры прохода — массив графа, растёт удвоением (кадр без выделений).</summary>
    private ResourceBarrier[] _barriers = new ResourceBarrier[8];
    private int _barrierCount;
    /// <summary>Записан ли ресурс к проходу (<see cref="Validate"/>) — массив графа, а не новый на кадр.</summary>
    private bool[] _written = new bool[16];
    private ulong _frameConstants;

    /// <summary>Первая фаза, в которой проход вправе писать задний буфер: с HDR — <see cref="FramePhase.Post"/>, без HDR — любая.</summary>
    internal FramePhase BackBufferFrom;

    /// <summary>
    /// Сырые данные исполненного прохода: отчёт строится из них только по запросу, в кадре — ни списков, ни строк.
    /// Имена — ссылки подряд в <see cref="FrameRecords.Names"/> с <see cref="NamesStart"/>.
    /// </summary>
    private struct PassRecord
    {
        public string Name;
        public PassKind Kind;
        public FramePhase Phase;
        public int Barriers, Draws, Dispatches, NamesStart, Writes, Reads;
    }

    /// <summary>Проходы одного кадра: пишется кадр в одни, показан — другие (брошенный посреди записи кадр прежних не портит).</summary>
    private sealed class FrameRecords
    {
        public PassRecord[] Passes = new PassRecord[8];
        public string[] Names = new string[16];
        public int Count, NameCount, Draws;

        public void Clear()
        {
            Array.Clear(Passes, 0, Count);   // ссылки на имена прошлого кадра не держим
            Array.Clear(Names, 0, NameCount);
            Count = NameCount = Draws = 0;
        }

        public void AddName(string name)
        {
            if (NameCount == Names.Length) Array.Resize(ref Names, Names.Length * 2);
            Names[NameCount++] = name;
        }

        public ref PassRecord Add()
        {
            if (Count == Passes.Length) Array.Resize(ref Passes, Passes.Length * 2);
            return ref Passes[Count++];
        }
    }

    private FrameRecords _recording = new(), _shown = new();
    private IReadOnlyList<RenderPassReport>? _lastFrame;

    internal RenderGraph(GraphicsDevice device, PipelineCache pipelines)
    {
        _device = device;
        _pipelines = pipelines;
        _pool = new TransientPool(device);
        _context = new RenderPassContext(pipelines);
        BackBuffer = ImportPersistent(new GraphResource("backbuffer", isBuffer: false) { TargetFormat = GraphicsDevice.BackBufferFormat });
    }

    /// <summary>Задний буфер кадра. В начале кадра содержимое не определено; в конце граф отдаёт его на показ.</summary>
    public RenderTexture BackBuffer { get; }

    /// <summary>Проходы и барьеры последнего кадра; строится при первом запросе и держится до следующего кадра (0 Б/кадр).</summary>
    public IReadOnlyList<RenderPassReport> LastFrame => _lastFrame ??= BuildReport(_shown);

    /// <summary>Проходов в последнем исполненном кадре — без построения отчёта.</summary>
    public int LastFramePasses => _shown.Count;

    /// <summary>Вызовов отрисовки в последнем исполненном кадре (все проходы) — для HUD каждый кадр, без построения отчёта.</summary>
    public int LastFrameDraws => _shown.Draws;

    private static List<RenderPassReport> BuildReport(FrameRecords frame)
    {
        var report = new List<RenderPassReport>(frame.Count);
        for (int i = 0; i < frame.Count; i++)
        {
            ref readonly var r = ref frame.Passes[i];
            report.Add(new RenderPassReport(r.Name, r.Kind, r.Phase, frame.Names.AsSpan(r.NamesStart, r.Writes).ToArray(),
                frame.Names.AsSpan(r.NamesStart + r.Writes, r.Reads).ToArray(), r.Barriers, r.Draws, r.Dispatches));
        }
        return report;
    }

    /// <summary>Записей в пуле временных ресурсов (текстуры и буферы) — после смены размера старые уходят сами.</summary>
    public int PooledResources => _pool.Count;

    public int Width => _device.BackBufferWidth;
    public int Height => _device.BackBufferHeight;

    /// <summary>Постоянный импорт: ресурс входит в каждый кадр под одним номером (до первого кадра).</summary>
    internal RenderTexture ImportPersistent(GraphResource resource)
    {
        if (_resources.Count != _persistentCount)
            throw new InvalidOperationException("постоянные ресурсы графа регистрируются до первого кадра");
        _resources.Add(resource);
        _persistentCount++;
        return new RenderTexture(_resources.Count - 1);
    }

    /// <summary>Временная текстура этого кадра из пула; размер 0 — размер заднего буфера. Содержимое не определено.</summary>
    public RenderTexture CreateTexture(string name, in TextureDesc desc)
    {
        int w = desc.Width > 0 ? desc.Width : Width, h = desc.Height > 0 ? desc.Height : Height;
        return new RenderTexture(Enter(_pool.Texture(name, desc, w, h, _device.FrameNumber)));
    }

    /// <summary>Временный структурированный буфер этого кадра из пула (<paramref name="count"/> × <paramref name="stride"/> Б).</summary>
    public RenderBuffer CreateBuffer(string name, uint stride, uint count)
    {
        return new RenderBuffer(Enter(_pool.Buffer(name, stride, count, _device.FrameNumber)));
    }

    /// <summary>Буфер-хранилище в этот кадр; повторный импорт того же хранилища — тот же номер.</summary>
    public RenderBuffer Import(GpuStorage storage)
    {
        for (int i = _persistentCount; i < _resources.Count; i++)
            if (ReferenceEquals(_resources[i], storage.Resource)) return new RenderBuffer(i);
        return new RenderBuffer(Enter(storage.Resource));
    }

    /// <summary>Ресурс входит в кадр: записей в нём в этом кадре ещё не было.</summary>
    private int Enter(GraphResource r)
    {
        r.Written = false;
        _resources.Add(r);
        return _resources.Count - 1;
    }

    /// <summary>Индекс SRV в bindless-куче — для корневой константы (<c>Texture2D</c> / <c>StructuredBuffer</c>).</summary>
    public int SrvOf(RenderTexture t) => Srv(Get(t.Index, buffer: false));
    public int SrvOf(RenderBuffer b) => Srv(Get(b.Index, buffer: true));

    /// <summary>Индекс UAV в bindless-куче (<c>RWTexture2D</c> / <c>RWStructuredBuffer</c>).</summary>
    public int UavOf(RenderTexture t) => Uav(Get(t.Index, buffer: false));
    public int UavOf(RenderBuffer b) => Uav(Get(b.Index, buffer: true));

    /// <summary>Имя ресурса графа — для отчётов и сообщений.</summary>
    public string NameOf(RenderTexture t) => Get(t.Index, buffer: false).Name;
    public string NameOf(RenderBuffer b) => Get(b.Index, buffer: true).Name;

    private static int Srv(GraphResource r) =>
        r.Srv >= 0 ? r.Srv : throw new InvalidOperationException($"{r.Name} не читается шейдером");

    private static int Uav(GraphResource r) =>
        r.Uav >= 0 ? r.Uav : throw new InvalidOperationException($"{r.Name} не пишется из шейдера (нет Storage)");

    internal GraphResource Get(int index, bool buffer)
    {
        if ((uint)index >= (uint)_resources.Count)
            throw new InvalidOperationException($"ресурс графа {index} — не из этого кадра (временные ресурсы живут один кадр)");
        var r = _resources[index];
        if (r.IsBuffer != buffer)
            throw new InvalidOperationException($"{r.Name}: {(r.IsBuffer ? "буфер" : "текстура")}, а не {(buffer ? "буфер" : "текстура")}");
        return r;
    }

    internal void BeginFrame(ulong frameConstants)
    {
        var back = _resources[BackBuffer.Index];
        back.Handle = _device.CurrentBackBuffer;
        back.Rtv = _device.CurrentRtv;
        back.State = ResourceStates.Present;
        back.Width = _device.BackBufferWidth;
        back.Height = _device.BackBufferHeight;
        _resources.RemoveRange(_persistentCount, _resources.Count - _persistentCount);
        _pool.BeginFrame(_device.FrameNumber);
        foreach (var r in _resources) r.Written = false;
        foreach (var p in _passes) _passPool.Add(p);
        _passes.Clear();
        _frameConstants = frameConstants;
    }

    /// <summary>Константы кадра (b0) для проходов, добавленных после вызова: адрес запоминает проход.</summary>
    internal void SetFrameConstants(ulong address) => _frameConstants = address;

    /// <summary>Новый проход фазы <see cref="FramePhase.Opaque"/>; работа — в <see cref="RenderPassBuilder.Execute"/>.</summary>
    public RenderPassBuilder AddPass(string name, PassKind kind = PassKind.Graphics) => AddPass(name, FramePhase.Opaque, kind);

    /// <summary>Новый проход фазы <paramref name="phase"/>: исполнится после прежних фаз и уже зарегистрированных проходов этой.</summary>
    public RenderPassBuilder AddPass(string name, FramePhase phase, PassKind kind = PassKind.Graphics)
    {
        if ((uint)phase > (uint)FramePhase.Debug) throw new ArgumentOutOfRangeException(nameof(phase), phase, "нет такой фазы кадра");
        Pass p;
        if (_passPool.Count > 0)
        {
            p = _passPool[^1];
            _passPool.RemoveAt(_passPool.Count - 1);
        }
        else p = new Pass();
        p.Reset(name, kind, phase, _frameConstants);
        _passes.Add(p);
        return new RenderPassBuilder(this, p);
    }

    /// <summary>Вычислительный проход (dispatch на graphics-очереди) фазы <paramref name="phase"/>.</summary>
    public RenderPassBuilder AddComputePass(string name, FramePhase phase = FramePhase.Opaque) => AddPass(name, phase, PassKind.Compute);

    /// <summary>Проходов строящегося кадра, зарегистрированных до сих пор — хост сверяет, что добавил модуль.</summary>
    public int Registered => _passes.Count;

    /// <summary>Имя и фаза прохода строящегося кадра по номеру регистрации (0 … <see cref="Registered"/> − 1).</summary>
    public (string Name, FramePhase Phase) RegisteredPass(int index) => (_passes[index].Name, _passes[index].Phase);

    /// <summary>Построитель прохода: цели по порядку привязки, глубина, читаемые и записываемые ресурсы.</summary>
    public readonly struct RenderPassBuilder
    {
        private readonly RenderGraph _graph;
        private readonly Pass _pass;

        internal RenderPassBuilder(RenderGraph graph, Pass pass)
        {
            _graph = graph;
            _pass = pass;
        }

        /// <summary>
        /// Цель цвета (следующий SV_Target); <paramref name="clear"/> — очистить. Цель с заданным значением очистки чистится только им.
        /// </summary>
        public RenderPassBuilder Color(RenderTexture target, Vector4? clear = null)
        {
            var t = _graph.Get(target.Index, buffer: false);
            RequireGraphics("цель цвета");
            if (t.Rtv is null) throw new InvalidOperationException($"{_pass.Name}: {t.Name} не бывает целью цвета");
            if (clear is { } want && t.ClearColor is { } made && want != made)
                throw new InvalidOperationException($"{_pass.Name}: {t.Name} создана со значением очистки {made}, а чистится {want} — " +
                    "задайте его в TextureDesc.ClearColor (цель HDR-сцены — Renderer.SceneColorClear)");
            if (_pass.Colors.Count == 2) throw new InvalidOperationException($"{_pass.Name}: больше двух целей цвета");
            _pass.Colors.Add((target.Index, clear is { } c ? new Color4(c.X, c.Y, c.Z, c.W) : null));
            return this;
        }

        /// <summary>Глубина; <paramref name="clear"/> — очистить (обратный Z: 0 — дальняя плоскость).</summary>
        public RenderPassBuilder Depth(RenderTexture depth, DepthAccess access, float? clear = null)
        {
            var t = _graph.Get(depth.Index, buffer: false);
            RequireGraphics("глубина");
            if (t.Dsv is null) throw new InvalidOperationException($"{_pass.Name}: {t.Name} не бывает глубиной");
            if (access == DepthAccess.Read && clear is not null)
                throw new InvalidOperationException($"{_pass.Name}: глубина только на чтение не очищается");
            if (clear is { } want && t.ClearDepth is { } made && want != made)
                throw new InvalidOperationException($"{_pass.Name}: {t.Name} создана со значением очистки {made}, а чистится {want}");
            _pass.Depth = depth.Index;
            _pass.DepthAccess = access;
            _pass.DepthClear = clear;
            return this;
        }

        /// <summary>Текстура, которую шейдеры прохода читают (SRV).</summary>
        public RenderPassBuilder Read(RenderTexture texture) => AddRead(_graph.Get(texture.Index, buffer: false), texture.Index);

        /// <summary>Буфер, который шейдеры прохода читают (SRV).</summary>
        public RenderPassBuilder Read(RenderBuffer buffer) => AddRead(_graph.Get(buffer.Index, buffer: true), buffer.Index);

        /// <summary>Текстура, которую шейдеры прохода пишут (UAV, <c>RWTexture2D</c>).</summary>
        public RenderPassBuilder Write(RenderTexture texture) => AddWrite(_graph.Get(texture.Index, buffer: false), texture.Index);

        /// <summary>Буфер, который шейдеры прохода пишут (UAV); читать его через тот же UAV в проходе можно.</summary>
        public RenderPassBuilder Write(RenderBuffer buffer) => AddWrite(_graph.Get(buffer.Index, buffer: true), buffer.Index);

        /// <summary>Работа прохода: вызывается при исполнении графа — по фазам, внутри фазы в порядке регистрации.</summary>
        public void Execute(Action<RenderPassContext> execute) => _pass.Execute = execute;

        private RenderPassBuilder AddRead(GraphResource r, int index)
        {
            if (r.Srv < 0) throw new InvalidOperationException($"{_pass.Name}: {r.Name} не читается шейдером");
            if (!_pass.Reads.Contains(index)) _pass.Reads.Add(index);
            return this;
        }

        private RenderPassBuilder AddWrite(GraphResource r, int index)
        {
            if (r.Uav < 0) throw new InvalidOperationException($"{_pass.Name}: {r.Name} не пишется из шейдера (нет Storage)");
            if (!_pass.Writes.Contains(index)) _pass.Writes.Add(index);
            return this;
        }

        private void RequireGraphics(string what)
        {
            if (_pass.Kind != PassKind.Graphics)
                throw new InvalidOperationException($"{_pass.Name}: {what} у вычислительного прохода");
        }
    }

    /// <summary>
    /// Упорядочить проходы кадра по фазам, проверить их, затем записать в список команд; в конце задний буфер — в Present.
    /// </summary>
    internal void Execute(ID3D12GraphicsCommandList list)
    {
        SortByPhase();
        Validate();
        var frame = _recording;
        frame.Clear();
        var profiler = _profiler;
        var timer = _device.Timer;   // таймстемпы GPU вокруг каждого прохода (с его барьерами)
        foreach (var p in _passes)
        {
            int zone = profiler.Enabled ? PassZone(p.Name) : 0;
            profiler.Begin(zone);
            int scope = timer.Begin(list, p.Name);
            _barrierCount = 0;
            foreach (var (index, _) in p.Colors) Require(_resources[index], ResourceStates.RenderTarget);
            if (p.Depth >= 0)
            {
                var depthState = p.DepthAccess == DepthAccess.Write ? ResourceStates.DepthWrite : ResourceStates.DepthRead;
                if (p.Reads.Contains(p.Depth)) depthState |= ResourceStates.AllShaderResource;
                Require(_resources[p.Depth], depthState);
            }
            foreach (int r in p.Reads)
                if (r != p.Depth) Require(_resources[r], ResourceStates.AllShaderResource);
            foreach (int w in p.Writes) RequireUav(_resources[w]);
            int barriers = _barrierCount;
            if (barriers > 0)
            {
                list.ResourceBarrier((uint)barriers, _barriers);
                Array.Clear(_barriers, 0, barriers);   // ссылки на ресурсы прохода массив графа не держит
            }

            GraphResource? size = null;
            if (p.Kind == PassKind.Graphics)
            {
                size = _resources[p.Colors.Count > 0 ? p.Colors[0].Resource : p.Depth];
                BindGraphics(list, p, size);
            }
            else BindCompute(list, p);

            int draws = 0, dispatches = 0;
            if (p.Execute is not null)
            {
                _context.Begin(list, p.Kind, p.Kind == PassKind.Graphics ? Layout(p) : default, size?.Width ?? 0, size?.Height ?? 0);
                p.Execute(_context);
                draws = _context.DrawCalls;
                dispatches = _context.Dispatches;
            }

            foreach (var (index, _) in p.Colors) _resources[index].Written = true;
            if (p.Depth >= 0) _resources[p.Depth].Written |= p.DepthAccess == DepthAccess.Write;
            foreach (int w in p.Writes)
            {
                _resources[w].Written = true;
                _resources[w].UavPending = true;
            }
            timer.End(list, scope);
            profiler.End(zone);
            Record(frame, p, barriers, draws, dispatches);
        }

        var back = _resources[BackBuffer.Index];
        if (back.State != ResourceStates.Present)
        {
            list.ResourceBarrierTransition(back.Handle!, back.State, ResourceStates.Present);
            back.State = ResourceStates.Present;
        }
        // Кадр записан целиком — он и показан: отчёт по нему — по следующему запросу.
        (_recording, _shown) = (_shown, frame);
        _lastFrame = null;
    }

    /// <summary>Устойчивая сортировка вставками по фазе: регистрация обычно уже по фазам, без выделений.</summary>
    private void SortByPhase()
    {
        for (int i = 1; i < _passes.Count; i++)
        {
            var p = _passes[i];
            int j = i - 1;
            if (_passes[j].Phase <= p.Phase) continue;
            while (j >= 0 && _passes[j].Phase > p.Phase)
            {
                _passes[j + 1] = _passes[j];
                j--;
            }
            _passes[j + 1] = p;
        }
    }

    /// <summary>Инварианты кадра — до первой команды: при нарушении кадр бросается целиком, состояния ресурсов не сдвинуты.</summary>
    private void Validate()
    {
        if (_written.Length < _resources.Count) _written = new bool[Math.Max(_written.Length * 2, _resources.Count)];
        var written = _written.AsSpan(0, _resources.Count);
        for (int i = 0; i < written.Length; i++) written[i] = _resources[i].Persistent;
        foreach (var p in _passes)
        {
            if (p.Kind == PassKind.Graphics && p.Colors.Count == 0 && p.Depth < 0)
                throw new InvalidOperationException($"рендер-граф: у графического прохода {p.Name} нет целей");
            if (p.Kind == PassKind.Compute && p.Execute is null)
                throw new InvalidOperationException($"рендер-граф: у вычислительного прохода {p.Name} нет работы");
            if (p.Phase < BackBufferFrom)
                foreach (var (index, _) in p.Colors)
                    if (index == BackBuffer.Index)
                        throw new InvalidOperationException($"рендер-граф: HDR включён, а проход {p.Name} фазы {p.Phase} пишет задний буфер — " +
                            $"до фазы {BackBufferFrom} сцена пишет RenderFrame.SceneColor");
            foreach (int r in p.Reads)
            {
                string name = _resources[r].Name;
                if (p.Writes.Contains(r))
                    throw new InvalidOperationException($"рендер-граф: проход {p.Name} пишет (UAV) и читает {name}");
                foreach (var (index, _) in p.Colors)
                    if (index == r) throw new InvalidOperationException($"рендер-граф: проход {p.Name} пишет и читает {name}");
                if (r == p.Depth && p.DepthAccess == DepthAccess.Write)
                    throw new InvalidOperationException($"рендер-граф: проход {p.Name} пишет и читает глубину {name}");
                if (!written[r])
                    throw new InvalidOperationException($"рендер-граф: проход {p.Name} читает {name}, которую в этом кадре никто не записал");
            }
            foreach (int w in p.Writes)
            {
                foreach (var (index, _) in p.Colors)
                    if (index == w)
                        throw new InvalidOperationException($"рендер-граф: проход {p.Name} пишет {_resources[w].Name} и целью, и через UAV");
            }
            foreach (var (index, _) in p.Colors) written[index] = true;
            if (p.Depth >= 0 && p.DepthAccess == DepthAccess.Write) written[p.Depth] = true;
            foreach (int w in p.Writes) written[w] = true;
        }
    }

    /// <summary>Сырые данные прохода — в записи кадра (имена ресурсов — ссылками): записи, затем чтения.</summary>
    private void Record(FrameRecords frame, Pass p, int barriers, int draws, int dispatches)
    {
        int start = frame.NameCount;
        foreach (var (index, _) in p.Colors) frame.AddName(_resources[index].Name);
        if (p.Depth >= 0 && p.DepthAccess == DepthAccess.Write) frame.AddName(_resources[p.Depth].Name);
        foreach (int w in p.Writes) frame.AddName(_resources[w].Name);
        int writes = frame.NameCount - start;
        foreach (int r in p.Reads) frame.AddName(_resources[r].Name);
        ref var record = ref frame.Add();
        record.Name = p.Name;
        record.Kind = p.Kind;
        record.Phase = p.Phase;
        record.Barriers = barriers;
        record.Draws = draws;
        record.Dispatches = dispatches;
        record.NamesStart = start;
        record.Writes = writes;
        record.Reads = frame.NameCount - start - writes;
        frame.Draws += draws;
    }

    private void AddBarrier(in ResourceBarrier barrier)
    {
        if (_barrierCount == _barriers.Length) Array.Resize(ref _barriers, _barriers.Length * 2);
        _barriers[_barrierCount++] = barrier;
    }

    private void Require(GraphResource r, ResourceStates state)
    {
        r.UavPending = false;
        if (r.State == state) return;
        AddBarrier(new ResourceBarrier(new ResourceTransitionBarrier(r.Handle!, r.State, state)));
        r.State = state;
    }

    /// <summary>
    /// Запись UAV: переход в UnorderedAccess или барьер UAV, если прошлый доступ — запись UAV или это первая запись кадра
    /// (граница кадров видимость записей UAV не обещает).
    /// </summary>
    private void RequireUav(GraphResource r)
    {
        if (r.State != ResourceStates.UnorderedAccess)
        {
            AddBarrier(new ResourceBarrier(new ResourceTransitionBarrier(r.Handle!, r.State, ResourceStates.UnorderedAccess)));
            r.State = ResourceStates.UnorderedAccess;
        }
        else if (r.UavPending || !r.Written)
            AddBarrier(new ResourceBarrier(new ResourceUnorderedAccessViewBarrier(r.Handle!)));
        r.UavPending = false;
    }

    private TargetLayout Layout(Pass p) => new(
        p.Colors.Count > 0 ? _resources[p.Colors[0].Resource].TargetFormat : Format.Unknown,
        p.Colors.Count > 1 ? _resources[p.Colors[1].Resource].TargetFormat : Format.Unknown,
        p.Depth >= 0 ? _resources[p.Depth].TargetFormat : Format.Unknown);

    private void BindGraphics(ID3D12GraphicsCommandList list, Pass p, GraphResource size)
    {
        // Не больше двух целей (RenderPassBuilder.Color) — на стеке, а не массив на проход.
        Span<CpuDescriptorHandle> rtvs = stackalloc CpuDescriptorHandle[2];
        rtvs = rtvs[..p.Colors.Count];
        for (int i = 0; i < rtvs.Length; i++) rtvs[i] = _resources[p.Colors[i].Resource].Rtv!.Value;
        CpuDescriptorHandle? dsv = p.Depth < 0 ? null
            : p.DepthAccess == DepthAccess.Write ? _resources[p.Depth].Dsv : _resources[p.Depth].ReadOnlyDsv;
        list.OMSetRenderTargets(rtvs, dsv);

        for (int i = 0; i < rtvs.Length; i++)
            if (p.Colors[i].Clear is { } c)
                list.ClearRenderTargetView(rtvs[i], c);
        if (p.DepthClear is { } d) list.ClearDepthStencilView(dsv!.Value, ClearFlags.Depth, d, 0);

        list.RSSetViewport(0, 0, size.Width, size.Height);
        list.RSSetScissorRect(new Vortice.RawRect(0, 0, size.Width, size.Height));
        list.SetGraphicsRootSignature(_pipelines.RootSignature);
        if (p.FrameConstants != 0) list.SetGraphicsRootConstantBufferView(RootLayout.FrameCb, p.FrameConstants);
        list.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
    }

    private void BindCompute(ID3D12GraphicsCommandList list, Pass p)
    {
        list.SetComputeRootSignature(_pipelines.RootSignature);
        if (p.FrameConstants != 0) list.SetComputeRootConstantBufferView(RootLayout.FrameCb, p.FrameConstants);
    }

    private CpuProfiler _profiler = CpuProfiler.Off;
    /// <summary>Зоны проходов по имени прохода: строка «render.имя» строится один раз на имя, а не каждый кадр.</summary>
    private readonly Dictionary<string, int> _passZones = new(StringComparer.Ordinal);

    /// <summary>Профайлер CPU записи кадра (<see cref="Renderer.Profiler"/>): проход — зона <c>render.&lt;проход&gt;</c>.</summary>
    internal CpuProfiler Profiler
    {
        get => _profiler;
        set
        {
            _passZones.Clear();
            _gpuZones.Clear();
            _profiler = value;
        }
    }

    /// <summary>Зоны GPU-времени проходов: <c>gpu.&lt;проход&gt;</c> вида <see cref="ProfileZoneKind.Gpu"/>.</summary>
    private readonly Dictionary<string, int> _gpuZones = new(StringComparer.Ordinal);

    /// <summary>
    /// Зона прохода по имени (имена стабильны, порядок — нет). Зона GPU-времени заводится тут же, а не через 3 кадра, когда
    /// придут времена: иначе строки зон выделялись бы в обычном кадре.
    /// </summary>
    private int PassZone(string name)
    {
        if (!_passZones.TryGetValue(name, out int zone))
        {
            zone = _profiler.Zone("render." + name);
            _passZones.Add(name, zone);
            GpuZone(name);
        }
        return zone;
    }

    /// <summary>Зона GPU-времени прохода <paramref name="name"/>; нет — заводится.</summary>
    internal int GpuZone(string name)
    {
        if (!_gpuZones.TryGetValue(name, out int zone))
        {
            zone = _profiler.Zone(CpuProfiler.GpuPrefix + name, ProfileZoneKind.Gpu);
            _gpuZones.Add(name, zone);
        }
        return zone;
    }

    internal void Dispose() => _pool.Dispose();
}
