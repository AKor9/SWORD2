using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Sword.Engine.Runtime;

namespace Sword.Engine.Introspection;

/// <summary>
/// Ветка эксперимента (<c>experiment.fork</c>): ключи конфига поверх конфига реплея (действуют с тика 0 — конфиг
/// собирает симуляцию), команды на тиках от развилки (перебивают команды реплея своего типа в своих тиках — как
/// <c>command.send</c> поверх реплея), <see cref="Cut"/> — команды реплея после развилки не идут вовсе.
/// </summary>
public sealed record ForkVariant(string Name, IReadOnlyList<string> Sets, IReadOnlyList<ScenarioCommand> Commands, bool Cut);

/// <summary>Что измерить в конце каждой ветки: поле сущности (<see cref="FieldProbe"/>).</summary>
public sealed record ForkMeasure(string Entity, string Field);

/// <summary>
/// Ветка после прогона: запись (с хешами каждого тика), сравнение с базой (<see cref="ReplayDiff.Result"/> — первый
/// тик с разными командами, первый хеш с разными участниками), измерения в конце, нарушения инвариантов, мс на тик.
/// </summary>
public sealed record ForkBranch(string Name, ForkVariant? Variant, Replay? Recorded, ulong Ticks, double MsPerTick, long Violations,
    string? FirstViolation, IReadOnlyDictionary<string, double> Measures, ReplayDiff.Result? VsBase, string? PlaybackDivergence, string? Error);

/// <summary>Разница описаний участника (<c>state.describe</c>) двух веток на одном тике: пути листьев и значения.</summary>
public sealed record DescribeDiff(string Part, int Total, IReadOnlyList<(string Path, string? A, string? B)> Lines);

/// <summary>Строка перебора (<c>experiment.sweep</c>): комбинация ключей и замеры повторов.</summary>
public sealed record SweepRow(IReadOnlyList<string> Sets, int Repeats, double MsMedian, double MsMin, double MsMax,
    IReadOnlyDictionary<string, double> Stages, double AllocatedBytesPerTick, int Entities, string? Hash, bool Deterministic,
    long Violations, string? Error);

/// <summary>
/// Эксперименты (раздел 8 плана): развилка реплея на ветки (<see cref="Fork"/>), сравнение двух прогонов
/// (<see cref="Compare"/>), перебор конфига с замерами (<see cref="Sweep"/>). Всё — в отдельных симуляциях той же
/// композиции, что у exe; основа — детерминизм: ветки до развилки совпадают бит в бит, расходятся на тике изменения.
/// </summary>
public static class Experiments
{
    public const string BaseBranch = "base";
    public const int MaxBranches = 16, MaxCombinations = 64;

