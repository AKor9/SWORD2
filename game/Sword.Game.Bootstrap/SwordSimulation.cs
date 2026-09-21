using Sword.Engine.Runtime;
using Sword.Game.Contracts;

namespace Sword.Game.Bootstrap;

/// <summary>
/// Единая композиция симуляции SWORD (раздел 3 плана): стадии игры, ядро и модули симуляции — один вызов для App и
/// Headless, поэтому игра и её прогон без окна не расходятся составом. Модули симуляции регистрируются только здесь.
/// Проверку конфига целиком (<see cref="ConfigSet.Complete"/>) делает exe после всей композиции — секции читает и клиент.
/// </summary>
public static class SwordSimulation
{
    /// <summary>Модули симуляции по порядку. Ядро — не модуль (принцип 4 плана): <see cref="GameCore"/>.</summary>
    public static IReadOnlyList<IModule> Modules() => [];

    public static Simulation Create(ConfigSet config) => Simulation.Build(config, GameStages.All, GameCore.Configure, Modules());
}
