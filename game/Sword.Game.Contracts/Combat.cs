using System.Numerics;
using Sword.Engine.Ecs;

namespace Sword.Game.Contracts;

/// <summary>
/// Оружие в руке бойца: номер в таблице оружия (<see cref="ICombatView.Weapons"/>, мечи пака по алфавиту). Враг получает
/// случайный меч при каждой постройке, игрок — меч инвентаря (<see cref="PlayerInventory.Equipped"/>).
/// </summary>
public struct WeaponSlot
{
    public int Index;
}

/// <summary>
/// Клинок бойца в мире после постановки тела тика (PrePhysics): основание (22 см от кисти) и кончик, как показан боец
/// (кисть hand_r позы тика в мире бойца), и их положение прошлого тика — отрезок, который клинок заметает за тик.
/// Тело клинка — капсула 11 сочленения (<see cref="BladeBody"/>). При постройке и смене меча прошлое равно нынешнему
/// (<see cref="Weapon"/> — меч расчёта): фантомного прохода между разными клинками нет.
/// </summary>
public struct Blade
{
    public Vector3 Base, Tip;
    public Vector3 PrevBase, PrevTip;

    /// <summary>Меч, клинок которого здесь (номер таблицы оружия); −1 — клинок ещё не поставлен (боец только что построен).</summary>
    public int Weapon;

    /// <summary>Клинок не поставлен: следующая постановка начнёт без прошлого (прошлое = нынешнему).</summary>
    public static readonly Blade None = new() { Weapon = -1 };
}

/// <summary>
/// Инвентарь игрока — ресурс-значение (хешируется): доступное оружие битами номеров таблицы (до 64 мечей) и выбранное —
/// меч в руке игрока. Меняют его команды арены (<see cref="ArenaAction.NextWeapon"/>, <see cref="ArenaAction.EquipWeapon"/>).
/// </summary>
public struct PlayerInventory
{
    /// <summary>Мечей в инвентаре не больше: доступное — биты <see cref="ulong"/>.</summary>
    public const int MaxWeapons = 64;

    /// <summary>Доступное оружие: бит <c>1 &lt;&lt; номер</c>. Порядок получения — по возрастанию номера (всё выдано сразу).</summary>
    public ulong Owned;

    /// <summary>Выбранное оружие — номер в таблице.</summary>
    public int Equipped;

    public readonly bool Has(int weapon) => (uint)weapon < MaxWeapons && (Owned & 1UL << weapon) != 0;

    /// <summary>Сколько оружия доступно.</summary>
    public readonly int Count => BitOperations.PopCount(Owned);

    /// <summary>Оружие <paramref name="index"/>-е по порядку получения (строка панели и клавиша 1–9) — номер в таблице; нет — −1.</summary>
    public readonly int OwnedAt(int index)
    {
        ulong rest = Owned;
        for (int i = 0; rest != 0; i++, rest &= rest - 1)
            if (i == index) return BitOperations.TrailingZeroCount(rest);
        return -1;
    }

    /// <summary>Взять оружие <paramref name="weapon"/> (<c>WeaponInventory.Equip</c>); нет его — false, выбор прежний.</summary>
    public bool Equip(int weapon)
    {
        if (!Has(weapon)) return false;
        Equipped = weapon;
        return true;
    }

    /// <summary>Следующее доступное оружие по кругу; выбранное недоступно — первое доступное; инвентарь пуст — ничего.</summary>
    public void EquipNext()
    {
        if (Owned == 0) return;
        ulong after = (uint)Equipped < MaxWeapons - 1 && Has(Equipped) ? Owned & ~0UL << Equipped + 1 : 0;
        Equipped = BitOperations.TrailingZeroCount(after != 0 ? after : Owned);
    }

    /// <summary>Всё оружие таблицы из <paramref name="count"/> мечей, в руке — <paramref name="equipped"/>.</summary>
    public static PlayerInventory All(int count, int equipped)
    {
        if (count is < 1 or > MaxWeapons) throw new ArgumentOutOfRangeException(nameof(count), count, $"мечей 1…{MaxWeapons}");
        var inventory = new PlayerInventory { Owned = count == MaxWeapons ? ~0UL : (1UL << count) - 1 };
        if (!inventory.Equip(equipped)) throw new ArgumentOutOfRangeException(nameof(equipped), equipped, $"меча {equipped} нет (мечей {count})");
        return inventory;
    }
}

/// <summary>
/// Оружие таблицы: имя (файл пака без расширения), меш (<c>weapons/имя.swmesh</c>, вершины в системе кисти hand_r)
/// и клинок в системе кисти: кончик — дальняя от кисти вершина, основание — 22 см от кисти по той же оси.
/// </summary>
public readonly record struct WeaponInfo(string Name, string Mesh, Vector3 BladeBase, Vector3 BladeTip)
{
    /// <summary>Длина клинка от основания до кончика, м.</summary>
    public float Length => Vector3.Distance(BladeBase, BladeTip);

    /// <summary>Тело клинка этого меча в системе кисти: капсула от хвата до кончика (<see cref="BladeBody.Of"/>).</summary>
    public BladeBody Body => BladeBody.Of(BladeTip);
}

