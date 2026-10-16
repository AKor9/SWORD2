using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sword.Engine.Runtime;

/// <summary>О записи от exe: сборка (версия с коммитом), кто записал (имя exe), отпечаток контента (<c>none</c> — без пака).</summary>
public sealed record ReplayInfo(string Build, string RecordedBy, string Content)
{
    /// <summary>Сборка и имя запущенного exe (версия сборки — с коммитом, её ставит MSBuild из git).</summary>
    public static ReplayInfo Current(string content)
    {
        var entry = System.Reflection.Assembly.GetEntryAssembly();
        string build = entry?.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "";
        return new ReplayInfo(build, entry?.GetName().Name ?? "", content);
    }
}

/// <summary>Тип команды в реплее: стабильное имя и листья полей <c>путь:вид</c> (<see cref="FieldLayout.KindName"/>).</summary>
public sealed record ReplayCommandType(string Name, IReadOnlyList<string> Fields);

/// <summary>Команда в реплее: номер типа в <see cref="Replay.CommandTypes"/> и листья — 64 битами (<see cref="FieldLayout.Bits"/>).</summary>
public sealed record ReplayCommand(int Type, ulong[] Leaves);

/// <summary>Тики <see cref="First"/> … <see cref="First"/> + <see cref="Count"/> − 1 с одним списком команд (по порядку
/// выдачи). Тик без команд не записан.</summary>
public sealed record ReplayRun(ulong First, int Count, IReadOnlyList<ReplayCommand> Commands)
{
    public ulong End => First + (ulong)Count;
}

/// <summary>Хеш состояния после <see cref="Tick"/> тиков — по участникам в порядке <see cref="Replay.Parts"/>.</summary>
public sealed record ReplayHash(ulong Tick, ulong[] Parts);

/// <summary>
/// Реплей: всё, чтобы симуляция повторилась бит в бит (сборка, контент, частота тиков, зерно, конфиг, модули, команды по
/// тикам), и хеш по участникам каждые <see cref="HashEvery"/> тиков — для поиска первого расхождения. Файл — JSON, CRLF;
/// float — точной записью, <c>-0</c> и не-числа — битами <c>"0x…"</c>. Без хешей и заголовка — сценарий.
/// </summary>
public sealed class Replay
{
    public const string Format = "sword-replay", Extension = ".swreplay";
    public const int Version = 1;

    /// <summary>Версия сборки с коммитом (<c>1.0.0+efd0b74…</c>); пусто — неизвестна.</summary>
    public string Build { get; init; } = "";

    /// <summary>Какой exe записал (<c>Sword</c>, <c>Sword.Headless</c>).</summary>
    public string RecordedBy { get; init; } = "";

    /// <summary>Отпечаток пака; <c>none</c> — без пака или неизвестно (не сверяется).</summary>
    public string Content { get; init; } = "none";

    public DateTime? RecordedUtc { get; init; }

    /// <summary>Тиков в секунду при записи; 0 — не задано (сценарий).</summary>
    public int TickRate { get; init; }

    /// <summary>Главное зерно ГСЧ при записи; null — не задано.</summary>
    public ulong? Seed { get; init; }

    /// <summary>Длина реплея в тиках (сыграно тиков к концу записи).</summary>
    public ulong Ticks { get; init; }

    /// <summary>Хеш каждые столько тиков (и на тике 0); 0 — хешей нет.</summary>
    public int HashEvery { get; init; }

    /// <summary>Активные и выключенные модули при записи; null — не задано.</summary>
    public IReadOnlyList<string>? Modules { get; init; }
    public IReadOnlyList<string>? InactiveModules { get; init; }

    /// <summary>Участники хеша по Id — порядок значений в <see cref="ReplayHash.Parts"/>.</summary>
    public IReadOnlyList<string> Parts { get; init; } = [];

    /// <summary>Сведения ядра и модулей при записи (<see cref="Simulation.Facts"/>); null — их не было. Воспроизведение
    /// сверяет их с симуляцией.</summary>
    public IReadOnlyDictionary<string, string>? Facts { get; init; }

    public IReadOnlyList<ReplayCommandType> CommandTypes { get; init; } = [];

    /// <summary>Итоговый конфиг запуска после всех слоёв (<see cref="ConfigSet.Effective"/>).</summary>
    public JsonObject Config { get; init; } = [];

    /// <summary>Отрезки тиков с командами — по возрастанию, без перекрытий.</summary>
    public IReadOnlyList<ReplayRun> Runs { get; init; } = [];

    /// <summary>Хеши — по возрастанию тика.</summary>
    public IReadOnlyList<ReplayHash> Hashes { get; init; } = [];

    /// <summary>Команд всего (по тикам, с повторами отрезков).</summary>
    public long CommandCount => Runs.Sum(r => (long)r.Count * r.Commands.Count);

    /// <summary>Конфиг запуска из реплея: только записанные значения (ни файлов, ни переменных); <paramref name="sets"/> — поверх (эксперимент).</summary>
    public ConfigSet CreateConfig(IEnumerable<string>? sets = null) => ConfigSet.FromReplay(Config, sets ?? []);

    /// <summary>Коммит сборки — после <c>+</c> в версии; пусто — неизвестен.</summary>
    public static string CommitOf(string build) => build.IndexOf('+') is var plus and >= 0 ? build[(plus + 1)..] : "";

