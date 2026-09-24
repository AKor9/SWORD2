using Sword.Engine.Client;
using Sword.Engine.DevTools;

namespace Sword.Game.App;

/// <summary>
/// Инструменты разработчика Sword.exe (<c>Sword.Engine.DevTools</c>, раздел 4 плана): панели поверх кадра. Сейчас — панель
/// ручек (<see cref="TuningPanel"/>) без групп и без клавиши, закрытая: клавиша F3 (действие секции <c>input</c>), группа
/// «Бой» (секция <c>combat</c>), сохранение в <c>sword.user.json</c> и край под HUD подключает точка 6е (как — AGENTS.md
/// Engine.DevTools). В бенче панелей нет (SWORD: инструменты в бенче молчат). В сборке без DevTools
/// (<c>-p:WithoutDevTools=true</c>) этого файла нет.
/// </summary>
public static partial class Program
{
    static partial void AddDevTools(List<IClientPanel> panels, bool bench)
    {
        if (bench) return;
        panels.Add(new TuningPanel());
    }
}