/// <summary>
/// Тело клинка — тело 11 сочленения (<see cref="BodySegs.Weapon"/>) в системе кисти hand_r: ось (локальная Y) — от хвата
/// (начало системы кисти, конец A — точка сустава запястья трупа) до кончика. Локальная X — <see cref="XHint"/>, поворот
/// относительно кисти постоянен. Масса и радиус одни для всех мечей; смена меча меняет форму капсулы.
/// </summary>
public readonly record struct BladeBody(Vector3 Center, Vector3 Axis, Vector3 XHint, float HalfLength)
{
    /// <summary>Масса тела клинка, кг (одна для всех мечей).</summary>
    public const float Mass = 1.4f;

    /// <summary>Радиус капсулы клинка, м (отправная точка, есть ручка).</summary>
    public const float Radius = 0.03f;

    /// <summary>Хват в системе кисти — её начало: точка сустава запястья трупа.</summary>
    public static Vector3 Grip => Vector3.Zero;

    /// <summary>Хват в системе тела клинка — нижний конец оси (конец A капсулы).</summary>
    public Vector3 GripLocal => new(0, -HalfLength, 0);

    /// <summary>
    /// Тело клинка по кончику <paramref name="tip"/>: ось — от хвата (0) к кончику; опорная X — самая поперечная к клинку ось
    /// кисти без продольной части. Кончик в хвате — ось Y кисти, полудлина 0.
    /// </summary>
    public static BladeBody Of(Vector3 tip)
    {
        float length = tip.Length();
        var axis = length > 1e-6f ? tip / length : Vector3.UnitY;
        var hint = MathF.Abs(axis.X) <= MathF.Abs(axis.Y) && MathF.Abs(axis.X) <= MathF.Abs(axis.Z) ? Vector3.UnitX
            : MathF.Abs(axis.Y) <= MathF.Abs(axis.Z) ? Vector3.UnitY : Vector3.UnitZ;
        hint = Vector3.Normalize(hint - axis * Vector3.Dot(hint, axis));
        return new BladeBody(tip * 0.5f, axis, hint, length * 0.5f);
    }
}

/// <summary>
/// Окно урона одного удара в клипе: время, с, пиковая скорость кончика, м/с, её время и направление замаха
/// (x — вправо бойца, y — вверх).
/// </summary>
public readonly record struct StrikeWindow(float Start, float End, float PeakTime, float PeakSpeed, Vector2 SwingDir);

/// <summary>
/// Удар из таблицы ударов: слот (slash1–4, stab1–3), клип, клип отбитого (−1 — нет), длительность, укол, слой верха
/// и окна урона по кончику меча по умолчанию (хотя бы одно). Неизменяемое значение: его отдаёт вид только для чтения
/// (<see cref="ICombatView"/>), окна — только <see cref="ReadOnlySpan{T}"/>.
/// </summary>
public readonly struct AttackInfo(string slot, int clip, int blockedClip, float duration, bool thrust, bool upper, StrikeWindow[] windows)
{
    private readonly StrikeWindow[] _windows = windows.Length > 0 ? [.. windows]
        : throw new ArgumentException("у удара хотя бы одно окно", nameof(windows));

    /// <summary>Ключ слота действия (<c>slash1</c>…<c>stab3</c>).</summary>
    public string Slot { get; } = slot;
    public int Clip { get; } = clip;
    public int BlockedClip { get; } = blockedClip;
    public float Duration { get; } = duration;
    public bool Thrust { get; } = thrust;
    public bool Upper { get; } = upper;
    public ReadOnlySpan<StrikeWindow> Windows => _windows;

    public float ActiveEnd => _windows[^1].End;

    /// <summary>Направление замаха первого удара — по нему жест выбирает клип.</summary>
    public Vector2 SwingDir => _windows[0].SwingDir;

    /// <summary>Самый быстрый кончик среди ударов, м/с.</summary>
    public float PeakSpeed
    {
        get
        {
            float max = 0;
            foreach (var w in _windows) max = MathF.Max(max, w.PeakSpeed);
            return max;
        }
    }

    /// <summary>Время внутри одного из окон.</summary>
    public bool InWindow(float time)
    {
        foreach (var w in _windows)
            if (time >= w.Start && time <= w.End) return true;
        return false;
    }
}

/// <summary>Оружие и удары для наблюдателей симуляции: вид только для чтения на таблицу оружия и таблицу ударов.</summary>
public interface ICombatView : IReadView
{
    /// <summary>Оружие таблицы по номеру (<see cref="WeaponSlot.Index"/>).</summary>
    ReadOnlySpan<WeaponInfo> Weapons { get; }

    /// <summary>Меч по умолчанию (<c>sword_1h</c>, нет — первый): по его клинку размечены окна ударов.</summary>
    int DefaultWeapon { get; }

    /// <summary>Оружие врагов — номера таблицы по возрастанию, из которых враг берёт случайное (без оружия только игрока).</summary>
    ReadOnlySpan<int> EnemyWeapons { get; }

    /// <summary>Сустав скелета, в котором оружие (hand_r): меч в мире — его модельная матрица · мир бойца.</summary>
    int HandJoint { get; }

    /// <summary>Удары: рубящие slash1–4, затем уколы stab1–3.</summary>
    ReadOnlySpan<AttackInfo> Attacks { get; }

    /// <summary>Номер оружия по имени (без учёта регистра); нет — −1.</summary>
    int FindWeapon(string name);
}
