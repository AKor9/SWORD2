using System.Numerics;

namespace Sword.Engine.Physics;

/// <summary>
/// Физика одной симуляции (раздел 6 плана): тела-капсулы и сочленения за хэндлами (индекс + поколение). Интерфейс, а не
/// класс, — чтобы в одном процессе жили две реализации (эталон и оптимизированный кандидат: дифференциальный прогон,
/// раздел 8); у каждой симуляции свой экземпляр, общего состояния нет. Выдача слотов — часть договора: при одних вызовах
/// две реализации выдают одни хэндлы (свободные — с конца списка, первыми — младшие номера), иначе их не сравнить.
///
/// Этап 5: только кинематические тела — форма (<see cref="SetShape"/>), постановка (<see cref="Place"/>,
/// <see cref="SetKinematicTarget"/>), концы (<see cref="BodyState.EndA"/>, <see cref="BodyState.EndB"/>). Шаг, режим
/// <c>Simulated</c>, импульсы, контакты и запросы — с первыми потребителями (этапы 6–7). Устаревший хэндл — исключение
/// (<see cref="InvalidOperationException"/>) в любом вызове, кроме <c>IsAlive</c>. Установившийся режим (постановка,
/// создание и удаление сочленений прежних размеров) не выделяет памяти.
/// </summary>
public interface IPhysicsWorld
{
    /// <summary>
    /// Новое сочленение из <paramref name="bodies"/> тел (от 1): тела кинематические, без формы, в начале координат
    /// (<see cref="BodyState.New"/>). Тела берутся из свободных слотов по порядку: тело 0 — первым.
    /// </summary>
    ArticulationHandle CreateArticulation(int bodies);

    /// <summary>Удалить сочленение вместе с его телами: их хэндлы устаревают.</summary>
    void DestroyArticulation(ArticulationHandle articulation);

    bool IsAlive(ArticulationHandle articulation);

    bool IsAlive(BodyHandle body);

    /// <summary>Тел в сочленении.</summary>
    int BodyCount(ArticulationHandle articulation);

    /// <summary>Тело <paramref name="index"/> сочленения (0 … <see cref="BodyCount"/> − 1).</summary>
    BodyHandle Body(ArticulationHandle articulation, int index);

    /// <summary>Сочленение, которому принадлежит тело.</summary>
    ArticulationHandle ArticulationOf(BodyHandle body);

    /// <summary>Состояние тела (копия).</summary>
    BodyState Get(BodyHandle body);

    /// <summary>
    /// Форма капсулы (<c>RigidBody.SetShape</c> без массы и инерции — они у динамики, этап 7): радиус и полудлина оси, м,
    /// конечные и не отрицательные.
    /// </summary>
    void SetShape(BodyHandle body, float radius, float halfLength);

    /// <summary>Поставить тело без скорости (<c>RigidBody.Place</c>): прошлая постановка — та же, скорости — 0.</summary>
    void Place(BodyHandle body, Vector3 position, Quaternion rotation);

    /// <summary>
    /// Кинематическое тело — в цель тика (кинематическая ветка <c>PlaceBodies</c> SWORD): прошлая постановка ← нынешняя,
    /// скорость — разность за <paramref name="dt"/>, угловая — <see cref="PhysicsMath.RotationVector"/> / dt; тело сразу в
    /// цели (шаг физики кинематическое тело не двигает). <paramref name="dt"/> — конечный и больше 0.
    /// </summary>
    void SetKinematicTarget(BodyHandle body, Vector3 position, Quaternion rotation, float dt);

    /// <summary>Живых сочленений.</summary>
    int ArticulationCount { get; }

    /// <summary>Живых тел.</summary>
    int LiveBodyCount { get; }

    /// <summary>Слотов сочленений (живых и свободных).</summary>
    int ArticulationCapacity { get; }

    /// <summary>Поколение слота сочленения: у живого — поколение, у свободного — со знаком минус, у ни разу не выданного — 0.</summary>
    int ArticulationGenerationAt(int index);

    /// <summary>Живое сочленение в слоте; свободный — <see cref="ArticulationHandle.None"/> (обход по слотам — стабильный порядок).</summary>
    ArticulationHandle ArticulationAt(int index);

    /// <summary>Свободные слоты сочленений в порядке выдачи с конца (часть состояния: от него зависят будущие хэндлы).</summary>
    ReadOnlySpan<int> FreeArticulations { get; }

    /// <summary>Слотов тел (живых и свободных).</summary>
    int BodyCapacity { get; }

    /// <summary>Поколение слота тела — как у <see cref="ArticulationGenerationAt"/>.</summary>
    int BodyGenerationAt(int index);

    /// <summary>Живое тело в слоте; свободный — <see cref="BodyHandle.None"/>.</summary>
    BodyHandle BodyAt(int index);

    /// <summary>Свободные слоты тел в порядке выдачи с конца.</summary>
    ReadOnlySpan<int> FreeBodies { get; }
}
