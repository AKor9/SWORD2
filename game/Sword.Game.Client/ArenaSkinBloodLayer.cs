using Sword.Game.Contracts;

namespace Sword.Game.Client;

/// <summary>
/// Слой крови на коже (<see cref="ISkinBloodLayer"/>): источник подключает модуль крови; клиент арены спрашивает SRV бойца при сборке
/// экземпляров скина. Источника нет — слоя нет.
/// </summary>
public sealed class ArenaSkinBloodLayer : ISkinBloodLayer
{
    private Func<ulong, int>? _srvOf;

    public void Attach(Func<ulong, int> srvOf) => _srvOf = srvOf;

    /// <summary>Подключён ли источник.</summary>
    public bool Attached => _srvOf is not null;

    /// <summary>Поле <c>owner</c> экземпляра скина бойца: SRV слоя + 1, 0 — слоя нет.</summary>
    public uint OwnerOf(ulong fighter)
    {
        int srv = _srvOf?.Invoke(fighter) ?? -1;
        return srv >= 0 ? (uint)srv + 1 : 0;
    }
}
