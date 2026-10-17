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
#if !WITHOUT_BLOOD
        new Blood.BloodModule(),
#endif
    ];

    /// <summary>
    /// Модули клиента над <paramref name="simulation"/> — до <see cref="ConfigSet.Complete"/> (модули читают свои секции), с сервисами
    /// игры: оружие тика (<see cref="IWeaponTips"/>), события боя и позы тика (<see cref="IBloodFeed"/>, <see cref="IFighterPoses"/>), меш
    /// персонажа (<see cref="ISkinMesh"/>) и слои крови на коже и оружии (<see cref="ISkinBloodLayer"/>, <see cref="IWeaponBloodLayer"/>), рельеф (<see cref="ITerrainView"/>), туман, солнце и небо сцены — от клиента арены.
    /// </summary>
    public static ClientModules Compose(ConfigSet config, Simulation simulation, ArenaClient arena)
    {
        var modules = Modules();
        foreach (string id in SwordSimulation.ClientModuleIds)
            if (!modules.Any(m => m.Id == id)) config.Skip(id);
        var services = new ClientServices()
            .Add<IWeaponTips>(link => new ArenaWeaponTips(link))
            .Add<IBloodFeed>(link => new ArenaBloodFeed(link))
            .Add<IFighterPoses>(link => new ArenaFighterPoses(link))
            .Add<ISkinMesh>(arena.SkinMesh)
            .Add<ISkinBloodLayer>(arena.SkinBlood)
            .Add<IWeaponBloodLayer>(arena.WeaponBlood)
            .Add<ITerrainView>(arena.TerrainView)
            .Add<ISceneFog>(arena)
            .Add<ISceneSky>(arena);
        return ClientModules.Compose(config, simulation, modules, services);
    }
}
