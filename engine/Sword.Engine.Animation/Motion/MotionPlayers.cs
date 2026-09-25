using System.Numerics;

namespace Sword.Engine.Animation.Motion;

/// <summary>
/// Проигрыватели Motion Matching (точка 8г) — хранилище состояний за хэндлами с поколением (как у <see cref="PosePool"/>),
/// у каждой симуляции своё; привязано к одной базе (номера кадров имеют смысл только в ней). Состояние проигрывателя —
/// авторитетное (хешируется): кадр и доля интервала, число переходов, на каждый сустав скелета смещение поворота и смещение
/// позиции инерциализации и их скорости (смещения позиции — только у <see cref="MotionDatabase.MovingJoints"/>), угловые и
/// линейные скорости выхода и сам выход прошлого задания.
///
/// Меняет состояние ТОЛЬКО задание пула <see cref="PosePool.Motion"/> — в общей очереди, по порядку, вместе с позой (всё, что
/// зависит от выхода позы: продвижение, переход, затухание смещений, сэмпл кадра с долей, скорости выхода). Система игры ставит
/// задание (с запросом перехода) и читает состояние прошлого тика; открытые методы ниже — только выдача, освобождение и
/// чтение (для поиска, хеша и <c>Describe</c>). Задание к освобождённому проигрывателю пропускается, чтение — исключение.
/// </summary>
public sealed class MotionPlayers
{
    /// <param name="database">База, в кадрах которой живут проигрыватели (скелет — её библиотеки).</param>
    /// <param name="capacity">Начальная ёмкость (растёт удвоением).</param>
    public MotionPlayers(MotionDatabase database, int capacity = 1) =>
        throw new NotImplementedException("8г: хранилище проигрывателей — MotionPlayers");

    public MotionDatabase Database => throw new NotImplementedException("8г");

    /// <summary>Суставов у проигрывателя — суставов скелета базы.</summary>
    public int JointCount => throw new NotImplementedException("8г");

    /// <summary>Живых проигрывателей.</summary>
    public int Count => throw new NotImplementedException("8г");

    /// <summary>Слотов (живых и свободных) — логическая ёмкость, растёт удвоением.</summary>
    public int Capacity => throw new NotImplementedException("8г");

    /// <summary>Слотов, под которые уже есть память (<see cref="Reserve"/>); не меньше <see cref="Capacity"/>.</summary>
    public int Reserved => throw new NotImplementedException("8г");

    /// <summary>
    /// Память заранее под <paramref name="players"/> живых проигрывателей — как <see cref="PosePool.Reserve"/>: логика (ёмкость,
    /// номера, поколения, порядок свободных слотов) та же, что без запаса.
    /// </summary>
    public void Reserve(int players) => throw new NotImplementedException("8г");

    /// <summary>Новый проигрыватель — не начат (кадр −1): первое задание с переходом ставит кадр без инерциализации.</summary>
    public MotionPlayerHandle Alloc() => throw new NotImplementedException("8г");

    /// <exception cref="InvalidOperationException">Проигрыватель уже освобождён или чужой.</exception>
    public void Free(MotionPlayerHandle player) => throw new NotImplementedException("8г");

    public bool IsAlive(MotionPlayerHandle player) => throw new NotImplementedException("8г");

    /// <summary>Живой проигрыватель в слоте <paramref name="index"/>; свободен — <see cref="MotionPlayerHandle.None"/> (обход — стабильный порядок).</summary>
    public MotionPlayerHandle HandleAt(int index) => throw new NotImplementedException("8г");

    /// <summary>Свободные слоты в порядке выдачи с конца — часть состояния (от него зависят номера будущих проигрывателей).</summary>
    public ReadOnlySpan<int> FreeSlots => throw new NotImplementedException("8г");

    /// <summary>Поколение слота: у живого — его, у свободного — со знаком минус, у ни разу не выданного — 0.</summary>
    public int GenerationAt(int index) => throw new NotImplementedException("8г");

    /// <summary>Кадр, доля, переходы, есть ли выход, перенос корня последнего задания.</summary>
    public MotionPlayerState State(MotionPlayerHandle player) => throw new NotImplementedException("8г");

    /// <summary>
    /// Ближний к положению проигрывателя кадр базы — текущий кадр для поиска и запроса: доля ≥ 0,5 — следующий кадр того же
    /// клипа (последний кадр цикла — его начало), иначе сам кадр; не начат — −1.
    /// </summary>
    public int NearestFrame(MotionPlayerHandle player) => throw new NotImplementedException("8г");

    /// <summary>[сустав] смещение поворота инерциализации (выход = смещение · поворот кадра).</summary>
    public ReadOnlySpan<Quaternion> RotationOffsets(MotionPlayerHandle player) => throw new NotImplementedException("8г");

    /// <summary>[сустав] скорость смещения поворота — вектор поворота, рад/с.</summary>
    public ReadOnlySpan<Vector3> RotationOffsetVelocities(MotionPlayerHandle player) => throw new NotImplementedException("8г");

    /// <summary>[сустав] смещение позиции инерциализации, м (не ноль — только у движущихся суставов).</summary>
    public ReadOnlySpan<Vector3> PositionOffsets(MotionPlayerHandle player) => throw new NotImplementedException("8г");

    /// <summary>[сустав] скорость смещения позиции, м/с.</summary>
    public ReadOnlySpan<Vector3> PositionOffsetVelocities(MotionPlayerHandle player) => throw new NotImplementedException("8г");

    /// <summary>[сустав] угловая скорость выхода (локальный поворот), рад/с.</summary>
    public ReadOnlySpan<Vector3> AngularVelocities(MotionPlayerHandle player) => throw new NotImplementedException("8г");

    /// <summary>[сустав] линейная скорость выхода (локальное смещение), м/с.</summary>
    public ReadOnlySpan<Vector3> LinearVelocities(MotionPlayerHandle player) => throw new NotImplementedException("8г");

    /// <summary>[сустав] локальные повороты выхода прошлого задания.</summary>
    public ReadOnlySpan<Quaternion> OutputRotations(MotionPlayerHandle player) => throw new NotImplementedException("8г");

    /// <summary>[сустав] локальные смещения выхода прошлого задания.</summary>
    public ReadOnlySpan<Vector3> OutputPositions(MotionPlayerHandle player) => throw new NotImplementedException("8г");
}
