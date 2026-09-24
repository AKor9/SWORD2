using System.Numerics;
using Sword.Engine.Ecs;

namespace Sword.Game.Contracts;

/// <summary>
/// Оружие в руке бойца (<c>Fighter.WeaponKind</c> SWORD 2eab13b): номер в таблице оружия (<see cref="ICombatView.Weapons"/>,
/// мечи пака по алфавиту). Враг получает случайный меч при каждой постройке (<c>DuelArena.Spawn</c>), игрок — меч инвентаря
/// (<see cref="PlayerInventory.Equipped"/>: конфиг <c>combat.playerWeapon</c>, T — следующий, этап 6б). Клинок меча — в
/// <see cref="Blade"/>.
/// </summary>
public struct WeaponSlot
{
    public int Index;
}

/// <summary>
/// Клинок бойца в мире после постановки тела тика (<c>KinBase</c>, <c>KinTip</c>, <c>KinPrevBase</c>, <c>KinPrevTip</c> SWORD
/// 2eab13b; PrePhysics, этап 6б): основание (у гарды, 22 см от кисти) и кончик клинка меча <see cref="WeaponSlot"/>, как
/// показан боец — модельная матрица кисти (hand_r) позы тика в мире бойца (корень, курс, высота рельефа — тот же мир, что у
/// капсул тела), — и их положение прошлого тика: отрезок, который клинок заметает за тик (проходы клинка — 6г). Физического
/// тела у клинка нет (этап 7). Постройка бойца и смена меча — прошлое равно нынешнему (<see cref="Weapon"/> — меч, которым
/// клинок посчитан): прохода между разными клинками нет (в SWORD смена меча давала один такой фантомный проход).
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
/// Инвентарь игрока (<c>WeaponInventory</c> SWORD 2eab13b) — ресурс-значение (хешируется): доступное оружие — битами номеров
/// таблицы оружия (пока всё оружие пака сразу, как <c>DuelArena.AttachRig</c>; до 64 мечей) и выбранное — меч в руке игрока
/// (<see cref="WeaponSlot"/> игрока равен ему; постройка игрока заново — с ним). Меняют его команды арены
/// (<see cref="ArenaAction.NextWeapon"/>, <see cref="ArenaAction.EquipWeapon"/>); панель инвентаря (I, 1–9) — этап 6е.
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

    /// <summary>Взять оружие <paramref name="weapon"/> (<c>WeaponInventory.Equip</c>); нет его — false, выбор прежний.</summary>
    public bool Equip(int weapon)
    {
        if (!Has(weapon)) return false;
        Equipped = weapon;
        return true;
    }

    /// <summary>
    /// Следующее доступное оружие по кругу (<c>WeaponInventory.EquipNext</c>: за выбранным в порядке получения, после
    /// последнего — первое); выбранное недоступно — первое доступное; инвентарь пуст — ничего.
    /// </summary>
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
/// Оружие таблицы (<c>CharacterRig.Weapon</c> SWORD 2eab13b): имя (файл пака без расширения), путь меша в контенте
/// (<c>weapons/имя.swmesh</c>: статический меш, вершины в системе кисти hand_r) и клинок в системе кисти — кончик — самая
/// дальняя от кисти вершина меша, основание — 22 см от кисти по той же оси (<c>MakeWeapon</c>).
/// </summary>
public readonly record struct WeaponInfo(string Name, string Mesh, Vector3 BladeBase, Vector3 BladeTip)
{
    /// <summary>Длина клинка от основания до кончика, м.</summary>
    public float Length => Vector3.Distance(BladeBase, BladeTip);
}

/// <summary>
/// Окно урона одного удара в клипе (<c>StrikeWindow</c> SWORD 2eab13b): время, с, пиковая скорость кончика, м/с, её время и
/// направление замаха (x — вправо бойца, y — вверх).
/// </summary>
public readonly record struct StrikeWindow(float Start, float End, float PeakTime, float PeakSpeed, Vector2 SwingDir);

/// <summary>
/// Удар из таблицы ударов (<c>AttackInfo</c> SWORD 2eab13b: <c>CharacterRig.Analyse</c>) — слот (slash1–4, stab1–3), его клип,
/// клип отбитого удара (<c>имя_Blocked</c>, −1 — нет), длительность, укол, слой верха тела и окна урона по кончику клинка меча
/// по умолчанию (хотя бы одно; связка — несколько). Неизменяем: таблица — ассет симуляции; значение, а не ссылка на объект —
/// его отдаёт вид только для чтения (<see cref="ICombatView"/>, тест <c>ReadViews_OnlyRead</c>), окна — только <see cref="ReadOnlySpan{T}"/>.
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

    /// <summary>Начало первого окна и конец последнего: связка занята от первого удара до последнего.</summary>
    public float ActiveStart => _windows[0].Start;
    public float ActiveEnd => _windows[^1].End;

    /// <summary>Направление замаха первого удара — по нему жест выбирает клип (этап 6в).</summary>
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

/// <summary>
/// Оружие и удары для наблюдателей симуляции (клиент — меч в руке, агент — <c>combat.weapons</c>): вид только для чтения на
/// неизменяемые ассеты боя — таблицу оружия (мечи пака по алфавиту) и таблицу ударов.
/// </summary>
public interface ICombatView : IReadView
{
    /// <summary>Оружие таблицы по номеру (<see cref="WeaponSlot.Index"/>).</summary>
    ReadOnlySpan<WeaponInfo> Weapons { get; }

    /// <summary>Меч по умолчанию (<c>sword_1h</c>, нет — первый): по его клинку размечены окна ударов.</summary>
    int DefaultWeapon { get; }

    /// <summary>Сустав скелета, в котором оружие (hand_r): меч в мире — его модельная матрица · мир бойца.</summary>
    int HandJoint { get; }

    /// <summary>Удары: рубящие slash1–4, затем уколы stab1–3.</summary>
    ReadOnlySpan<AttackInfo> Attacks { get; }

    /// <summary>Номер оружия по имени (без учёта регистра); нет — −1.</summary>
    int FindWeapon(string name);
}
