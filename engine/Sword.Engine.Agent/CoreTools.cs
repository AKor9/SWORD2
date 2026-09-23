using System.Globalization;
using System.Text.Json.Nodes;
using Sword.Engine.Diagnostics;
using Sword.Engine.Ecs;
using Sword.Engine.Introspection;
using Sword.Engine.Runtime;

namespace Sword.Engine.Agent;

/// <summary>
/// Инструменты движка (раздел 8, точка 1е): сеанс, время, мир, команды, журнал, устройство. Чтение мира — копия в точке
/// обслуживания, разбор — здесь, на потоке агента.
/// </summary>
internal static class CoreTools
{
    private const int MaxTake = 100;

    public static void Register(AgentHost host)
    {
        // ── сеанс ──────────────────────────────────────────────────────────
        host.Add(new AgentTool
        {
            Name = "session.status", Title = "Состояние сеанса",
            Description = "Сеанс: вид процесса, сборка, контент, тик, пауза, модули, команды тика, каталог артефактов.",
            Run = async call =>
            {
                var s = await host.Serve(ctx => (ctx.Point, ctx.Dt, ctx.World.EntityCount, ctx.ActiveModules, ctx.InactiveModules), call.Cancel);
                return new ToolOutput(new JsonObject
                {
                    ["mode"] = host.Mode,
                    ["tick"] = s.Point.Tick,
                    ["paused"] = host.Time.Paused,
                    ["remainingTicks"] = host.Time.Remaining,
                    ["dtSeconds"] = s.Dt,
                    ["entities"] = s.EntityCount,
                    ["activeModules"] = Strings(s.ActiveModules),
                    ["inactiveModules"] = Strings(s.InactiveModules),
                    ["commands"] = Strings(host.Catalog.Commands.Select(Catalog.ShortName)),
                    ["namedEntities"] = Strings(host.Names?.Known ?? []),
                    ["artifacts"] = host.Artifacts,
                    ["tools"] = host.Tools.Count,
                }) { Point = s.Point };
            },
        });
        host.Add(new AgentTool
        {
            Name = "session.stop", Title = "Остановить процесс",
            Description = "Завершить процесс SWORD (Headless — выход; живая игра — закрыть окно). Ответ приходит до выхода.",
            ReadOnly = false, Idempotent = true, Destructive = true,
            Run = call =>
            {
                Log.Info("agent", "session.stop: остановка по запросу агента");
                _ = Task.Delay(200).ContinueWith(_ => host.RequestStop(), TaskScheduler.Default);
                return Task.FromResult(new ToolOutput(new JsonObject { ["stopping"] = true }));
            },
        });

        // ── время ──────────────────────────────────────────────────────────
        host.Add(new AgentTool
        {
            Name = "time.pause", Title = "Пауза симуляции",
            Description = "Остановить симуляцию (в живой игре кадры рисуются дальше). Запас шагов снимается. Ответ — тик, на " +
                "котором симуляция действительно встала (кадр, начатый до паузы, доигрывает свои тики).",
            ReadOnly = false,
            Run = async call =>
            {
                host.Time.Pause();
                // Точка обслуживания — после тиков кадра, уже начатого до паузы: её тик — тот, на котором стоим.
                return await TimeState(host, call).ConfigureAwait(false);
            },
        });
        host.Add(new AgentTool
        {
            Name = "time.resume", Title = "Снять паузу",
            Description = "Продолжить симуляцию: в живой игре — по часам, в Headless — без остановки (сколько успеет).",
            ReadOnly = false,
            Run = async call =>
            {
                host.Time.Resume();
                return await TimeState(host, call).ConfigureAwait(false);
            },
        });
        host.Add(new AgentTool
        {
            Name = "time.step", Title = "Сыграть тики",
            Description = "Сыграть ещё N тиков (на паузе — и остановиться) и дождаться. Ответ — на достигнутом тике.",
            Arguments = Props(
                ("ticks", Int("сколько тиков (по умолчанию 1)", 1, 1_000_000)),
                ("timeoutSeconds", Timeout(host))),
            ReadOnly = false, Idempotent = false, Cost = ToolCost.Moderate,
            Run = async call =>
            {
                ulong ticks = (ulong)call.Integer("ticks", 1, 1, 1_000_000);
                double seconds = TimeoutSeconds(host, call);   // все аргументы — до того, как время заказано
                ulong target = host.Time.Step(ticks);
                return await Wait(host, call, target, seconds).ConfigureAwait(false);
            },
        });
        host.Add(new AgentTool
        {
            Name = "time.runUntil", Title = "Играть до тика или условия",
            Description = "Играть, пока не сыграно tick тиков (на паузе — и остановиться), и дождаться. Прошедший тик — сразу ответ. " +
                "С condition — пока условие не выполнится после очередного тика (не дольше maxTicks): симуляция встаёт на паузу " +
                "ровно на этом тике (и в живой игре); ответ — met, tick. " + ObservationPredicate.Syntax,
            Arguments = Props(
                ("tick", Int("сыграно тиков — цель", 0, long.MaxValue)),
                ("condition", new JsonObject { ["type"] = "object", ["description"] = "условие вместо тика (язык — в описании)" }),
                ("maxTicks", Int("с condition: не больше стольких тиков (по умолчанию 100 000)", 1, 10_000_000)),
                ("timeoutSeconds", Timeout(host))),
            ReadOnly = false, Idempotent = true, Cost = ToolCost.Moderate,
            Run = async call =>
            {
                if (call.Node("condition") is { } condition)
                {
                    if (call.Node("tick") is not null) throw new ArgumentException("tick или condition — что-то одно");
                    return await ObservationTools.RunUntil(host, call, null, condition).ConfigureAwait(false);
                }
                if (call.Node("tick") is null) throw new ArgumentException("нужен tick или condition");
                ulong tick = (ulong)call.Integer("tick", 0, 0, long.MaxValue);
                double seconds = TimeoutSeconds(host, call);
                if (tick - Math.Min(tick, host.Time.Tick) > 10_000_000) throw new ArgumentException("tick: не дальше 10 000 000 тиков вперёд");
                host.Time.RunUntil(tick);
                return await Wait(host, call, tick, seconds).ConfigureAwait(false);
            },
        });

        // ── мир ────────────────────────────────────────────────────────────
        host.Add(new AgentTool
        {
            Name = "world.types", Title = "Типы данных мира",
            Description = "Компоненты, события, ресурсы и команды тика мира — короткие имена и поля (схема полей для command.send и fields).",
            Arguments = Props(("name", Str("только этот тип (имя компонента, события или команды), с полной схемой полей"))),
            Run = call => Task.FromResult(new ToolOutput(Types(host.Catalog, call.String("name")))),
        });
        host.Add(new AgentTool
        {
            Name = "world.query", Title = "Запрос к миру",
            Description = "Сущности со всеми компонентами with, без without и (where) с условием на сущность, по возрастанию " +
                "номера, страницей (skip/take). fields — только эти компоненты или поля («Transform», «Transform.Position»). " +
                "take 0 — только число. aggregate — по всем подходящим: count, min, max, mean, sum числовых полей " +
                "(«Transform.Position.X»). " + ObservationPredicate.Syntax,
            Arguments = Props(
                ("with", StrArray("компоненты, которые должны быть (короткие или полные имена)")),
                ("without", StrArray("компоненты, которых быть не должно")),
                ("where", new JsonObject { ["type"] = "object", ["description"] = "условие на сущность (язык — в описании)" }),
                ("fields", StrArray("проекция: компоненты или пути полей")),
                ("aggregate", StrArray("числовые поля-листья: count, min, max, mean, sum по всем подходящим")),
                ("skip", Int("пропустить (по умолчанию 0)", 0, int.MaxValue)),
                ("take", Int($"сколько вернуть (по умолчанию 20, не больше {MaxTake})", 0, MaxTake))),
            Run = async call =>
            {
                var with = call.Strings("with").Select(n => host.Catalog.Component(n).Id).ToList();
                var without = call.Strings("without").Select(n => host.Catalog.Component(n).Id).ToList();
                var fields = call.Strings("fields");
                var project = fields.Count == 0 ? null
                    : fields.Select(f => ComponentOfPath(host.Catalog, f).Id).Distinct().ToList();
                // Пути — к отображаемому имени компонента (короткое или полное) и точным именам полей, как в ответе.
                fields = [.. fields.Select(f => NormalizePath(host.Catalog, f))];
                var where = call.Node("where");
                var aggregates = call.Strings("aggregate");
                int skip = (int)call.Integer("skip", 0, 0, int.MaxValue), take = (int)call.Integer("take", 20, 0, MaxTake);
                var (page, stats, point) = await host.Serve(ctx =>
                {
                    var predicate = where is null ? null : ObservationPredicate.Compile(where, ctx, host.Catalog, host.Names, PredicateScope.Entity);
                    if (predicate is null && aggregates.Count == 0)
                        return (WorldQuery.Copy(ctx, with, without, project, skip, take, host.Names), (JsonObject?)null, ctx.Point);
                    var all = new List<Entity>();
                    ctx.World.Select([.. with], [.. without], 0, int.MaxValue, all);
                    if (predicate is not null) all = [.. all.Where(e => predicate.Matches(ctx, e, host.Observation.History))];
                    var copy = WorldQuery.CopyMany(ctx, all.Skip(skip).Take(take).ToList(), all.Count, project, host.Names);
                    return (copy, aggregates.Count == 0 ? null : Aggregate(ctx, host.Catalog, all, aggregates), ctx.Point);
                }, call.Cancel);
                var rows = new JsonArray([.. page.Rows.Select(r => (JsonNode?)WorldQuery.ToJson(r, host.Catalog, fields.Count == 0 ? null : fields))]);
                // take 0 — только число: ответ полный, продолжения нет (иначе обход по страницам зациклился бы на skip).
                bool truncated = take > 0 && skip + page.Rows.Count < page.Total;
                var data = new JsonObject
                {
                    ["total"] = page.Total, ["skip"] = skip, ["count"] = page.Rows.Count, ["entities"] = rows,
                };
                if (stats is not null) data["aggregate"] = stats;
                return new ToolOutput(data)
                { Point = point, Truncated = truncated, Continuation = truncated ? $"skip={skip + page.Rows.Count}" : null };
            },
        });
        host.Add(new AgentTool
        {
            Name = "entity.get", Title = "Сущность",
            Description = "Одна сущность со всеми компонентами (или только components): номер «12v3» или имя (player — игрок).",
            Arguments = Props(
                ("entity", Str("номер «12v3» или имя сущности (см. session.status → namedEntities)")),
                ("components", StrArray("только эти компоненты"))),
            Required = ["entity"],
            Run = async call =>
            {
                string which = call.RequiredString("entity");
                var names = call.Strings("components");
                var project = names.Count == 0 ? null : names.Select(n => host.Catalog.Component(n).Id).ToList();
                var result = await host.Serve(ctx =>
                {
                    var e = ResolveEntity(ctx, which, host.Names);
                    return (Entity: e, Copy: WorldQuery.CopyOne(ctx, e, project, host.Names), ctx.Point);
                }, call.Cancel);
                if (result.Copy is null) throw new ArgumentException($"сущность {result.Entity} не жива");
                return new ToolOutput(WorldQuery.ToJson(result.Copy, host.Catalog)) { Point = result.Point };
            },
        });

        // ── состояние ──────────────────────────────────────────────────────
        host.Add(new AgentTool
        {
            Name = "state.hash", Title = "Хеш состояния",
            Description = "Хеш состояния мира после последнего тика (посреди тика, time.break, — после стадии из квитанции; с " +
                "хешами реплея такой не сравнивается): итог и хеш каждого участника (ecs — сущности, компоненты, " +
                "ресурсы-значения, слоты, журналы, системы; animation — пул поз; другие — подсистемы). Два прогона с одинаковыми командами дают " +
                "одинаковые хеши; разный хеш участника называет подсистему, её состояние — state.describe.",
            Run = async call =>
            {
                var result = await host.Serve(ctx => (Hash: ctx.HashState(), ctx.Point), call.Cancel);
                var parts = new JsonObject();
                foreach (var (id, value) in result.Hash.Parts) parts[id] = $"{value:X16}";
                return new ToolOutput(new JsonObject { ["total"] = $"{result.Hash.Total:X16}", ["parts"] = parts }) { Point = result.Point };
            },
        });
        host.Add(new AgentTool
        {
            Name = "state.describe", Title = "Состояние участника",
            Description = "Ровно то, что участник хеша (state.hash → parts) хеширует, по частям: у ecs — сущности (компоненты " +
                "полями) и ресурсы, slots, journal:… (непрочитанные записи), system:… (включена ли, сколько не прочла); filter — сущность «12v3», " +
                "часть имени компонента или id части (slots, journal:, system:); у animation — позы пула, filter — поза " +
                "«2v1», часть имени сустава или «2v1:foot». Страницей: skip, take. Больше " + MaxDescribeChars / 1000 + " тыс. знаков — " +
                "в файл артефакта.",
            Arguments = Props(
                ("part", Str("участник: ecs, animation, …")),
                ("filter", Str("отбор (смысл — у участника)")),
                ("skip", Int("пропустить частей", 0, int.MaxValue)),
                ("take", Int("частей на странице (по умолчанию 20)", 1, 500))),
            Required = ["part"],
            Run = async call =>
            {
                string part = call.RequiredString("part");
                var query = new DescribeQuery(call.String("filter"), (int)call.Integer("skip", 0, 0, int.MaxValue),
                    (int)call.Integer("take", 20, 1, 500));
                var result = await host.Serve(ctx => (Json: ctx.DescribeState(part, query), ctx.Point), call.Cancel);
                var data = (JsonObject)JsonNode.Parse(result.Json)!;
                int matched = (int)data["matched"]!, written = ((JsonArray)data["items"]!).Count;
                bool truncated = query.Skip + written < matched;
                data["part"] = part;
                string? artifact = null;
                if (result.Json.Length > MaxDescribeChars)
                {
                    artifact = $"state-{part}-t{result.Point.Tick}-{query.Skip}.json";
                    File.WriteAllText(host.ArtifactPath(artifact), data.ToJsonString());
                    data = new JsonObject
                    {
                        ["part"] = part, ["matched"] = matched, ["written"] = written,
                        ["artifact"] = host.ArtifactPath(artifact), ["hint"] = "ответ велик — он в файле; сузьте filter или take",
                    };
                }
                return new ToolOutput(data)
                {
                    Point = result.Point, Truncated = truncated, Continuation = truncated ? $"skip={query.Skip + written}" : null,
                    ArtifactId = artifact,
                };
            },
        });

        // ── команды ────────────────────────────────────────────────────────
        host.Add(new AgentTool
        {
            Name = "command.send", Title = "Команда тика",
            Description = "Поставить команду тика (например PlayerCommand) на будущий тик: tick — номер тика, иначе через delay тиков " +
                "от ближайшего; repeat — на столько тиков подряд (команда игрока действует один тик). Поля — объект fields " +
                "(схема: world.types); незаданные — нули. В тике с командой клиента побеждает команда агента.",
            Arguments = Props(
                ("command", Str("имя команды (world.types → commands)")),
                ("fields", new JsonObject { ["type"] = "object", ["description"] = "поля команды, например {\"Move\":{\"X\":1,\"Y\":0}}" }),
                ("tick", Int("номер тика (не раньше ближайшего)", 0, long.MaxValue)),
                ("delay", Int("через сколько тиков от ближайшего (по умолчанию 0)", 0, 1_000_000)),
                ("repeat", Int("на сколько тиков подряд (по умолчанию 1)", 1, 100_000))),
            Required = ["command"],
            ReadOnly = false, Idempotent = false,
            Run = async call =>
            {
                var type = host.Catalog.Command(call.RequiredString("command"));
                byte[] bytes = FieldCodec.Encode(type, call.Node("fields"));
                long? at = call.Node("tick") is null ? null : call.Integer("tick", 0, 0, long.MaxValue);
                ulong delay = (ulong)call.Integer("delay", 0, 0, 1_000_000), repeat = (ulong)call.Integer("repeat", 1, 1, 100_000);
                var scheduled = await host.Serve(ctx =>
                {
                    // Ближайший — посреди тика (останов time.break) следующий: команды идущего тика уже выданы.
                    ulong nearest = ctx.NextTick;
                    ulong first = at is { } t ? (ulong)t : nearest + delay;
                    if (first < nearest) throw new ArgumentException($"tick {first}: тик уже прошёл или идёт (ближайший — {nearest})");
                    // Далёкое будущее — почти наверняка ошибка, а очередь команд просматривается каждый тик. Весь диапазон,
                    // до первой постановки: last = first + repeat − 1 (repeat ≤ 100 000 < MaxAhead — вычитание без переполнения).
                    if (first - nearest > MaxAhead - (repeat - 1))
                        throw new ArgumentException($"тики {first}…{first + (repeat - 1)}: последний — не дальше " +
                            $"{MaxAhead} тиков от ближайшего ({nearest})");
                    ctx.Enqueue(type, first, bytes, (int)repeat);   // диапазон — одной постановкой
                    return (First: first, ctx.Point);
                }, call.Cancel);
                Log.Info("agent", $"command.send {Catalog.ShortName(type)} на тики {scheduled.First}…{scheduled.First + repeat - 1}");
                return new ToolOutput(new JsonObject
                {
                    ["command"] = Catalog.ShortName(type),
                    ["fields"] = FieldCodec.Decode(type, bytes),
                    ["firstTick"] = scheduled.First,
                    ["lastTick"] = scheduled.First + repeat - 1,
                }) { Point = scheduled.Point };
            },
        });

        // ── журнал ─────────────────────────────────────────────────────────
        host.Add(new AgentTool
        {
            Name = "log.tail", Title = "Журнал",
            Description = "Последние записи журнала движка (с номерами): after — после этого номера (продолжение), level, category.",
            Arguments = Props(
                ("after", Int("номер последней прочитанной записи (по умолчанию — последние limit)", 0, long.MaxValue)),
                ("limit", Int("сколько записей (по умолчанию 50, не больше 500)", 1, 500)),
                ("level", Str("не ниже уровня: debug, info, warning, error")),
                ("category", Str("только категория (client, agent, d3d12…)"))),
            Run = call =>
            {
                int limit = (int)call.Integer("limit", 50, 1, 500);
                long? after = call.Node("after") is null ? null : call.Integer("after", 0, 0, long.MaxValue);
                var level = call.String("level") is { } l
                    ? Enum.TryParse<LogLevel>(l, ignoreCase: true, out var parsed) && !l.Contains(',') ? parsed
                        : throw new ArgumentException($"level «{l}»: debug, info, warning или error")
                    : LogLevel.Debug;
                // Без after — последние limit записей, прошедших фильтр (а не фильтр по последним limit записям): продолжать
                // нечего, следующий вызов — с after = last. С after — вперёд от номера, страницами.
                long missed = 0, last;
                List<LogRecord> records;
                if (after is { } a)
                {
                    records = host.LogRing.Tail(a, limit, level, call.String("category"), out missed);
                    last = records.Count > 0 ? records[^1].Sequence : a;
                }
                else
                    records = host.LogRing.Latest(limit, level, call.String("category"), out last);
                bool more = after is not null && last < host.LogRing.Last && records.Count == limit;
                return Task.FromResult(new ToolOutput(new JsonObject
                {
                    ["entries"] = new JsonArray([.. records.Select(r => (JsonNode?)new JsonObject
                    {
                        ["seq"] = r.Sequence,
                        ["utc"] = r.Entry.Utc.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                        ["level"] = r.Entry.Level.ToString().ToLowerInvariant(),
                        ["category"] = r.Entry.Category,
                        ["message"] = r.Entry.Message,
                    })]),
                    ["last"] = last,
                    ["missed"] = missed,
                }) { Truncated = more, Continuation = more ? $"after={last}" : null });
            },
        });

        // ── устройство ─────────────────────────────────────────────────────
        host.Add(new AgentTool
        {
            Name = "architecture.projects", Title = "Сборки процесса",
            Description = "Сборки SWORD этого процесса: слой, сторона, ссылки на другие сборки SWORD.",
            Run = call => Task.FromResult(new ToolOutput(ArchitectureMap.Projects())),
        });
        host.Add(new AgentTool
        {
            Name = "ecs.plan", Title = "План тика",
            Description = "Стадии тика по порядку и системы каждой в порядке исполнения (модуль, активна ли, срабатывание).",
            Run = async call =>
            {
                var s = await host.Serve(ctx => (ctx.World.Systems, ctx.Point), call.Cancel);
                return new ToolOutput(ArchitectureMap.Plan(host.Catalog.Stages, s.Systems)) { Point = s.Point };
            },
        });
        host.Add(new AgentTool
        {
            Name = "ecs.explainSystem", Title = "Объяснить систему",
            Description = "Система (Метка@Стадия, метка или тип; часть имени — если однозначна): стадия и порядок, что читает и " +
                "пишет, что порождает и потребляет, NextTickOk, место регистрации, кто поставляет входы и кто берёт выходы.",
            Arguments = Props(("name", Str("имя системы"))),
            Required = ["name"],
            Run = async call =>
            {
                string name = call.RequiredString("name");
                var s = await host.Serve(ctx => (ctx.World.Systems, ctx.Point), call.Cancel);
                return new ToolOutput(ArchitectureMap.ExplainSystem(s.Systems, name)) { Point = s.Point };
            },
        });
        host.Add(new AgentTool
        {
            Name = "ecs.componentUsage", Title = "Кто пользуется данными",
            Description = "Какие системы читают, пишут, добавляют, снимают, ставят, порождают или потребляют компонент или событие; " +
                "hooks — хуки времени жизни компонента (OnAdd/OnRemove, модуль; null — ядро): кто заводит и освобождает ресурс подсистемы за ним (тело за BodyRef).",
            Arguments = Props(("name", Str("имя компонента, события или команды"))),
            Required = ["name"],
            Run = async call =>
            {
                string stable = StableName(host.Catalog, call.RequiredString("name"));
                var s = await host.Serve(ctx => (ctx.World.Systems, ctx.World.Hooks, ctx.Point), call.Cancel);
                return new ToolOutput(ArchitectureMap.ComponentUsage(s.Systems, stable, s.Hooks)) { Point = s.Point };
            },
        });
        host.Add(new AgentTool
        {
            Name = "module.graph", Title = "Модули",
            Description = "Модули симуляции: включённые и выключенные, системы каждого и данные, которые они пишут и читают.",
            Run = async call =>
            {
                var s = await host.Serve(ctx => (ctx.World.Systems, ctx.ActiveModules, ctx.InactiveModules, ctx.Point), call.Cancel);
                return new ToolOutput(ArchitectureMap.ModuleGraph(s.Systems, s.ActiveModules, s.InactiveModules)) { Point = s.Point };
            },
        });
    }

