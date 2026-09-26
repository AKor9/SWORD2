using Sword.Engine.Client;
using Sword.Engine.DevTools;
using Sword.Engine.Runtime;
using Sword.Game.Client;

namespace Sword.Game.App;

/// <summary>
/// Инструменты разработчика Sword.exe (<c>Sword.Engine.DevTools</c>, раздел 4 плана): панели поверх кадра. Панель ручек
/// (<see cref="TuningPanel"/>): открывает действие <c>tuning</c> (F3, секция <c>input</c>), вкладки — группы ручек игры
/// (<see cref="ArenaClient.TuningGroups"/>: «Бой» — секция <c>combat</c>, «Рэгдолл» — секция <c>ragdoll</c>; значение — ресурс мира или
/// ожидающее, изменение — команда ручки на ближайший тик; «Свет» — ручки клиента, <see cref="LightTuning"/>: значение рендера, без команд;
/// «Графика» — показ клиента движка, <see cref="GraphicsTuning"/>: vsync),
/// «сохранить» и «загрузить» — секция группы в <c>sword.user.json</c> рядом с <c>sword.json</c> (бережно: остальное содержимое файла
/// остаётся; группу без ручек симуляции не пишут и не читают), «сброс» — к значениям в коде, край — под панелью
/// производительности HUD. В бенче панелей нет (SWORD: инструменты в бенче молчат). В сборке без DevTools
/// (<c>-p:WithoutDevTools=true</c>) этого файла нет.
/// </summary>
public static partial class Program
{
    static partial void AddDevTools(List<IClientPanel> panels, bool bench)
    {
        if (bench) return;
        panels.Add(new TuningPanel(new InputAction(Hud.TuningAction, "F3")));
    }

    static partial void ConnectDevTools(IReadOnlyList<IClientPanel> panels, ClientHost client, ArenaClient arena, ClientModules modules)
    {
        if (panels.OfType<TuningPanel>().FirstOrDefault() is not { } tuning || arena.Tuning is not { } binding) return;
        string file = UserConfig.PathFrom(AppContext.BaseDirectory);
        tuning.Groups.AddRange(arena.TuningGroups);
        tuning.Groups.AddRange(modules.TuningGroups);   // вкладки модулей («Огонь») — после вкладок игры; выключенный модуль вкладки не даёт
        tuning.Groups.Add(GraphicsTuning.Build(client));   // «Графика» — показ клиента движка (vsync), последней
        tuning.Save = g => binding.Save(g, file);
        tuning.Load = g => binding.Load(g, file);
        tuning.Top = () => arena.HudBottom;
    }
}
