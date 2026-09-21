using System.Numerics;
using System.Runtime.InteropServices;
using Sword.Engine.Client;
using Sword.Engine.Diagnostics;
using Sword.Engine.Ecs;
using Sword.Engine.Geometry;
using Sword.Engine.Render;
using Sword.Game.Arena;
using Sword.Game.Client.Scene;
using Sword.Game.Contracts;

namespace Sword.Game.Client;

/// <summary>
/// Клиент игры: арена — пол, рельеф из <see cref="HeightField"/>, колонны — и игрок: Manny в позе привязки стоит в корне
/// бойца игрока (<see cref="Transform"/>) с его курсом на высоте поля рельефа (<c>DuelArena.KinWorld</c> SWORD).
/// Один непрозрачный проход «scene.opaque» пишет цвет, нормали и глубину (порядок отрисовки —
/// <c>SceneRenderer.Record</c> SWORD 2eab13b: пол, рельеф, колонны, скин).
///
/// Точка 1в: корень читается из мира прямо в кадре, без интерполяции; камера смотрит на игрока без сглаживания;
/// команда игрока — заданная (<c>--move</c>), одна и та же каждый тик. Ввод, Extract в <c>RenderWorld</c> с
/// интерполяцией и управляемая RTS-камера — точка 1г.
/// </summary>
public sealed class ArenaClient : IClientGame
{
    /// <summary>Меш персонажа в контенте (<c>CharacterRig.MeshFile</c> SWORD).</summary>
    public const string MannyMesh = "characters/manny/mesh.swmesh";

    /// <summary>Цвет игрока (<c>SkinnedCharacters.Colour</c> SWORD).</summary>
    public static readonly Vector3 PlayerColour = new(0.3f, 0.4f, 0.65f);

    /// <summary>Цвет очистки кадра (как <c>SceneRenderer.Record</c> SWORD).</summary>
    public static readonly Vector4 ClearColour = new(0.33f, 0.29f, 0.27f, 1f);

    public const int SlotTerrain = 8, SlotMesh = 9;

    private const string SceneShader = "scene.hlsl";
    private static readonly GraphicsPipelineDesc Ground = new(SceneShader, "VsGround", "PsGround", Depth: DepthMode.TestWrite);
    private static readonly GraphicsPipelineDesc Terrain = new(SceneShader, "VsTerrain", "PsGround", Depth: DepthMode.TestWrite);
    private static readonly GraphicsPipelineDesc Pillar = new(SceneShader, "VsPillar", "PsPillar", Depth: DepthMode.TestWrite);
    private static readonly GraphicsPipelineDesc Skin = new(SceneShader, "VsSkin", "PsSkin", Depth: DepthMode.TestWrite);

    private readonly RtsCamera _camera = new();
    private readonly HeightField _field = ArenaTerrain.BuildField();
    private readonly Action<RenderPassContext> _drawOpaque;
    private Renderer? _renderer;
    private GpuData? _heights, _capsule;
    private GpuIndices? _capsuleIndices;
    private int _manny = -1;
    private SimulationLink? _simulation;
    private ReadQuery? _players;
    private readonly PlayerCommand? _scripted;
    private readonly bool _followPlayer;

    /// <param name="camera">Ракурс вместо исходного SWORD (проверочные снимки); null — исходный, взгляд на игрока.</param>
    /// <param name="scripted">Команда игрока на каждый тик (проверочные прогоны); null — стоять.</param>
    public ArenaClient(CameraPose? camera = null, PlayerCommand? scripted = null)
    {
        _drawOpaque = DrawOpaque;
        _scripted = scripted;
        _followPlayer = camera is null;
        if (camera is { } c)
        {
            _camera.Yaw = c.Yaw;
            _camera.Pitch = c.Pitch;
            _camera.Distance = c.Distance;
            _camera.Target = new Vector3(c.TargetX, 0, c.TargetZ);
        }
    }

    /// <summary>Поле высот арены — то же, что рисуется.</summary>
    public HeightField Field => _field;

    public void ContributeShaders(ShaderContract c)
    {
        c.Section("арена (Sword.Game.Client)");
        c.Add("TERRAIN_CELLS", (uint)_field.Cells, "ячеек сетки рельефа на сторону");
        c.Add("TERRAIN_HALF", _field.Half, "полуразмер сетки рельефа, м");
        c.Add("PILLAR_COUNT", ArenaLayout.PillarCount, "колонн в константах кадра");
        c.AddSlot("R_TERRAIN", SlotTerrain, "высоты узлов рельефа (HeightField, по строкам z)");
        c.AddSlot("R_MESH", SlotMesh, "вершины единичной капсулы");
        c.AddSize<FrameConstants>("FRAME_CONSTANTS_SIZE");
        c.AddSize<CapsuleVertex>("CAPSULE_VERTEX_SIZE");
    }

