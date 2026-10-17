using System.Numerics;
using System.Runtime.InteropServices;
using Sword.Engine.Client;
using Sword.Engine.Diagnostics;
using Sword.Engine.Platform;
using Sword.Engine.Platform.Win32;
using Sword.Engine.Render;
using Sword.Engine.Runtime;
using Sword.Game.Animation;
using Sword.Game.Arena;
using Sword.Game.Bootstrap;
using Sword.Game.Client;
using Sword.Game.Client.Scene;
using Sword.Game.Contracts;
using Xunit.Abstractions;

namespace Sword.Game.Tests;

/// <summary>
/// Освещение арены (устройство, Manny из пака, категории Gpu и Content): тени солнца и GTAO только гасят — ни один пиксель сцены не
/// светлее, чем без них; земля за колонной по свету — в тени, стыки — темнее от GTAO; открытая земля — как без них; блики материалов
/// только прибавляют свет диэлектрикам.
/// </summary>
[Collection("game-gpu")]
public sealed class ArenaLightingGpuTests(ITestOutputHelper output) : IDisposable, ILogSink
{
    private const int W = 1280, H = 720;
    private readonly List<string> _d3d12 = [];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sword-arena-shadow-{Guid.NewGuid():N}");

    void ILogSink.Write(in LogEntry entry)
    {
        if (entry.Category == "d3d12" && entry.Level >= LogLevel.Warning) _d3d12.Add(entry.Message);
    }

