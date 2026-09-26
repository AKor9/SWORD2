using System.Numerics;
using System.Runtime.InteropServices;
using Sword.Engine.Client;
using Sword.Engine.Diagnostics;
using Sword.Engine.Platform;
using Sword.Engine.Platform.Win32;
using Sword.Engine.Render;
using Sword.Engine.Runtime;
using Sword.Game.Animation;
using Sword.Game.Bootstrap;
using Sword.Game.Client;
using Sword.Game.Client.Scene;
using Sword.Game.Contracts;
using Xunit.Abstractions;

namespace Sword.Game.Tests;

/// <summary>
/// Ручки «Свет» на арене (настоящее устройство с GPU-валидацией, Manny из пака, 1280×720): строки группы меняют значения клиента и
/// рендера со следующего кадра; ночь — кадр много темнее, фон — проходом (на вызов больше), выключенная ночь — кадр бит в бит дневной;
/// тестовый факел ночью — тёплое пятно; переключения — без выделений сверх кадра без них и без команд симуляции (реплей — только
/// команды игрока). Сохранение группы без ручек симуляции файла не трогает. Категории Gpu и Content.
/// </summary>
[Collection("game-gpu")]
public sealed class ArenaLightTuningGpuTests(ITestOutputHelper output) : IDisposable, ILogSink
{
    private const int W = 1280, H = 720;
    private readonly List<string> _d3d12 = [];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sword-arena-light-{Guid.NewGuid():N}");

    void ILogSink.Write(in LogEntry entry)
    {
        if (entry.Category == "d3d12" && entry.Level >= LogLevel.Warning) _d3d12.Add(entry.Message);
    }

