using System.Collections.Concurrent;
using System.Numerics;
using System.Text.Json.Nodes;
using Sword.Engine.Client;
using Sword.Engine.Ecs;
using Sword.Engine.Introspection;
using Sword.Engine.Render;
using Sword.Engine.Runtime;

namespace Sword.Engine.Agent.Client;

/// <summary>
/// Агент в живой игре (раздел 8 плана, Engine.Agent.Client): подключается к кадру клиента как инструмент
/// (<see cref="IClientTool"/>) и ворота тиков (<see cref="ITickGate"/>) — пауза и шаги агента; раз в кадр после тиков
/// обслуживает очередь агента (точка обслуживания); на границе кадра снимает пакет наблюдения (<c>capture.bundle</c>).
/// Инструменты клиента: <c>capture.bundle</c>, <c>render.graph</c>. Транспорт — HTTP на 127.0.0.1.
/// </summary>
public sealed class ClientAgent : IClientTool, ITickGate
{
    /// <summary>Бюджет точки обслуживания за кадр: остальные запросы — в следующем кадре.</summary>
    public static readonly TimeSpan ServiceBudget = TimeSpan.FromMilliseconds(4);

    private readonly AgentHost _host;
    private readonly ClientHost _client;
    private readonly ConcurrentQueue<CaptureRequest> _captures = new();
    private CaptureRequest? _inFlight;

    // Последний показанный кадр (поток кадра) — для render.graph. Проходы — не ссылкой каждый кадр, а по запросу
    // (RenderGraph.LastFrame строит отчёт лениво — кадр графа 0 Б, исправления после Пд): точка обслуживания идёт до записи
    // следующего кадра, последний исполненный кадр графа — показанный.
    private int _shownWidth, _shownHeight, _shownPipelines;
    private ulong _shownFrame, _shownTick;

    /// <summary>Кадров рендера отправлено к показу кадра <see cref="_shownFrame"/> (точка Пг: сверка GPU-времён по номеру).</summary>
    private ulong _shownSubmitted;

    private sealed class CaptureRequest(string name, bool ids, IReadOnlyList<string> overlays)
    {
        public readonly string Name = name;
        public readonly bool Ids = ids;
        public readonly IReadOnlyList<string> Overlays = overlays;
        public readonly TaskCompletionSource<JsonObject> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private ClientAgent(AgentHost host, ClientHost client)
    {
        _host = host;
        _client = client;
    }

    /// <summary>Подключить агента к клиенту (до <see cref="ClientHost.Run"/>) и зарегистрировать инструменты клиента.</summary>
    public static ClientAgent Attach(AgentHost host, ClientHost client)
    {
        var agent = new ClientAgent(host, client);
        client.AddTool(agent);
        client.TickGate = agent;
        agent.Register();
        return agent;
    }

    // ── ворота тиков ───────────────────────────────────────────────────────

    public int? Override(int maxPerFrame) => _host.Time.Gate(maxPerFrame);

    public void Ran(ulong tick) => _host.Time.Ran(tick);

    /// <summary>Пауза или условие наблюдения встали на этом тике — остальные тики кадра не играются.</summary>
    public bool StopAfterTick(ulong tick) => _host.Time.Stopping(tick);

    // ── кадр ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Точка обслуживания кадра: запросы агента из очереди (не дольше <see cref="ServiceBudget"/>). Истина — выполнен хотя бы один:
    /// кадр — кадр инструментов (после Пв: любой запрос выделяет на главном потоке и тратит время, которого у игры нет, — кадр
    /// служебный, в средних и бюджете его нет, профайлер не судит его как кадр).
    /// </summary>
    public bool Serve(Simulation simulation, ulong frame)
    {
        _host.LastFrame = frame;
        return _host.Service.Serve(new ServiceContext(simulation, frame), ServiceBudget) > 0;
    }

