using Sword.Engine.Client;
using Sword.Engine.Runtime;
using Sword.Game.Bootstrap;
using Sword.Game.Client;
using Sword.Game.Contracts;

namespace Sword.Game.App;

/// <summary>
/// Композиция клиентских модулей Sword.exe (раздел 7 плана) — по образцу <see cref="SwordSimulation.Modules"/>: модули статически, сборка
/// без модуля (<c>-p:WithoutFire=true</c>) его не знает вовсе, а его секцию конфига пропускает (<see cref="SwordSimulation.ClientModuleIds"/>) —
/// общий <c>sword.json</c> с его настройками не роняет запуск (решение 9). Сервисы игры для модулей — здесь же: игра о модулях не знает.
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
    /// Модули клиента над <paramref name="simulation"/> — до <see cref="ConfigSet.Complete"/> (модули читают свои секции): включённые настроены,
    /// сервис оружия тика (<see cref="IWeaponTips"/>) — клиент арены над миром симуляции, туман, солнце и небо сцены (<see cref="ISceneFog"/>, <see cref="ISceneSky"/>) — клиент арены.
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
