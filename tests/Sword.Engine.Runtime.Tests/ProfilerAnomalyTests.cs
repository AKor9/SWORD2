using System.Diagnostics;
using System.Reflection.Emit;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sword.Engine.Diagnostics;

namespace Sword.Engine.Runtime.Tests;

/// <summary>
/// Захват аномалий профайлера (этап П, точка Пв): медленный кадр или тик — ровно один файл, его зона — главная, класс верный;
/// ровный прогон — ни одного; тик в кадре App — файл кадра; пауза JIT — класс JIT (и событие в файле в диагностическом режиме);
/// лимит файлов и частота; 0 байт на главном потоке и в кадре аномалии; хеши симуляции те же; Chrome Trace — корректный JSON
/// с правильной вложенностью (X кольца, B/E ленты).
///
/// Суд, класс, главная зона, лимиты, ведро и заметность — на поддельных часах (<see cref="FakeProfileClock"/>, решение
/// пользователя после 7з-3): длительность каждой зоны и её доля на CPU заданы тестом (<see cref="FakeProfileClock.Run"/> — счёт,
/// <see cref="FakeProfileClock.OffCpu"/> — поток вне CPU), настоящего времени профайлер не читает — снятие потока тестов с
/// процессора под параллельной нагрузкой ни лишних аномалий «машина», ни другого класса, ни пропуска файла по частоте не даёт,
/// поэтому проверки — точные. На настоящих часах — только то, что меряет настоящее: JIT и события среды (диагностический режим),
/// 0 байт и 0 методов JIT пути аномалии в продукте, хеши симуляции, трассы.
/// </summary>
[Collection(RuntimeEventsCollection.Name)]
public sealed class ProfilerAnomalyTests : IDisposable
{
    /// <summary>Обычный период на поддельных часах — 10 мкс счёта.</summary>
    private const double NormalMs = 0.01;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sword-anomaly-tests", Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeProfileClock _clock = new();

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Счётный цикл до <paramref name="ms"/> мс времени потока на CPU (снятие с процессора его не укорачивает).</summary>
    private static void SpinCpu(double ms)
    {
        ulong until = ThreadClock.Cycles() + (ulong)(ms / 1000 * ThreadClock.CyclesPerSecond);
        while (ThreadClock.Cycles() < until) { }
    }

    /// <summary>
    /// Профайлер с захватом аномалий — на поддельных часах теста (<see cref="_clock"/>); <paramref name="realClock"/> — на
    /// настоящих (продукт: проверка 0 байт в кадрах аномалии).
    /// </summary>
    private CpuProfiler Profiler(out ProfileAnomalies anomalies, AnomalyOptions? options = null, int history = 64, bool realClock = false)
    {
        var p = realClock ? new CpuProfiler(history) : new CpuProfiler(history, _clock);
        anomalies = p.CaptureAnomalies(options ?? new AnomalyOptions { Directory = _dir });
        p.SetEnabled(true);
        return p;
    }

    /// <summary>Кадр с одной зоной и работой в ней; без работы — обычный (<see cref="NormalMs"/> счёта на поддельных часах).</summary>
    private void Frame(CpuProfiler p, ulong number, int zone, Action? work = null)
    {
        p.BeginFrame(number);
        p.Begin(zone);
        if (work is null) _clock.Run(NormalMs);
        else work();
        p.End(zone);
        p.EndFrame(1, service: false);
    }

