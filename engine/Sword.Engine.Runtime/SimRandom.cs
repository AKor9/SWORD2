using Sword.Engine.Ecs;

namespace Sword.Engine.Runtime;

/// <summary>
/// Счётчиковый ГСЧ симуляции — ресурс мира: число — чистая функция <c>f(зерно, поток, тик, сущность, выборка)</c>. Не
/// зависит от порядка обхода и чужих выборок; состояния нет, в хеш входит только зерно. Смешивание — раунды
/// <see cref="StateHasher"/>.
/// </summary>
public readonly record struct SimRandom(ulong Seed)
{
    /// <summary>Номер потока по имени назначения (<c>"ai.think"</c>, <c>"blood.spray"</c>…) — считать один раз при настройке.</summary>
    public static uint Stream(string name) => unchecked((uint)StateHasher.Of(name));

    /// <summary>64 случайных бита для (поток, тик, сущность, выборка).</summary>
    public ulong Bits(uint stream, ulong tick, Entity entity, uint sample = 0)
    {
        var h = new StateHasher(Seed);
        h.Add(stream);
        h.Add(tick);
        h.Add(entity.Index);
        h.Add(entity.Generation);
        h.Add(sample);
        return h.Value;
    }

    /// <summary>Равномерно в [0, 1) — 24 бита, точно представимые во float.</summary>
    public float Float(uint stream, ulong tick, Entity entity, uint sample = 0) =>
        (Bits(stream, tick, entity, sample) >> 40) * (1f / (1 << 24));

    /// <summary>Равномерно в [<paramref name="min"/>, <paramref name="max"/>).</summary>
    public float Range(uint stream, ulong tick, Entity entity, float min, float max, uint sample = 0) =>
        min + (max - min) * Float(stream, tick, entity, sample);

    /// <summary>Целое в [0, <paramref name="count"/>) без смещения по модулю (умножение старших битов).</summary>
    public int Index(uint stream, ulong tick, Entity entity, int count, uint sample = 0)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count), count, "нужно ≥ 1");
        return (int)(((Bits(stream, tick, entity, sample) >> 32) * (ulong)count) >> 32);
    }
}

/// <summary>Хеш состояния после тиков <see cref="Tick"/> (номер следующего тика): итог и хеш каждого участника по Id.</summary>
public sealed record StateHash(ulong Tick, ulong Total, IReadOnlyDictionary<string, ulong> Parts)
{
    public override string ToString() => $"тик {Tick}: {Total:X16} ({string.Join(", ", Parts.Select(p => $"{p.Key} {p.Value:X16}"))})";
}
