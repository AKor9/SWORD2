namespace Sword.Architecture.Tests;

/// <summary>Нарушение правила архитектуры.</summary>
public sealed record Violation(string Rule, string Project, string Message)
{
    public override string ToString() => $"[{Rule}] {Project}: {Message}";
}

/// <summary>
/// Правила ссылок между проектами — раздел 4 ARCHITECTURE_PLAN.md. Слой и сторону проверяет ещё и сборка
/// (Directory.Build.targets, SWORD001–003); здесь — полная матрица, транзитивные правила, пакеты и модули.
/// </summary>
public static class ArchitectureRules
{
    private const string Core = "Sword.Engine.Core", Diag = "Sword.Engine.Diagnostics", Geometry = "Sword.Engine.Geometry",
        Formats = "Sword.Engine.Formats", Platform = "Sword.Engine.Platform", Ecs = "Sword.Engine.Ecs",
        Content = "Sword.Engine.Content", Physics = "Sword.Engine.Physics", Animation = "Sword.Engine.Animation",
        Ui = "Sword.Engine.Ui", Rhi = "Sword.Engine.Rhi", Render = "Sword.Engine.Render",
        Win32 = "Sword.Engine.Platform.Win32", Runtime = "Sword.Engine.Runtime", Client = "Sword.Engine.Client",
        Introspection = "Sword.Engine.Introspection", Agent = "Sword.Engine.Agent", AgentClient = "Sword.Engine.Agent.Client",
        DevTools = "Sword.Engine.DevTools",
        Contracts = "Sword.Game.Contracts", Data = "Sword.Game.Data", Game = "Sword.Game", GameClient = "Sword.Game.Client",
        Bootstrap = "Sword.Game.Bootstrap", GameAgent = "Sword.Game.Agent", GameDevTools = "Sword.Game.DevTools",
        Bake = "Sword.Bake", BakeEngine = "Sword.Bake.Engine", BakeGame = "Sword.Bake.Game";

    /// <summary>Любой проект продукта (не тест) — для exe, которые собирают композицию.</summary>
    private const string AnyProduct = "*";

    /// <summary>Любой модуль симуляции (game/modules/, не .Client) — для Sword.Game.Bootstrap.</summary>
    private const string SimulationModules = "@simulation-modules";

    /// <summary>
    /// Матрица прямых ссылок — таблица раздела 4 ARCHITECTURE_PLAN.md. <c>Sword.Engine.Core</c> разрешён всем неявно.
    /// Новый проект без строки здесь — нарушение: матрица обновляется вместе с решением.
    /// </summary>
    private static readonly Dictionary<string, string[]> Matrix = new()
    {
        [Core] = [],
        [Diag] = [],
        [Geometry] = [],
        [Formats] = [],
        [Platform] = [],
        [Ecs] = [Diag],
        [Content] = [Diag],
        [Physics] = [Diag, Geometry],
        [Animation] = [Diag, Geometry, Formats],
        [Ui] = [Formats],
        [Rhi] = [Diag, Platform],
        [Render] = [Diag, Geometry, Rhi, Formats, Content, Ui],
        [Win32] = [Diag, Platform],
        [Runtime] = [Diag, Ecs, Content],
        [Client] = [Runtime, Ecs, Platform, Rhi, Render, Ui, Diag],
        [Introspection] = [Diag, Ecs, Runtime],
        [Agent] = [Introspection, Runtime, Ecs, Diag],
        [AgentClient] = [Agent, Introspection, Client, Render, Runtime, Ecs],
        [DevTools] = [Client, Ui, Ecs, Introspection, Diag],

        [Contracts] = [Ecs],
        [Data] = [Formats],
        [Game] = [Contracts, Data, Runtime, Physics, Animation, Geometry, Ecs, Content, Diag],
        [GameClient] = [Game, Contracts, Data, Client, Render, Ui, Geometry, Ecs, Diag],
        [Bootstrap] = [Game, Runtime, Contracts, Data, Diag, SimulationModules],
        [GameAgent] = [Game, Contracts, Data, Agent, Introspection, Ecs],
        [GameDevTools] = [Game, GameClient, DevTools],
        ["Sword.Game.App"] = [AnyProduct],
        ["Sword.Game.Headless"] = [AnyProduct],

        [Bake] = [Content],
        [BakeEngine] = [Bake, Content, Formats, Animation],
        [BakeGame] = [Bake, BakeEngine, Formats, Data],
        ["Sword.Bake.Cli"] = [Bake, Content],
        ["Sword.Bake.Studio"] = [Bake, BakeEngine, Content, Formats, Client, Render, Ui, Win32],
    };

