using Sword.Engine.Client;
using Sword.Engine.Runtime;
using Sword.Game.Bootstrap;
using Sword.Game.Client;
using Sword.Game.Contracts;

namespace Sword.Game.App;

/// <summary>
/// Композиция клиентских модулей Sword.exe — по образцу <see cref="SwordSimulation.Modules"/>: сборка без модуля его не знает, а
/// его секцию конфига пропускает (<see cref="SwordSimulation.ClientModuleIds"/>). Сервисы игры для модулей — здесь же.
/// </summary>
internal static class ClientModuleList
{
    /// <summary>Клиентские модули по порядку (их проходы в фазе — в этом порядке). Новый модуль — строка здесь и в <see cref="SwordSimulation.ClientModuleIds"/>.</summary>
    public static IReadOnlyList<IClientModule> Modules() =>
    [
#if !WITHOUT_FIRE
        new Fire.FireModule(),
#endif
    ];

    /// <summary>
    /// Модули клиента над <paramref name="simulation"/> — до <see cref="ConfigSet.Complete"/> (модули читают свои секции), с сервисами
    /// игры: оружие тика (<see cref="IWeaponTips"/>), туман, солнце и небо сцены.
    /// </summary>
    public static ClientModules Compose(ConfigSet config, Simulation simulation, ISceneFog fog, ISceneSky sky)
    {
        var modules = Modules();
        foreach (string id in SwordSimulation.ClientModuleIds)
            if (!modules.Any(m => m.Id == id)) config.Skip(id);
        var services = new ClientServices().Add<IWeaponTips>(link => new ArenaWeaponTips(link)).Add(fog).Add(sky);
        return ClientModules.Compose(config, simulation, modules, services);
    }
}