    /// <summary>
    /// Развилка: реплей <paramref name="replay"/> до тика <paramref name="at"/> и дальше ветками — база (реплей как есть) и
    /// <paramref name="variants"/>; каждая — до <paramref name="ticks"/> тиков, параллельно (свои симуляции на своих
    /// потоках). Результат — ветки по порядку: база первой.
    /// </summary>
    public static IReadOnlyList<ForkBranch> Fork(Replay replay, ulong at, ulong ticks, IReadOnlyList<ForkVariant> variants,
        IReadOnlyList<ForkMeasure> measure, Func<ConfigSet, Simulation> build, ReplayInfo info, IEntityNames? names, CancellationToken cancel = default)
    {
        if (at > ticks) throw new ArgumentException($"at {at}: развилка за концом прогона ({ticks} тиков)");
        if (variants.Count == 0) throw new ArgumentException("variants: хотя бы одна ветка");
        if (variants.Count + 1 > MaxBranches) throw new ArgumentException($"веток не больше {MaxBranches - 1} (и база)");
        FeatureManifest.Unique(variants.Select(v => v.Name), "variants");
        foreach (var v in variants)
        {
            if (v.Name == BaseBranch || !FeatureManifest.IsValidId(v.Name.Replace('-', '_')))
                throw new ArgumentException($"variants: имя «{v.Name}» — {FeatureManifest.IdRule} (и '-'), не «{BaseBranch}»");
            foreach (var c in v.Commands.Where(c => c.From < at))
                throw new ArgumentException($"ветка {v.Name}: команда {c.Command} с тика {c.From} — раньше развилки {at} (до развилки ветка повторяет базу)");
            if (v.Sets.Count == 0 && v.Commands.Count == 0 && !v.Cut) throw new ArgumentException($"ветка {v.Name} ничем не отличается от базы");
        }

        var all = new ForkVariant?[] { null }.Concat(variants).ToArray();
        var branches = new ForkBranch[all.Length];
        try
        {
            Parallel.For(0, all.Length, new ParallelOptions { CancellationToken = cancel }, i =>
                branches[i] = Branch(replay, at, ticks, all[i], measure, build, info, names, cancel));
        }
        catch (AggregateException e) when (e.InnerExceptions.All(x => x is OperationCanceledException))
        {
            // Таймаут вызова — отмена, а не «ошибка веток»: инструмент ответит таймаутом.
            throw new OperationCanceledException(cancel);
        }

        var baseRec = branches[0].Recorded;
        for (int i = 1; i < branches.Length; i++)
            if (baseRec is not null && branches[i].Recorded is { } rec)
                branches[i] = branches[i] with { VsBase = ReplayDiff.Compare(baseRec, rec) };
        return branches;
    }

    private static ForkBranch Branch(Replay replay, ulong at, ulong ticks, ForkVariant? v, IReadOnlyList<ForkMeasure> measure,
        Func<ConfigSet, Simulation> build, ReplayInfo info, IEntityNames? names, CancellationToken cancel)
    {
        string name = v?.Name ?? BaseBranch;
        try
        {
            var sim = build(replay.CreateConfig(v?.Sets ?? []));
            var player = sim.Play(v is { Cut: true } ? replay.Head(at) : replay);
            var recorder = sim.Record(info);
            var catalog = new Catalog(sim.World, sim.CommandTypes);
            foreach (var c in v?.Commands ?? [])
            {
                var type = catalog.Command(c.Command);
                sim.EnqueueRaw(type, c.From, FieldCodec.Encode(type, c.Fields), checked((int)(c.To - c.From)));
            }
            var probes = measure.Select(m => new FieldProbe(catalog, names, m.Entity, m.Field, "measure")).ToList();
            long started = Stopwatch.GetTimestamp();
            while (sim.Tick < ticks)
            {
                if ((sim.Tick & 1023) == 0) cancel.ThrowIfCancellationRequested();
                sim.Export.BeginFrame();
                sim.Step();
            }
            double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var ctx = new ServiceContext(sim);
            var measures = probes.ToDictionary(p => $"{p.EntityName}:{p.FieldPath}", p => p.Read(ctx));
            // Хеши реплея сверяет только база: у ветки расхождение с файлом и есть её смысл.
            return new ForkBranch(name, v, recorder.ToReplay(), sim.Tick, ticks > 0 ? ms / ticks : 0, sim.Invariants.Total,
                sim.Invariants.Recent().FirstOrDefault() is { Id: not null } first ? first.ToString() : null, measures, null,
                v is null ? player.Divergence?.ToString() : null, null);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or InvalidDataException)
        {
            return new ForkBranch(name, v, null, 0, 0, 0, null, new Dictionary<string, double>(), null, null, e.Message);
        }
    }