    /// <summary>Модуль симуляции: то же, что ядру игры, кроме самого Sword.Game.</summary>
    private static readonly string[] SimulationModuleAllowed = [Contracts, Data, Runtime, Physics, Animation, Geometry, Ecs, Content, Diag];

    /// <summary>Клиентская часть модуля: свой модуль симуляции (проверяется отдельно) и клиентские сборки движка.</summary>
    private static readonly string[] ClientModuleAllowed = [Contracts, Data, Client, Render, Ui, Diag];

    /// <summary>Где можно ссылаться на пакеты Vortice.</summary>
    private static readonly HashSet<string> VorticeOwners = [Rhi, Render];

    /// <summary>Временные исключения для Vortice. Список может только сокращаться (раздел 4).</summary>
    private static readonly HashSet<string> VorticeExceptions = [];

    /// <summary>Сборки агента: на них ссылаются только exe (и они друг на друга).</summary>
    private static readonly HashSet<string> AgentFamily = [Agent, AgentClient, GameAgent];

    /// <summary>
    /// Сборки инструментов разработчика: на них ссылаются только exe (и они друг на друга) — иначе сборка без DevTools
    /// (<c>-p:WithoutDevTools=true</c>) не смогла бы их убрать.
    /// </summary>
    public static readonly IReadOnlySet<string> DevToolsFamily = new HashSet<string>([DevTools, GameDevTools]);

    public static IReadOnlyList<Violation> Check(IReadOnlyList<ProjectNode> projects)
    {
        var violations = new List<Violation>();
        var byName = new Dictionary<string, ProjectNode>(StringComparer.Ordinal);
        foreach (var p in projects)
            if (!byName.TryAdd(p.Name, p))
                violations.Add(new("unique-names", p.Name, $"имя проекта встречается дважды: {p.RelativePath}"));

        foreach (var p in projects)
        {
            CheckProject(p, violations);
            foreach (string name in p.References)
            {
                if (!byName.TryGetValue(name, out var target))
                {
                    violations.Add(new("reference-known", p.Name, $"ссылка на {name}, которого нет в решении"));
                    continue;
                }
                CheckReference(p, target, violations);
            }
        }

        // Замыкание симуляции: сборка стороны Simulation не достаёт до клиентской даже транзитивно
        // (так Game.Headless остаётся без окна и D3D12).
        foreach (var p in projects.Where(p => p.Side == Sides.Simulation))
            foreach (var reached in Closure(p, byName).Where(t => t.Side == Sides.Client))
                violations.Add(new("simulation-closure", p.Name, $"транзитивно достаёт до клиентской сборки {reached.Name}"));

        return violations;
    }

    /// <summary>
    /// Ссылки собранной сборки проекта <paramref name="from"/> (из метаданных .dll) — по тем же правилам, что ссылки
    /// .csproj. Ловят то, чего .csproj не видит: типы транзитивно доступной сборки, использованные напрямую.
    /// </summary>
    public static IReadOnlyList<Violation> CheckCompiled(ProjectNode from, IEnumerable<string> referencedAssemblies,
        IReadOnlyList<ProjectNode> projects)
    {
        var byAssembly = projects.Where(p => !p.IsTest).ToDictionary(p => p.Assembly, StringComparer.Ordinal);
        var found = new List<Violation>();
        foreach (string assembly in referencedAssemblies)
            if (byAssembly.TryGetValue(assembly, out var target) && target.Name != from.Name)
                CheckReference(from, target, found);
        return found.Select(v => v with { Rule = "compiled-" + v.Rule, Message = v.Message + " (ссылка собранной сборки)" }).ToList();
    }

    private static void CheckProject(ProjectNode p, List<Violation> violations)
    {
        if (p.IsTest) return;

        if (p.Side is not (Sides.Neutral or Sides.Simulation or Sides.Client))
            violations.Add(new("side-declared", p.Name, $"сторона «{p.Side ?? "не задана"}»; нужна Neutral, Simulation или Client"));

        if (p.Layer is not (Layers.Engine or Layers.Game or Layers.Tool))
            violations.Add(new("layer-known", p.Name, $"слой «{p.Layer}»: проект вне engine/, game/, tools/, tests/"));

        if (!p.IsModule && !Matrix.ContainsKey(p.Name))
            violations.Add(new("matrix", p.Name, "проекта нет в матрице ссылок — добавьте строку в ArchitectureRules.Matrix и в таблицу раздела 4"));

        foreach (var package in p.Packages.Where(k => k.Id.StartsWith("Vortice.", StringComparison.OrdinalIgnoreCase)))
        {
            if (!VorticeOwners.Contains(p.Name) && !VorticeExceptions.Contains(p.Name))
                violations.Add(new("vortice", p.Name, $"пакет {package.Id}: Vortice — только у Rhi и Render"));
            else if (!IsCompilePrivate(package.PrivateAssets))
                violations.Add(new("vortice-private", p.Name,
                    $"пакет {package.Id} без PrivateAssets=compile: типы D3D12 утекут к ссылающимся на {p.Name}"));
        }
    }