    private static JsonObject ReadJson(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    /// <summary>
    /// Аномалия периода <paramref name="number"/> — единственная в списке (поддельные часы: снятия потока с процессора нет, и
    /// «машины» у обычных периодов — тоже); что было — в тексте провала.
    /// </summary>
    private static ProfileAnomaly Only(ProfileAnomalies a, ulong number)
    {
        var list = a.List();
        string all = string.Join(" | ", list.Select(x => $"{x.Text} [{x.Causes}]"));
        Assert.True(list.Count == 1 && list[0].Number == number, $"ждали одну аномалию — периода {number}: {all}");
        return list[0];
    }

    /// <summary>
    /// Класс медленного периода на настоящих часах (JIT — настоящая компиляция): ожидаемый (<paramref name="expected"/>, его доля ≥
    /// <paramref name="minMs"/>) — или «машина», но только если поток под нагрузкой тестов сняли с процессора дольше, чем длилась
    /// сама причина (снятие больше её доли — честная «машина»; так бывает, когда все сборки тестов идут параллельно).
    /// </summary>
    private static void ClassOf(in ProfileAnomaly x, AnomalyClass expected, double part, double minMs)
    {
        Assert.True(part >= minMs, $"доля причины {part} мс < {minMs}: {x.Text} [{x.Causes}]");
        if (x.Class == expected) return;
        Assert.True(x.Class == AnomalyClass.Machine && x.Causes.MachineMs > part, $"класс {x.Class}, ждали {expected}: {x.Text} [{x.Causes}]");
    }

    private string[] Files(string pattern = "anomaly-*.json") =>
        Directory.Exists(_dir) ? [.. Directory.GetFiles(_dir, pattern).Where(f => !f.EndsWith(".trace.json", StringComparison.Ordinal)).Order()] : [];

    [Fact]
    public void SlowFrame_ExactlyOneFile_ItsZoneOnTop_ClassCode()
    {
        var p = Profiler(out var a);
        int work = p.Zone("work"), slow = p.Zone("slow");
        for (ulong f = 1; f <= 100; f++) Frame(p, f, work);
        p.BeginFrame(101);
        p.Begin(work);
        p.Begin(slow);
        _clock.Run(10);
        p.End(slow);
        p.End(work);
        p.EndFrame(1, false);
        for (ulong f = 102; f <= 200; f++) Frame(p, f, work);
        Assert.True(a.Flush(TimeSpan.FromSeconds(10)));

        var only = Only(a, 101);
        Assert.Equal(ProfilePeriod.Frame, only.Period);
        Assert.Equal(101ul, only.Number);
        Assert.Equal(AnomalyClass.Code, only.Class);
        Assert.Equal(10 - NormalMs, only.Causes.CodeMs, 3);   // лишнее сверх медианы — всё за код (поток на CPU)
        Assert.Equal("slow", only.ZoneName);
        Assert.True(only.Ms >= 10 && only.ZoneExcessMs >= 9, $"кадр {only.Ms} мс, лишнее зоны {only.ZoneExcessMs}");
        Assert.Equal(AnomalyFile.Written, only.File);

        // Ровно один файл — этого кадра.
        Assert.Single(Files());
        string file = only.Path!;
        Assert.True(File.Exists(file));
        Assert.EndsWith("anomaly-frame-101.json", file);
        var json = ReadJson(file);
        Assert.Equal(ProfileAnomalies.FileFormat, json["format"]!.GetValue<string>());
        Assert.Equal(AnomalyCauses.Key(only.Class), json["class"]!.GetValue<string>());
        Assert.Equal("slow", json["mainZone"]!["name"]!.GetValue<string>());
        Assert.Equal("frame > work > slow", json["mainZone"]!["path"]!.GetValue<string>());
        Assert.Equal("slow", json["zones"]![0]!["name"]!.GetValue<string>());   // по своему лишнему времени — наверху
        var tree = json["tree"]!;
        Assert.Equal("frame", tree["name"]!.GetValue<string>());
        Assert.Equal("slow", tree["children"]![0]!["children"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(32, json["recent"]!.AsArray().Count);   // 31 перед ним и он сам
        Assert.True(json["recent"]![31]!["anomaly"]!.GetValue<bool>());
        Assert.Equal("counters", json["events"]!["mode"]!.GetValue<string>());
        Assert.NotNull(json["machine"]?["verdict"] ?? json["header"]);
        Assert.True(File.Exists(Path.Combine(_dir, json["trace"]!.GetValue<string>())));
        a.Dispose();
    }

    /// <summary>
    /// Независимость от нагрузки — по устройству: на поддельных часах настоящие задержки потока (сон 5 мс — поток по-настоящему вне
    /// CPU дольше порога кадра 2 мс, как снятие с процессора под нагрузкой) в обычных кадрах, в медленном и между кадрами не меняют
    /// ни списка аномалий, ни класса, ни главной зоны, ни длительности, ни судьбы файла — итог тот же, что без них (на настоящих
    /// часах такие кадры были бы аномалиями «машина»).
    /// </summary>
    [Fact]
    public void FakeClock_RealThreadDelays_ChangeNothing()
    {
        (ulong, AnomalyClass, AnomalyFile, string, double)[] Run(bool delays)
        {
            var clock = new FakeProfileClock();
            var p = new CpuProfiler(64, clock);
            var a = p.CaptureAnomalies(new AnomalyOptions { Directory = Path.Combine(_dir, delays ? "delays" : "plain"), Trace = false });
            p.SetEnabled(true);
            int work = p.Zone("work"), slow = p.Zone("slow");
            for (ulong f = 1; f <= 60; f++)
            {
                p.BeginFrame(f);
                p.Begin(work);
                clock.Run(NormalMs);
                if (delays && f % 10 == 0) Thread.Sleep(5);   // поток вне CPU по-настоящему — часы профайлера стоят
                if (f == 50)
                {
                    p.Begin(slow);
                    clock.Run(10);
                    if (delays) Thread.Sleep(5);
                    p.End(slow);
                }
                p.End(work);
                p.EndFrame(1, false);
                if (delays && f % 10 == 5) Thread.Sleep(5);   // и между кадрами
            }
            Assert.True(a.Flush(TimeSpan.FromSeconds(10)));
            var result = a.List().Select(x => (x.Number, x.Class, x.File, x.ZoneName, Math.Round(x.Ms, 6))).ToArray();
            a.Dispose();
            return result;
        }
        var plain = Run(delays: false);
        Assert.Equal([(50ul, AnomalyClass.Code, AnomalyFile.Written, "slow", 10 + NormalMs)], plain);
        Assert.Equal(plain, Run(delays: true));
    }

    /// <summary>
    /// Исправления после Пд (замечание проверяющего): абсолютный порог сработал на кадре короче обычного — ни одна зона не дольше
    /// своего среднего; главной зоны нет (а не случайная зона «frame.resize 0 мс (+0)») — в записи, тексте журнала, JSON
    /// <c>profile.stages</c> и файле. Кадр дольше обычного при том же пороге — с главной зоной, как раньше.
    /// </summary>
    [Fact]
    public void AbsoluteThreshold_FrameBelowMean_NoMainZone()
    {
        var p = Profiler(out var a, new AnomalyOptions
        {
            Directory = _dir, AbsoluteMs = 0.1, MaxFiles = 100, MaxFilesMachine = 100, MaxFilesGpuWait = 100, Burst = 100,
            FilesPerSecond = 1000, Trace = false,
        });
        int work = p.Zone("work"), idle = p.Zone("idle");
        for (ulong f = 1; f <= 40; f++)
        {
            Frame(p, f, work, () => _clock.Run(3));   // с 33-го — тоже аномалии (дольше абсолютного порога)
            Assert.True(a.Flush(TimeSpan.FromSeconds(10)));   // писатель свободен: файл кадра 41 не «занят»
        }
        p.BeginFrame(41);   // выше абсолютного порога, но вдесятеро короче обычного
        p.Begin(idle);
        p.End(idle);
        p.Begin(work);
        _clock.Run(0.3);
        p.End(work);
        p.EndFrame(1, false);
        Assert.True(a.Flush(TimeSpan.FromSeconds(10)));
        p.BeginFrame(42);   // дольше обычного — главная зона есть
        p.Begin(work);
        _clock.Run(12);
        p.End(work);
        p.EndFrame(1, false);
        Assert.True(a.Flush(TimeSpan.FromSeconds(10)));

        var list = a.List();
        string all = string.Join(" | ", list.Select(x => x.Text));
        var shorter = list.Single(x => x.Number == 41);
        Assert.False(shorter.HasZone, all);
        Assert.Equal(ProfileAnomaly.NoZone, shorter.Zone);
        Assert.Contains(ProfileAnomaly.NoZoneText, shorter.Text);
        Assert.DoesNotContain("главная зона", shorter.Text);
        var record = shorter.ToJson();
        Assert.Null(record["zone"]);
        Assert.NotNull(record["zoneNote"]);
        Assert.Equal(AnomalyFile.Written, shorter.File);
        var json = ReadJson(shorter.Path!);
        Assert.Null(json["mainZone"]!["name"]);
        Assert.Contains(ProfileAnomaly.NoZoneText, json["mainZone"]!["note"]!.GetValue<string>());
        Assert.Contains(ProfileAnomaly.NoZoneText, json["text"]!.GetValue<string>());

        var longer = list.Single(x => x.Number == 42);
        Assert.True(longer.HasZone, all);
        Assert.Equal("work", longer.ZoneName);
        Assert.Contains("главная зона work", longer.Text);
        a.Dispose();
    }

    [Fact]
    public void SlowTick_WithoutFrames_TickFile()
    {
        var p = Profiler(out var a);
        int sys = p.Zone("sys"), slow = p.Zone("slow");
        for (ulong t = 0; t < 300; t++)
        {
            p.BeginTick(t);
            p.Begin(sys);
            _clock.Run(NormalMs);
            if (t == 200)
            {
                p.Begin(slow);
                _clock.Run(10);   // 10 мс на CPU — класс «код»
                p.End(slow);
            }
            p.End(sys);
            p.EndTick();
        }
        Assert.True(a.Flush(TimeSpan.FromSeconds(10)));
        var only = Only(a, 200);
        Assert.Equal(ProfilePeriod.Tick, only.Period);
        Assert.Equal(200ul, only.Number);
        Assert.Equal("slow", only.ZoneName);
        Assert.Equal(AnomalyClass.Code, only.Class);
        Assert.Equal(10, only.Causes.CodeMs, 3);
        var json = ReadJson(only.Path!);
        Assert.Equal("tick", json["period"]!.GetValue<string>());
        Assert.Equal("tick > sys > slow", json["mainZone"]!["path"]!.GetValue<string>());
        Assert.EndsWith("anomaly-tick-200.json", only.Path);
        a.Dispose();
    }

    [Fact]
    public void SlowTickInsideFrame_OneFrameFile_TickTrigger()
    {
        var p = Profiler(out var a);
        int sim = p.Zone("frame.simulate"), sys = p.Zone("sys"), slow = p.Zone("slow");
        ulong tick = 0;
        for (ulong f = 1; f <= 200; f++)
        {
            p.BeginFrame(f);
            p.Begin(sim);
            for (int k = 0; k < 2; k++, tick++)
            {
                p.BeginTick(tick);
                p.Begin(sys);
                _clock.Run(NormalMs);
                if (f == 150 && k == 1)
                {
                    p.Begin(slow);
                    _clock.Run(1.5);   // тик: порог — 1 мс сверх медианы — аномалия; кадр: порог — 2 мс — нет
                    p.End(slow);
                }
                p.End(sys);
                p.EndTick();
            }
            p.End(sim);
            p.EndFrame(0.5f, false);
        }
        Assert.True(a.Flush(TimeSpan.FromSeconds(10)));
        var only = Only(a, 150);
        Assert.Equal(ProfilePeriod.Frame, only.Period);
        Assert.Equal(150ul, only.Number);
        Assert.Equal(AnomalyClass.Code, only.Class);
        Assert.Equal(1.5, only.Causes.CodeMs, 3);   // разбор — тика: лишнее сверх его медианы
        // Сработал тик: кадр (1,52 мс при медиане 0,02) — не дольше своего порога 2,02 мс; файл — кадра, тик в нём.
        Assert.True(only.TickTrigger, $"сработал кадр {only.PeriodMs} мс, а не тик");
        Assert.Equal(1.5 + 2 * NormalMs, only.PeriodMs, 3);
        Assert.Equal(299ul, only.Tick);
        Assert.Equal("slow", only.ZoneName);
        Assert.EndsWith("anomaly-frame-150.json", only.Path);
        var json = ReadJson(only.Path!);
        Assert.Equal("tick", json["trigger"]!.GetValue<string>());
        Assert.Equal(299ul, json["tick"]!["number"]!.GetValue<ulong>());
        Assert.Equal("frame > frame.simulate > tick > sys > slow", json["mainZone"]!["path"]!.GetValue<string>());
        Assert.NotNull(json["windowTicks"]);
        a.Dispose();
    }

    /// <summary>
    /// Тик, прерванный ожиданием агента (останов посреди тика — <see cref="CpuProfiler.Interrupt"/>), — служебный: не аномалия и
    /// не в итогах; следующий медленный — аномалия, как обычно.
    /// </summary>
    [Fact]
    public void InterruptedTick_Service_NotAnomaly()
    {
        var p = Profiler(out var a);
        int obs = p.Zone("sim.observers"), slow = p.Zone("slow");
        for (ulong t = 0; t < 120; t++)
        {
            p.BeginTick(t);
            p.Begin(obs);
            _clock.Run(NormalMs);
            if (t == 60)
            {
                p.Interrupt();   // агент стоит посреди тика
                _clock.OffCpu(30);
            }
            if (t == 100)
            {
                p.Begin(slow);
                _clock.Run(20);   // 20 мс на CPU — класс «код»
                p.End(slow);
            }
            p.End(obs);
            p.EndTick();
        }
        Assert.True(a.Flush(TimeSpan.FromSeconds(10)));
        var only = Only(a, 100);
        Assert.Equal(AnomalyClass.Code, only.Class);
        Assert.Equal(20, only.Causes.CodeMs, 3);
        Assert.False(only.Noticeable);   // после Пв: тик без кадров не «заметный» (показа нет), хоть он и дольше 16,7 мс
        Assert.DoesNotContain(a.List(), x => x.Number == 60);   // прерванный — не аномалия (и не «машина»)
        Assert.Equal(1, p.Count(ProfilePeriod.Tick).Service);
        Assert.Equal(119, p.Count(ProfilePeriod.Tick).Periods);
        Assert.Contains(p.Recent(ProfilePeriod.Tick, 200), h => h.Number == 60 && h.Service);   // в кольце — служебным
        a.Dispose();
    }

    [Fact]
    public void Causes_Class_LargestShare_TiesByPriority_ZeroIsCode()
    {
        Assert.Equal(AnomalyClass.Code, new AnomalyCauses(0, 0, 0, 0, 0, 0).Class);
        Assert.Equal(AnomalyClass.Jit, new AnomalyCauses(3, 1.5, 1.5, 0, 0, 0).Class);
        Assert.Equal(AnomalyClass.Gc, new AnomalyCauses(3, 0.5, 2, 0, 0.5, 0).Class);
        Assert.Equal(AnomalyClass.GpuWait, new AnomalyCauses(3, 0, 0, 2.5, 0, 0.5).Class);
        Assert.Equal(AnomalyClass.Machine, new AnomalyCauses(3, 0, 0, 0, 2, 1).Class);
        Assert.Equal(AnomalyClass.Code, new AnomalyCauses(3, 0.5, 0, 0, 0.5, 2).Class);
    }

    [Fact]
    public void SmoothRun_NoFiles()
    {
        var p = Profiler(out var a);
        int z = p.Zone("z");
        ulong tick = 0;
        for (ulong f = 1; f <= 5000; f++)
        {
            p.BeginFrame(f);
            p.Begin(z);
            _clock.Run(NormalMs);
            if (f % 3 == 0)
            {
                p.BeginTick(tick++);
                _clock.Run(NormalMs / 2);
                p.EndTick();
            }
            p.End(z);
            p.EndFrame(1, false);
        }
        Assert.True(a.Flush(TimeSpan.FromSeconds(10)));
        // Ровный прогон — ни одной аномалии и ни одного файла (поддельные часы: снятия потока с процессора нет).
        Assert.Empty(a.List());
        Assert.Empty(Files());
        Assert.True(a.Thresholds.FrameMs >= 2, $"порог кадра {a.Thresholds.FrameMs} мс");   // медиана + 2 мс
        Assert.True(a.Thresholds.TickMs >= 1, $"порог тика {a.Thresholds.TickMs} мс");
        Assert.Equal(NormalMs + 2, a.Thresholds.FrameMs, 3);   // медиана кадра — обычный кадр (у двух из трёх нет тика)
        Assert.Equal(NormalMs / 2 + 1, a.Thresholds.TickMs, 3);
        a.Dispose();
    }

    [Fact]
    public void ServiceFrame_NotJudged_WarmupNotJudged()
    {
        var p = Profiler(out var a);
        int z = p.Zone("z");
        // Первые MinSamples кадров не судятся (прогрев свежего процесса), служебный кадр — тоже.
        Frame(p, 1, z, () => _clock.Run(5));
        for (ulong f = 2; f <= 60; f++) Frame(p, f, z);
        p.BeginFrame(61);
        p.Begin(z);
        _clock.Run(5);
        p.End(z);
        p.EndFrame(1, service: true);
        Assert.True(a.Flush(TimeSpan.FromSeconds(10)));
        Assert.Empty(a.List());
        a.Dispose();
    }

    /// <summary>
    /// После Пв (решение пользователя): заметная — кадр не короче интервала показа (частота монитора владельца кадров; неизвестна —
    /// 60 Гц). Порог суда тот же: кадр в 3 мс — аномалия, но не заметная; кадр в 12 мс при 100 Гц — заметная. Правило — точно по
    /// длительности кадра (на настоящих часах снятие потока с процессора удлинило бы короткий — тогда и он заметный, и это верно).
    /// Заметность — в записи, тексте (строка журнала), файле, счёте и JSON (<c>profile.stages</c>).
    /// </summary>
    [Fact]
    public void Noticeable_FrameNotShorterThanDisplayInterval()
    {
        var p = Profiler(out var a);
        Assert.Equal(1000.0 / 60, a.NoticeableMs, 3);   // частота неизвестна — 60 Гц
        Assert.Equal("default", a.NoticeableSource);
        p.DisplayHz = 100;
        Assert.Equal(10, a.NoticeableMs, 3);
        Assert.Equal("monitor", a.NoticeableSource);
        int work = p.Zone("work"), slow = p.Zone("slow");
        for (ulong f = 1; f <= 100; f++)
        {
            Frame(p, f, work);
            if (f == 60) Frame(p, ++f, work, () => _clock.Run(3));    // кадр 61: аномалия (порог ~2 мс), короче интервала
        }
        p.BeginFrame(101);
        p.Begin(slow);
        _clock.Run(12);   // кадр 101: не короче 10 мс — заметная
        p.End(slow);
        p.EndFrame(1, false);
        for (ulong f = 102; f <= 140; f++) Frame(p, f, work);
        Assert.True(a.Flush(TimeSpan.FromSeconds(10)));

        var list = a.List();
        Assert.Equal([61ul, 101ul], list.Select(x => x.Number));
        Assert.All(list, x => Assert.Equal(x.PeriodMs >= a.NoticeableMs, x.Noticeable));   // правило — точно
        var small = Assert.Single(list, x => x.Number == 61);
        Assert.Equal(3, small.PeriodMs, 3);
        Assert.False(small.Noticeable);
        Assert.DoesNotContain(ProfileAnomaly.NoticeableWord, small.Text);
        var big = Assert.Single(list, x => x.Number == 101);
        Assert.True(big.Noticeable);
        Assert.Contains($"— {AnomalyCauses.Word(big.Class)}, заметная (кадр ", big.Text);   // строка журнала — это Text
        Assert.Equal(list.Count(x => x.Noticeable), a.NoticeableCount);
        var json = ReadJson(big.Path!);
        Assert.True(json["noticeable"]!.GetValue<bool>());
        Assert.Equal(10, json["display"]!["noticeableMs"]!.GetValue<double>(), 3);
        Assert.Equal("monitor", json["display"]!["source"]!.GetValue<string>());
        Assert.Contains("заметная", json["text"]!.GetValue<string>());
        var summary = a.ToJson();   // то же — в profile.stages (anomalies) и profile.json
        Assert.Equal(a.NoticeableCount, summary["noticeable"]!.GetValue<long>());
        Assert.Equal(100, summary["display"]!["hz"]!.GetValue<double>(), 3);
        Assert.Contains(summary["list"]!.AsArray(), x => x!["number"]!.GetValue<ulong>() == 101 && x["noticeable"]!.GetValue<bool>());
        Assert.Contains("заметных", a.Summary());
        a.Dispose();
    }

    /// <summary>
    /// После Пв (решение пользователя): лимит файлов по классам — «ожидание GPU» и «машина» (чужая нагрузка) не съедают файлов
    /// кода, JIT, GC; в списке — все; заметная аномалия, которой не хватило лимита своего класса, всё же пишется — из запаса
    /// заметных (он мал: лавины нет).
    /// </summary>
    [Fact]
    public void ClassLimits_GpuAndMachineDoNotEatOthers_NoticeableStillWritten()
    {
        var p = Profiler(out var a, new AnomalyOptions
        {
            Directory = _dir, MaxFiles = 5, MaxFilesGpuWait = 2, MaxFilesMachine = 2, NoticeableFiles = 1, Burst = 100, Trace = false,
        });
        p.DisplayHz = 25;   // интервал 40 мс: ожидание, снятие с процессора и счёт в 3 мс — не заметные
        int work = p.Zone("work"), fence = p.Zone("fence", ProfileZoneKind.GpuWait);
        ulong f = 0;
        void Normal(int n)
        {
            for (int i = 0; i < n; i++) Frame(p, ++f, work);
        }
        ulong Slow(int zone, Action act)
        {
            Frame(p, ++f, zone, act);
            Assert.True(a.Flush(TimeSpan.FromSeconds(10)));   // писатель свободен — отказ только по лимиту
            Normal(20);
            return f - 20;
        }
        Normal(100);
        var gpu = Enumerable.Range(0, 6).Select(_ => Slow(fence, () => _clock.Run(3))).ToList();        // ожидание GPU (вид зоны)
        var machine = Enumerable.Range(0, 6).Select(_ => Slow(work, () => _clock.OffCpu(3))).ToList();  // поток вне CPU
        var code = Enumerable.Range(0, 3).Select(_ => Slow(work, () => _clock.Run(3))).ToList();        // после всех чужих
        ulong noticeable = Slow(fence, () => _clock.Run(45));   // ожидание GPU, лимит класса исчерпан, но заметная (≥ 40 мс)
        Assert.True(a.Flush(TimeSpan.FromSeconds(10)));

        var list = a.List();
        string all = string.Join(" | ", list.Select(x => $"{x.Number} {x.Class} {x.File} {x.Noticeable}"));
        ProfileAnomaly At(ulong n) => Assert.Single(list, x => x.Number == n);
        // Аномалии — только задуманные, классы — те, что задуманы (поддельные часы: снятия потока тестов с процессора нет).
        Assert.Equal([.. gpu, .. machine, .. code, noticeable], list.Select(x => x.Number));
        Assert.All(gpu, n => Assert.Equal(AnomalyClass.GpuWait, At(n).Class));
        Assert.All(machine, n => Assert.Equal(AnomalyClass.Machine, At(n).Class));
        Assert.All(code, n => Assert.Equal(AnomalyClass.Code, At(n).Class));
        // «Ожидание GPU» и «машина» — не больше своих лимитов, остальные — «сверх лимита класса», но в списке.
        Assert.Equal(2, list.Count(x => x.Class == AnomalyClass.GpuWait && x.File == AnomalyFile.Written && !x.Noticeable));
        Assert.Equal(2, list.Count(x => x.Class == AnomalyClass.Machine && x.File == AnomalyFile.Written && !x.Noticeable));
        Assert.Equal(8, list.Count(x => x.File == AnomalyFile.ClassLimit));
        Assert.All(list.Where(x => x.File == AnomalyFile.ClassLimit), x => Assert.True(x.Class is AnomalyClass.GpuWait or AnomalyClass.Machine));
        // Код — после 12 чужих аномалий — всё равно с файлами (раньше общий лимит 20 съедала чужая нагрузка).
        Assert.All(code, n => Assert.Equal(AnomalyFile.Written, At(n).File));
        // Заметная — пишется из запаса заметных.
        var seen = At(noticeable);
        Assert.True(seen.Noticeable, all);
        Assert.Equal(AnomalyFile.Written, seen.File);
        var limits = a.LimitsJson();
        Assert.Equal(1, limits["noticeable"]!["files"]!.GetValue<int>());
        Assert.Equal(2, limits["gpu"]!["files"]!.GetValue<int>());
        Assert.Equal(2, limits["machine"]!["files"]!.GetValue<int>());
        Assert.Equal(list.Count(x => x.File == AnomalyFile.Written), Files().Length);
        a.Dispose();
    }

    /// <summary>
    /// Пд (исправление доводки после Пв): запас заметных — сверх лимита класса, но не сверх частоты. Поток заметных «ожиданий GPU»
    /// (при кадре ≥ интервала показа — каждое, ~60/с на бенче под чужой нагрузкой GPU) раньше писался и без жетона и тратил весь
    /// запас за доли секунды; теперь — не быстрее ведра, и заметная аномалия кода после потока (её класс без лимита — только запас)
    /// ещё пишется.
    /// </summary>
    [Fact]
    public void NoticeableReserve_NotFasterThanRate_CodeAfterGpuStreamStillWritten()
    {
        var p = Profiler(out var a, new AnomalyOptions
        {
            // Лимиты кода и «машины» — 0: заметная любого класса, кроме «ожидания GPU» (1 файл), пишется только из запаса.
            Directory = _dir, MaxFiles = 0, MaxFilesGpuWait = 1, MaxFilesMachine = 0, NoticeableFiles = 6, Burst = 2,
            FilesPerSecond = 2, Trace = false,
        });
        p.DisplayHz = 1000;   // интервал 1 мс: каждая аномалия (лишнее ≥ 2 мс) — заметная
        int work = p.Zone("work"), fence = p.Zone("fence", ProfileZoneKind.GpuWait);
        ulong f = 0;
        for (int i = 0; i < 100; i++) Frame(p, ++f, work);
        long start = _clock.Now;
        var stream = new List<ulong>();
        for (int k = 0; k < 30; k++)   // поток «ожиданий GPU» — быстрее ведра (30 за ~0,12 с при 2 жетонах сразу и 2 в секунду)
        {
            Frame(p, ++f, fence, () => _clock.Run(4));
            stream.Add(f);
            for (int i = 0; i < 4; i++) Frame(p, ++f, work);
        }
        double streamSec = (_clock.Now - start) / (double)Stopwatch.Frequency;
        Assert.True(a.Flush(TimeSpan.FromSeconds(10)));
        var list = a.List();
        string all = string.Join(" | ", list.Select(x => $"{x.Number} {x.Class} {x.File} {x.Noticeable}"));
        Assert.Equal(stream, list.Select(x => x.Number));   // аномалии — только поток (поддельные часы: «машины» нет)
        var fromStream = list.ToList();
        Assert.All(fromStream, x => Assert.True(x.Noticeable && x.Class == AnomalyClass.GpuWait, all));
        // Файлов потока — не больше жетонов: 2 сразу и 2 в секунду (+1 — округление), а не 1 + весь запас (6), как до Пд. Поток
        // на поддельных часах — 0,1212 с (+0,24 жетона): файлов ровно 2 — лимит класса (1) и один из запаса, остальные — по частоте.
        int streamFiles = fromStream.Count(x => x.File == AnomalyFile.Written);
        int tokens = 2 + (int)Math.Ceiling(streamSec * 2) + 1;
        Assert.True(streamFiles <= tokens, $"файлов потока {streamFiles} > жетонов {tokens} за {streamSec:0.###} с: {all}");
        Assert.Equal(2, streamFiles);
        Assert.Equal(28, fromStream.Count(x => x.File == AnomalyFile.Rate));
        int reserveUsed = a.LimitsJson()["noticeable"]!["files"]!.GetValue<int>();
        Assert.True(reserveUsed < 6, $"запас потрачен потоком: {reserveUsed}; {all}");
        Assert.Equal(1, reserveUsed);

        // Ведро наполнилось (0,7 с — +1,4 жетона) — заметная аномалия кода (лимит кода 0) пишется из оставшегося запаса.
        _clock.OffCpu(700);   // между кадрами: в периоды не входит
        Frame(p, ++f, work, () => _clock.Run(4));
        ulong code = f;
        for (int i = 0; i < 4; i++) Frame(p, ++f, work);
        Assert.True(a.Flush(TimeSpan.FromSeconds(10)));
        var seen = Assert.Single(a.List(), x => x.Number == code);
        Assert.True(seen.Noticeable, all);
        Assert.Equal(AnomalyClass.Code, seen.Class);
        Assert.Equal(AnomalyFile.Written, seen.File);
        Assert.Equal(reserveUsed + 1, a.LimitsJson()["noticeable"]!["files"]!.GetValue<int>());
        a.Dispose();
    }

    /// <summary>
    /// После Пв (решение пользователя): кадр инструментов (точка обслуживания выполняла запросы агента) — служебный: в кольце с
    /// пометкой <see cref="ProfileHeader.Tools"/>, не в средних и максимумах, как кадр не судится (10 мс работы агента — не
    /// аномалия «код»); аномальный тик такого кадра — аномалия (тики — до точки обслуживания); обычный медленный кадр — аномалия,
    /// как раньше.
    /// </summary>
    [Fact]
    public void ToolsFrame_NotJudged_Marked_TickStillJudged_SlowNormalFrameIsAnomaly()
    {
        var p = Profiler(out var a, history: 256);   // кольцо держит все 180 кадров
        p.DisplayHz = 100;
        int sim = p.Zone("frame.simulate"), tools = p.Zone("frame.tools"), sys = p.Zone("sys"), slow = p.Zone("slow"), stall = p.Zone("stall"),
            agent = p.Zone("agent.work");   // stall — не slow: зона с двумя родителями не вычитается из своего времени кадра
        ulong tick = 0;
        void Run(ulong f, bool toolWork, double toolMs = 0, double tickMs = 0, double frameMs = 0)
        {
            p.BeginFrame(f);
            p.Begin(sim);
            p.BeginTick(tick++);
            p.Begin(sys);
            _clock.Run(NormalMs);
            if (tickMs > 0)
            {
                p.Begin(slow);
                _clock.Run(tickMs);
                p.End(slow);
            }
            p.End(sys);
            p.EndTick();
            p.End(sim);
            p.Begin(tools);
            if (toolMs > 0)
            {
                p.Begin(agent);
                _clock.Run(toolMs);   // запрос агента в точке обслуживания
                p.End(agent);
            }
            p.End(tools);
            if (frameMs > 0)
            {
                p.Begin(stall);
                _clock.Run(frameMs);
                p.End(stall);
            }
            p.EndFrame(1, service: false, tools: toolWork);
        }
        for (ulong f = 1; f <= 100; f++) Run(f, false);
        Run(101, true, toolMs: 15);                 // тяжёлый запрос агента: кадр инструментов, не аномалия
        for (ulong f = 102; f <= 120; f++) Run(f, true);   // лёгкие запросы — тоже кадры инструментов
        for (ulong f = 121; f <= 140; f++) Run(f, false);
        Run(141, true, toolMs: 15, tickMs: 1.5);    // кадр инструментов с медленным тиком: аномалия — тик
        for (ulong f = 142; f <= 160; f++) Run(f, false);
        Run(161, false, frameMs: 10);               // обычный медленный кадр: аномалия «код», как раньше
        for (ulong f = 162; f <= 180; f++) Run(f, false);
        Assert.True(a.Flush(TimeSpan.FromSeconds(10)));

        var list = a.List();
        string all = string.Join(" | ", list.Select(x => $"{x.Text} [{x.Causes}]"));
        Assert.DoesNotContain(list, x => x.Number is >= 101 and <= 120);   // кадры инструментов — не аномалии (и не «машина»)
        var tickInTools = Assert.Single(list, x => x.Number == 141);
        Assert.True(tickInTools.TickTrigger, all);
        Assert.Equal("slow", tickInTools.ZoneName);
        Assert.False(tickInTools.Noticeable);   // длина кадра — за агентом, не за тиком
        var normal = Assert.Single(list, x => x.Number == 161);
        Assert.Equal(AnomalyClass.Code, normal.Class);
        Assert.Equal(10, normal.Causes.CodeMs, 3);
        Assert.Equal("stall", normal.ZoneName);
        Assert.Equal([141ul, 161ul], list.Select(x => x.Number));   // других аномалий нет (поддельные часы: и «машины»)

        // Кольцо: кадры инструментов — служебные с пометкой; итоги — без них.
        var ring = p.Recent(ProfilePeriod.Frame, 200);
        Assert.All(ring.Where(h => h.Number is >= 101 and <= 120 or 141), h => Assert.True(h.Tools && h.Service));
        Assert.DoesNotContain(ring, h => h.Tools && h.Number is < 101 or > 120 and not 141);
        Assert.Equal(21, p.ToolFrames);
        Assert.Equal(21, p.Count(ProfilePeriod.Frame).Service);
        Assert.Equal(180 - 21, p.Count(ProfilePeriod.Frame).Periods);
        Assert.True(p.Stats(ProfilePeriod.Frame).Single(z => z.Name == "frame.tools").MaxMs < 9, "кадры инструментов попали в итоги");
        var report = p.Report(history: 200)["frames"]!;
        Assert.Equal(21, report["toolFrames"]!.GetValue<int>());
        Assert.Contains(report["history"]!.AsArray(), h => h!["frame"]!.GetValue<ulong>() == 101 && h["tools"]?.GetValue<bool>() == true);
        a.Dispose();
    }

    /// <summary>Большой метод — его компиляция JIT (оптимизирующая, динамический метод) занимает миллисекунды.</summary>
    private static Func<int, int> BigMethod(string name, int blocks)
    {
        var method = new DynamicMethod(name, typeof(int), [typeof(int)]);
        var il = method.GetILGenerator();
        var locals = Enumerable.Range(0, 16).Select(_ => il.DeclareLocal(typeof(int))).ToArray();
        for (int i = 0; i < locals.Length; i++)
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldc_I4, i * 7 + 1);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stloc, locals[i]);
        }
        for (int b = 0; b < blocks; b++)
        {
            var skip = il.DefineLabel();
            var x = locals[b % 16];
            var y = locals[(b * 5 + 3) % 16];
            il.Emit(OpCodes.Ldloc, x);
            il.Emit(OpCodes.Ldc_I4, b);
            il.Emit(OpCodes.Bgt, skip);
            il.Emit(OpCodes.Ldloc, x);
            il.Emit(OpCodes.Ldloc, y);
            il.Emit(OpCodes.Mul);
            il.Emit(OpCodes.Ldc_I4, b * 31 + 7);
            il.Emit(OpCodes.Xor);
            il.Emit(OpCodes.Stloc, y);
            il.MarkLabel(skip);
        }
        il.Emit(OpCodes.Ldloc, locals[0]);
        for (int i = 1; i < locals.Length; i++)
        {
            il.Emit(OpCodes.Ldloc, locals[i]);
            il.Emit(OpCodes.Add);
        }
        il.Emit(OpCodes.Ret);
        return method.CreateDelegate<Func<int, int>>();
    }

