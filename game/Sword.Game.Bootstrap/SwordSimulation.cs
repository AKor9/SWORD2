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

    /// <summary>
    /// Id модулей игры без части симуляции — только клиентских (<c>modules/Sword.Game.&lt;Модуль&gt;.Client</c>, композиция — Sword.exe):
    /// группа <c>modules.&lt;id&gt;.enabled</c> знает их и у Headless, их секцию (<c>fire</c>) читает только клиент — exe без клиента и
    /// сборка без модуля её пропускают (<see cref="SkipClientSections"/>). Новый клиентский модуль — строка здесь и в композиции exe.
    /// </summary>
    public static readonly IReadOnlyList<string> ClientModuleIds = ["fire"];

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
