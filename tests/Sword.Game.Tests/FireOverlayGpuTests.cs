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
using Sword.Game.Fire;

namespace Sword.Game.Tests;

/// <summary>
/// Оверлеи модуля огня на настоящем устройстве (слой отладки D3D12 с GPU-валидацией) и паке: клиент арены и клиентские модули в одном
/// кадре, как у <c>ClientHost</c> (проходы модулей — после проходов игры, своей фазы). Факел пака в руке игрока (<c>fire.weapons</c> по
/// умолчанию) и меч из конфига — головки, поле, пламя, дым и окно газа нарисованы на экране, снимок кадра кладёт рядом поля огня и срезы
/// газа; горящее оружие не в руке — головок нет; оружия нет в паке — имя в <c>missing</c>. Категории Gpu и Content.
/// </summary>
[Collection("game-gpu")]
[Trait("Category", "Gpu")]
[Trait("Category", "Content")]
public sealed class FireOverlayGpuTests : IDisposable, ILogSink
{
    private const int W = 640, H = 480;
    private readonly Win32Window _window;
    private readonly List<string> _d3d12 = [];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sword-fire-{Guid.NewGuid():N}");

    public FireOverlayGpuTests()
    {
        Log.AddSink(this);
        _window = new Win32Window(new WindowOptions("Sword.Game.Tests.Fire", W, H, Activate: false));
        Directory.CreateDirectory(_dir);
    }

    void ILogSink.Write(in LogEntry entry)
    {
        if (entry.Category == "d3d12" && entry.Level >= LogLevel.Warning) _d3d12.Add(entry.Message);
    }

    public void Dispose()
    {
        _window.Dispose();
        Log.RemoveSink(this);
        if (Environment.GetEnvironmentVariable("SWORD_TEST_KEEP") == "1") return;
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void FireOverlays_OnTorchAndSword_Drawn_FieldsBesideShot_NoFire()
    {
        Run("torch", "torch", "[\"torch\"]", burning: true);
        Run("sword", "sword_1h", "[\"sword_1h\"]", burning: true);
        Run("idle", "sword_1h", "[\"torch\", \"axe\"]", burning: false);
        Assert.True(_d3d12.Count == 0, "сообщения отладочного слоя D3D12:\n" + string.Join("\n", _d3d12));
    }

    private void Run(string name, string playerWeapon, string weapons, bool burning)
    {
        var config = ConfigSet.Empty("arena.enemies=0", $"combat.playerWeapon={playerWeapon}", $"fire.weapons={weapons}");
        var sim = SwordSimulation.Create(config, CharacterAssets.LoadFromPak());
        var fire = new FireModule();
        var modules = ClientModules.Compose(config, sim, [fire], new ClientServices().Add<IWeaponTips>(link => new ArenaWeaponTips(link)));
        var clock = new SimulationClock(sim, (_, _) => { }, ticksPerFrame: 2, afterTick: modules.AfterTick);
        var arena = new ArenaClient(new CameraPose(0.6f, 0.8f, 5f, 0, 0), hud: false);
        var contract = new ShaderContract();
        Renderer.Contribute(contract);
        arena.ContributeShaders(contract);
        modules.ContributeShaders(contract);
        var renderer = new Renderer(new RendererOptions(_window.NativeHandle, W, H, DebugLayer: true,
            ShaderRoot: Path.Combine(AppContext.BaseDirectory, "Shaders"), GpuValidation: true), contract);
        try
        {
            arena.Initialize(renderer, clock.Link);
            modules.Initialize(renderer, () => ((IClientObservable)arena).Camera);
            void Build(RenderFrame f)
            {
                modules.AddLights(f.Lights);
                arena.BuildFrame(f);
                modules.BuildFrame(f);
            }
            for (int i = 0; i < 20; i++)
            {
                modules.BeginFrame(sim.Tick);
                clock.RunFrame(0);
                var view = new FrameView(1, 2, 0, default);
                arena.Extract(view);
                modules.Extract(view);
                _window.PumpMessages();
                renderer.Render(Build, vsync: false);
            }
            fire.Overlay!.Shown = true;
            fire.FieldOverlay!.Shown = true;
            fire.FlameOverlay!.Shown = true;
            fire.SmokeOverlay!.Shown = true;
            fire.GasOverlay!.Shown = true;
            var capture = new FrameCapture(Path.Combine(_dir, $"{name}.png"));
            modules.BeginFrame(sim.Tick);
            var shot = new FrameView(1, 0, 0, default);
            arena.Extract(shot);
            modules.Extract(shot);
            renderer.Render(Build, vsync: false, capture);
            Assert.True(File.Exists(capture.ColorPath));
            var report = Assert.Single(modules.Report());
            if (burning)
            {
                Assert.Equal(2, fire.DrawnPoints);   // головки последнего кадра с тиками: 2 тика на кадр
                Assert.Equal(1, fire.DrawnFields);
                Assert.Equal(playerWeapon, Assert.Single(fire.Fires!.Fires.ToArray()).WeaponName);
                Assert.Contains("debug.fire", report.Passes);
                Assert.Contains("debug.fire.field", report.Passes);
                Assert.Contains("fluid.export", report.Passes);
                Assert.Contains(FireFlame.PassName, report.Passes);
                Assert.Contains("debug.fire.flame", report.Passes);
                Assert.Equal(1, fire.Flame!.LastQuads);
                Assert.Contains(FireSmoke.PassName, report.Passes);
                Assert.Contains("debug.fire.smoke", report.Passes);
                Assert.Equal(1, fire.Smoke!.LastVolumes);
                Assert.Contains(FireSmoke.BricksPassName, report.Passes);
                Assert.Contains("debug.fire.gas", report.Passes);
            }
            else
            {
                Assert.Equal(0, fire.DrawnPoints);
                Assert.Equal(["axe"], fire.Fires!.Missing);   // факел в паке есть, но не в руке
            }
        }
        finally
        {
            renderer.WaitForIdle();
            modules.Dispose();
            arena.Dispose();
            renderer.Dispose();
        }
        // Поля огня снимка последнего кадра — при выходе, до освобождения полей: плотность, скорость, сырые half и кисть.
        foreach (string file in new[] { ".fire.density.png", ".fire.velocity.png", ".fire.bin", ".fire.brush.density.png" })
            Assert.Equal(burning, File.Exists(Path.Combine(_dir, name + file)));
        Assert.Equal(burning, fire.Sims[0].FieldHash is not null);
        // Срезы газа — через центроид дыма: XY и XZ (плотность, температура, скорость, сырые half).
        foreach (string file in new[] { ".fire.gas.xy.density.png", ".fire.gas.xy.velocity.png", ".fire.gas.xz.density.png", ".fire.gas.xz.bin" })
            Assert.Equal(burning, File.Exists(Path.Combine(_dir, name + file)));
        Assert.Equal(burning, fire.GasSims[0].FieldHash is not null);
    }
}