    [Fact]
    public void JitPause_DiagnosticMode_ClassJit_EventInFile()
    {
        var p = new CpuProfiler(64);
        var a = p.CaptureAnomalies(new AnomalyOptions { Directory = _dir });
        int z = p.Zone("z"), jit = p.Zone("jit");
        p.SetEvents(true);
        try
        {
            var big = BigMethod("sword.test.anomalyBig", 6000);
            for (ulong f = 1; f <= 100; f++) Frame(p, f, z);
            int result = 0;
            p.BeginFrame(101);
            p.Begin(z);
            p.Begin(jit);
            result = big(3);   // первый вызов — компиляция на этом потоке
            p.End(jit);
            p.End(z);
            p.EndFrame(1, false);
            for (ulong f = 102; f <= 150; f++) Frame(p, f, z);
            GC.KeepAlive(result);
            Assert.True(a.Flush(TimeSpan.FromSeconds(10)));
            var anomaly = Assert.Single(a.List(), x => x.Number == 101);
            ClassOf(anomaly, AnomalyClass.Jit, anomaly.Causes.JitMs, 2);
            Assert.Equal("jit", anomaly.ZoneName);
            var json = ReadJson(anomaly.Path!);
            Assert.Equal(AnomalyCauses.Key(anomaly.Class), json["class"]!.GetValue<string>());
            Assert.Equal("events", json["events"]!["mode"]!.GetValue<string>());
            var events = json["events"]!["period"]!["jit"]!.AsArray();
            Assert.Contains(events, e => e!["name"]!.GetValue<string>().Contains("sword.test.anomalyBig", StringComparison.Ordinal)
                && e["thread"]!.GetValue<string>() == "main" && e["zone"]?.GetValue<string>() == "jit");
            // Трасса аномалии в диагностическом режиме — ещё лента зон (B/E) и событие JIT.
            var trace = Parse(Path.Combine(_dir, json["trace"]!.GetValue<string>()));
            Assert.Contains(trace, e => e.Ph == "B" && e.Pid == ChromeTrace.TimelinePid && e.Name == "jit");
            Assert.Contains(trace, e => e.Ph == "X" && e.Pid == ChromeTrace.EventsPid && e.Name.Contains("sword.test.anomalyBig", StringComparison.Ordinal));
            CheckNesting(trace);
        }
        finally
        {
            p.SetEvents(false);
            a.Dispose();
        }
    }

