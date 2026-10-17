using System.Numerics;
using Sword.Engine.Ecs;

namespace Sword.Game.Contracts;

/// <summary>Отклик тела на удар по силе: вздрогнуть, пошатнуться (прерывает удар, отталкивает), упасть.</summary>
public enum HitLevel : byte
{
    Flinch = 0,
    Stagger = 1,
    Knockdown = 2,
}

/// <summary>
/// Отклик на попадание по живому (событие Dismember, модуль Impact) — исполняет ядро в React: вздрагивание с силой
/// <see cref="Strength"/>, пошатывание или падение, толчок корня <see cref="Push"/>, отдача оружия атакующего <see cref="Recoil"/>.
/// Без модуля попадания не толкают и реакций нет.
/// </summary>
public struct HitResponse
{
    public Entity Attacker, Victim;
    public int Part;
    public HitLevel Level;

    /// <summary>Сила вздрагивания, 0…1.</summary>
    public float Strength;

    /// <summary>Импульс удара телу, Н·с (мир), и точка касания.</summary>
    public Vector3 Impulse, Point;

    /// <summary>Прибавка скорости корня жертвы, м/с (по земле).</summary>
    public Vector3 Push;

    /// <summary>Доля импульса оружия, которую забрало тело: 0 — прошло насквозь, 1 и больше — оружие встало (тупое — отскакивает).</summary>
    public float Recoil;

    /// <summary>Тупое оружие (молот).</summary>
    public bool Blunt;
}

/// <summary>
/// Живой боец сбит с ног (ядро, по отклику <see cref="HitLevel.Knockdown"/>): тела — на физике с профилем трупа (меч в руке), анимация
/// стоит, корень идёт за тазом; отлежался — встаёт клипом вставания от позы тел. Убит лёжа — остаётся лежать трупом.
/// </summary>
public struct Downed
{
    /// <summary>Когда сбит, с; сколько уже лежит спокойно, с.</summary>
    public float Time, Still;

    /// <summary>Удар, которым сбит: импульс, Н·с (мир), точка и часть — прикладывается при переходе на физику.</summary>
    public Vector3 Impulse, Point;
    public int Part;

    /// <summary>Не встаёт: нога сломана — на ней не стоят.</summary>
    public bool Crippled;
}
