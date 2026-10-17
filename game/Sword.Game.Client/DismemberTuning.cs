using Sword.Engine.Client;
using Sword.Engine.Diagnostics;

namespace Sword.Game.Client;

/// <summary>
/// Вкладка «Расчленение» панели ручек F3: ручки секции <c>cutting</c> (модуль Cutting; клиент его не видит — по секции,
/// <see cref="TuningBinding.AddSection"/>) и взрыв проверки (клавиша B — взрыв у курсора): энергия и радиус — значения клиента
/// (<see cref="ArenaClient.BlastEnergy"/>, <see cref="ArenaClient.BlastRadius"/>), в команду взрыва. Без модуля — только взрыв.
/// </summary>
public static class DismemberTuning
{
    public const string Id = "cutting", Title = "Расчленение";

    public static TuningGroup Build(TuningBinding tuning, ArenaClient arena)
    {
        var g = new TuningGroup(Id, Title);
        g.Header("РАЗРЕЗ, ПЕРЕЛОМ, ОТРЫВ");
        tuning.AddSection(g, Id);
        g.Header("ВЗРЫВ У КУРСОРА (B)");
        g.Float("blastEnergy", "энергия", () => arena.BlastEnergy, v => arena.BlastEnergy = v, 1000, 200000, log: true, format: "0",
            hint: "энергия взрыва клавишей B, Дж: до части доходит доля, которую она закрывает от центра");
        g.Float("blastRadius", "радиус", () => arena.BlastRadius, v => arena.BlastRadius = v, 0.5f, 15, format: "0.0",
            hint: "радиус действия взрыва, м");
        return g;
    }
}