    [Fact]
    public void FileLimit_Rate_ListKeepsAll()
    {
        // Кадры в 5 мкс при абсолютном пороге в один отсчёт часов: каждый — аномалия; лишнего сверх медианы нет — класс «код»
        // (всё по нулям — код; поддельные часы: поток на CPU весь кадр, «машины» нет). У «машины» — свой лимит (4): его не трогают.
        var options = new AnomalyOptions
        {
            Directory = _dir, AbsoluteMs = 0.00001, MaxFiles = 4, Burst = 100, MaxFilesGpuWait = 0, MaxFilesMachine = 4, NoticeableFiles = 0,
        };
        var p = Profiler(out var a, options);
        int z = p.Zone("z");
        Action work = () => _clock.Run(0.005);
        for (ulong f = 1; f <= 100; f++)
        {
            Frame(p, f, z, work);
            Assert.True(a.Flush(TimeSpan.FromSeconds(10)));   // писатель свободен — отказ только по лимиту (иначе «занят»)
        }
        Assert.True(a.Flush(TimeSpan.FromSeconds(10)));
        var list = a.List();
        Assert.Equal(100 - options.MinSamples, list.Count);   // судятся с 33-го кадра — каждый дольше порога
        Assert.All(list, x => Assert.Equal(AnomalyClass.Code, x.Class));
        int general = list.Count;
        Assert.Equal(4, list.Count(x => x.File == AnomalyFile.Written));
        Assert.Equal(general - 4, list.Count(x => x.File == AnomalyFile.Limit));
        Assert.Equal(0, list.Count(x => x.File == AnomalyFile.ClassLimit));
        Assert.Equal(4, Files().Length);
        a.Dispose();

        // Частота: два файла сразу, дальше жетонов почти нет.
        Directory.Delete(_dir, true);
        var p2 = Profiler(out var b, options with { MaxFiles = 100, MaxFilesMachine = 100, Burst = 2, FilesPerSecond = 0.001 });   // ведро — одно на все классы
        z = p2.Zone("z");
        for (ulong f = 1; f <= 60; f++)
        {
            Frame(p2, f, z, work);
            Assert.True(b.Flush(TimeSpan.FromSeconds(10)));
        }
        Assert.True(b.Flush(TimeSpan.FromSeconds(10)));
        Assert.Equal(2, b.List().Count(x => x.File == AnomalyFile.Written));
        Assert.Contains(b.List(), x => x.File == AnomalyFile.Rate);
        Assert.Equal(60 - options.MinSamples - 2, b.List().Count(x => x.File == AnomalyFile.Rate));   // 0,15 мс кадров — 0 жетонов
        Assert.Equal(2, Files().Length);
        b.Dispose();
    }

