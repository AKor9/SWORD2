using Sword.Engine.Runtime;
using Sword.Game.Animation;
using Sword.Game.Contracts;

namespace Sword.Game.Bootstrap;

/// <summary>
/// Единая композиция симуляции SWORD (раздел 3 плана): стадии игры, ядро и модули симуляции — один вызов для App и
/// Headless, поэтому игра и её прогон без окна не расходятся составом. Модули симуляции регистрируются только здесь.
/// Проверку конфига целиком (<see cref="ConfigSet.Complete"/>) делает exe после всей композиции — секции читает и клиент.
/// Ассеты бойца (<see cref="CharacterAssets.Load"/>) грузит exe из своего контента: откуда контент — решает он.
/// </summary>
public static class SwordSimulation
{
    /// <summary>
    /// Модули симуляции по порядку. Ядро — не модуль (принцип 4 плана): <see cref="GameCore"/>. Сборка без модуля
    /// (<c>-p:WithoutLocomotion=true</c>) его не знает вовсе.
    /// </summary>
    public static IReadOnlyList<IModule> Modules() =>
    [
#if !WITHOUT_LOCOMOTION
        new Locomotion.LocomotionModule(),
#endif
    ];

    /// <summary>
    /// Id всех модулей симуляции игры — и тех, что не вошли в эту сборку (<c>-p:Without&lt;Модуль&gt;=true</c>). Секция конфига модуля, которого
    /// нет в сборке, пропускается, как у выключенного (<see cref="ConfigSet.Skip"/>): общий <c>sword.json</c> с его настройками
    /// (<c>locomotion.player</c>) не роняет запуск — без модуля игра стартует (решение 9). Новый модуль — строка здесь и в <see cref="Modules"/>.
    /// </summary>
    public static readonly IReadOnlyList<string> ModuleIds = ["locomotion"];

    public static Simulation Create(ConfigSet config, CharacterAssets assets)
    {
        var modules = Modules();
        foreach (string id in ModuleIds)
            if (!modules.Any(m => m.Id == id)) config.Skip(id);
        return Simulation.Build(config, GameStages.All, b => GameCore.Configure(b, assets), modules);
    }
}
