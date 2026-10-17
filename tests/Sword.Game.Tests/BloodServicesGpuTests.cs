using System.Numerics;
using Sword.Engine.Client;
using Sword.Engine.Diagnostics;
using Sword.Engine.Platform;
using Sword.Engine.Platform.Win32;
using Sword.Engine.Render;
using Sword.Engine.Runtime;
using Sword.Game.Animation;
using Sword.Game.Bootstrap;
using Sword.Game.Client;
using Sword.Game.Contracts;

namespace Sword.Game.Tests;

/// <summary>
/// Сервисы крови, которые даёт клиент арены вместе с рендером (пак игры): меш персонажа на CPU — тот же скелет, что у поз, сегменты суставов
/// — как у скина трупа, скин в позе привязки возвращает вершины привязки; буфер высот рельефа на GPU; направление на солнце — обратное свету
/// сцены. Любое предупреждение D3D12 — провал.
/// </summary>
[Collection("game-gpu")]
[Trait("Category", "Gpu")]
[Trait("Category", "Content")]
public sealed class BloodServicesGpuTests : IDisposable, ILogSink
{
    private const int W = 320, H = 240;
    private readonly Win32Window _window;
    private readonly List<string> _d3d12 = [];

    public BloodServicesGpuTests()
    {
        Log.AddSink(this);
        _window = new Win32Window(new WindowOptions("Sword.Game.Tests.Blood", W, H, Activate: false));
    }

    void ILogSink.Write(in LogEntry entry)
    {
        if (entry.Category == "d3d12" && entry.Level >= LogLevel.Warning) _d3d12.Add(entry.Message);
    }

    public void Dispose()
    {
        _window.Dispose();
        Log.RemoveSink(this);
    }

    [Fact]
    public void SkinMesh_Terrain_Sun_FromArenaClient()
    {
        var sim = SwordSimulation.Create(ConfigSet.Empty("arena.enemies=1"), CharacterAssets.LoadFromPak());
        var clock = new SimulationClock(sim, (_, _) => { }, ticksPerFrame: 1);
        var arena = new ArenaClient(hud: false);
        Assert.False(arena.SkinMesh.Loaded);   // рендера нет — источника нет
        Assert.Equal(-1, arena.TerrainView.HeightsSrv);
        var contract = new ShaderContract();
        Renderer.Contribute(contract);
        arena.ContributeShaders(contract);
        var renderer = new Renderer(new RendererOptions(_window.NativeHandle, W, H, DebugLayer: true,
            ShaderRoot: Path.Combine(AppContext.BaseDirectory, "Shaders"), GpuValidation: true), contract);
        try
        {
            arena.Initialize(renderer, clock.Link);
            ISkinMesh mesh = arena.SkinMesh;
            Assert.True(mesh.Loaded);
            var bodies = sim.World.View<IBodyView>();
            int vertices = mesh.Positions.Length;
            Assert.True(vertices > 10_000);
            Assert.Equal((vertices, vertices, vertices), (mesh.Normals.Length, mesh.Joints.Length, mesh.Weights.Length));
            Assert.Equal(0, mesh.Indices.Length % 3);
            Assert.Equal(bodies.JointCount, mesh.InverseBind.Length);
            for (int j = 0; j < bodies.JointCount; j++) Assert.Equal(bodies.JointSegment(j), mesh.JointSegment(j));
            // Скин в позе привязки (модельные матрицы привязки, мир единичный) — вершины привязки.
            var reference = sim.World.View<IAnimationView>().ReferenceModel();
            float worst = 0, weights = 0;
            for (int v = 0; v < vertices; v += 7)
            {
                var w = mesh.Weights[v];
                weights = MathF.Max(weights, MathF.Abs(w.X + w.Y + w.Z + w.W - 1));
                worst = MathF.Max(worst, Vector3.Distance(SkinMath.Position(mesh, v, reference), mesh.Positions[v]));
            }
            Assert.True(weights <= 1e-3f, $"сумма весов отличается от 1 на {weights}");
            Assert.True(worst <= 1e-3f, $"скин в позе привязки уводит вершину на {worst} м");

            ITerrainView terrain = arena.TerrainView;
            Assert.True(terrain.HeightsSrv >= 0);
            Assert.Equal((arena.Field.Cells, arena.Field.Half), (terrain.Grid.Cells, terrain.Grid.Half));

            var sun = arena.Sky.SunDirection;
            Assert.Equal(-ArenaClient.SunLight, sun);
            Assert.True(MathF.Abs(sun.Length() - 1) < 1e-6f && sun.Y > 0);
            Assert.Empty(_d3d12);
        }
        finally
        {
            renderer.Dispose();
            arena.Dispose();
        }
    }
}
