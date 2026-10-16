using System.Numerics;

namespace Sword.Game.Contracts;

/// <summary>Что делает команда сцены (<see cref="SceneCommand"/>).</summary>
public enum SceneAction : byte
{
    /// <summary>Ничего (пустая команда — пропускается).</summary>
    None = 0,

    /// <summary>
    /// Поставить бойца места <see cref="SceneCommand.Slot"/> в точку <see cref="SceneCommand.Position"/> (x, z) курсом
    /// <see cref="SceneCommand.Facing"/>: постройка места заново на той же сущности — тело без скорости по позе постройки,
    /// мёртвый оживает; зерно и меч — прежние.
    /// </summary>
    Place = 1,

    /// <summary>Убить бойца места без удара: <see cref="Dead"/> с причиной <see cref="DeathCause.Command"/>.</summary>
    Kill = 2,

    /// <summary>
    /// Сдвинуть все тела мёртвого бойца места на <see cref="SceneCommand.Offset"/> без скорости (например, два трупа
    /// внахлёст); поворот, корень и поза не двигаются. Живому — ничего: его тела каждый тик ставит поза.
    /// </summary>
    ShiftBodies = 3,

    /// <summary>
    /// Меч места: <see cref="SceneCommand.Weapon"/> — номер таблицы оружия (<see cref="ICombatView.Weapons"/>). Игроку — через
    /// инвентарь (нет такого меча у игрока — ничего), мёртвому — ничего (меч выпадает).
    /// </summary>
    Weapon = 4,

    /// <summary>
    /// Намерение игрока на этот тик вместо команды игрока: желаемая скорость <see cref="SceneCommand.Velocity"/> (x, z), м/с
    /// (нечисловая — стоять) и курс-цель <see cref="SceneCommand.Facing"/> (нечисловой — из команды игрока). Только место 0.
    /// </summary>
    PlayerIntent = 5,
}

/// <summary>
/// Команда сцены (инструменты, тесты; реплей хранит её готовой): расстановка, смерть без удара, сдвиг тел трупа, меч
/// места, намерение игрока. Игра её не посылает. Места без бойца, нечисловые точка и курс — ничего. Все, кроме намерения,
/// — в Intent по порядку (после команд арены), Gameplay того же тика видит итог; намерение — в Gameplay, только на этот тик.
/// </summary>
public struct SceneCommand
{
    public SceneAction Action;

    /// <summary>Место бойца: 0 — игрок, 1…32 — враги (<see cref="FighterSlot"/>).</summary>
    public int Slot;

    /// <summary><see cref="SceneAction.Place"/>: точка корня на земле (x, z), м.</summary>
    public Vector2 Position;

    /// <summary><see cref="SceneAction.Place"/>: курс постройки, рад (0 — взгляд вдоль +Z). <see cref="SceneAction.PlayerIntent"/>: курс-цель.</summary>
    public float Facing;

    /// <summary><see cref="SceneAction.ShiftBodies"/>: сдвиг тел, м (мир).</summary>
    public Vector3 Offset;

    /// <summary><see cref="SceneAction.PlayerIntent"/>: желаемая скорость по земле (x, z), м/с — как её дал бы ИИ (не направление).</summary>
    public Vector2 Velocity;

    /// <summary><see cref="SceneAction.Weapon"/>: номер меча в таблице оружия.</summary>
    public int Weapon;
}
