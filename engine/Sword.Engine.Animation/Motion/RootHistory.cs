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
    private readonly RootSample[] _ring;
    private int _oldest;
    private int _count;

    /// <param name="tickSeconds">Длительность тика, с (&gt; 0).</param>
    /// <param name="seconds">Глубина истории, с (&gt; 0; обычно — <see cref="MotionSchema.PastDepth"/>): ёмкость — столько тиков и запас.</param>
    /// <exception cref="ArgumentOutOfRangeException">Тик или глубина не конечны или не больше 0.</exception>
    public RootHistory(float tickSeconds, float seconds)
    {
        if (!(tickSeconds > 0) || !float.IsFinite(tickSeconds))
            throw new ArgumentOutOfRangeException(nameof(tickSeconds), tickSeconds, "тик — конечный и больше 0");
        if (!(seconds > 0) || !float.IsFinite(seconds))
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "глубина — конечная и больше 0");
        TickSeconds = tickSeconds;
        _ring = new RootSample[CapacityFor(tickSeconds, seconds)];
    }

    /// <summary>
    /// Ёмкость: <c>ceil(глубина / тик) + 2</c>. Самый дальний тик, который читает запрос, — <c>now − round(глубина / тик)</c>,
    /// не дальше <c>now − ceil(…)</c>; запись не позже него есть в кольце, пока в нём тики <c>now − ceil(…) … now</c> — это
    /// <c>ceil + 1</c> записей (последняя — текущего тика, если игра уже записала его), и ещё одна — запас (последняя запись —
    /// прошлого тика). Частное, отличающееся от целого меньше чем на 10⁻⁴, — это целое (округление float: 1/3 с при 120 Гц —
    /// 39,999999): ёмкость не прыгает на единицу от способа записать тик.
    /// </summary>
    private static int CapacityFor(float tickSeconds, float seconds)
    {
        double ticks = Math.Ceiling(seconds / (double)tickSeconds - 1e-4);
        if (ticks > int.MaxValue / 2) throw new ArgumentOutOfRangeException(nameof(seconds), seconds, $"глубина — {ticks} тиков");
        return (int)ticks + 2;
    }

    /// <summary>Длительность тика, с.</summary>
    public float TickSeconds { get; }

    /// <summary>Записей помещается.</summary>
    public int Capacity => _ring.Length;

    /// <summary>Записей сейчас.</summary>
    public int Count => _count;

    /// <summary>Запись <paramref name="index"/>: 0 — старейшая, <c>Count − 1</c> — последняя.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Нет такой записи.</exception>
    public RootSample this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index), index, $"записей {_count}");
            return Get(index);
        }
    }

    /// <summary>Записать корень тика <paramref name="tick"/> (строго позже последней записи); полное кольцо теряет старейшую.</summary>
    /// <exception cref="ArgumentException">Тик не позже последней записи или корень не конечен.</exception>
    public void Add(long tick, Pose2 root)
    {
        if (!root.IsFinite) throw new ArgumentException($"корень тика {tick} не конечен: {root}", nameof(root));
        if (_count > 0 && tick <= Get(_count - 1).Tick)
            throw new ArgumentException($"тик {tick} не позже последней записи {Get(_count - 1).Tick}", nameof(tick));
        if (_count < _ring.Length)
        {
            _ring[Slot(_count)] = new RootSample(tick, root);
            _count++;
        }
        else
        {
            _ring[_oldest] = new RootSample(tick, root);
            _oldest = Slot(1);
        }
    }

    /// <summary>Забыть всё (возрождение, сброс).</summary>
    public void Clear()
    {
        _oldest = 0;
        _count = 0;
    }

    /// <summary>
    /// Корень на тике <paramref name="tick"/>: последняя запись не позже него (без интерполяции); все записи позже — старейшая,
    /// сдвинутая назад на <paramref name="velocity"/> (мир, м/с) × разницу тиков × <see cref="TickSeconds"/>; записей нет —
    /// <paramref name="fallback"/>.
    /// </summary>
    public Pose2 At(long tick, Vector3 velocity, Pose2 fallback)
    {
        if (_count == 0) return fallback;
        var oldest = Get(0);
        if (oldest.Tick > tick)
        {
            float back = (oldest.Tick - tick) * TickSeconds;
            return new Pose2(oldest.Root.X - velocity.X * back, oldest.Root.Z - velocity.Z * back, oldest.Root.Yaw);
        }
        // последняя запись не позже тика: Get(lo).Tick ≤ tick всегда, записи — по возрастанию тиков
        int lo = 0, hi = _count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >>> 1;
            if (Get(mid).Tick <= tick) lo = mid;
            else hi = mid - 1;
        }
        return Get(lo).Root;
    }

    private int Slot(int index)
    {
        int slot = _oldest + index;
        return slot < _ring.Length ? slot : slot - _ring.Length;
    }

    private RootSample Get(int index) => _ring[Slot(index)];
}
