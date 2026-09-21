namespace Sword.Architecture.Tests;

/// <summary>
/// Тесты на сами правила: заведомо запрещённая ссылка должна находиться. Без них зелёный
/// <see cref="SolutionTests.Solution_FollowsArchitecture"/> ничего не доказывает.
/// </summary>
public sealed class RuleTests
{
    private static ProjectNode P(string path, string? side, string[]? references = null, bool exe = false, PackageRef[]? packages = null)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        return new ProjectNode(name, path, SolutionModel.LayerOf(path), side, exe, references ?? [], packages ?? []);
    }

    private static ProjectNode Core => P("engine/Sword.Engine.Core/Sword.Engine.Core.csproj", Sides.Neutral);

    private static IReadOnlyList<Violation> Check(params ProjectNode[] projects) => ArchitectureRules.Check(projects);

    private static void AssertViolates(string rule, params ProjectNode[] projects)
    {
        var violations = Check(projects);
        Assert.True(violations.Any(v => v.Rule == rule),
            $"ожидалось нарушение «{rule}», найдено: {(violations.Count == 0 ? "ничего" : string.Join("; ", violations))}");
    }

    [Fact]
    public void AllowedGraph_HasNoViolations()
    {
        var violations = Check(
            Core,
            P("engine/Sword.Engine.Diagnostics/Sword.Engine.Diagnostics.csproj", Sides.Neutral, ["Sword.Engine.Core"]),
            P("engine/Sword.Engine.Ecs/Sword.Engine.Ecs.csproj", Sides.Neutral, ["Sword.Engine.Core", "Sword.Engine.Diagnostics"]));
        Assert.Empty(violations);
    }

    [Fact]
    public void EngineReferencingGame_IsLayerViolation() => AssertViolates("layer",
        Core,
        P("game/Sword.Game.Contracts/Sword.Game.Contracts.csproj", Sides.Neutral, ["Sword.Engine.Core"]),
        P("engine/Sword.Engine.Diagnostics/Sword.Engine.Diagnostics.csproj", Sides.Neutral, ["Sword.Game.Contracts"]));

    [Fact]
    public void SimulationReferencingClient_IsSideViolation() => AssertViolates("side",
        Core,
        P("engine/Sword.Engine.Platform/Sword.Engine.Platform.csproj", Sides.Client, ["Sword.Engine.Core"]),
        P("engine/Sword.Engine.Runtime/Sword.Engine.Runtime.csproj", Sides.Simulation, ["Sword.Engine.Platform"]));

    [Fact]
    public void NeutralReferencingSimulation_IsSideViolation() => AssertViolates("side",
        P("engine/Sword.Engine.Runtime/Sword.Engine.Runtime.csproj", Sides.Simulation),
        P("engine/Sword.Engine.Ecs/Sword.Engine.Ecs.csproj", Sides.Neutral, ["Sword.Engine.Runtime"]));

    [Fact]
    public void SimulationReachingClientTransitively_IsClosureViolation()
    {
        // Нейтральная прослойка с ошибочной ссылкой на клиент: прямое правило ловит её, замыкание — Headless.
        var violations = Check(
            P("engine/Sword.Engine.Rhi/Sword.Engine.Rhi.csproj", Sides.Client),
            P("engine/Sword.Engine.Ecs/Sword.Engine.Ecs.csproj", Sides.Neutral, ["Sword.Engine.Rhi"]),
            P("game/Sword.Game.Headless/Sword.Game.Headless.csproj", Sides.Simulation, ["Sword.Engine.Ecs"], exe: true));
        Assert.Contains(violations, v => v.Rule == "simulation-closure" && v.Project == "Sword.Game.Headless");
    }

    [Fact]
    public void MissingSide_IsViolation() => AssertViolates("side-declared",
        P("engine/Sword.Engine.Geometry/Sword.Engine.Geometry.csproj", null));

    [Fact]
    public void ProjectOutsideMatrix_IsViolation() => AssertViolates("matrix",
        P("engine/Sword.Engine.Unplanned/Sword.Engine.Unplanned.csproj", Sides.Neutral));

    [Fact]
    public void ReferenceOutsideMatrix_IsViolation() => AssertViolates("matrix",
        Core,
        P("engine/Sword.Engine.Geometry/Sword.Engine.Geometry.csproj", Sides.Neutral, ["Sword.Engine.Core"]),
        P("engine/Sword.Engine.Formats/Sword.Engine.Formats.csproj", Sides.Neutral, ["Sword.Engine.Geometry"]));

    [Fact]
    public void VorticeOutsideRhiAndRender_IsViolation() => AssertViolates("vortice",
        P("engine/Sword.Engine.Client/Sword.Engine.Client.csproj", Sides.Client,
            packages: [new PackageRef("Vortice.Direct3D12", "compile")]));

    [Fact]
    public void VorticeWithoutPrivateCompile_IsViolation() => AssertViolates("vortice-private",
        P("engine/Sword.Engine.Rhi/Sword.Engine.Rhi.csproj", Sides.Client,
            packages: [new PackageRef("Vortice.Direct3D12", null)]));

    [Fact]
    public void Win32FromLibrary_IsViolation() => AssertViolates("win32-exe-only",
        P("engine/Sword.Engine.Platform.Win32/Sword.Engine.Platform.Win32.csproj", Sides.Client),
        P("engine/Sword.Engine.Client/Sword.Engine.Client.csproj", Sides.Client, ["Sword.Engine.Platform.Win32"]));

    [Fact]
    public void AgentFromLibrary_IsViolation() => AssertViolates("agent-exe-only",
        P("engine/Sword.Engine.Agent/Sword.Engine.Agent.csproj", Sides.Simulation),
        P("game/Sword.Game/Sword.Game.csproj", Sides.Simulation, ["Sword.Engine.Agent"]));

    [Fact]
    public void ModuleReferencingGameCore_IsViolation() => AssertViolates("modules-isolated",
        P("game/Sword.Game/Sword.Game.csproj", Sides.Simulation),
        P("game/modules/Sword.Game.Guard/Sword.Game.Guard.csproj", Sides.Simulation, ["Sword.Game"]));

    [Fact]
    public void ModuleReferencingOtherModule_IsViolation() => AssertViolates("modules-isolated",
        P("game/modules/Sword.Game.Cutting/Sword.Game.Cutting.csproj", Sides.Simulation),
        P("game/modules/Sword.Game.Guard/Sword.Game.Guard.csproj", Sides.Simulation, ["Sword.Game.Cutting"]));

    [Fact]
    public void ClientModuleReferencingOwnSimulationPart_IsAllowed()
    {
        var violations = Check(
            P("game/modules/Sword.Game.Cutting/Sword.Game.Cutting.csproj", Sides.Simulation),
            P("game/modules/Sword.Game.Cutting.Client/Sword.Game.Cutting.Client.csproj", Sides.Client, ["Sword.Game.Cutting"]));
        Assert.DoesNotContain(violations, v => v.Rule is "modules-isolated" or "matrix");
    }

    [Fact]
    public void ProductReferencingTest_IsViolation() => AssertViolates("no-test-references",
        P("tests/Sword.Architecture.Tests/Sword.Architecture.Tests.csproj", null),
        P("game/Sword.Game.App/Sword.Game.App.csproj", Sides.Client, ["Sword.Architecture.Tests"], exe: true));

    [Fact]
    public void UnknownReference_IsViolation() => AssertViolates("reference-known",
        P("engine/Sword.Engine.Ecs/Sword.Engine.Ecs.csproj", Sides.Neutral, ["Sword.Engine.Missing"]));
}