    /// <summary>Команды тика <paramref name="tick"/> (пусто — тик без команд).</summary>
    public IReadOnlyList<ReplayCommand> CommandsAt(ulong tick)
    {
        int lo = 0, hi = Runs.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            var r = Runs[mid];
            if (tick < r.First) hi = mid - 1;
            else if (tick >= r.End) lo = mid + 1;
            else return r.Commands;
        }
        return [];
    }

    /// <summary>
    /// Таймлайн: <paramref name="head"/> до тика <paramref name="at"/> и <paramref name="tail"/> с него (перемотка назад при
    /// воспроизведении). Заголовок и конфиг — от <paramref name="head"/>; команды <paramref name="tail"/> — по именам и путям
    /// (нет поля — ноль), его хеши — если участники те же.
    /// </summary>
    public static Replay Splice(Replay head, Replay tail, ulong at)
    {
        var types = head.CommandTypes.ToList();
        var map = new (int Type, int[] Fields)[tail.CommandTypes.Count];
        for (int t = 0; t < tail.CommandTypes.Count; t++)
        {
            var tt = tail.CommandTypes[t];
            int type = types.FindIndex(x => x.Name == tt.Name);
            if (type < 0)
            {
                types.Add(tt);
                map[t] = (types.Count - 1, [.. Enumerable.Range(0, tt.Fields.Count)]);
                continue;
            }
            var tailPaths = tt.Fields.Select(f => f[..f.LastIndexOf(':')]).ToList();
            map[t] = (type, [.. types[type].Fields.Select(f => tailPaths.IndexOf(f[..f.LastIndexOf(':')]))]);
        }
        ReplayCommand Remap(ReplayCommand c)
        {
            var (type, fields) = map[c.Type];
            return new ReplayCommand(type, [.. fields.Select(f => f < 0 ? 0UL : c.Leaves[f])]);
        }

        var runs = new List<ReplayRun>();
        foreach (var r in head.Runs)
        {
            if (r.First >= at) break;
            runs.Add(r.End <= at ? r : r with { Count = (int)(at - r.First) });
        }
        foreach (var r in tail.Runs)
        {
            if (r.End <= at) continue;
            ulong first = Math.Max(r.First, at);
            runs.Add(new ReplayRun(first, (int)(r.End - first), [.. r.Commands.Select(Remap)]));
        }
        var hashes = head.Hashes.Where(h => h.Tick <= at).ToList();
        if (tail.Parts.SequenceEqual(head.Parts)) hashes.AddRange(tail.Hashes.Where(h => h.Tick > at));
        return new Replay
        {
            Build = head.Build, RecordedBy = head.RecordedBy, Content = head.Content, RecordedUtc = head.RecordedUtc,
            TickRate = head.TickRate, Seed = head.Seed, Ticks = Math.Max(at, tail.Ticks), HashEvery = head.HashEvery,
            Modules = head.Modules, InactiveModules = head.InactiveModules, Parts = head.Parts, Facts = head.Facts, CommandTypes = types,
            Config = head.Config, Runs = runs, Hashes = hashes,
        };
    }

    /// <summary>
    /// Первые <paramref name="at"/> тиков: команды и хеши до <paramref name="at"/>, заголовок и конфиг те же (ветка
    /// эксперимента). <paramref name="hashes"/> false — без хешей: для замеров времени, чтобы не мерить хеш.
    /// </summary>
    public Replay Head(ulong at, bool hashes = true)
    {
        var runs = new List<ReplayRun>();
        foreach (var r in Runs)
        {
            if (r.First >= at) break;
            runs.Add(r.End <= at ? r : r with { Count = (int)(at - r.First) });
        }
        return new Replay
        {
            Build = Build, RecordedBy = RecordedBy, Content = Content, RecordedUtc = RecordedUtc, TickRate = TickRate, Seed = Seed,
            Ticks = Math.Min(Ticks, at), Modules = Modules, InactiveModules = InactiveModules, Parts = Parts, Facts = Facts,
            CommandTypes = CommandTypes, Config = (JsonObject)Config.DeepClone(), Runs = runs,
            Hashes = hashes ? [.. Hashes.Where(h => h.Tick <= at)] : [], HashEvery = hashes ? HashEvery : 0,
        };
    }

    // ── файл ────────────────────────────────────────────────────────────────

    public void Save(string path)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is not null) Directory.CreateDirectory(dir);
        File.WriteAllText(path, ToJson(), new UTF8Encoding(false));
    }

    public static Replay Load(string path)
    {
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or FormatException or OverflowException or InvalidOperationException or KeyNotFoundException)
        {
            throw new InvalidDataException($"{path}: {e.Message}", e);
        }
    }

    public string ToJson()
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = true, NewLine = "\r\n", Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            w.WriteStartObject();
            w.WriteString("format", Format);
            w.WriteNumber("version", Version);
            w.WriteString("build", Build);
            w.WriteString("recordedBy", RecordedBy);
            w.WriteString("content", Content);
            if (RecordedUtc is { } utc) w.WriteString("recordedUtc", utc.ToString("O", CultureInfo.InvariantCulture));
            w.WriteNumber("tickRate", TickRate);
            if (Seed is { } seed) w.WriteNumber("seed", seed);
            w.WriteNumber("ticks", Ticks);
            w.WriteNumber("hashEvery", HashEvery);
            if (Modules is not null) WriteStrings(w, "modules", Modules);
            if (InactiveModules is not null) WriteStrings(w, "inactiveModules", InactiveModules);
            WriteStrings(w, "parts", Parts);
            if (Facts is { Count: > 0 })
            {
                w.WriteStartObject("facts");
                foreach (var (name, value) in Facts.OrderBy(f => f.Key, StringComparer.Ordinal)) w.WriteString(name, value);
                w.WriteEndObject();
            }
            w.WriteStartArray("commandTypes");
            foreach (var t in CommandTypes)
            {
                w.WriteStartObject();
                w.WriteString("name", t.Name);
                w.WritePropertyName("fields");
                w.WriteRawValue("[" + string.Join(", ", t.Fields.Select(f => JsonSerializer.Serialize(f))) + "]");
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WritePropertyName("config");
            Config.WriteTo(w);
            w.WriteEndObject();
        }
        // Команды и хеши — по строке на отрезок и на хеш (файл читается и сравнивается построчно): дописываются к
        // заголовку вручную — писатель JSON не ставит перевод строки перед сырым значением.
        var text = new StringBuilder(Encoding.UTF8.GetString(buffer.WrittenSpan).TrimEnd()[..^1].TrimEnd());
        var kinds = CommandTypes.Select(t => t.Fields.Select(KindOfField).ToArray()).ToArray();
        text.Append(",\r\n  \"commands\": [");
        for (int r = 0; r < Runs.Count; r++)
        {
            var run = Runs[r];
            text.Append(r == 0 ? "\r\n    [" : ",\r\n    [").Append(run.First).Append(", ").Append(run.Count);
            foreach (var c in run.Commands)
            {
                text.Append(", [").Append(c.Type);
                for (int i = 0; i < c.Leaves.Length; i++) text.Append(", ").Append(EncodeValue(kinds[c.Type][i], c.Leaves[i]));
                text.Append(']');
            }
            text.Append(']');
        }
        text.Append(Runs.Count > 0 ? "\r\n  ],\r\n  \"hashes\": [" : "],\r\n  \"hashes\": [");
        for (int i = 0; i < Hashes.Count; i++)
            text.Append(i == 0 ? "\r\n    \"" : ",\r\n    \"").Append(Hashes[i].Tick).Append(' ')
                .AppendJoin(' ', Hashes[i].Parts.Select(p => p.ToString("X16", CultureInfo.InvariantCulture))).Append('"');
        return text.Append(Hashes.Count > 0 ? "\r\n  ]\r\n}\r\n" : "]\r\n}\r\n").ToString();
    }

    private static void WriteStrings(Utf8JsonWriter w, string name, IEnumerable<string> values)
    {
        w.WritePropertyName(name);
        w.WriteRawValue("[" + string.Join(", ", values.Select(v => JsonSerializer.Serialize(v))) + "]");
    }

    public static Replay Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("реплей — объект JSON");
        if (!root.TryGetProperty("format", out var format) || format.GetString() != Format)
            throw new InvalidDataException($"не реплей: нет \"format\": \"{Format}\"");
        if (root.TryGetProperty("version", out var version) && version.GetInt32() > Version)
            throw new InvalidDataException($"реплей версии {version.GetInt32()}, эта сборка читает до {Version}");

        string Str(string name, string fallback) => root.TryGetProperty(name, out var e) ? e.GetString() ?? fallback : fallback;
        IReadOnlyList<string>? Strings(string name) =>
            root.TryGetProperty(name, out var e) ? [.. e.EnumerateArray().Select(x => x.GetString() ?? "")] : null;

        var types = new List<ReplayCommandType>();
        if (root.TryGetProperty("commandTypes", out var ct))
            foreach (var t in ct.EnumerateArray())
            {
                var fields = t.GetProperty("fields").EnumerateArray().Select(f => f.GetString() ?? "").ToList();
                foreach (var f in fields) KindOfField(f);   // вид листа — известный
                types.Add(new ReplayCommandType(t.GetProperty("name").GetString() ?? "", fields));
            }
        var kinds = types.Select(t => t.Fields.Select(KindOfField).ToArray()).ToArray();

        var runs = new List<ReplayRun>();
        if (root.TryGetProperty("commands", out var commands))
            foreach (var r in commands.EnumerateArray())
            {
                int n = r.GetArrayLength();
                if (n < 2) throw new InvalidDataException($"команды: {r.GetRawText()} — нужно [первый тик, тиков, [тип, поля…]…]");
                ulong first = r[0].GetUInt64();
                int count = r[1].GetInt32();
                if (count < 1) throw new InvalidDataException($"команды: отрезок с тика {first} — тиков {count}, нужно ≥ 1");
                if (runs.Count > 0 && first < runs[^1].End)
                    throw new InvalidDataException($"команды: отрезок с тика {first} налезает на предыдущий (до {runs[^1].End}) — отрезки по возрастанию");
                var list = new List<ReplayCommand>(n - 2);
                for (int i = 2; i < n; i++)
                {
                    var c = r[i];
                    int type = c[0].GetInt32();
                    if ((uint)type >= (uint)types.Count) throw new InvalidDataException($"команды, тик {first}: типа {type} нет (типов {types.Count})");
                    if (c.GetArrayLength() != kinds[type].Length + 1)
                        throw new InvalidDataException($"команды, тик {first}: у {types[type].Name} полей {kinds[type].Length}, а значений {c.GetArrayLength() - 1}");
                    var leaves = new ulong[kinds[type].Length];
                    for (int k = 0; k < leaves.Length; k++) leaves[k] = DecodeValue(kinds[type][k], c[k + 1], types[type].Fields[k]);
                    list.Add(new ReplayCommand(type, leaves));
                }
                runs.Add(new ReplayRun(first, count, list));
            }

        var parts = Strings("parts") ?? [];
        var hashes = new List<ReplayHash>();
        if (root.TryGetProperty("hashes", out var hs))
            foreach (var h in hs.EnumerateArray())
            {
                var p = (h.GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length != parts.Count + 1) throw new InvalidDataException($"хеш «{h.GetString()}»: нужно тик и {parts.Count} значений ({string.Join(", ", parts)})");
                var tick = ulong.Parse(p[0], NumberStyles.None, CultureInfo.InvariantCulture);
                if (hashes.Count > 0 && tick <= hashes[^1].Tick) throw new InvalidDataException($"хеш тика {tick}: хеши — по возрастанию тика");
                hashes.Add(new ReplayHash(tick, [.. p.Skip(1).Select(x => ulong.Parse(x, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture))]));
            }

        ulong ticks = root.TryGetProperty("ticks", out var tk) ? tk.GetUInt64() : Math.Max(runs.Count > 0 ? runs[^1].End : 0, hashes.Count > 0 ? hashes[^1].Tick : 0);
        if (runs.Count > 0 && runs[^1].End > ticks) throw new InvalidDataException($"команды до тика {runs[^1].End}, а реплей длиной {ticks} тиков");
        return new Replay
        {
            Build = Str("build", ""),
            RecordedBy = Str("recordedBy", ""),
            Content = Str("content", "none"),
            RecordedUtc = root.TryGetProperty("recordedUtc", out var utc) ? DateTime.Parse(utc.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : null,
            TickRate = root.TryGetProperty("tickRate", out var rate) ? rate.GetInt32() : 0,
            Seed = root.TryGetProperty("seed", out var seed) ? seed.GetUInt64() : null,
            Ticks = ticks,
            HashEvery = root.TryGetProperty("hashEvery", out var every) ? every.GetInt32() : 0,
            Modules = Strings("modules"),
            InactiveModules = Strings("inactiveModules"),
            Parts = parts,
            Facts = root.TryGetProperty("facts", out var facts)
                ? facts.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? throw new InvalidDataException($"facts.{p.Name} — строка"), StringComparer.Ordinal)
                : null,
            CommandTypes = types,
            Config = root.TryGetProperty("config", out var config) ? JsonNode.Parse(config.GetRawText()) as JsonObject
                ?? throw new InvalidDataException("config — объект секций") : [],
            Runs = runs,
            Hashes = hashes,
        };
    }

    /// <summary>Вид листа из поля <c>путь:вид</c>.</summary>
    internal static FieldLayout.Kind KindOfField(string field)
    {
        int colon = field.LastIndexOf(':');
        return colon > 0 && FieldLayout.KindOf(field[(colon + 1)..]) is { } kind ? kind
            : throw new InvalidDataException($"поле «{field}»: нужно путь:вид (bool, i8…u64, f32, f64, char, entity)");
    }

    /// <summary>Лист значением JSON: числа — числами (float — точной записью), <c>-0</c> и не-числа — битами строкой.</summary>
    internal static string EncodeValue(FieldLayout.Kind kind, ulong bits)
    {
        var inv = CultureInfo.InvariantCulture;
        switch (kind)
        {
            case FieldLayout.Kind.Bool: return bits != 0 ? "true" : "false";
            case FieldLayout.Kind.I8 or FieldLayout.Kind.I16 or FieldLayout.Kind.I32 or FieldLayout.Kind.I64:
                return unchecked((long)bits).ToString(inv);
            case FieldLayout.Kind.F32:
            {
                float f = BitConverter.UInt32BitsToSingle((uint)bits);
                return float.IsFinite(f) && !(f == 0 && float.IsNegative(f)) ? f.ToString("R", inv) : $"\"0x{(uint)bits:X8}\"";
            }
            case FieldLayout.Kind.F64:
            {
                double d = BitConverter.UInt64BitsToDouble(bits);
                return double.IsFinite(d) && !(d == 0 && double.IsNegative(d)) ? d.ToString("R", inv) : $"\"0x{bits:X16}\"";
            }
            case FieldLayout.Kind.Entity: return $"\"{unchecked((int)(uint)bits)}v{unchecked((int)(bits >> 32))}\"";
            default: return bits.ToString(inv);
        }
    }

    internal static ulong DecodeValue(FieldLayout.Kind kind, JsonElement e, string field)
    {
        var inv = CultureInfo.InvariantCulture;
        try
        {
            switch (kind)
            {
                case FieldLayout.Kind.Bool: return e.GetBoolean() ? 1UL : 0UL;
                case FieldLayout.Kind.F32:
                    if (e.ValueKind == JsonValueKind.String) return uint.Parse(Hex(e.GetString()!), NumberStyles.AllowHexSpecifier, inv);
                    return BitConverter.SingleToUInt32Bits(float.Parse(e.GetRawText(), NumberStyles.Float, inv));
                case FieldLayout.Kind.F64:
                    if (e.ValueKind == JsonValueKind.String) return ulong.Parse(Hex(e.GetString()!), NumberStyles.AllowHexSpecifier, inv);
                    return BitConverter.DoubleToUInt64Bits(double.Parse(e.GetRawText(), NumberStyles.Float, inv));
                case FieldLayout.Kind.Entity:
                {
                    string s = e.GetString()!;
                    int v = s.IndexOf('v');
                    int index = int.Parse(s.AsSpan(0, v), NumberStyles.AllowLeadingSign, inv);
                    int generation = int.Parse(s.AsSpan(v + 1), NumberStyles.AllowLeadingSign, inv);
                    return (uint)index | (ulong)(uint)generation << 32;
                }
                case FieldLayout.Kind.I8: return unchecked((ulong)(long)checked((sbyte)e.GetInt64()));
                case FieldLayout.Kind.I16: return unchecked((ulong)(long)checked((short)e.GetInt64()));
                case FieldLayout.Kind.I32: return unchecked((ulong)(long)e.GetInt32());
                case FieldLayout.Kind.I64: return unchecked((ulong)e.GetInt64());
                case FieldLayout.Kind.U8: return e.GetByte();
                case FieldLayout.Kind.U16 or FieldLayout.Kind.Char: return e.GetUInt16();
                case FieldLayout.Kind.U32: return e.GetUInt32();
                default: return e.GetUInt64();
            }
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidOperationException or ArgumentException)
        {
            throw new InvalidDataException($"поле {field}: значение {e.GetRawText()} не подходит к виду {FieldLayout.KindName(kind)}", ex);
        }

        static string Hex(string s) => s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..]
            : throw new FormatException("строкой — только биты «0x…»");
    }
}