    /// <summary>
    /// Сравнить два прогона (записи веток, реплеи): заголовки, команды, хеши (<see cref="ReplayDiff"/>) и — если хеши
    /// разошлись — описания разошедшихся участников на первом разошедшемся хеше: оба реплея переигрываются до этого тика
    /// в своих конфигах (<paramref name="describe"/>).
    /// </summary>
    public static (ReplayDiff.Result Diff, ulong? DescribedAt, IReadOnlyList<DescribeDiff> Describe) Compare(Replay a, Replay b,
        Func<ConfigSet, Simulation> build, bool describe, int maxLines = 50, CancellationToken cancel = default)
    {
        var diff = ReplayDiff.Compare(a, b);
        if (!describe || diff.FirstHashTick is not { } tick) return (diff, null, []);
        Simulation At(Replay r)
        {
            var sim = build(r.CreateConfig());
            sim.Play(r);
            while (sim.Tick < tick)
            {
                if ((sim.Tick & 1023) == 0) cancel.ThrowIfCancellationRequested();
                sim.Export.BeginFrame();
                sim.Step();
            }
            return sim;
        }
        var sa = At(a);
        var sb = At(b);
        var all = new DescribeQuery(null, 0, int.MaxValue);
        var result = new List<DescribeDiff>();
        foreach (string part in diff.HashParts)
        {
            var fa = Flatten(JsonNode.Parse(sa.DescribeState(part, all)));
            var fb = Flatten(JsonNode.Parse(sb.DescribeState(part, all)));
            var lines = new List<(string, string?, string?)>();
            int total = 0;
            foreach (string key in fa.Keys.Union(fb.Keys).Order(StringComparer.Ordinal))
            {
                string? va = fa.GetValueOrDefault(key), vb = fb.GetValueOrDefault(key);
                if (va == vb) continue;
                total++;
                if (lines.Count < maxLines) lines.Add((key, va, vb));
            }
            result.Add(new DescribeDiff(part, total, lines));
        }
        return (diff, tick, result);
    }