    /// <summary>Приёмник журнала, который держит писателя аномалий этого каталога на первой строке «аномалия: …».</summary>
    private sealed class HoldWriterSink(string dir) : ILogSink
    {
        public readonly ManualResetEventSlim Entered = new(), Release = new();
        private int _held;

        public void Write(in LogEntry entry)
        {
            if (entry.Category == "profile" && entry.Message.Contains(dir, StringComparison.Ordinal) && Interlocked.Exchange(ref _held, 1) == 0)
            {
                Entered.Set();
                Release.Wait(TimeSpan.FromSeconds(20));
            }
        }
    }

    /// <summary>
    /// Писатель отстал больше чем на <see cref="ProfileAnomalies.Capacity"/> аномалий (прошлый файл, хвост событий): файл всё равно
    /// о своей аномалии — запись берётся из места копирования, а не из кольца списка, где её место уже занято другой.
    /// </summary>
    [Fact]
    public void WriterLate_FileKeepsItsOwnRecord()
    {
        var sink = new HoldWriterSink(_dir);
        Log.AddSink(sink);
        try
        {
            var p = Profiler(out var a, new AnomalyOptions { Directory = _dir, AbsoluteMs = 0.00001, MaxFiles = 100, Burst = 100, Trace = false });
            int z = p.Zone("z");
            Action work = () => _clock.Run(0.005);
            for (ulong f = 1; f <= 33; f++) Frame(p, f, z, work);   // кадр 33 — первая аномалия, её файл держит журнал
            Assert.True(sink.Entered.Wait(TimeSpan.FromSeconds(10)));
            // Кадры 34 и 35 — в два свободных места, дальше — «писатель занят»; всего аномалий больше ёмкости списка.
            for (ulong f = 34; f <= 34 + ProfileAnomalies.Capacity + 10; f++) Frame(p, f, z, work);
            sink.Release.Set();
            Assert.True(a.Flush(TimeSpan.FromSeconds(10)));
            foreach (ulong f in (ulong[])[34, 35])
            {
                var json = ReadJson(Path.Combine(_dir, $"anomaly-frame-{f}.json"));
                Assert.Equal((long)(f - 33), json["sequence"]!.GetValue<long>());
                Assert.StartsWith($"кадр {f}:", json["text"]!.GetValue<string>());
            }
            a.Dispose();
        }
        finally
        {
            sink.Release.Set();
            Log.RemoveSink(sink);
        }
    }