    public void Dispose()
    {
        Log.RemoveSink(this);
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    [Trait("Category", "Gpu")]
    [Trait("Category", "Content")]
    public void LightGroup_NightDarkAndReversible_TorchSpot_KnobsNextFrame_NoAllocationsNoCommands()
    {
        Log.AddSink(this);
        Directory.CreateDirectory(_dir);
        using var window = new Win32Window(new WindowOptions("Sword.Game.Tests.LightTuning", W, H, Activate: false));
        var sim = SwordSimulation.Create(ConfigSet.Empty("arena.enemies=3"), CharacterAssets.LoadFromPak());
        var recorder = sim.Record(new ReplayInfo("test", "tests", "none"));
        var clock = new SimulationClock(sim, (link, _) => link.Enqueue(new PlayerCommand { Move = new Vector2(1, 0.3f) }), ticksPerFrame: 2);
        var arena = new ArenaClient(new CameraPose(0.6f, 0.95f, 12f), hud: false);
        var contract = new ShaderContract();
        Renderer.Contribute(contract);
        arena.ContributeShaders(contract);
        using var renderer = new Renderer(new RendererOptions(window.NativeHandle, W, H, DebugLayer: true,
            ShaderRoot: Path.Combine(AppContext.BaseDirectory, "Shaders"), GpuValidation: true), contract);
        arena.Initialize(renderer, clock.Link);
        for (int i = 0; i < 30; i++)
        {
            clock.RunFrame(0);
            arena.Extract(new FrameView(1, 2, 0, default));
            window.PumpMessages();
            renderer.Render(arena.BuildFrame, vsync: false);
        }

        // Группа: вкладка «Свет», ключи по порядку, значения по умолчанию — дневные.
        var group = arena.Light!;
        Assert.Contains(group, arena.TuningGroups);
        Assert.Equal((LightTuning.Id, LightTuning.Title), (group.Id, group.Title));
        Assert.Equal(LightTuning.Keys, group.Values().Keys);
        Assert.Equal(0, group.Changed());
        TuningBool Flag(string key) => Assert.IsType<TuningBool>(group.Find(key));
        TuningFloat Number(string key) => Assert.IsType<TuningFloat>(group.Find(key));

        // Кадр без новых тиков — та же сцена; вернёт сырой цвет сцены (half RGBA).
        Half[] Scene(string name)
        {
            string hdr = Path.Combine(_dir, name + ".hdr.png");
            window.PumpMessages();
            renderer.Render(arena.BuildFrame, vsync: false, new FrameCapture(Path.Combine(_dir, name + ".png")) { HdrPath = hdr });
            return MemoryMarshal.Cast<byte, Half>(File.ReadAllBytes(Path.ChangeExtension(hdr, ".bin"))).ToArray();
        }
        static double Mean(Half[] c)
        {
            double sum = 0;
            for (int p = 0; p < c.Length; p += 4)
                sum += 0.2126 * (float)c[p] + 0.7152 * (float)c[p + 1] + 0.0722 * (float)c[p + 2];
            return sum / (c.Length / 4);
        }

        var day = Scene("day");
        int dayDraws = renderer.Graph.LastFrameDraws;
        Flag(LightTuning.Night).Set(true);
        Assert.True(arena.Night);
        var night = Scene("night");
        Assert.Equal(dayDraws + 1, renderer.Graph.LastFrameDraws);   // фон ночи — проходом
        double dayMean = Mean(day), nightMean = Mean(night);
        output.WriteLine($"средняя яркость сцены: день {dayMean:0.####}, ночь {nightMean:0.####} (×{dayMean / nightMean:0.#})");
        Assert.True(nightMean < dayMean / 10, $"ночь {nightMean}, день {dayMean}");

        // Тестовый факел ночью: тёплое пятно у игрока (красный растёт, синий тот же — у факела синего нет).
        Flag(LightTuning.TestLight).Set(true);
        Assert.Equal(1, arena.TestLights);
        var torch = Scene("torch");
        int warm = 0;
        for (int p = 0; p < W * H; p++)
        {
            Assert.Equal(night[p * 4 + 2], torch[p * 4 + 2]);
            if ((float)torch[p * 4] - (float)night[p * 4] > 0.05f) warm++;
        }
        output.WriteLine($"факел ночью: пикселей с прибавкой красного > 0.05 — {warm} ({100.0 * warm / (W * H):0.##} %), средняя {Mean(torch):0.####}");
        Assert.True(warm > 1000, $"пятно факела — {warm} пикселей");

        // Ручки рендера — значения со следующего кадра.
        Number(LightTuning.Exposure).Set(2f);
        Flag(LightTuning.Bloom).Set(false);
        Number(LightTuning.BloomIntensity).Set(20f);
        Number(LightTuning.BloomThreshold).Set(0.5f);
        Assert.Equal(2f, renderer.Exposure);
        Assert.Equal(new BloomSettings(false, 20f, 0.5f), renderer.Bloom);
        Scene("knobs");
        Assert.Equal(2f, renderer.LastCapture!.Hdr!.Exposure);
        Assert.Null(renderer.LastCapture.Hdr.Bloom);
        Assert.Equal(6, group.Changed());

        // «Сброс» — к дневному: кадр бит в бит первый (ночь обратима, ресурсы те же).
        group.ResetAll();
        Assert.Equal((false, 0, Renderer.DefaultExposure, BloomSettings.Default), (arena.Night, arena.TestLights, renderer.Exposure, renderer.Bloom));
        var back = Scene("back");
        Assert.True(day.AsSpan().SequenceEqual(back), "день после ночи и ручек — не тот же кадр");
        Assert.Equal(dayDraws, renderer.Graph.LastFrameDraws);

        // Переключения каждый кадр — без выделений сверх кадра без них.
        long Bytes(int frames, Action<int> toggle)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < frames; i++)
            {
                toggle(i);
                renderer.Render(arena.BuildFrame, vsync: false);
            }
            return (GC.GetAllocatedBytesForCurrentThread() - before) / frames;
        }
        Bytes(20, _ => { });
        long steady = Bytes(200, _ => { });
        long toggled = Bytes(200, i =>
        {
            Flag(LightTuning.Night).Set(i % 2 == 0);
            Flag(LightTuning.TestLight).Set(i % 3 == 0);
            Number(LightTuning.Exposure).Set(i % 2 == 0 ? 1.5f : 0.99f);
            Flag(LightTuning.Bloom).Set(i % 4 != 0);
            Number(LightTuning.BloomIntensity).Set(5 + i % 7);
            Number(LightTuning.BloomThreshold).Set(0.5f + i % 3);
        });
        output.WriteLine($"выделения на кадр: без переключений {steady} Б, с переключениями {toggled} Б");
        Assert.True(toggled <= steady, $"переключения выделяют: {toggled} Б на кадр против {steady}");

        // Симуляция ручек не видит: тики после переключений — в реплее только команды игрока.
        group.ResetAll();
        for (int i = 0; i < 5; i++)
        {
            Flag(LightTuning.Night).Set(i % 2 == 0);
            clock.RunFrame(0);
        }
        var replay = Replay.Parse(recorder.ToReplay().ToJson());
        int player = replay.CommandTypes.ToList().FindIndex(t => t.Name == typeof(PlayerCommand).FullName);
        int commands = 0;
        for (ulong t = 0; t < replay.Ticks; t++)
            foreach (var c in replay.CommandsAt(t))
            {
                Assert.Equal(player, c.Type);
                commands++;
            }
        Assert.Equal((int)replay.Ticks, commands);   // по команде игрока на тик — и ничего больше
        Assert.True(_d3d12.Count == 0, "сообщения отладочного слоя D3D12:\n" + string.Join("\n", _d3d12));
    }

    [Fact]
    public void ClientGroup_SaveLoad_DoNotTouchUserFile()
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, ConfigSet.UserFileName);
        var binding = new TuningBinding(new SimulationLink(TestAssets.Simulation(ConfigSet.Empty())));
        float value = 1;
        var group = new TuningGroup(LightTuning.Id, LightTuning.Title).Float(LightTuning.Exposure, "экспозиция", () => value, v => value = v, 0, 8);
        Assert.False(binding.HasKnobs(group));
        Assert.Contains("ручки клиента", binding.Save(group, file));
        Assert.Contains("ручки клиента", binding.Load(group, file));
        Assert.False(File.Exists(file));
        Assert.Equal(1, value);
    }
}