    /// <summary>JSON → плоские пути листьев (<c>entities[3].Transform.Position.X</c>) и значения текстом.</summary>
    public static Dictionary<string, string> Flatten(JsonNode? node)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        void Walk(JsonNode? n, string path)
        {
            switch (n)
            {
                case JsonObject o:
                    foreach (var (k, v) in o) Walk(v, path.Length == 0 ? k : $"{path}.{k}");
                    break;
                case JsonArray a:
                    for (int i = 0; i < a.Count; i++) Walk(a[i], $"{path}[{i}]");
                    break;
                default:
                    map[path] = n?.ToJsonString() ?? "null";
                    break;
            }
        }
        Walk(node, "");
        return map;
    }

    /// <summary>
    /// Перебор конфига (<c>experiment.sweep</c>): каждая комбинация значений ключей <paramref name="parameters"/> —
    /// <paramref name="repeats"/> прогонов по очереди (замеры времени не мешают друг другу): сценарий <paramref name="scenario"/>
    /// (или мир без команд) до <paramref name="ticks"/> тиков, первые <paramref name="warmup"/> — прогрев. Строка — медиана,
    /// разброс, стадии, байты на тик, число сущностей, хеш итога и одинаков ли он у повторов (детерминизм), нарушения.
    /// </summary>
    public static (IReadOnlyList<SweepRow> Rows, IReadOnlyList<string> Scaling) Sweep(Replay? scenario, ulong ticks,
        IReadOnlyList<(string Key, IReadOnlyList<string> Values)> parameters, int repeats, int warmup, Func<ConfigSet, Simulation> build,
        CancellationToken cancel = default)
    {
        if (parameters.Count == 0) throw new ArgumentException("params: хотя бы один ключ со значениями");
        foreach (var (key, values) in parameters)
            if (values.Count == 0) throw new ArgumentException($"params.{key}: хотя бы одно значение");
        FeatureManifest.Unique(parameters.Select(p => p.Key), "params");
        long combinations = parameters.Aggregate(1L, (n, p) => n * p.Values.Count);
        if (combinations > MaxCombinations) throw new ArgumentException($"комбинаций {combinations} — не больше {MaxCombinations}");
        if (ticks <= (ulong)warmup) throw new ArgumentException($"ticks {ticks} не больше прогрева {warmup}");

        var rows = new List<SweepRow>();
        foreach (var combo in Combinations(parameters))
        {
            var sets = combo.Select(p => $"{p.Key}={p.Value}").ToList();
            var measures = new List<SimulationMeasure>();
            var hashes = new HashSet<string>();
            int entities = 0;
            long violations = 0;
            string? error = null;
            for (int r = 0; r < repeats && error is null; r++)
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    var sim = build(scenario?.CreateConfig(sets) ?? ConfigSet.Empty([.. sets]));
                    // Без сверки хешей, как бюджеты: иначе мерится хеш каждого тика, и только у комбинаций с конфигом
                    // реплея (с другим конфигом сверка обрывается на первом расхождении) — ложное «резкое ухудшение».
                    if (scenario is not null) sim.Play(scenario.Head(scenario.Ticks, hashes: false));
                    measures.Add(SimulationMeasure.Run(sim, ticks, warmup, cancel));
                    hashes.Add(sim.HashState().Total.ToString("X16", CultureInfo.InvariantCulture));
                    entities = sim.World.EntityCount;
                    violations = Math.Max(violations, sim.Invariants.Total);
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or InvalidDataException)
                {
                    error = e.Message;
                }
            }
            if (measures.Count == 0)
            {
                rows.Add(new SweepRow(sets, 0, 0, 0, 0, new Dictionary<string, double>(), 0, 0, null, false, 0, error));
                continue;
            }
            var ms = measures.Select(m => m.MsPerTick).Order().ToList();
            var stages = measures[0].Stages.Keys.ToDictionary(s => s, s => FeatureVerifier.Median([.. measures.Select(m => m.Stages[s]).Order()]));
            rows.Add(new SweepRow(sets, measures.Count, FeatureVerifier.Median(ms), ms[0], ms[^1], stages, measures.Max(m => m.AllocatedBytesPerTick),
                entities, hashes.Count == 1 ? hashes.First() : string.Join(" ≠ ", hashes), hashes.Count == 1, violations, error));
        }
        return (rows, Scaling(parameters, rows));
    }

    private static IEnumerable<List<(string Key, string Value)>> Combinations(IReadOnlyList<(string Key, IReadOnlyList<string> Values)> parameters)
    {
        IEnumerable<List<(string, string)>> acc = [[]];
        foreach (var (key, values) in parameters)
            acc = acc.SelectMany(prefix => values.Select(v => new List<(string, string)>(prefix) { (key, v) }));
        return acc;
    }

    /// <summary>
    /// Первое резкое ухудшение масштабирования: по каждому числовому ключу при прочих равных — первый шаг, где время тика
    /// выросло больше чем в 1,5 раза сильнее, чем значение ключа.
    /// </summary>
    private static List<string> Scaling(IReadOnlyList<(string Key, IReadOnlyList<string> Values)> parameters, List<SweepRow> rows)
    {
        var notes = new List<string>();
        for (int k = 0; k < parameters.Count; k++)
        {
            var (key, values) = parameters[k];
            if (values.Count < 2 || !values.All(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d > 0))
                continue;
            string? found = null;
            foreach (var group in rows.Where(r => r.Error is null).GroupBy(r => string.Join(", ", r.Sets.Where((_, i) => i != k))))
            {
                var line = group.Select(r => (Value: double.Parse(r.Sets[k][(key.Length + 1)..], CultureInfo.InvariantCulture), r.MsMedian))
                    .OrderBy(x => x.Value).ToList();
                for (int i = 1; i < line.Count && found is null; i++)
                {
                    double grow = line[i].Value / line[i - 1].Value, cost = line[i - 1].MsMedian > 0 ? line[i].MsMedian / line[i - 1].MsMedian : 0;
                    if (grow > 1 && cost > 1.5 * grow)
                        found = $"{key}: {line[i - 1].Value.ToString(CultureInfo.InvariantCulture)} → {line[i].Value.ToString(CultureInfo.InvariantCulture)} — " +
                            $"время тика ×{cost:0.##} при росте ×{grow:0.##}" + (group.Key.Length > 0 ? $" ({group.Key})" : "");
                }
            }
            notes.Add(found ?? $"{key}: резкого ухудшения нет (время растёт не быстрее значения ×1,5)");
        }
        return notes;
    }
}