    /// <summary>
    /// Первая аномалия не компилирует ничего на главном потоке: код захвата и всё, что он зовёт, скомпилированы при настройке
    /// (<c>WarmCapture</c>). Сборка профайлера — свежая копия в своём контексте загрузки: её методы ещё не компилировались
    /// ничем в процессе тестов (иначе проверка зависела бы от порядка тестов). Кадр App с аномальным тиком и тик без кадров.
    /// </summary>
    [Fact]
    public void FirstAnomaly_NoJitOnMainThread_FreshAssembly()
    {
        var alc = new System.Runtime.Loader.AssemblyLoadContext("sword-anomaly-fresh", isCollectible: true);
        try
        {
            var asm = alc.LoadFromAssemblyPath(typeof(CpuProfiler).Assembly.Location);
            var type = asm.GetType(typeof(CpuProfiler).FullName!)!;
            Assert.NotSame(typeof(CpuProfiler), type);
            foreach (bool frames in (bool[])[true, false])
            {
                object p = Activator.CreateInstance(type, 64)!;
                var options = Activator.CreateInstance(asm.GetType(typeof(AnomalyOptions).FullName!)!)!;
                options.GetType().GetProperty(nameof(AnomalyOptions.Directory))!.SetValue(options, Path.Combine(_dir, frames ? "fresh-frames" : "fresh-ticks"));
                var anomalies = (IDisposable)type.GetMethod(nameof(CpuProfiler.CaptureAnomalies))!.Invoke(p, [options])!;
                type.GetMethod(nameof(CpuProfiler.SetEnabled))!.Invoke(p, [true]);
                T Bind<T>(string name) where T : Delegate => type.GetMethod(name, [.. typeof(T).GetMethod("Invoke")!.GetParameters().Select(x => x.ParameterType)])!
                    .CreateDelegate<T>(p);
                var zone = Bind<Func<string, int>>(nameof(CpuProfiler.Zone));
                var beginFrame = Bind<Action<ulong>>(nameof(CpuProfiler.BeginFrame));
                var endFrame = Bind<Action<float, bool>>(nameof(CpuProfiler.EndFrame));
                var beginTick = Bind<Action<ulong>>(nameof(CpuProfiler.BeginTick));
                var endTick = Bind<Action>(nameof(CpuProfiler.EndTick));
                var begin = Bind<Action<int>>(nameof(CpuProfiler.Begin));
                var end = Bind<Action<int>>(nameof(CpuProfiler.End));
                int z = zone("z"), slow = zone("slow");
                ulong tick = 0;
                void Tick(int slowMs)
                {
                    beginTick(tick++);
                    begin(slow);
                    Thread.Sleep(slowMs);   // не счётный цикл: его OSR компилировалась бы на этом потоке и мешала бы счёту
                    end(slow);
                    endTick();
                }
                void Period(ulong n, int slowMs)
                {
                    if (!frames)
                    {
                        Tick(slowMs);
                        return;
                    }
                    beginFrame(n);
                    begin(z);
                    Tick(slowMs);
                    end(z);
                    endFrame(1, false);
                }
                for (ulong n = 1; n <= 100; n++) Period(n, 0);   // прогрев обычного пути (32 первых не судятся)
                long before = System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: true);
                Period(101, 5);   // тик — аномалия; у кадра App — и кадр (с тиком)
                long compiled = System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: true) - before;
                long detected = (long)anomalies.GetType().GetProperty(nameof(ProfileAnomalies.Detected))!.GetValue(anomalies)!;
                Assert.True(detected >= 1, $"аномалии не было ({(frames ? "кадры" : "тики")})");
                Assert.True(compiled == 0, $"первая аномалия ({(frames ? "кадр App с тиком" : "тик без кадров")}) компилировала на главном потоке {compiled} методов");
                anomalies.Dispose();
            }
        }
        finally
        {
            alc.Unload();
        }
    }

    /// <summary>0 байт на главном потоке и в кадрах аномалии — у продукта: на настоящих часах.</summary>
    [Fact]
    public void Capture_ZeroBytesOnMainThread_EvenInAnomalyFrames()
    {
        var p = Profiler(out var a, new AnomalyOptions { Directory = _dir, MaxFiles = 100, Burst = 100 }, realClock: true);
        int[] zones = [.. Enumerable.Range(0, 20).Select(i => p.Zone("zone" + i))];
        int slow = p.Zone("slow"), slowTick = p.Zone("slowTick");
        ulong tick = 0;
        void Run(ulong f, bool anomaly)
        {
            p.BeginFrame(f);
            foreach (int z in zones.AsSpan(0, 8))
            {
                p.Begin(z);
                p.End(z);
            }
            p.BeginTick(tick++);
            foreach (int z in zones.AsSpan(8))
            {
                p.Begin(z);
                p.End(z);
            }
            if (f is 1750 or 3250)
            {
                p.Begin(slowTick);
                SpinCpu(1.5);
                p.End(slowTick);
            }
            p.EndTick();
            if (anomaly)
            {
                p.Begin(slow);
                SpinCpu(3);
                p.End(slow);
            }
            p.EndFrame(0.5f, false, tools: f % 500 == 250);   // после Пв: и кадры инструментов (1750, 3250 — с медленным тиком)
        }
        for (ulong f = 1; f <= 2000; f++) Run(f, f == 1500);   // прогрев: и одна аномалия
        Assert.True(a.Flush(TimeSpan.FromSeconds(10)));
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (ulong f = 2001; f <= 5000; f++) Run(f, f is 3000 or 4000);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(a.Flush(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, bytes);
        Assert.True(a.List().Count(x => x.ZoneName == "slow") >= 3);
        Assert.Contains(a.List(), x => x.Number == 3250 && x.TickTrigger);   // тик кадра инструментов — судится
        a.Dispose();
    }

    [Fact]
    public void Simulation_WithCaptureEveryTick_SameHashes()
    {
        var plain = SpinModule.Build();
        var profiled = SpinModule.Build();
        var p = new CpuProfiler(64);
        profiled.AttachProfiler(p);
        var a = p.CaptureAnomalies(new AnomalyOptions { Directory = _dir, AbsoluteMs = 0.00001, MaxFiles = 3, MaxFilesGpuWait = 0, MaxFilesMachine = 0 });
        p.SetEnabled(true);
        Span<ulong> x = stackalloc ulong[plain.StateParts.Count], y = stackalloc ulong[profiled.StateParts.Count];
        for (int t = 0; t < 120; t++)
        {
            plain.Step();
            profiled.Step();
            Assert.Equal(plain.HashState(x), profiled.HashState(y));
        }
        Assert.True(a.Flush(TimeSpan.FromSeconds(10)));
        Assert.True(a.Detected >= 80);
        // Лимит общего места — 3 (у «машины» файлов нет: класс коротких тиков — шум, бывает «машина» у многих).
        var list = a.List();
        Assert.Equal(Math.Min(3, list.Count(x => x.Class is not (AnomalyClass.Machine or AnomalyClass.GpuWait))), Files().Length);
        Assert.Equal(list.Count(x => x.File == AnomalyFile.Written), Files().Length);
        a.Dispose();
    }

    [Fact]
    public void RingTrace_ValidJson_XNested_FramesAndTicks()
    {
        var p = new CpuProfiler(64);
        p.SetEnabled(true);
        int outer = p.Zone("outer"), inner = p.Zone("inner"), other = p.Zone("other"), sys = p.Zone("sys");
        ulong tick = 0;
        for (ulong f = 1; f <= 80; f++)
        {
            p.BeginFrame(f);
            p.Begin(outer);
            p.Begin(inner);
            SpinCpu(0.02);
            p.End(inner);
            p.Begin(inner);
            p.End(inner);
            p.BeginTick(tick++);
            p.Begin(sys);
            p.End(sys);
            p.EndTick();
            p.End(outer);
            p.Begin(other);
            p.End(other);
            p.EndFrame(0.25f, false);
        }
        string path = p.WriteTrace(Path.Combine(_dir, "trace.json"));
        var trace = Parse(path);
        Assert.Equal(64, trace.Count(e => e.Ph == "X" && e.Tid == ChromeTrace.FramesTid && e.Name.StartsWith("frame ", StringComparison.Ordinal)));
        Assert.Equal(64, trace.Count(e => e.Ph == "X" && e.Tid == ChromeTrace.TicksTid && e.Name.StartsWith("tick ", StringComparison.Ordinal)));
        Assert.Contains(trace, e => e.Name == "inner" && e.Args?["calls"]?.GetValue<int>() == 2);
        Assert.Contains(trace, e => e.Name == "sys" && e.Tid == ChromeTrace.FramesTid);   // тик кадра — внутри кадра
        CheckNesting(trace);
    }

    // ── Chrome Trace: разбор и вложенность ─────────────────────────────────

    private sealed record TraceEvent(string Name, string Ph, int Pid, int Tid, double Ts, double Dur, JsonObject? Args);

    private static List<TraceEvent> Parse(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        Assert.Equal("ms", root.GetProperty("displayTimeUnit").GetString());
        var list = new List<TraceEvent>();
        foreach (var e in root.GetProperty("traceEvents").EnumerateArray())
        {
            string ph = e.GetProperty("ph").GetString()!;
            Assert.Contains(ph, (string[])["X", "B", "E", "M", "i"]);
            list.Add(new TraceEvent(e.TryGetProperty("name", out var n) ? n.GetString()! : "", ph, e.GetProperty("pid").GetInt32(),
                e.GetProperty("tid").GetInt32(), e.TryGetProperty("ts", out var ts) ? ts.GetDouble() : 0,
                e.TryGetProperty("dur", out var dur) ? dur.GetDouble() : 0,
                e.TryGetProperty("args", out var args) ? JsonNode.Parse(args.GetRawText())!.AsObject() : null));
        }
        return list;
    }

    /// <summary>На каждом треке: «X» — дочерние целиком внутри родителя (стеком по началу); «B»/«E» — парны, конец не раньше начала.</summary>
    private static void CheckNesting(List<TraceEvent> trace)
    {
        const double eps = 1e-6;
        foreach (var track in trace.Where(e => e.Ph == "X").GroupBy(e => (e.Pid, e.Tid)))
        {
            var stack = new Stack<TraceEvent>();
            foreach (var e in track.OrderBy(e => e.Ts).ThenByDescending(e => e.Dur))
            {
                Assert.True(e.Dur >= 0);
                while (stack.Count > 0 && stack.Peek().Ts + stack.Peek().Dur <= e.Ts + eps) stack.Pop();
                if (stack.Count > 0)
                {
                    var parent = stack.Peek();
                    Assert.True(e.Ts + e.Dur <= parent.Ts + parent.Dur + eps,
                        $"{e.Name} [{e.Ts}, {e.Ts + e.Dur}] выходит за {parent.Name} [{parent.Ts}, {parent.Ts + parent.Dur}] (pid {e.Pid}, tid {e.Tid})");
                }
                stack.Push(e);
            }
        }
        foreach (var track in trace.Where(e => e.Ph is "B" or "E").GroupBy(e => (e.Pid, e.Tid)))
        {
            var open = new Stack<TraceEvent>();
            foreach (var e in track)
            {
                if (e.Ph == "B") open.Push(e);
                else
                {
                    Assert.NotEmpty(open);
                    Assert.True(e.Ts + eps >= open.Pop().Ts);
                }
            }
            Assert.Empty(open);
        }
    }

    [Fact]
    public void AnomalyTrace_RingAndTimeline_Nested()
    {
        var p = new CpuProfiler(64);
        var a = p.CaptureAnomalies(new AnomalyOptions { Directory = _dir });
        int outer = p.Zone("outer"), inner = p.Zone("inner"), slow = p.Zone("slow");
        p.SetEvents(true);
        try
        {
            for (ulong f = 1; f <= 100; f++)
            {
                p.BeginFrame(f);
                p.Begin(outer);
                p.Begin(inner);
                p.End(inner);
                if (f == 90)
                {
                    p.Begin(slow);
                    SpinCpu(5);
                    p.End(slow);
                }
                p.End(outer);
                p.EndFrame(1, false);
            }
            Assert.True(a.Flush(TimeSpan.FromSeconds(10)));
            var anomaly = Assert.Single(a.List(), x => x.Number == 90);
            var json = ReadJson(anomaly.Path!);
            var trace = Parse(Path.Combine(_dir, json["trace"]!.GetValue<string>()));
            Assert.Contains(trace, e => e.Ph == "X" && e.Name == "frame 90" && e.Args?["anomaly"]?.GetValue<bool>() == true);
            Assert.Contains(trace, e => e.Ph == "X" && e.Name == "slow" && e.Pid == ChromeTrace.RingPid);
            Assert.Contains(trace, e => e.Ph == "B" && e.Name == "frame 90" && e.Pid == ChromeTrace.TimelinePid);
            Assert.Contains(trace, e => e.Ph == "B" && e.Name == "slow" && e.Pid == ChromeTrace.TimelinePid);
            CheckNesting(trace);
        }
        finally
        {
            p.SetEvents(false);
            a.Dispose();
        }
    }

    /// <summary>Сводка F2 — по своему времени зон; на поддельных часах (снятие потока с процессора не переставит строки).</summary>
    [Fact]
    public void Digest_TopZonesBySelfTime_NoAllocations()
    {
        var p = new CpuProfiler(64, _clock);
        p.SetEnabled(true);
        int big = p.Zone("big"), small = p.Zone("small"), child = p.Zone("child");
        var digest = new ProfileDigest(p);
        long now = _clock.Now;
        void Frames(int n)
        {
            for (int i = 0; i < n; i++)
            {
                p.BeginFrame((ulong)i);
                p.Begin(big);
                p.Begin(child);
                _clock.Run(0.2);
                p.End(child);
                _clock.Run(0.05);
                p.End(big);
                p.Begin(small);
                _clock.Run(0.1);
                p.End(small);
                p.EndFrame(1, false);
            }
        }
        Frames(5);
        digest.Update(now);   // первое окно: запомнить суммы
        Frames(20);
        now += Stopwatch.Frequency;
        digest.Update(now);
        Assert.True(digest.Ready);
        Assert.Equal(20, digest.Frames);
        Assert.Equal("child", digest.Name(0));   // своё время: child 0,2 > small 0,1 > big 0,05
        Assert.Equal("small", digest.Name(1));
        Assert.Equal("big", digest.Name(2));
        Assert.InRange(digest.Ms(0), 0.15, 5);
        Assert.Equal((0.2, 0.1, 0.05), (Math.Round(digest.Ms(0), 3), Math.Round(digest.Ms(1), 3), Math.Round(digest.Ms(2), 3)));
        Assert.True(digest.FrameMs >= digest.Ms(0) + digest.Ms(1));
        Assert.Equal(0.35, digest.FrameMs, 3);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) digest.Update(now + i);   // в окне — только сравнение времени
        now += Stopwatch.Frequency;
        Frames(3);
        digest.Update(now);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(3, digest.Frames);
        p.SetEnabled(false);
        p.BeginFrame(99);
        p.EndFrame(1, false);
        digest.Update(now + Stopwatch.Frequency);
        Assert.False(digest.Ready);
    }
}

