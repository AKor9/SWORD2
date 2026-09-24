using Sword.Engine.Client;
using Sword.Engine.DevTools;
using Sword.Engine.Runtime;
using Sword.Game.Client;

namespace Sword.Game.App;

/// <summary>
/// Инструменты разработчика Sword.exe (<c>Sword.Engine.DevTools</c>, раздел 4 плана): панели поверх кадра. Точка 6е — панель ручек
/// (<see cref="TuningPanel"/>): открывает действие <c>tuning</c> (F3, секция <c>input</c>), вкладки — группы ручек игры
/// (<see cref="ArenaClient.TuningGroups"/>: «Бой» — секция <c>combat</c>; значение — ресурс мира или ожидающее, изменение — команда
/// ручки на ближайший тик), «сохранить» и «загрузить» — секция группы в <c>sword.user.json</c> рядом с <c>sword.json</c> (бережно:
/// остальное содержимое файла остаётся), «сброс» — к значениям в коде командами, край — под панелью производительности HUD. В бенче
/// панелей нет (SWORD: инструменты в бенче молчат). В сборке без DevTools (<c>-p:WithoutDevTools=true</c>) этого файла нет.
/// </summary>
public static partial class Program
{
    static partial void AddDevTools(List<IClientPanel> panels, bool bench)
    {
        if (bench) return;
        panels.Add(new TuningPanel(new InputAction(Hud.TuningAction, "F3")));
    }

    static partial void ConnectDevTools(IReadOnlyList<IClientPanel> panels, ArenaClient arena)
    {
        if (panels.OfType<TuningPanel>().FirstOrDefault() is not { } tuning || arena.Tuning is not { } binding) return;
        string file = UserConfig.PathFrom(AppContext.BaseDirectory);
        tuning.Groups.AddRange(arena.TuningGroups);
        tuning.Save = g => binding.Save(g, file);
        tuning.Load = g => binding.Load(g, file);
        tuning.Top = () => arena.HudBottom;
    }
}
