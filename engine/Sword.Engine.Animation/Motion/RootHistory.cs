using System.Numerics;

namespace Sword.Engine.Animation.Motion;

/// <summary>Запись истории корня: номер тика и система корня (мир) на нём.</summary>
public readonly record struct RootSample(long Tick, Pose2 Root);

/// <summary>
/// История корня (точка 8в) — кольцо записей по тикам (номера тиков целые: без накопления времени во float), ёмкость — от
/// частоты тика и нужной глубины, а не константа. Прошлое запроса — «последняя запись не позже тика»; история короче
/// нужного — старейшая запись, сдвинутая назад по скорости. Авторитетное состояние игрока (участвует в хеше): чтение по
/// порядку записей — <see cref="this[int]"/>. Пишет её игра после разрешения столкновений (корень, который был на самом деле).
/// Память выделяет только при создании.
/// </summary>
public sealed class RootHistory
{
    /// <param name="tickSeconds">Длительность тика, с (&gt; 0).</param>
    /// <param name="seconds">Глубина истории, с (&gt; 0; обычно — <see cref="MotionSchema.PastDepth"/>): ёмкость — столько тиков и запас.</param>
    public RootHistory(float tickSeconds, float seconds) => throw new NotImplementedException("8в: история корня — RootHistory");

    /// <summary>Длительность тика, с.</summary>
    public float TickSeconds => throw new NotImplementedException("8в");

    /// <summary>Записей помещается.</summary>
    public int Capacity => throw new NotImplementedException("8в");

    /// <summary>Записей сейчас.</summary>
    public int Count => throw new NotImplementedException("8в");

    /// <summary>Запись <paramref name="index"/>: 0 — старейшая, <c>Count − 1</c> — последняя.</summary>
    public RootSample this[int index] => throw new NotImplementedException("8в");

    /// <summary>Записать корень тика <paramref name="tick"/> (строго позже последней записи); полное кольцо теряет старейшую.</summary>
    /// <exception cref="ArgumentException">Тик не позже последней записи или корень не конечен.</exception>
    public void Add(long tick, Pose2 root) => throw new NotImplementedException("8в");

    /// <summary>Забыть всё (возрождение, сброс).</summary>
    public void Clear() => throw new NotImplementedException("8в");

    /// <summary>
    /// Корень на тике <paramref name="tick"/>: последняя запись не позже него (без интерполяции); все записи позже — старейшая,
    /// сдвинутая назад на <paramref name="velocity"/> (мир, м/с) × разницу тиков × <see cref="TickSeconds"/>; записей нет —
    /// <paramref name="fallback"/>.
    /// </summary>
    public Pose2 At(long tick, Vector3 velocity, Pose2 fallback) => throw new NotImplementedException("8в");
}