    // ── помощники ──────────────────────────────────────────────────────────

    // Предел — от транспорта (AgentHostOptions.MaxWait): схема не обещает ожидание, которое транспорт оборвёт.
    private static JsonObject Timeout(AgentHost host) =>
        Num($"сколько ждать, с (по умолчанию {DefaultWait(host):0.#}, не больше {host.MaxWaitSeconds:0.#})", 0.1, host.MaxWaitSeconds);

    private static double DefaultWait(AgentHost host) => Math.Min(30, host.MaxWaitSeconds);

    private static double TimeoutSeconds(AgentHost host, ToolCall call) =>
        call.Number("timeoutSeconds", DefaultWait(host), 0.1, host.MaxWaitSeconds);

    private static async Task<ToolOutput> Wait(AgentHost host, ToolCall call, ulong target, double seconds)
    {
        // Таймаут — отменой ожидания, а не WaitAsync: отменённое ожидание сразу уходит из TimeControl.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(call.Cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        try
        {
            await host.Time.WhenReached(target, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!call.Cancel.IsCancellationRequested)
        {
            throw new TimeoutException($"тик {target} не достигнут за {seconds:0.#} с (сыграно {host.Time.Tick}); " +
                "симуляция продолжает идти к цели — повторите time.runUntil или поставьте time.pause");
        }
        return await TimeState(host, call).ConfigureAwait(false);
    }

    /// <summary>
    /// Состояние времени в ближайшей точке обслуживания: тик и кадр квитанции — одной точки (ожидание тика отпускается
    /// внутри кадра, до того как клиент запишет номер кадра; отдельно прочитанные тик и кадр разошлись бы на кадр).
    /// </summary>
    private static async Task<ToolOutput> TimeState(AgentHost host, ToolCall call)
    {
        var point = await host.Serve(ctx => ctx.Point, call.Cancel).ConfigureAwait(false);
        return new ToolOutput(new JsonObject { ["tick"] = point.Tick, ["paused"] = host.Time.Paused, ["remainingTicks"] = host.Time.Remaining })
        { Point = point };
    }

    /// <summary>Агрегаты числовых листьев по сущностям (точка обслуживания): count, min, max, mean, sum по тем, у кого поле есть.</summary>
    private static JsonObject Aggregate(in ServiceContext ctx, Catalog catalog, List<Entity> entities, IReadOnlyList<string> paths)
    {
        var result = new JsonObject();
        foreach (string path in paths)
        {
            var (component, rest) = ObservationPredicate.SplitComponent(catalog, path, "aggregate");
            if (component.IsTag) throw new ArgumentException($"aggregate: {component.Name} — метка без полей");
            var layout = FieldLayout.Of(component.Type);
            var leaves = ObservationPredicate.LeavesOf(layout, rest, $"aggregate ({path})");
            if (leaves.Length != 1 || layout.Leaves[leaves[0]].Kind == FieldLayout.Kind.Entity)
                throw new ArgumentException($"aggregate: «{path}» — не числовой лист ({string.Join(", ", leaves.Take(6).Select(l => layout.Leaves[l].Path))})");
            int leaf = leaves[0], count = 0;
            double min = double.PositiveInfinity, max = double.NegativeInfinity, sum = 0;
            foreach (var e in entities)
            {
                if (!ctx.World.TryRead(e, component.Id, out var bytes)) continue;
                double v = ObservationPredicate.Number(layout.Leaves[leaf].Kind, layout.Bits(bytes, leaf));
                min = Math.Min(min, v);
                max = Math.Max(max, v);
                sum += v;
                count++;
            }
            result[path] = new JsonObject
            {
                ["count"] = count,
                ["min"] = count > 0 ? Finite(min) : null,
                ["max"] = count > 0 ? Finite(max) : null,
                ["mean"] = count > 0 ? Finite(sum / count) : null,
                ["sum"] = Finite(sum),
            };
        }
        return result;
    }

    private static JsonNode Finite(double v) => double.IsFinite(v) ? JsonValue.Create(v) : JsonValue.Create(v.ToString(CultureInfo.InvariantCulture));

    /// <summary>Команда — не дальше стольких тиков вперёд (≈ 2,3 ч при 120 Гц).</summary>
    private const ulong MaxAhead = 1_000_000;

    /// <summary>
    /// Компонент пути поля: самое длинное начало пути, которое называет компонент (<c>Transform.Position</c>,
    /// <c>Sword.Game.Contracts.Transform.Position.X</c>).
    /// </summary>
    private static Sword.Engine.Ecs.DataTypeInfo ComponentOfPath(Catalog catalog, string path)
    {
        var parts = path.Split('.');
        for (int n = parts.Length; n >= 1; n--)
        {
            try
            {
                return catalog.Component(string.Join('.', parts[..n]));
            }
            catch (ArgumentException) when (n > 1)
            {
            }
        }
        throw new ArgumentException($"нет компонента в пути «{path}»");
    }

    private static string NormalizePath(Catalog catalog, string path)
    {
        var info = ComponentOfPath(catalog, path);
        var parts = path.Split('.');
        for (int n = parts.Length; n >= 1; n--)
            if (string.Equals(string.Join('.', parts[..n]), info.Name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(string.Join('.', parts[..n]), Catalog.ShortName(info.Type), StringComparison.OrdinalIgnoreCase))
            {
                // Путь внутри компонента — по схеме полей до запроса: «Transform.Positoin.X» — ошибка, а не пустой ответ.
                string display = catalog.DisplayName(info);
                return n == parts.Length ? display
                    : display + "." + FieldCodec.CheckPath(info.Type, string.Join('.', parts[n..]), display);
            }
        return path;
    }

    private static Entity ResolveEntity(in ServiceContext ctx, string which, IEntityNames? names)
    {
        if (which.Length > 0 && char.IsDigit(which[0])) return FieldCodec.ParseEntity(which);
        return names?.Resolve(ctx, which) ?? throw new ArgumentException(
            $"нет сущности с именем «{which}»; имена: {string.Join(", ", names?.Known ?? [])} (или номер «12v3»)");
    }

    private static string StableName(Catalog catalog, string name)
    {
        try
        {
            return catalog.Component(name).Name;
        }
        catch (ArgumentException)
        {
            var e = catalog.Events.FirstOrDefault(e => string.Equals(Catalog.ShortName(e.Type), name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            return e?.Name ?? throw new ArgumentException($"нет компонента или события «{name}»; см. world.types");
        }
    }

    private static JsonObject Types(Catalog catalog, string? only)
    {
        JsonObject Entry(Type t, string kind, bool full) => new()
        {
            ["name"] = Catalog.ShortName(t),
            ["kind"] = kind,
            ["stableName"] = t.FullName,
            ["fields"] = full || only is not null ? FieldCodec.Schema(t) : new JsonArray([.. FieldCodec.Fields(t).Select(f => (JsonNode?)JsonValue.Create(f.Name))]),
        };
        var all = catalog.Components.Select(c => (c.Type, Kind: c.IsTag ? "tag" : "component"))
            .Concat(catalog.Commands.Select(t => (Type: t, Kind: "command")))
            .Concat(catalog.Events.Where(e => !catalog.Commands.Contains(e.Type)).Select(e => (e.Type, Kind: "event")))
            .Concat(catalog.Resources.Where(r => r.Size > 0).Select(r => (r.Type, Kind: "resource")))
            .ToList();
        if (only is not null)
        {
            var match = all.Where(a => string.Equals(Catalog.ShortName(a.Type), only, StringComparison.OrdinalIgnoreCase)
                || string.Equals(a.Type.FullName, only, StringComparison.OrdinalIgnoreCase)).ToList();
            if (match.Count == 0) throw new ArgumentException($"нет типа «{only}»; есть: {string.Join(", ", all.Select(a => Catalog.ShortName(a.Type)).Distinct())}");
            return new JsonObject { ["types"] = new JsonArray([.. match.Select(m => (JsonNode?)Entry(m.Type, m.Kind, true))]) };
        }
        return new JsonObject { ["types"] = new JsonArray([.. all.Select(m => (JsonNode?)Entry(m.Type, m.Kind, false))]) };
    }

    /// <summary>Потолок ответа state.describe в знаках — больше уходит в файл артефакта.</summary>
    internal const int MaxDescribeChars = 60_000;

    // ── схема аргументов ───────────────────────────────────────────────────

    internal static JsonObject Props(params (string Name, JsonObject Schema)[] props)
    {
        var o = new JsonObject();
        foreach (var (name, schema) in props) o[name] = schema;
        return o;
    }

    internal static JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };

    internal static JsonObject StrArray(string description) =>
        new() { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = description };

    internal static JsonObject Int(string description, long min, long max) =>
        new() { ["type"] = "integer", ["minimum"] = min, ["maximum"] = max, ["description"] = description };

    internal static JsonObject Num(string description, double min, double max) =>
        new() { ["type"] = "number", ["minimum"] = min, ["maximum"] = max, ["description"] = description };

    private static JsonArray Strings(IEnumerable<string> items) => new([.. items.Select(i => (JsonNode?)JsonValue.Create(i))]);
}
