using System.Numerics;
using System.Runtime.CompilerServices;

namespace Sword.Engine.Animation.Motion;

// Контракт Motion Matching: типы, которыми обмениваются постройка базы, поиск и запрос, проигрыватель и
// задание пула и модуль игры. Движок не знает игры и контента: знание о клипах — у политики графа, её реализует модуль.

/// <summary>Прошлое траектории у начала нециклического клипа (там, где −t уходит за начало клипа).</summary>
public enum MotionPast : byte
{
    /// <summary>«Стоял»: до начала клипа корень стоял в его первом кадре (старт с места, поворот на месте, стойка).</summary>
    Standing,

    /// <summary>«Шёл с начальной скоростью»: назад от первого кадра — по средней скорости начала клипа (<see cref="MotionClipInfo.StartVelocity"/>).</summary>
    StartVelocity,
}

/// <summary>
/// Измеренное движение клипа базы — по нему политика графа выбирает продолжение. Скорости корня, м/с, в системе корня
/// (+Z вперёд, +X влево): начала и выхода — средние за <see cref="MotionBuildParams.VelocityWindow"/>, средняя — за клип.
/// </summary>
/// <param name="LibraryClip">Номер клипа в <see cref="ClipLibrary"/> (имя — <c>library.Clips[LibraryClip].Name</c>).</param>
/// <param name="Turn">Поворот корня за клип, рад (курс последнего кадра относительно первого): непрерывный, может превышать π.</param>
public readonly record struct MotionClipInfo(int LibraryClip, bool Loop, int Frames, Vector3 StartVelocity, Vector3 ExitVelocity,
    Vector3 MeanVelocity, float Turn)
{
    public float ExitSpeed => ExitVelocity.Length();
    public float MeanSpeed => MeanVelocity.Length();

    /// <summary>Направление скорости выхода в системе корня последнего кадра, рад: 0 — вперёд, + — от +Z к +X (влево), в (−π, π].</summary>
    public float ExitDirection => Pose2.Wrap(MathF.Atan2(ExitVelocity.X, ExitVelocity.Z));

    /// <summary>Направление средней скорости в системе первого кадра, рад (как <see cref="ExitDirection"/>).</summary>
    public float MeanDirection => Pose2.Wrap(MathF.Atan2(MeanVelocity.X, MeanVelocity.Z));
}

/// <summary>
/// Политика графа переходов — знание о контенте, которого нет у движка. Реализует модуль игры; зовётся только при
/// постройке базы, в тике — никогда. Номера клипов — индексы в <paramref name="clips"/>.
/// </summary>
public interface IMotionGraphPolicy
{
    /// <summary>
    /// Кандидаты продолжения нециклического клипа: клипы базы, в кадры которых может перейти его конец. Лучший кадр входа база
    /// выбирает сама — ценой шва. Ни одного кандидата — продолжения нет: кадры без полного горизонта будущего не ищутся.
    /// </summary>
    /// <param name="candidates">Пустой список, куда писать номера клипов базы (порядок и повторы не важны).</param>
    void Continuations(int clip, ReadOnlySpan<MotionClipInfo> clips, List<int> candidates);

    /// <summary>Прошлое у начала нециклического клипа <paramref name="clip"/>: стоял или шёл с начальной скоростью.</summary>
    MotionPast Past(int clip, ReadOnlySpan<MotionClipInfo> clips);
}

/// <summary>
/// Детектор контактов стоп: сустав на земле, если низко (над самой низкой точкой в клипе) и почти не движется; вход и выход —
/// с гистерезисом, серии короче <paramref name="MinRun"/> — шум. Голеностоп при 30 к/с: 0,05/0,07 м, 0,35/0,55 м/с, серия 2.
/// </summary>
/// <param name="HeightEnter">Выше «пола» клипа не больше — может встать, м.</param>
/// <param name="HeightExit">Выше — отрывается, м (не меньше <paramref name="HeightEnter"/>).</param>
/// <param name="SpeedEnter">Горизонтальная скорость меньше — может встать, м/с.</param>
/// <param name="SpeedExit">Больше — отрывается, м/с (не меньше <paramref name="SpeedEnter"/>).</param>
/// <param name="MinRun">Внутренняя серия контакта или отрыва короче — переворачивается, кадров (≥ 1).</param>
public readonly record struct ContactParams(float HeightEnter, float HeightExit, float SpeedEnter, float SpeedExit, int MinRun);

