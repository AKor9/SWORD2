using Sword.Engine.Client;
using Sword.Engine.Diagnostics;

namespace Sword.Engine.DevTools;

/// <summary>
/// Вкладка «Графика» панели F3 — настройки показа клиента, не симуляции: делегаты пишут <see cref="ClientHost"/> напрямую,
/// без команд и реплея; «сохранить» и «загрузить» её не трогают, «сброс» — к значению запуска.
/// </summary>
public static class GraphicsTuning
{
    public const string Id = "graphics", Title = "Графика";

    public const string Vsync = "vsync";

    /// <summary>Группа «Графика» над клиентом движка <paramref name="host"/>.</summary>
    public static TuningGroup Build(ClientHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var g = new TuningGroup(Id, Title);
        g.Header("ПОКАЗ");
        g.Bool(Vsync, "вертикальная синхронизация", () => host.Vsync, v => host.Vsync = v,
            "показ кадра по обратному ходу луча монитора: без разрывов, кадров не больше частоты монитора; V — переключить; в бенче выключена",
            defaultValue: host.Vsync);
        return g;
    }
}