    public void Dispose()
    {
        Log.RemoveSink(this);
        try
        {
            if (Directory.Exists(_dir) && Environment.GetEnvironmentVariable("SWORD_TEST_KEEP") != "1") Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Арена без врагов, камера на колонне 0; кадры — сырой цвет HDR-сцены (half RGBA), до тонмапа.</summary>
    private sealed class Rig : IDisposable
    {
        private readonly Win32Window _window;
        private readonly SimulationClock _clock;
        private readonly string _dir;
        public readonly ArenaClient Arena;
        public readonly Renderer Renderer;

        public Rig(string dir, CameraPose pose)
        {
            _dir = dir;
            _window = new Win32Window(new WindowOptions("Sword.Game.Tests.Lighting", W, H, Activate: false));
            var sim = SwordSimulation.Create(ConfigSet.Empty("arena.enemies=0"), CharacterAssets.LoadFromPak());
            _clock = new SimulationClock(sim, (_, _) => { }, ticksPerFrame: 1);
            Arena = new ArenaClient(pose, hud: false);
            var contract = new ShaderContract();
            Renderer.Contribute(contract);
            Arena.ContributeShaders(contract);
            Renderer = new Renderer(new RendererOptions(_window.NativeHandle, W, H, DebugLayer: true,
                ShaderRoot: Path.Combine(AppContext.BaseDirectory, "Shaders"), GpuValidation: true), contract);
            Arena.Initialize(Renderer, _clock.Link);
            // Кадры сравниваются между собой: дрожание камеры TAA меняло бы сцену от кадра к кадру.
            Renderer.Taa.Enabled = false;
            Renderer.Smaa = false;
            for (int i = 0; i < 3; i++)
            {
                _clock.RunFrame(0);
                Arena.Extract(new FrameView(1, 1, 0, default));
                _window.PumpMessages();
                Renderer.Render(Arena.BuildFrame, vsync: false);
            }
        }

        /// <summary>
        /// Та же сцена без новых тиков (настройки — со следующего кадра): кадр прогрева и снимок; <paramref name="extra"/> — добавка к
        /// кадру после арены (свет, как у модулей).
        /// </summary>
        public Half[] Scene(string name, Action<RenderFrame>? extra = null)
        {
            void Build(RenderFrame f)
            {
                Arena.BuildFrame(f);
                extra?.Invoke(f);
            }
            _window.PumpMessages();
            Renderer.Render(Build, vsync: false);
            string hdr = Path.Combine(_dir, name + ".hdr.png");
            Renderer.Render(Build, vsync: false, new FrameCapture(Path.Combine(_dir, name + ".png")) { HdrPath = hdr });
            return MemoryMarshal.Cast<byte, Half>(File.ReadAllBytes(Path.ChangeExtension(hdr, ".bin"))).ToArray();
        }

        public int Pixel(Vector3 world)
        {
            var c = Vector4.Transform(new Vector4(world, 1), Arena.Camera.ViewProj);
            int x = (int)((c.X / c.W * 0.5f + 0.5f) * W), y = (int)((0.5f - c.Y / c.W * 0.5f) * H);
            Assert.InRange(x, 0, W - 1);
            Assert.InRange(y, 0, H - 1);
            return y * W + x;
        }

        public static float Luma(Half[] c, int p) => 0.2126f * (float)c[p * 4] + 0.7152f * (float)c[p * 4 + 1] + 0.0722f * (float)c[p * 4 + 2];

        /// <summary>Пикселей, которые в <paramref name="on"/> светлее, чем в <paramref name="off"/> (сверх округления half).</summary>
        public static int Brighter(Half[] on, Half[] off)
        {
            int n = 0;
            for (int p = 0; p < W * H; p++)
                if (Luma(on, p) > Luma(off, p) * 1.001f + 1e-4f) n++;
            return n;
        }

        public void Dispose()
        {
            Renderer.Dispose();
            _window.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "Gpu")]
    [Trait("Category", "Content")]
    public void SunShadow_OnlyDarkens_GroundBehindPillarShadowed_OtherSideUntouched()
    {
        Log.AddSink(this);
        Directory.CreateDirectory(_dir);
        var pillar = ArenaLayout.Pillars[0];
        var basePoint = new Vector3(pillar.X, 0, pillar.Y);
        // Почти сверху: тень колонны (~3.5 м по свету) не заслонена самой колонной. GTAO выключено — сравнивается только тень.
        using var rig = new Rig(_dir, new CameraPose(0.6f, RtsCamera.MaxPitch, 16f, pillar.X, pillar.Y));
        Assert.True(rig.Renderer.Shadow.Enabled);
        rig.Renderer.Ao.Enabled = false;

        var on = rig.Scene("on");
        Assert.Contains(rig.Renderer.Graph.LastFrame, p => p.Name == "scene.shadow.near");
        rig.Renderer.Shadow.Enabled = false;
        var off = rig.Scene("off");
        Assert.DoesNotContain(rig.Renderer.Graph.LastFrame, p => p.Name.StartsWith("scene.shadow", StringComparison.Ordinal));

        Assert.Equal(0, Rig.Brighter(on, off));
        int darker = 0;
        for (int p = 0; p < W * H; p++)
            if (Rig.Luma(on, p) < Rig.Luma(off, p) * 0.6f) darker++;
        Assert.True(darker > 2000, $"в тени лишь {darker} пикселей");

        // Земля в 2.8 м от оси колонны по свету (колонна 6.5 м отбрасывает тень на ~3.5 м) и в 2.8 м против света.
        var sun = ArenaClient.SunLight;
        var along = Vector3.Normalize(new Vector3(sun.X, 0, sun.Z));
        int shade = rig.Pixel(basePoint + along * 2.8f), lit = rig.Pixel(basePoint - along * 2.8f);
        float shadeRatio = Rig.Luma(on, shade) / Rig.Luma(off, shade), litRatio = Rig.Luma(on, lit) / Rig.Luma(off, lit);
        output.WriteLine($"в тени пикселей (×0.6 и темнее): {darker}; за колонной ×{shadeRatio:0.###}, перед ней ×{litRatio:0.###}");
        Assert.True(shadeRatio < 0.5f, $"за колонной ×{shadeRatio}");
        Assert.InRange(litRatio, 0.995f, 1.001f);
        Assert.True(_d3d12.Count == 0, string.Join("\n", _d3d12));
    }

    /// <summary>
    /// Блики материалов: диэлектрики (пол, колонны, тела) только светлеют — блик прибавляется к прежнему свету; темнеть может лишь
    /// металл клинка (у него нет рассеянного) — таких пикселей мало; выключенные блики — прежний кадр.
    /// </summary>
    [Fact]
    [Trait("Category", "Gpu")]
    [Trait("Category", "Content")]
    public void Specular_DielectricsOnlyGain_MetalFew_OffRestores()
    {
        Log.AddSink(this);
        Directory.CreateDirectory(_dir);
        using var rig = new Rig(_dir, new CameraPose(0.6f, 0.75f, 7f));
        Assert.True(rig.Arena.Specular);
        var on = rig.Scene("spec.on");
        rig.Arena.Specular = false;
        var off = rig.Scene("spec.off");
        rig.Arena.Specular = true;
        var again = rig.Scene("spec.again");
        Assert.Equal(on, again);

        int brighter = 0, darker = 0;
        for (int p = 0; p < W * H; p++)
        {
            float a = Rig.Luma(on, p), b = Rig.Luma(off, p);
            if (a > b * 1.001f + 1e-5f) brighter++;
            else if (a < b * 0.999f - 1e-5f) darker++;
        }
        output.WriteLine($"блики: светлее {100.0 * brighter / (W * H):0.#} % пикселей, темнее (металл) {100.0 * darker / (W * H):0.##} %");
        Assert.True(brighter > W * H / 2, $"светлее лишь {brighter}");
        Assert.True(darker < W * H / 50, $"темнее {darker} — больше, чем клинков в кадре");
        Assert.True(_d3d12.Count == 0, string.Join("\n", _d3d12));
    }

    /// <summary>
    /// Тени точечного источника: ночью свет в 1 м от боковины колонны — земля за колонной (по лучу от источника) в тени, земля между
    /// источником и колонной — как без теней; ни один пиксель не светлее, чем без них.
    /// </summary>
    [Fact]
    [Trait("Category", "Gpu")]
    [Trait("Category", "Content")]
    public void PointShadow_GroundBehindPillarFromLightShadowed_LitSideUntouched()
    {
        Log.AddSink(this);
        Directory.CreateDirectory(_dir);
        var pillar = ArenaLayout.Pillars[0];
        var axis = new Vector3(pillar.X, 0, pillar.Y);
        var toLight = Vector3.Normalize(new Vector3(1, 0, 0.4f));
        // Камера сверху над землёй за колонной: колонна в перспективе уходит от центра кадра и землю за собой не закрывает.
        var look = axis - toLight * 3f;
        using var rig = new Rig(_dir, new CameraPose(0.6f, RtsCamera.MaxPitch, 14f, look.X, look.Z));
        rig.Arena.Night = true;
        rig.Renderer.AutoExposure.Enabled = false;
        rig.Renderer.Ao.Enabled = false;
        Assert.True(rig.Renderer.Lights.Shadows);
        var light = new PointLight(axis + toLight * (pillar.Z + 1f) + new Vector3(0, 2f, 0), new Vector3(1, 0.6f, 0.3f), 40f, 10f,
            LightFalloff.Exponent, 4f) { CastShadows = true };
        void AddLight(RenderFrame f) => f.Lights.Add(light);

        var on = rig.Scene("point.on", AddLight);
        Assert.Equal(1, rig.Renderer.Lights.Shadowed);
        rig.Renderer.Lights.Shadows = false;
        var off = rig.Scene("point.off", AddLight);
        Assert.Equal(0, rig.Renderer.Lights.Shadowed);

        Assert.Equal(0, Rig.Brighter(on, off));
        int behind = rig.Pixel(axis - toLight * (pillar.Z + 1.2f)), front = rig.Pixel(axis + toLight * (pillar.Z + 0.5f));
        float behindRatio = Rig.Luma(on, behind) / Rig.Luma(off, behind), frontRatio = Rig.Luma(on, front) / Rig.Luma(off, front);
        output.WriteLine($"тень точечного: за колонной ×{behindRatio:0.###}, между светом и колонной ×{frontRatio:0.###}");
        Assert.True(behindRatio < 0.5f, $"за колонной ×{behindRatio}");
        Assert.InRange(frontRatio, 0.98f, 1.001f);
        Assert.True(_d3d12.Count == 0, string.Join("\n", _d3d12));
    }

    /// <summary>
    /// Текстуры материалов (ткань бойцов, сталь клинков): кадр вблизи детальнее — местный контраст выше, а средняя яркость почти та же
    /// (рисунок — отношение к среднему цвету слоя, оттенок бойца сохраняется); выключенные — прежний кадр.
    /// </summary>
    [Fact]
    [Trait("Category", "Gpu")]
    [Trait("Category", "Content")]
    public void MaterialTextures_AddDetail_KeepMeanBrightness_OffRestores()
    {
        Log.AddSink(this);
        Directory.CreateDirectory(_dir);
        using var rig = new Rig(_dir, new CameraPose(0.6f, 0.7f, 4f));
        Assert.True(rig.Arena.MaterialTextures);
        var on = rig.Scene("tex.on");
        rig.Arena.MaterialTextures = false;
        var off = rig.Scene("tex.off");
        rig.Arena.MaterialTextures = true;
        Assert.Equal(on, rig.Scene("tex.again"));

        // Только пиксели, которые изменились, — бойцы и клинки; контраст — разность с правым соседом там, где без текстур поверхность
        // гладкая (не силуэт и не шов меша): рисунок ткани и шлифовки.
        double meanOn = 0, meanOff = 0, detailOn = 0, detailOff = 0;
        int changed = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W - 1; x++)
            {
                int p = y * W + x;
                if (Math.Abs(Rig.Luma(on, p) - Rig.Luma(off, p)) < 1e-4f) continue;
                changed++;
                meanOn += Rig.Luma(on, p);
                meanOff += Rig.Luma(off, p);
                float smooth = Math.Abs(Rig.Luma(off, p + 1) - Rig.Luma(off, p));
                if (smooth > 0.004f) continue;
                detailOn += Math.Abs(Rig.Luma(on, p + 1) - Rig.Luma(on, p));
                detailOff += smooth;
            }
        output.WriteLine($"текстуры: изменилось {changed} пикселей, яркость ×{meanOn / meanOff:0.###}, местный контраст ×{detailOn / detailOff:0.##}");
        Assert.True(changed > 10000, $"текстуры изменили лишь {changed} пикселей");
        Assert.InRange(meanOn / meanOff, 0.85, 1.15);
        Assert.True(detailOn > detailOff * 1.2, $"контраст с текстурами {detailOn:0.#} против {detailOff:0.#}");
        Assert.True(_d3d12.Count == 0, string.Join("\n", _d3d12));
    }

    [Fact]
    [Trait("Category", "Gpu")]
    [Trait("Category", "Content")]
    public void Gtao_OnlyDarkens_JointsDarker_OpenGroundUntouched()
    {
        Log.AddSink(this);
        Directory.CreateDirectory(_dir);
        var pillar = ArenaLayout.Pillars[0];
        var basePoint = new Vector3(pillar.X, 0, pillar.Y);
        using var rig = new Rig(_dir, new CameraPose(0.6f, 0.9f, 12f, pillar.X, pillar.Y));
        Assert.True(rig.Renderer.Ao.Enabled);

        var on = rig.Scene("ao.on");
        Assert.Contains(rig.Renderer.Graph.LastFrame, p => p.Name == "ao.gtao");
        rig.Renderer.Ao.Enabled = false;
        var off = rig.Scene("ao.off");
        Assert.DoesNotContain(rig.Renderer.Graph.LastFrame, p => p.Name.StartsWith("ao.", StringComparison.Ordinal) || p.Name == "scene.prepass");

        Assert.Equal(0, Rig.Brighter(on, off));
        // Стыки (колонна, игрок, кромки ступеней) темнее; открытая земля в 6 м от колонны вбок — как без GTAO. Угол стыка по
        // числам — AoGpuTests рендера (у колонны низ — полусфера: стык с землёй дальше радиуса).
        int darker = 0;
        for (int p = 0; p < W * H; p++)
            if (Rig.Luma(on, p) < Rig.Luma(off, p) * 0.97f) darker++;
        var eye = rig.Arena.Camera.Eye;
        var toCamera = Vector3.Normalize(new Vector3(eye.X - basePoint.X, 0, eye.Z - basePoint.Z));
        int open = rig.Pixel(basePoint + new Vector3(-toCamera.Z, 0, toCamera.X) * 6f);
        float openRatio = Rig.Luma(on, open) / Rig.Luma(off, open);
        output.WriteLine($"GTAO: темнее на 3 % и больше — {darker} пикселей, на открытой земле ×{openRatio:0.###}");
        Assert.True(darker > 300, $"GTAO затемнило лишь {darker} пикселей");
        Assert.InRange(openRatio, 0.99f, 1.001f);
        Assert.True(_d3d12.Count == 0, string.Join("\n", _d3d12));
    }
}