/// <summary>Расхождение участника: что записано и что получилось.</summary>
public sealed record ReplayPartDiff(string Part, ulong Expected, ulong Actual);

/// <summary>
/// Первое расхождение: после <see cref="Tick"/> тиков хеши участников <see cref="Parts"/> не те; расхождение возникло
/// в тиках <see cref="LastMatch"/> … <see cref="Tick"/> − 1. <see cref="LastMatch"/> null — совпадений не было.
/// </summary>
public sealed record ReplayDivergence(ulong Tick, ulong? LastMatch, IReadOnlyList<ReplayPartDiff> Parts)
{
    /// <summary>Вероятная причина: сведения реплея (<see cref="Replay.Facts"/>) не те, что у симуляции; null — совпали
    /// или их нет.</summary>
    public string? Cause { get; init; }

    public override string ToString() =>
        $"расхождение после {Tick} тиков (последнее совпадение — {(LastMatch is { } m ? $"после {m}" : "нет")}): " +
        string.Join(", ", Parts.Select(p => $"{p.Part} {p.Expected:X16} → {p.Actual:X16}")) + (Cause is null ? "" : $" — вероятная причина: {Cause}");
}

/// <summary>
/// Запись реплея: каждая выданная команда тика листьями полей, одинаковые тики подряд — одним отрезком; хеш по
/// участникам на тике 0 и каждые <see cref="HashEvery"/> тиков. Однообразный ввод памяти не выделяет.
/// </summary>
public sealed class ReplayRecorder : ICommandSink
{
    private readonly Simulation _sim;
    private readonly ReplayInfo _info;
    private readonly Type[] _types;
    private readonly FieldLayout[] _layouts;
    private readonly List<int> _tickTypes = [];
    private readonly List<ulong> _tickLeaves = [];
    private readonly List<(ulong First, int Count, ReplayCommand[] Commands)> _runs = [];
    // Хеши — куски постоянного размера (запись: тик, участники…): рост не копирует записанное и не идёт в кучу больших
    // объектов; выделение — только сами записи (8 байт на тик и участника), кусок (до 64 КБ) — раз в _perChunk хешей.
    private const int ChunkValues = 8192;   // 64 КБ — меньше порога кучи больших объектов (85 000 байт)
    private readonly List<ulong[]> _hashChunks = [];
    private readonly int _hashStride, _perChunk;
    private int _hashCount;
    private readonly DateTime _started = DateTime.UtcNow;

