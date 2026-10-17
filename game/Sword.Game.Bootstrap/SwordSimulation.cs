using Sword.Engine.Runtime;
using Sword.Game.Animation;
using Sword.Game.Contracts;

namespace Sword.Game.Bootstrap;

/// <summary>
/// Единая композиция симуляции: стадии игры, ядро и модули — один вызов для App и Headless, поэтому игра и прогон без окна не
/// расходятся составом. Проверку конфига целиком и загрузку ассетов делает exe.
/// </summary>
public static class SwordSimulation
{
    /// <summary>
    /// Модули симуляции по порядку. Ядро — не модуль: <see cref="GameCore"/>. Сборка без модуля его не знает вовсе.
    /// </summary>
    public static IReadOnlyList<IModule> Modules() =>
    [
#if !WITHOUT_LOCOMOTION
        new Locomotion.LocomotionModule(),
#endif
#if !WITHOUT_CUTTING
        new Cutting.CuttingModule(),
#endif
    ];

    /// <summary>
    /// Id всех модулей симуляции, и тех, что не вошли в сборку: секция модуля, которого нет, пропускается (<see cref="ConfigSet.Skip"/>) —
    /// без модуля игра стартует. Новый модуль — строка здесь и в <see cref="Modules"/>.
    /// </summary>
    public static readonly IReadOnlyList<string> ModuleIds = ["locomotion", "cutting"];

    /// <summary>
    /// Id только клиентских модулей: их секции читает только клиент, exe без клиента и сборка без модуля их пропускают
    /// (<see cref="SkipClientSections"/>). Новый клиентский модуль — строка здесь и в композиции exe.
    /// </summary>
    public static readonly IReadOnlyList<string> ClientModuleIds = ["fire", "blood"];

    public static Simulation Create(ConfigSet config, CharacterAssets assets)
    {
        var modules = Modules();
        foreach (string id in ModuleIds)
            if (!modules.Any(m => m.Id == id)) config.Skip(id);
        return Simulation.Build(config, GameStages.All, b => GameCore.Configure(b, assets), modules, ClientModuleIds);
    }

    /// <summary>
    /// Exe без клиента (Headless): секции, которые читает только клиент, — привязки клавиш (<see cref="ConfigSet.SkipClientSections"/>) и
    /// секции клиентских модулей (<see cref="ClientModuleIds"/>) — не читаются и неизвестными не считаются.
    /// </summary>
    public static void SkipClientSections(ConfigSet config)
    {
        config.SkipClientSections();
        foreach (string id in ClientModuleIds) config.Skip(id);
    }
}