/// <summary>
/// Тесты со слушателем событий среды (<c>SetEvents</c>) — не параллельно друг другу: слушатели одного процесса делят один сеанс
/// EventPipe, и подключение или отключение слушателя перезапускает его — события другого теста в этот момент теряются
/// (найдено на Пв: событие JIT пропадало из файла аномалии, когда рядом шли тесты Пб).
/// </summary>
[CollectionDefinition(Name)]
public sealed class RuntimeEventsCollection
{
    public const string Name = "runtime-events";
}

/// <summary>Доводки вердикта Пб на Пв: фоновый JIT вердикт не меняет; предел ОС — против своей базы процессора (гибридные CPU).</summary>
public class MachineVerdictRefinementTests
{
    private static MachineSample Sample(double seconds, double cpuSeconds, long jitMain = 0, long jitProcess = 0, int limit = 4201, int limitBase = 4201) =>
        new((long)(seconds * Stopwatch.Frequency), (long)(seconds * 9_000), (long)(seconds * 10_000), 0, (long)(seconds * 800),
            new ProfileCounters((long)(cpuSeconds * ThreadClock.CyclesPerSecond), 0, 0, 0, 0, 0, jitMain, jitProcess, jitMain * 1000, jitProcess * 1000),
            0, 4800, 4201, 4201, limit, limitBase);