    private static void CheckReference(ProjectNode from, ProjectNode to, List<Violation> violations)
    {
        string edge = $"ссылка на {to.Name}";

        if (!LayerAllows(from.Layer, to.Layer))
            violations.Add(new("layer", from.Name, $"{edge}: слой {from.Layer} не видит слой {to.Layer}"));

        if (from.IsTest) return;

        if (to.IsTest)
            violations.Add(new("no-test-references", from.Name, $"{edge}: продукт не ссылается на тесты"));

        if (!SideAllows(from.Side, to.Side))
            violations.Add(new("side", from.Name, $"{edge}: сторона {from.Side} не видит сторону {to.Side ?? "не задана"}"));

        if (!MatrixAllows(from, to))
            violations.Add(new("matrix", from.Name, $"{edge} не разрешена матрицей раздела 4"));

        if (to.Name == Win32 && !from.IsExe)
            violations.Add(new("win32-exe-only", from.Name, $"{edge}: Platform.Win32 — только из exe"));

        if (AgentFamily.Contains(to.Name) && !from.IsExe && !AgentFamily.Contains(from.Name))
            violations.Add(new("agent-exe-only", from.Name, $"{edge}: сборки агента — только из exe"));

        if (DevToolsFamily.Contains(to.Name) && !from.IsExe && !DevToolsFamily.Contains(from.Name))
            violations.Add(new("devtools-exe-only", from.Name, $"{edge}: сборки DevTools — только из exe"));

        if (from.IsModule && (to.Name is Game or GameClient || (to.IsModule && !IsOwnSimulationPart(from, to))))
            violations.Add(new("modules-isolated", from.Name, $"{edge}: модуль видит только контракты игры, не ядро игры и не другие модули"));
    }

    public static bool LayerAllows(string from, string to) => from switch
    {
        Layers.Engine => to == Layers.Engine,
        Layers.Game => to is Layers.Engine or Layers.Game,
        Layers.Tool => to is Layers.Engine or Layers.Game or Layers.Tool,
        Layers.Test => true,
        _ => false,
    };

    public static bool SideAllows(string? from, string? to) => from switch
    {
        Sides.Neutral => to == Sides.Neutral,
        Sides.Simulation => to is Sides.Neutral or Sides.Simulation,
        Sides.Client => to is Sides.Neutral or Sides.Simulation or Sides.Client,
        _ => false,
    };

    private static bool MatrixAllows(ProjectNode from, ProjectNode to)
    {
        if (to.Name == Core && from.Name != Core) return true;

        if (from.IsClientModule)
            return IsOwnSimulationPart(from, to) || ClientModuleAllowed.Contains(to.Name);
        if (from.IsModule)
            return SimulationModuleAllowed.Contains(to.Name);

        if (!Matrix.TryGetValue(from.Name, out var allowed)) return false;
        return allowed.Contains(AnyProduct) && !to.IsTest
            || allowed.Contains(to.Name)
            || allowed.Contains(SimulationModules) && to.IsModule && !to.IsClientModule;
    }

    /// <summary>Клиентская часть модуля ссылается на свою часть симуляции: Sword.Game.X.Client → Sword.Game.X.</summary>
    private static bool IsOwnSimulationPart(ProjectNode from, ProjectNode to) =>
        from.IsClientModule && to.IsModule && !to.IsClientModule && from.Name == to.Name + ".Client";

    private static bool IsCompilePrivate(string? privateAssets) =>
        privateAssets is not null && privateAssets.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(a => a.Equals("compile", StringComparison.OrdinalIgnoreCase) || a.Equals("all", StringComparison.OrdinalIgnoreCase));

    /// <summary>Всё, до чего проект достаёт по ссылкам (без него самого).</summary>
    public static IEnumerable<ProjectNode> Closure(ProjectNode start, IReadOnlyDictionary<string, ProjectNode> byName)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { start.Name };
        var stack = new Stack<ProjectNode>([start]);
        while (stack.Count > 0)
            foreach (string name in stack.Pop().References)
                if (seen.Add(name) && byName.TryGetValue(name, out var next))
                {
                    stack.Push(next);
                    yield return next;
                }
    }
}