/// <summary>Параметры постройки базы (<see cref="MotionDatabase.Build"/>) — все от модуля, своих умолчаний у движка нет.</summary>
/// <param name="Contacts">Детектор контактов суставов <see cref="MotionSchema.ContactJoints"/>.</param>
/// <param name="ExitWindow">Окно раннего выхода, с: кадр выхода нециклического клипа ищется в его последних секундах; кадры окна входом не бывают.</param>
/// <param name="VelocityWindow">Окно средних скоростей начала и выхода клипа, с.</param>
/// <param name="MovingThreshold">Смещение сустава дальше этого, м, от первого кадра клипа — сустав с движущимся смещением (<see cref="MotionDatabase.MovingJoints"/>).</param>
/// <param name="SeamTie">
/// Разрыв ничьих, доля в [0, 1): цены шва, различающиеся меньше доли от большей из (цена, 1), равны — берётся меньший номер
/// кадра; ранний выход — только если заметно дешевле последнего кадра (устойчивость к классу процессора).
/// </param>
public readonly record struct MotionBuildParams(ContactParams Contacts, float ExitWindow, float VelocityWindow, float MovingThreshold,
    float SeamTie);

/// <summary>Клип базы: номер в библиотеке, первый кадр базы (локальный номер), число кадров, цикл.</summary>
public readonly record struct MotionClip(int LibraryClip, int Start, int Count, bool Loop)
{
    public int End => Start + Count;
}

/// <summary>Сводка базы: <paramref name="Edges"/> — кадров выхода, <paramref name="EarlyExits"/> — из них не последних в клипе; время постройки в отпечаток не входит.</summary>
public readonly record struct MotionDatabaseSummary(int Clips, int Frames, int Searchable, int Edges, int EarlyExits, double BuildMilliseconds);

/// <summary>
/// Цены поиска по группам схемы (заняты первые <see cref="MotionSchema.GroupCount"/>). Равенство — поэлементное: встроенное у
/// <c>InlineArray</c> бросает исключение.
/// </summary>
[InlineArray(MotionSchema.MaxGroups)]
public struct MotionGroupCosts : IEquatable<MotionGroupCosts>
{
    private float _cost;

    public readonly bool Equals(MotionGroupCosts other) => ((ReadOnlySpan<float>)this).SequenceEqual(other);

    public override readonly bool Equals(object? obj) => obj is MotionGroupCosts other && Equals(other);

    public override readonly int GetHashCode()
    {
        var hash = new HashCode();
        foreach (float cost in (ReadOnlySpan<float>)this) hash.Add(cost);
        return hash.ToHashCode();
    }
}

/// <summary>Итог поиска — значение без ссылок, память не выделяет.</summary>
/// <param name="Frame">Лучший кадр базы; −1 — искать было не в чем (ни одного искомого кадра вне окна пропуска).</param>
/// <param name="Cost">Цена лучшего кадра (взвешенная сумма квадратов разниц нормализованных признаков), без скидки.</param>
/// <param name="ContinuationCost">Цена продолжения — текущего кадра, без скидки; текущего нет — +∞.</param>
/// <param name="Groups">Цены лучшего кадра по группам (сумма по группам — <paramref name="Cost"/>).</param>
public readonly record struct MotionMatch(int Frame, float Cost, float ContinuationCost, int Blocks, int Frames, MotionGroupCosts Groups);

/// <summary>Проигрыватель в <see cref="MotionPlayers"/>: номер слота и поколение. Поколение 0 — «нет проигрывателя».</summary>
public readonly record struct MotionPlayerHandle(int Index, int Generation)
{
    public static readonly MotionPlayerHandle None = default;
    public bool IsNone => Generation == 0;
    public override string ToString() => IsNone ? "нет" : $"{Index}v{Generation}";
}

/// <summary>Скалярное состояние проигрывателя (<see cref="MotionPlayers.State"/>).</summary>
/// <param name="Frame">Кадр базы; −1 — ещё не начат (первое задание с переходом ставит кадр без перехода).</param>
/// <param name="Alpha">Доля интервала <c>Frame → Next[Frame]</c>, [0, 1).</param>
/// <param name="Transitions">Переходов с инерциализацией (запрошенных и по рёбрам графа).</param>
/// <param name="HasOutput">Выход прошлого задания есть — скорости выхода считаются разностью с ним.</param>
/// <param name="RootStep">Перенос корня клипа за продвижение последнего задания — в системе корня до шага.</param>
public readonly record struct MotionPlayerState(int Frame, float Alpha, int Transitions, bool HasOutput, Pose2 RootStep);

/// <summary>Шаг проигрывателя — параметры задания <see cref="PosePool.Motion"/>.</summary>
/// <param name="Advance">Продвижение по графу, с клипа (dt × темп), ≥ 0.</param>
/// <param name="Dt">Время тика, с (&gt; 0): затухание смещений и скорости выхода — во времени игры, а не клипа.</param>
/// <param name="Transition">Запрошенный переход — кадр базы (итог поиска); −1 — без перехода.</param>
/// <param name="Halflife">Полупериод затухания смещений инерциализации, с (&gt; 0).</param>
public readonly record struct MotionStep(float Advance, float Dt, int Transition, float Halflife);