    internal ReplayRecorder(Simulation sim, ReplayInfo info, int hashEvery)
    {
        _sim = sim;
        _info = info;
        HashEvery = hashEvery;
        _types = [.. sim.CommandTypes];
        _layouts = [.. _types.Select(FieldLayout.Of)];
        _hashStride = 1 + sim.StateParts.Count;
        _perChunk = Math.Max(1, ChunkValues / _hashStride);
    }

    public int HashEvery { get; }

    /// <summary>Записано тиков.</summary>
    public ulong Ticks => _sim.Tick;

    void ICommandSink.Command(ulong tick, int type, ReadOnlySpan<byte> command)
    {
        _tickTypes.Add(type);
        var layout = _layouts[type];
        for (int i = 0; i < layout.Leaves.Count; i++) _tickLeaves.Add(layout.Bits(command, i));
    }

    /// <summary>Тик <paramref name="tick"/> сыгран: его команды — продолжение последнего отрезка или новый отрезок.</summary>
    internal void EndTick(ulong tick)
    {
        if (_tickTypes.Count == 0) return;
        if (_runs.Count > 0 && _runs[^1] is var last && last.First + (ulong)last.Count == tick && SameAsTick(last.Commands))
            _runs[^1] = (last.First, last.Count + 1, last.Commands);
        else
        {
            var commands = new ReplayCommand[_tickTypes.Count];
            for (int c = 0, at = 0; c < commands.Length; c++)
            {
                int n = _layouts[_tickTypes[c]].Leaves.Count;
                commands[c] = new ReplayCommand(_tickTypes[c], [.. System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_tickLeaves).Slice(at, n)]);
                at += n;
            }
            _runs.Add((tick, 1, commands));
        }
        _tickTypes.Clear();
        _tickLeaves.Clear();
    }

    private bool SameAsTick(ReplayCommand[] commands)
    {
        if (commands.Length != _tickTypes.Count) return false;
        for (int c = 0, at = 0; c < commands.Length; c++)
        {
            if (commands[c].Type != _tickTypes[c]) return false;
            foreach (ulong leaf in commands[c].Leaves)
                if (_tickLeaves[at++] != leaf) return false;
        }
        return true;
    }

    internal bool HashDue(ulong played) => played % (ulong)HashEvery == 0;

    internal void AddHash(ulong played, ReadOnlySpan<ulong> parts)
    {
        int slot = _hashCount % _perChunk;
        if (slot == 0) _hashChunks.Add(new ulong[_perChunk * _hashStride]);
        var record = _hashChunks[^1].AsSpan(slot * _hashStride, _hashStride);
        record[0] = played;
        parts.CopyTo(record[1..]);
        _hashCount++;
    }

    private ReplayHash HashAt(int i)
    {
        var record = _hashChunks[i / _perChunk].AsSpan(i % _perChunk * _hashStride, _hashStride);
        return new ReplayHash(record[0], record[1..].ToArray());
    }

    /// <summary>Реплей записанного на сейчас (копия; запись продолжается).</summary>
    public Replay ToReplay()
    {
        return new Replay
        {
            Build = _info.Build,
            RecordedBy = _info.RecordedBy,
            Content = _info.Content,
            RecordedUtc = _started,
            TickRate = _sim.Settings.TickRate,
            Seed = _sim.Settings.Seed,
            Ticks = _sim.Tick,
            HashEvery = HashEvery,
            Modules = _sim.ActiveModules,
            InactiveModules = _sim.InactiveModules,
            Parts = _sim.StateParts,
            Facts = _sim.Facts.Count > 0 ? new Dictionary<string, string>(_sim.Facts, StringComparer.Ordinal) : null,
            CommandTypes = [.. _types.Select((t, i) => new ReplayCommandType(t.FullName ?? t.Name,
                [.. _layouts[i].Leaves.Select(l => $"{l.Path}:{FieldLayout.KindName(l.Kind)}")]))],
            Config = _sim.Config.Effective,
            Runs = [.. _runs.Select(r => new ReplayRun(r.First, r.Count, r.Commands))],
            Hashes = [.. Enumerable.Range(0, _hashCount).Select(HashAt)],
        };
    }
}