    public void Initialize(Renderer renderer, SimulationLink? simulation)
    {
        _renderer = renderer;
        // Игрок и его команда — ядро игры: симуляция у арены есть всегда.
        _simulation = simulation ?? throw new ArgumentNullException(nameof(simulation), "клиенту арены нужна симуляция");
        _players = simulation.World.Query().With<PlayerTag>().Read<Transform>();
        _heights = renderer.CreateData<float>("Arena.Heights", _field.Heights);

        var verts = new List<CapsuleVertex>();
        var idx = new List<uint>();
        CapsuleMesh.Build(verts, idx);
        _capsule = renderer.CreateData<CapsuleVertex>("Mesh.CapsuleVertices", CollectionsMarshal.AsSpan(verts));
        _capsuleIndices = renderer.CreateIndices("Mesh.CapsuleIndices", CollectionsMarshal.AsSpan(idx));

        _manny = renderer.Skinned.LoadMesh("Manny", MannyMesh);
        var (min, max) = _field.Range();
        Log.Info("arena", $"рельеф {_field.Cells}×{_field.Cells} ячеек ({_field.Nodes}×{_field.Nodes} узлов), шаг {_field.Step} м, " +
            $"высоты {min:0.###}…{max:0.###} м");
    }

    public void BeforeTick(SimulationLink simulation)
    {
        if (_scripted is { } command) simulation.Enqueue(command);
    }

    public void BuildFrame(RenderFrame frame)
    {
        var player = LastPlayer = Player();
        if (_followPlayer) _camera.Target = player.Position;
        _camera.Rebuild(frame.Width, frame.Height);
        frame.SetFrameConstants(Constants(player.Position));

        // Manny в позе привязки: палитра InverseBind·Component·World = World (компоненты — те же, что при привязке),
        // World — поворот на курс и перенос в корень на высоте рельефа (DuelArena.KinWorld).
        var world = Matrix4x4.CreateRotationY(player.Facing);
        world.Translation = new Vector3(player.Position.X, _field.Sample(player.Position.X, player.Position.Z), player.Position.Z);
        var palette = _renderer!.Skinned.Add(_manny, PlayerColour);
        palette.Fill(GpuMat34.From(world));

        frame.Graph.AddPass("scene.opaque")
            .Color(frame.Graph.BackBuffer, ClearColour)
            .Color(frame.Scene.Normal, SceneTargets.NormalClear)
            .Depth(frame.Scene.Depth, DepthAccess.Write, clear: 0f)
            .Execute(_drawOpaque);
    }

    private void DrawOpaque(RenderPassContext ctx)
    {
        ctx.SetConstant(SlotTerrain, _heights!.Srv);
        ctx.SetConstant(SlotMesh, _capsule!.Srv);

        ctx.SetPipeline(Ground);
        ctx.Draw(6);
        // Рельеф тестовых участков — сетка без вершинного буфера поверх ровного пола.
        ctx.SetPipeline(Terrain);
        ctx.Draw((uint)(_field.Cells * _field.Cells * 6));
        ctx.SetPipeline(Pillar);
        ctx.DrawIndexed(_capsuleIndices!, ArenaLayout.PillarCount);

        _renderer!.Skinned.Record(ctx, Skin);
    }

    /// <summary>Корень игрока, показанный последним кадром (итог Sword.exe, сверка с Headless).</summary>
    public Transform LastPlayer { get; private set; }

    /// <summary>Корень игрока из мира (игрока создаёт ядро игры при сборке мира).</summary>
    private Transform Player()
    {
        foreach (var chunk in _simulation!.World.Iterate(_players!))
            if (chunk.Count > 0) return chunk.Read<Transform>()[0];
        throw new InvalidOperationException("в мире нет игрока");
    }

    private FrameConstants Constants(Vector3 playerPos)
    {
        var pl = ArenaLayout.Pillars;
        return new FrameConstants
        {
            Dt = 1f / 60f, Alpha = 1f,
            ViewProj = _camera.ViewProj,
            InvViewProj = Matrix4x4.Invert(_camera.ViewProj, out var inv) ? inv : Matrix4x4.Identity,
            CameraPos = _camera.Eye, ArenaRadius = ArenaLayout.ArenaRadius,
            Pillar0 = pl[0], Pillar1 = pl[1], Pillar2 = pl[2], Pillar3 = pl[3],
            Pillar4 = pl[4], Pillar5 = pl[5], Pillar6 = pl[6], Pillar7 = pl[7],
            LightDir = Vector3.Normalize(new Vector3(-0.45f, -1f, -0.3f)),
            PlayerPos = playerPos,
            PlayerHpFrac = 1f,
        };
    }

    public void Dispose()
    {
        _heights?.Dispose();
        _capsule?.Dispose();
        _capsuleIndices?.Dispose();
    }
}