    [Fact]
    public void BackgroundJit_Reason_NotVerdict_MainThreadJit_Runtime()
    {
        var start = Sample(0, 0);
        var background = MachineReport.Between(start, Sample(1, 0.99, jitMain: 0, jitProcess: 40), computeBound: true);
        Assert.Equal(MachineVerdict.Code, background.Verdict);
        Assert.Contains(background.Reasons, r => r.StartsWith("фоновый JIT во время замера: 40 методов", StringComparison.Ordinal));
        Assert.Contains("фоновый JIT", background.Text);
        Assert.StartsWith("код:", background.Text);

        var main = MachineReport.Between(start, Sample(1, 0.99, jitMain: 2, jitProcess: 40), computeBound: true);
        Assert.Equal(MachineVerdict.Runtime, main.Verdict);
        Assert.Contains(main.Reasons, r => r.StartsWith("JIT во время замера на главном потоке: 2 метода", StringComparison.Ordinal));
        Assert.Contains(main.Reasons, r => r.StartsWith("фоновый JIT во время замера: 38 методов", StringComparison.Ordinal));
    }

    [Fact]
    public void OsLimit_AgainstOwnBase_HybridCpu()
    {
        // Гибридный: 8 P-ядер (база 3600) и 16 E-ядер (база 2500), предел у всех — своя база: ограничения нет.
        var hybrid = Enumerable.Range(0, 24).Select(i => i < 8 ? new ProcessorPower(3600, 3600, 3600) : new ProcessorPower(2500, 2500, 2500)).ToArray();
        var free = Machine.Summarize(hybrid);
        Assert.Equal(3600, free.Base);
        Assert.Equal(free.LimitBase, free.Limit);
        // Старое сравнение (наименьший предел против наибольшей базы) назвало бы это ограничением: 2500 < 3600.
        var report = MachineReport.Between(Sample(0, 0, limit: free.Limit, limitBase: free.LimitBase),
            Sample(1, 0.99, limit: free.Limit, limitBase: free.LimitBase), computeBound: true);
        Assert.DoesNotContain(report.Reasons, r => r.StartsWith("ОС ограничила", StringComparison.Ordinal));

        // E-ядра ограничены до 1800 из своих 2500 — ограничение, против их базы.
        hybrid[20] = new ProcessorPower(2500, 1800, 1800);
        var limited = Machine.Summarize(hybrid);
        Assert.Equal((1800, 2500), (limited.Limit, limited.LimitBase));
        var report2 = MachineReport.Between(Sample(0, 0, limit: limited.Limit, limitBase: limited.LimitBase),
            Sample(1, 0.99, limit: limited.Limit, limitBase: limited.LimitBase), computeBound: true);
        Assert.Equal(MachineVerdict.Machine, report2.Verdict);
        Assert.Contains(report2.Reasons, r => r == "ОС ограничила частоту CPU: 1800 из 2500 МГц");

        // P-ядро ограничено до 3000 из 3600 при свободных E-ядрах — тоже.
        hybrid[20] = new ProcessorPower(2500, 2500, 2500);
        hybrid[3] = new ProcessorPower(3600, 3000, 3000);
        Assert.Equal((3000, 3600), (Machine.Summarize(hybrid).Limit, Machine.Summarize(hybrid).LimitBase));
        Assert.Equal(default, Machine.Summarize([]));
    }
}