/// <summary>
/// Воспроизведение реплея: его команды — первыми в тике (команды клиента — после: ветвление), хеши сверяются по
/// участникам (<see cref="Divergence"/>). Привязка по именам типов и путям полей: лишнее поле реплея — ошибка,
/// недостающее — ноль и предупреждение.
/// </summary>
public sealed class ReplayPlayer : IReplaySource
{
    private readonly (int SimType, int[] Leaves, FieldLayout Layout, byte[] Scratch)[] _bind;
    private readonly int[] _parts;   // участник реплея → участник симуляции (-1 — нет)
    private readonly ulong[] _expected;
    private int _run, _hash;
    private readonly List<string> _warnings = [];

    internal ReplayPlayer(Simulation sim, Replay replay)
    {
        Replay = replay;
        var types = sim.CommandTypes;
        _bind = new (int, int[], FieldLayout, byte[])[replay.CommandTypes.Count];
        for (int t = 0; t < replay.CommandTypes.Count; t++)
        {
            var rt = replay.CommandTypes[t];
            int simType = -1;
            for (int i = 0; i < types.Count; i++)
                if (types[i].FullName == rt.Name) simType = i;
            if (simType < 0)
                throw new InvalidDataException($"в реплее команда {rt.Name}, а симуляция её не принимает (модуль выключен или его нет в сборке); " +
                    $"команды симуляции: {string.Join(", ", types.Select(x => x.FullName))}");
            var layout = FieldLayout.Of(types[simType]);
            var leaves = new int[rt.Fields.Count];
            for (int f = 0; f < rt.Fields.Count; f++)
            {
                string field = rt.Fields[f];
                int colon = field.LastIndexOf(':');
                string path = field[..colon];
                var kind = Replay.KindOfField(field);
                int leaf = -1;
                for (int l = 0; l < layout.Leaves.Count; l++)
                    if (layout.Leaves[l].Path == path) leaf = l;
                if (leaf < 0) throw new InvalidDataException($"в реплее у {rt.Name} поле {path}, а в команде его нет");
                if (layout.Leaves[leaf].Kind != kind)
                    throw new InvalidDataException($"{rt.Name}.{path}: в реплее {FieldLayout.KindName(kind)}, в команде {FieldLayout.KindName(layout.Leaves[leaf].Kind)}");
                leaves[f] = leaf;
            }
            foreach (var l in layout.Leaves.Where(l => !rt.Fields.Any(f => f[..f.LastIndexOf(':')] == l.Path)))
                _warnings.Add($"{rt.Name}.{l.Path}: в реплее поля нет — ноль");
            _bind[t] = (simType, leaves, layout, new byte[layout.Size]);
        }

        _parts = [.. replay.Parts.Select(p => sim.StateParts.ToList().IndexOf(p))];
        foreach (var (p, i) in replay.Parts.Select((p, i) => (p, i)).Where(x => _parts[x.i] < 0))
            _warnings.Add($"участника хеша {p} в симуляции нет — не сверяется");
        foreach (var p in sim.StateParts.Where(p => !replay.Parts.Contains(p)))
            if (replay.Hashes.Count > 0) _warnings.Add($"участника хеша {p} в реплее нет — не сверяется");
        _expected = new ulong[replay.Parts.Count];

        if (replay.TickRate != 0 && replay.TickRate != sim.Settings.TickRate)
            _warnings.Add($"частота тиков: в реплее {replay.TickRate}, в симуляции {sim.Settings.TickRate}");
        if (replay.Seed is { } seed && seed != sim.Settings.Seed)
            _warnings.Add($"зерно ГСЧ: в реплее {seed}, в симуляции {sim.Settings.Seed}");
        if (replay.Modules is { } modules && !modules.SequenceEqual(sim.ActiveModules))
            _warnings.Add($"модули: в реплее {string.Join(", ", modules)}, в симуляции {string.Join(", ", sim.ActiveModules)}");
        // Сведения реплея: другое значение — воспроизведение, скорее всего, разойдётся; причина — и в тексте расхождения.
        var causes = new List<string>();
        foreach (var (name, recorded) in replay.Facts ?? new Dictionary<string, string>())
        {
            if (!sim.Facts.TryGetValue(name, out var actual)) causes.Add($"сведения {name} в симуляции нет (в реплее {recorded})");
            else if (actual != recorded) causes.Add($"{name}: в реплее {recorded}, в симуляции {actual}");
        }
        foreach (var name in sim.Facts.Keys.Where(k => replay.Facts?.ContainsKey(k) != true))
            if (replay.Hashes.Count > 0) causes.Add($"сведения {name} в реплее нет (в симуляции {sim.Facts[name]})");
        foreach (var c in causes) _warnings.Add($"сведения реплея: {c}");
        _factCause = causes.Count > 0 ? $"сведения реплея другие — {string.Join("; ", causes)}" : null;
    }

