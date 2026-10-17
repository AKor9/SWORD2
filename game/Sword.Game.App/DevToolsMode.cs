using Sword.Engine.Client;
using Sword.Engine.DevTools;
using Sword.Engine.Runtime;
using Sword.Game.Client;

namespace Sword.Game.App;

/// <summary>
/// Инструменты разработчика Sword.exe: панель ручек (<see cref="TuningPanel"/>, F3) — вкладки групп ручек игры
/// (<see cref="ArenaClient.TuningGroups"/>), «сохранить» и «загрузить» — секция группы в <c>sword.user.json</c> (остальное
/// содержимое файла остаётся), «сброс» — к значениям в коде. В бенче панелей нет. В сборке без DevTools этого файла нет.
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
        // «Графика» — показ клиента движка (vsync, SMAA, тени, GTAO) и арены (материалы), последней.
        var graphics = GraphicsTuning.Build(client);
        arena.AddGraphicsTuning(graphics);
        tuning.Groups.Add(graphics);
        tuning.Save = g => binding.Save(g, file);
        tuning.Load = g => binding.Load(g, file);
        tuning.Top = () => arena.HudBottom;
    }
}
