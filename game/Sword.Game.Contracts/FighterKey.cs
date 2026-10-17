using Sword.Engine.Ecs;

namespace Sword.Game.Contracts;

/// <summary>
/// Ключ бойца для клиентских модулей — сущность бойца числом: поколение в старших 32 битах, номер — в младших. Тот же после смерти и
/// постройки заново на месте (возрождение, сброс R): модуль сам решает, что начать заново, по событию постройки.
/// </summary>
public static class FighterKey
{
    /// <summary>Ключ сущности бойца.</summary>
    public static ulong Of(Entity e) => (ulong)(uint)e.Generation << 32 | (uint)e.Index;

    /// <summary>Сущность по ключу (обратное <see cref="Of"/>).</summary>
    public static Entity Entity(ulong key) => new((int)(uint)key, (int)(uint)(key >> 32));
}