    /// <summary>Расхождение сведений реплея и симуляции — причина в тексте расхождения; совпали — null.</summary>
    private readonly string? _factCause;

    public Replay Replay { get; }

    /// <summary>Длина реплея в тиках: дальше команд реплея нет.</summary>
    public ulong Length => Replay.Ticks;

    /// <summary>Расхождения привязки и заголовка, не мешающие воспроизведению (поле без значения, участник без хеша…).</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Сверено хешей.</summary>
    public int HashesChecked { get; private set; }

    /// <summary>Последний совпавший хеш — после стольких тиков; null — ещё ни одного.</summary>
    public ulong? LastMatch { get; private set; }

    /// <summary>Первое расхождение; null — пока совпадает. После него хеши не сверяются.</summary>
    public ReplayDivergence? Divergence { get; private set; }

    /// <summary>Хешей реплея впереди (ещё не сверены).</summary>
    public int HashesLeft => Replay.Hashes.Count - _hash;

    void IReplaySource.EmitTick(ulong tick, TickCommands commands, Ecs.CommandBuffer buffer)
    {
        var runs = Replay.Runs;
        while (_run < runs.Count && runs[_run].End <= tick) _run++;
        if (_run >= runs.Count || runs[_run].First > tick) return;
        // По индексу: перечислитель списка через интерфейс упаковывался бы — 32 байта на тик воспроизведения.
        var list = runs[_run].Commands;
        for (int k = 0; k < list.Count; k++)
        {
            var c = list[k];
            var (simType, leaves, layout, scratch) = _bind[c.Type];
            Array.Clear(scratch);
            for (int i = 0; i < leaves.Length; i++) layout.SetBits(scratch, leaves[i], c.Leaves[i]);
            commands.EmitRaw(tick, simType, scratch, buffer);
        }
    }