    public FrameCapture? Capture(ulong frame)
    {
        // Запрос, которого уже не ждут (таймаут, отмена), не снимается: иначе после свёрнутого окна пошла бы серия
        // лишних снимков с ожиданием GPU.
        if (_inFlight?.Done.Task.IsCompleted == true) _inFlight = null;
        while (_inFlight is null && _captures.TryDequeue(out var next))
            if (!next.Done.Task.IsCompleted) _inFlight = next;
        if (_inFlight is null) return null;
        string stem = $"capture-{frame:D6}-{_inFlight.Name}";
        try
        {
            // ID-буфер и оверлеи — только по заказу (диагностический режим): их проходы игра регистрирует лишь в этом кадре.
            return new FrameCapture(_host.ArtifactPath(stem + ".color.png"), _host.ArtifactPath(stem + ".depth.png"))
            {
                IdsPath = _inFlight.Ids ? _host.ArtifactPath(stem + ".ids.png") : null,
                Layers = [.. _inFlight.Overlays.Select(o => new CaptureLayer(o, _host.ArtifactPath($"{stem}.overlay-{o}.png")))],
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Каталог артефактов недоступен — ошибка запросу агента, а не игре.
            _inFlight.Done.TrySetException(new IOException($"каталог артефактов {_host.Artifacts} недоступен: {e.Message}", e));
            _inFlight = null;
            return null;
        }
    }

    public void Presented(Simulation simulation, ulong frame, FrameCapture? capture, CaptureResult? result)
    {
        // Граф, размер и тик показанного кадра — вместе: точка обслуживания следующего кадра идёт уже после его тиков.
        var renderer = _client.Renderer;
        _shownWidth = renderer.Width;
        _shownHeight = renderer.Height;
        _shownPipelines = renderer.PipelineCount;
        _shownFrame = frame;
        _shownTick = simulation.Tick;
        _shownSubmitted = renderer.FrameNumber;
        if (capture is null || _inFlight is null) return;
        var request = _inFlight;
        _inFlight = null;
        if (result is null)
        {
            request.Done.TrySetException(new IOException("снимок кадра не записан — причина в log.tail (категория client)"));
            return;
        }
        try
        {
            request.Done.TrySetResult(Bundle(simulation, frame, result));
        }
        catch (Exception e)
        {
            request.Done.TrySetException(e);
        }
    }

    /// <summary>
    /// Пакет наблюдения v0 (раздел 8): цвет, глубина, видимые сущности с экранными прямоугольниками и именами, камера,
    /// тик и кадр — в <c>bundle.json</c> рядом с картинками; ответ — то же самое.
    /// </summary>
    private JsonObject Bundle(Simulation simulation, ulong frame, CaptureResult result)
    {
        var context = new ServiceContext(simulation, frame);
        var entities = new JsonArray();
        JsonObject? camera = null;
        if (_client.Game is IClientObservable observable)
        {
            var view = observable.Camera;
            camera = new JsonObject
            {
                ["eye"] = Vec(view.Eye), ["target"] = Vec(view.Target), ["yaw"] = view.Yaw, ["pitch"] = view.Pitch,
                ["distance"] = view.Distance, ["follow"] = view.Follow,
            };
            var visible = new List<VisibleEntity>();
            observable.Visible(visible);
            foreach (var v in visible)
            {
                if (ScreenRect(view.ViewProj, v.Min, v.Max, result.Width, result.Height) is not { } rect) continue;
                entities.Add(new JsonObject
                {
                    ["entity"] = v.Entity.ToString(),
                    ["name"] = _host.Names?.NameOf(context, v.Entity),
                    ["kind"] = v.Kind,
                    ["rect"] = new JsonArray(rect.X0, rect.Y0, rect.X1, rect.Y1),
                    ["worldMin"] = Vec(v.Min),
                    ["worldMax"] = Vec(v.Max),
                });
            }
        }
        JsonObject? ids = null;
        if (result.IdsPath is not null)
        {
            var legend = new JsonArray();
            var describer = _client.Game as IClientObservable;
            foreach (var c in result.Ids)
            {
                var meaning = describer?.DescribeId(c.Id);
                var (owner, part) = SkinnedMeshes.SplitId(c.Id);
                legend.Add(new JsonObject
                {
                    ["id"] = c.Id,
                    ["owner"] = owner,
                    ["part"] = part,
                    ["entity"] = meaning?.Entity.ToString(),
                    ["name"] = meaning is { } m ? _host.Names?.NameOf(context, m.Entity) : null,
                    ["kind"] = meaning?.Kind,
                    ["segment"] = meaning?.Part,
                    ["title"] = meaning?.Title ?? "код без значения у игры",
                    ["pixels"] = c.Pixels,
                    ["rect"] = new JsonArray(c.X0, c.Y0, c.X1, c.Y1),
                });
            }
            ids = new JsonObject
            {
                ["png"] = result.IdsPath,
                ["raw"] = result.IdsRawPath,
                ["note"] = $"ID-буфер: код пикселя = владелец << {SkinnedMeshes.IdPartBits} | часть; 0 — ничего (земля, колонны, " +
                    $"небо); часть {SkinnedMeshes.NoPart} — у меша нет частей. raw — uint32 LE на пиксель, {result.Width}×{result.Height}, " +
                    "строки сверху вниз; png — код своим цветом для глаз. legend — коды кадра: сущность, имя, сегмент, название, " +
                    "пикселей и рамка [x0, y0, x1, y1)",
                ["legend"] = legend,
            };
        }
        JsonObject? overlays = null;
        if (result.Layers.Count > 0)
        {
            overlays = new JsonObject();
            foreach (var l in result.Layers) overlays[l.Name] = l.Path;
        }
        var bundle = new JsonObject
        {
            ["tickId"] = simulation.Tick,
            ["frameId"] = frame,
            ["paused"] = _host.Time.Paused,
            ["alpha"] = Math.Round(_client.Alpha, 4),
            ["alphaNote"] = "доля тика кадра: 1 — кадр показывает ровно тик tickId (на паузе всегда); меньше — показ и рамки " +
                "интерполированы между тиками tickId−1 и tickId, а entity.get даёт состояние тика tickId",
            ["width"] = result.Width,
            ["height"] = result.Height,
            ["color"] = result.ColorPath,
            ["depth"] = result.DepthPath,
            ["depthNote"] = "reversed-Z: светлее — ближе, чёрное — дальняя плоскость (пусто)",
            ["depthRange"] = new JsonArray(result.DepthMin, result.DepthMax),
            ["depthCoverage"] = Math.Round(result.DepthCoverage, 4),
            ["camera"] = camera,
            ["ids"] = ids,
            ["overlays"] = overlays,
            ["overlaysNote"] = overlays is null ? null : "оверлей — снимок цвета с наложенным слоем игры (у арены: capsules — капсулы тел, " +
                "цвет по сегменту — левые голубые, правые красные, подписи бойцов, у тел на физике — крест в центре; blades — клинки; " +
                "joints — суставы: шарик на ребёнке, зелёный — в пределах, оранжевый…красный — выход за предел, серый — выключен, " +
                "красный отрезок — до точки на родителе; contacts — контакты последнего подшага из трассы физики: шарик и нормаль, " +
                "цвет — источник, без трассы пусто — см. notes; mm — Motion Matching игрока: голубая — желаемая траектория (запрос последнего " +
                "поиска), оранжевая — траектория найденного кадра, серая — история корня, зелёные шарики — стопы на земле по разметке кадра, " +
                "игрок не на MM — пусто, см. notes); капсулы — как показан боец: при alpha 1 ровно постановка последнего " +
                "тика, physics.bodies; цвет кадра — без него",
            ["notes"] = result.Notes.Count > 0 ? new JsonArray([.. result.Notes.Select(n => (JsonNode?)n)]) : null,
            // Точка Пг: кадр снимка ждёт простоя GPU — его GPU-время разобрано сразу (lagFrames 0), с копиями снимка в frameMs.
            ["gpu"] = GpuJson(passes: true),
            ["entities"] = entities,
            ["rectNote"] = "rect — [x0, y0, x1, y1] в пикселях кадра, начало — левый верхний угол; рамка мира, спроецированная камерой",
        };
        string json = bundle.ToJsonString(new System.Text.Json.JsonSerializerOptions(Json.Compact) { WriteIndented = true });
        string path = Path.ChangeExtension(result.ColorPath ?? result.DepthPath!, null);
        path = path[..path.LastIndexOf('.')] + ".bundle.json";
        File.WriteAllText(path, json);
        bundle["bundle"] = path;
        return bundle;
    }

    /// <summary>Рамка мира → прямоугольник на экране (обрезанный кадром); целиком за камерой или вне кадра — null.</summary>
    internal static (int X0, int Y0, int X1, int Y1)? ScreenRect(Matrix4x4 viewProj, Vector3 min, Vector3 max, int width, int height)
    {
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        int front = 0;
        for (int i = 0; i < 8; i++)
        {
            var p = new Vector3((i & 1) != 0 ? max.X : min.X, (i & 2) != 0 ? max.Y : min.Y, (i & 4) != 0 ? max.Z : min.Z);
            var c = Vector4.Transform(new Vector4(p, 1), viewProj);
            if (c.W <= 1e-4f) continue;   // угол за камерой (рамка SWORD WorldProjection.ToScreen)
            front++;
            float sx = (c.X / c.W * 0.5f + 0.5f) * width, sy = (0.5f - c.Y / c.W * 0.5f) * height;
            x0 = MathF.Min(x0, sx);
            y0 = MathF.Min(y0, sy);
            x1 = MathF.Max(x1, sx);
            y1 = MathF.Max(y1, sy);
        }
        if (front == 0) return null;
        int ix0 = (int)MathF.Floor(Math.Clamp(x0, 0, width)), iy0 = (int)MathF.Floor(Math.Clamp(y0, 0, height));
        int ix1 = (int)MathF.Ceiling(Math.Clamp(x1, 0, width)), iy1 = (int)MathF.Ceiling(Math.Clamp(y1, 0, height));
        return ix1 > ix0 && iy1 > iy0 ? (ix0, iy0, ix1, iy1) : null;
    }

    /// <summary>
    /// GPU-время последнего разобранного кадра (точка Пг) против показанного: его номер кадра клиента (показанный минус
    /// отставание), кадр целиком и сумма проходов. Поток кадра. Времён ещё нет — null.
    /// </summary>
    private JsonObject? GpuJson(bool passes = false)
    {
        var gpu = _client.Renderer.Gpu;
        if (!gpu.Valid) return null;
        // Кадр рендера показанного кадра — отправленных к нему минус 1; сверка — по номеру кадра рендера, а не по слоту.
        long lag = (long)_shownSubmitted - 1 - (long)gpu.Frame;
        var json = new JsonObject
        {
            ["frameId"] = lag >= 0 && (ulong)lag < _shownFrame ? _shownFrame - (ulong)lag : null,
            ["lagFrames"] = lag,
            ["frameMs"] = Math.Round(gpu.FrameMs, 5),
            ["passesMs"] = Math.Round(gpu.PassesMs, 5),
            ["dropped"] = gpu.Dropped,
            ["timestampHz"] = gpu.TimestampFrequency,
            ["note"] = "GPU-время кадра frameId — показанный минус lagFrames: разбор после ожидания слота кадра, без стойла (обычно 3 кадра " +
                "назад; после смены размера и снимка — 0). gpuMs прохода — одноимённого в том кадре (null — такого там не было). frameMs — " +
                "кадр целиком (проходы, барьеры между ними, копии снимка). Частота GPU плавает; среднее за окно — profile.stages (frames.gpu)",
        };
        if (passes)
        {
            var list = new JsonArray();
            for (int i = 0; i < gpu.Count; i++)
                list.Add(new JsonObject { ["name"] = gpu.NameOf(i), ["gpuMs"] = Math.Round(gpu.MillisecondsOf(i), 5) });
            json["passes"] = list;
        }
        return json;
    }

    private static JsonArray Vec(Vector3 v) => new(Math.Round(v.X, 4), Math.Round(v.Y, 4), Math.Round(v.Z, 4));

    // ── инструменты клиента ────────────────────────────────────────────────

    /// <summary>Предел ожидания снимка: 120 с, но не дольше, чем ждёт транспорт.</summary>
    private double MaxCaptureWait => Math.Min(120, _host.MaxWaitSeconds);

    private void Register()
    {
        _host.Add(new AgentTool
        {
            Name = "capture.bundle", Title = "Пакет наблюдения кадра",
            Description = "Снять ближайший кадр живой игры: цвет (с HUD) и глубина в PNG, видимые сущности с экранными " +
                "прямоугольниками и именами, камера, tickId и frameId — файлы в каталоге артефактов сеанса. По заказу — ID-буфер " +
                "(ids: сущность и сегмент тела на пиксель, легенда «левое предплечье врага 3», у трупа — «таз трупа врага 3») и оверлеи " +
                "слоем поверх цвета (overlays: у арены capsules — капсулы тел, blades — клинки, joints — суставы тел, contacts — " +
                "контакты физики, нужна трасса: physics.bodies {trace: on}). На паузе кадр показывает ровно последний тик.",
            Arguments = ToolSchema.Props(
                ("name", ToolSchema.Str("метка в имени файлов (буквы, цифры, «-»; по умолчанию bundle)")),
                ("ids", new JsonObject { ["type"] = "boolean", ["description"] = "снять ID-буфер: код сущности и сегмента на пиксель с легендой (по умолчанию false)" }),
                ("overlays", ToolSchema.StrArray("оверлеи игры слоем снимка (у арены — capsules: капсулы тел с подписями бойцов, blades, " +
                    "joints, contacts); неизвестное имя — ошибка")),
                ("timeoutSeconds", ToolSchema.Num($"сколько ждать кадр, с (по умолчанию 10, не больше {MaxCaptureWait:0.#})", 0.5, MaxCaptureWait))),
            ReadOnly = true, Idempotent = false, Cost = ToolCost.Heavy,
            Run = async call =>
            {
                string name = call.String("name") ?? "bundle";
                if (name.Length is 0 or > 40 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
                    throw new ArgumentException("name: буквы, цифры и «-», до 40 знаков");
                bool wantIds = call.Bool("ids", false);
                var overlays = call.Strings("overlays").Distinct().ToList();
                var known = (_client.Game as IClientObservable)?.Overlays ?? [];
                if (overlays.FirstOrDefault(o => !known.Contains(o)) is { } unknown)
                    throw new ArgumentException($"overlays: оверлея {unknown} нет; есть: {(known.Count > 0 ? string.Join(", ", known) : "никаких")}");
                double seconds = call.Number("timeoutSeconds", 10, 0.5, MaxCaptureWait);   // все аргументы — до постановки снимка
                var request = new CaptureRequest(name, wantIds, overlays);
                _captures.Enqueue(request);
                JsonObject bundle;
                try
                {
                    bundle = await request.Done.Task.WaitAsync(TimeSpan.FromSeconds(seconds), call.Cancel).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    request.Done.TrySetCanceled();   // не ждут — не снимать
                    throw new TimeoutException($"кадр не снят за {seconds:0.#} с (окно свёрнуто или его тянут за рамку?)");
                }
                catch (OperationCanceledException)
                {
                    request.Done.TrySetCanceled();
                    throw;
                }
                return new ToolOutput(bundle)
                {
                    Tick = bundle["tickId"]!.GetValue<ulong>(),
                    Frame = bundle["frameId"]!.GetValue<ulong>(),
                    ArtifactId = Path.GetFileName(bundle["bundle"]!.GetValue<string>()),
                };
            },
        });
        _host.Add(new AgentTool
        {
            Name = "render.graph", Title = "Рендер-граф кадра",
            Description = "Проходы последнего показанного кадра по порядку: вид, записи и чтения ресурсов, барьеры, вызовы " +
                "отрисовки и dispatch, GPU-время прохода (gpuMs; кадра gpu.frameId — обычно на 3 раньше показанного: кадры в полёте). " +
                "gpu — GPU-время того кадра целиком и сумма проходов. frameId и tickId — показанного кадра (тик, который он показывает). " +
                "Среднее GPU-время за окно — profile.stages (frames.gpu).",
            Run = async call =>
            {
                // Снято в Presented на потоке кадра, читается в точке обслуживания на нём же: граф, размер, кадр и тик —
                // одного показанного кадра, а не граф прошлого с тиком после тиков нового. GPU-времена (точка Пг) — тоже на
                // потоке кадра: они обновляются на месте в начале кадра.
                var snapshot = await _host.Serve(_ => _shownFrame == 0
                    ? throw new InvalidOperationException("ни одного кадра ещё не показано — повторите")
                    : (Passes: _client.Renderer.Gpu.Join(_client.Renderer.Graph.LastFrame), Gpu: GpuJson(), _client.Renderer.AdapterName, Width: _shownWidth,
                        Height: _shownHeight, Pipelines: _shownPipelines, Frame: _shownFrame, Tick: _shownTick), call.Cancel).ConfigureAwait(false);
                var list = new JsonArray();
                foreach (var p in snapshot.Passes)   // список кадра не меняется после исполнения графа
                    list.Add(new JsonObject
                    {
                        ["name"] = p.Name,
                        ["kind"] = p.Kind.ToString(),
                        ["writes"] = new JsonArray([.. p.Writes.Select(w => (JsonNode?)JsonValue.Create(w))]),
                        ["reads"] = new JsonArray([.. p.Reads.Select(r => (JsonNode?)JsonValue.Create(r))]),
                        ["barriers"] = p.Barriers,
                        ["draws"] = p.Draws,
                        ["dispatches"] = p.Dispatches,
                        ["gpuMs"] = p.GpuMs,
                    });
                return new ToolOutput(new JsonObject
                {
                    ["adapter"] = snapshot.AdapterName,
                    ["width"] = snapshot.Width,
                    ["height"] = snapshot.Height,
                    ["pipelines"] = snapshot.Pipelines,
                    ["gpu"] = snapshot.Gpu,
                    ["passes"] = list,
                }) { Frame = snapshot.Frame, Tick = snapshot.Tick };
            },
        });
    }
}