    /// <summary>Есть ли хеш реплея после <paramref name="played"/> тиков (прошедшие пропускаются).</summary>
    internal bool HashDue(ulong played)
    {
        while (_hash < Replay.Hashes.Count && Replay.Hashes[_hash].Tick < played) _hash++;
        return Divergence is null && _hash < Replay.Hashes.Count && Replay.Hashes[_hash].Tick == played;
    }

    internal void Check(ulong played, ReadOnlySpan<ulong> parts)
    {
        var expected = Replay.Hashes[_hash++].Parts;
        HashesChecked++;
        List<ReplayPartDiff>? diffs = null;
        for (int i = 0; i < _parts.Length; i++)
            if (_parts[i] >= 0 && parts[_parts[i]] != expected[i])
                (diffs ??= []).Add(new ReplayPartDiff(Replay.Parts[i], expected[i], parts[_parts[i]]));
        if (diffs is null) LastMatch = played;
        else Divergence = new ReplayDivergence(played, LastMatch, diffs) { Cause = _factCause };
    }
}

/// <summary>
/// Сравнение двух реплеев: заголовки, первый тик с разными командами, первый разный хеш и по каждому общему участнику —
/// первый тик, где он разошёлся (<see cref="Result.PartFirstTicks"/>, null — ни разу).
/// </summary>
public static class ReplayDiff
{
    public sealed record Result(
        IReadOnlyList<string> Header, ulong? FirstCommandTick, string? CommandDetail, ulong? FirstHashTick,
        IReadOnlyList<string> HashParts, ulong? LastCommonHash, int CommonHashes, IReadOnlyDictionary<string, ulong?> PartFirstTicks);

    public static Result Compare(Replay a, Replay b)
    {
        var header = new List<string>();
        void Field(string name, object? x, object? y)
        {
            if (!Equals(x, y)) header.Add($"{name}: {x ?? "—"} ≠ {y ?? "—"}");
        }
        Field("build", a.Build, b.Build);
        Field("content", a.Content, b.Content);
        Field("tickRate", a.TickRate, b.TickRate);
        Field("seed", a.Seed, b.Seed);
        Field("ticks", a.Ticks, b.Ticks);
        Field("hashEvery", a.HashEvery, b.HashEvery);
        Field("modules", Join(a.Modules), Join(b.Modules));
        Field("parts", Join(a.Parts), Join(b.Parts));
        foreach (var name in (a.Facts?.Keys ?? []).Union(b.Facts?.Keys ?? []).Order(StringComparer.Ordinal))
            Field("facts." + name, a.Facts?.GetValueOrDefault(name), b.Facts?.GetValueOrDefault(name));
        foreach (var d in ConfigDiff(a.Config, b.Config, "")) header.Add("config " + d);

        // Команды — по тикам, типом и полем по именам: у реплеев разных сборок номера типов могут не совпадать.
        ulong? firstCommand = null;
        string? detail = null;
        ulong end = Math.Max(a.Ticks, b.Ticks);
        foreach (ulong tick in a.Runs.Concat(b.Runs).SelectMany(r => new[] { r.First, r.End }).Append(0UL).Distinct().Order())
        {
            if (tick >= end) break;
            string ca = Describe(a, a.CommandsAt(tick)), cb = Describe(b, b.CommandsAt(tick));
            if (ca == cb) continue;
            firstCommand = tick;
            detail = $"{(ca.Length == 0 ? "нет команд" : ca)} ≠ {(cb.Length == 0 ? "нет команд" : cb)}";
            break;
        }

        // Хеши — на общих тиках, участники — по Id.
        ulong? firstHash = null, lastCommon = null;
        var parts = new List<string>();
        int common = 0;
        var byTick = b.Hashes.ToDictionary(h => h.Tick);
        foreach (var ha in a.Hashes)
        {
            if (!byTick.TryGetValue(ha.Tick, out var hb)) continue;
            common++;
            var diff = a.Parts.Select((p, i) => (p, i)).Where(x => b.Parts.Contains(x.p) && hb.Parts[b.Parts.ToList().IndexOf(x.p)] != ha.Parts[x.i])
                .Select(x => x.p).ToList();
            if (diff.Count == 0)
            {
                lastCommon = ha.Tick;
                continue;
            }
            firstHash = ha.Tick;
            parts = diff;
            break;
        }
        return new Result(header, firstCommand, detail, firstHash, parts, lastCommon, common, PartFirstTicks(a, b, byTick));
    }

    /// <summary>Каждый общий участник — первый общий хеш, где он разный (по всем общим хешам); null — совпал на всех.</summary>
    private static Dictionary<string, ulong?> PartFirstTicks(Replay a, Replay b, Dictionary<ulong, ReplayHash> byTick)
    {
        var inB = b.Parts.Select((p, i) => (p, i)).ToDictionary(x => x.p, x => x.i);
        var first = new Dictionary<string, ulong?>();
        foreach (var p in a.Parts)
            if (inB.ContainsKey(p)) first[p] = null;
        foreach (var ha in a.Hashes)
        {
            if (!byTick.TryGetValue(ha.Tick, out var hb)) continue;
            for (int i = 0; i < a.Parts.Count; i++)
                if (inB.TryGetValue(a.Parts[i], out int j) && first[a.Parts[i]] is null && ha.Parts[i] != hb.Parts[j]) first[a.Parts[i]] = ha.Tick;
        }
        return first;
    }

    // Отрезки между границами — одни и те же команды: сравнивать достаточно на границах отрезков.
    private static string Describe(Replay r, IReadOnlyList<ReplayCommand> commands) =>
        string.Join("; ", commands.Select(c =>
        {
            var t = r.CommandTypes[c.Type];
            return $"{t.Name.Split('.')[^1]}({string.Join(", ", t.Fields.Select((f, i) => $"{f[..f.LastIndexOf(':')]}={Replay.EncodeValue(Replay.KindOfField(f), c.Leaves[i]).Trim('"')}"))})";
        }));

    private static string Join(IReadOnlyList<string>? list) => list is null ? "—" : string.Join(", ", list);

    private static IEnumerable<string> ConfigDiff(JsonNode? a, JsonNode? b, string path)
    {
        if (a is JsonObject oa && b is JsonObject ob)
        {
            foreach (var key in oa.Select(p => p.Key).Union(ob.Select(p => p.Key)).Order(StringComparer.Ordinal))
                foreach (var d in ConfigDiff(oa[key], ob[key], path.Length == 0 ? key : $"{path}.{key}"))
                    yield return d;
        }
        else if (!JsonNode.DeepEquals(a, b))
            yield return $"{path}: {a?.ToJsonString() ?? "—"} ≠ {b?.ToJsonString() ?? "—"}";
    }
}
