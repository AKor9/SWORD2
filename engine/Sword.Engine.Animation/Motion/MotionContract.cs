using System.Numerics;
using System.Runtime.CompilerServices;

namespace Sword.Engine.Animation.Motion;

// Контракт Motion Matching (точка 8а): типы, которыми обмениваются постройка базы (8б), поиск и запрос (8в), проигрыватель и
// задание пула (8г) и модуль игры (8д, 8е). Движок не знает игры и контента: знание о клипах — у политики графа, её реализует модуль.

/// <summary>Прошлое траектории у начала нециклического клипа (там, где −t уходит за начало клипа).</summary>
public enum MotionPast : byte
{
    /// <summary>«Стоял»: до начала клипа корень стоял в его первом кадре (старт с места, поворот на месте, стойка).</summary>
    Standing,

    /// <summary>«Шёл с начальной скоростью»: назад от первого кадра — по средней скорости начала клипа (<see cref="MotionClipInfo.StartVelocity"/>).</summary>
    StartVelocity,
}

/// <summary>
/// Измеренное движение клипа базы — то, по чему политика графа (<see cref="IMotionGraphPolicy"/>) выбирает продолжение. Скорости —
/// корня, м/с, в системе корня: начала и средняя — первого кадра клипа, выхода — последнего кадра (+Z вперёд, +X влево);
/// начало и выход — средние за окно <see cref="MotionBuildParams.VelocityWindow"/>, средняя — за весь клип (у цикла — за оборот).
/// </summary>
/// <param name="LibraryClip">Номер клипа в <see cref="ClipLibrary"/> (имя — <c>library.Clips[LibraryClip].Name</c>).</param>
/// <param name="Turn">Поворот корня за клип, рад (курс последнего кадра относительно первого).</param>
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
/// Политика графа переходов — знание о контенте, которого нет у движка (имена клипов, «стойка», «походка»). Реализует модуль
/// игры; зовётся только при постройке базы (<see cref="MotionDatabase.Build"/>), в тике — никогда. Номера клипов — клипы базы
/// (индексы в <paramref name="clips"/>, по возрастанию номера в библиотеке).
/// </summary>
public interface IMotionGraphPolicy
{
    /// <summary>
    /// Кандидаты продолжения нециклического клипа <paramref name="clip"/>: клипы базы, в кадры которых может перейти его
    /// конец (по измеренному выходу <c>clips[clip]</c> — скорость, направление, поворот). Лучший кадр входа среди кадров
    /// кандидатов база выбирает сама — ценой шва (разница признаков позы). Ни одного кандидата — у клипа нет продолжения:
    /// кадры без полного горизонта будущего не ищутся. Запасные ветви (та же походка — любая — стойка) — забота политики.
    /// </summary>
    /// <param name="candidates">Пустой список, куда писать номера клипов базы (порядок и повторы не важны).</param>
    void Continuations(int clip, ReadOnlySpan<MotionClipInfo> clips, List<int> candidates);

    /// <summary>Прошлое у начала нециклического клипа <paramref name="clip"/>: стоял или шёл с начальной скоростью.</summary>
    MotionPast Past(int clip, ReadOnlySpan<MotionClipInfo> clips);
}

/// <summary>
/// Детектор контактов стоп (<see cref="ContactDetector"/>): сустав стоит на земле, если он низко и почти не движется по
/// земле; вход и выход — с гистерезисом, серии короче <paramref name="MinRun"/> кадров — шум. Высота — над самой низкой точкой
/// сустава в клипе. Типичные значения для голеностопа при 30 кадрах/с: вход 0,05 м и 0,35 м/с, выход 0,07 м и 0,55 м/с, серия 2.
/// </summary>
/// <param name="HeightEnter">Выше «пола» клипа не больше — может встать, м.</param>
/// <param name="HeightExit">Выше — отрывается, м (не меньше <paramref name="HeightEnter"/>).</param>
/// <param name="SpeedEnter">Горизонтальная скорость меньше — может встать, м/с.</param>
/// <param name="SpeedExit">Больше — отрывается, м/с (не меньше <paramref name="SpeedEnter"/>).</param>
/// <param name="MinRun">Внутренняя серия контакта или отрыва короче — переворачивается, кадров (≥ 1).</param>
public readonly record struct ContactParams(float HeightEnter, float HeightExit, float SpeedEnter, float SpeedExit, int MinRun);

/// <summary>
/// Параметры постройки базы (<see cref="MotionDatabase.Build"/>) — все от вызывающего (модуля), у движка своих умолчаний нет.
/// </summary>
/// <param name="Contacts">Детектор контактов суставов <see cref="MotionSchema.ContactJoints"/>.</param>
/// <param name="ExitWindow">Окно раннего выхода, с: кадр выхода нециклического клипа ищется в его последних секундах (у SWORD — 0,5).</param>
/// <param name="VelocityWindow">Окно средних скоростей начала и выхода клипа, с (<see cref="MotionClipInfo"/>; у SWORD — 0,1).</param>
/// <param name="MovingThreshold">
/// Сустав, чьё локальное смещение где-либо в клипах базы отходит от первого кадра своего клипа дальше, м, — с движущимся
/// смещением (<see cref="MotionDatabase.MovingJoints"/>; у SWORD — 0,001).
/// </param>
/// <param name="SeamTie">
/// Явный разрыв ничьих графа: цены шва, различающиеся меньше этой доли, равны — берётся меньший номер кадра (устойчивость к
/// классу процессора, research-mm-data §4.1).
/// </param>
public readonly record struct MotionBuildParams(ContactParams Contacts, float ExitWindow, float VelocityWindow, float MovingThreshold,
    float SeamTie);

/// <summary>Клип базы: номер в библиотеке, первый кадр базы (локальный номер), число кадров, цикл.</summary>
public readonly record struct MotionClip(int LibraryClip, int Start, int Count, bool Loop)
{
    public int End => Start + Count;
}

/// <summary>
/// Сводка базы — для строки старта, <c>Describe</c> и агента. <paramref name="Edges"/> — кадров выхода (у которых
/// <see cref="MotionDatabase.Entry"/> ≥ 0), <paramref name="EarlyExits"/> — из них не последних в своём клипе.
/// <paramref name="BuildMilliseconds"/> — время постройки (в отпечаток не входит).
/// </summary>
public readonly record struct MotionDatabaseSummary(int Clips, int Frames, int Searchable, int Edges, int EarlyExits, double BuildMilliseconds);

/// <summary>
/// Цены поиска по группам схемы (<see cref="MotionSchema.MaxGroups"/> мест, заняты первые <see cref="MotionSchema.GroupCount"/>).
/// Равенство — поэлементное (встроенное у <c>InlineArray</c> бросает исключение, а итог поиска сравнивается как запись).
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

/// <summary>
/// Итог поиска (<see cref="MotionMatcher"/>) — значение без ссылок, память не выделяет (можно хранить в компоненте).
/// </summary>
/// <param name="Frame">Лучший кадр базы; −1 — искать было не в чем (ни одного искомого кадра вне окна пропуска).</param>
/// <param name="Cost">Цена лучшего кадра (взвешенная сумма квадратов разниц нормализованных признаков), без скидки.</param>
/// <param name="ContinuationCost">Цена продолжения — текущего кадра, без скидки; текущего нет — +∞.</param>
/// <param name="Blocks">Блоков осмотрено (не отсечено нижней оценкой).</param>
/// <param name="Frames">Кадров посчитано.</param>
/// <param name="Groups">Цены лучшего кадра по группам (сумма по группам — <paramref name="Cost"/>).</param>
public readonly record struct MotionMatch(int Frame, float Cost, float ContinuationCost, int Blocks, int Frames, MotionGroupCosts Groups);

/// <summary>Проигрыватель в <see cref="MotionPlayers"/>: номер слота и поколение. Поколение 0 — «нет проигрывателя».</summary>
public readonly record struct MotionPlayerHandle(int Index, int Generation)
{
    public static readonly MotionPlayerHandle None = default;
    public bool IsNone => Generation == 0;
    public override string ToString() => IsNone ? "нет" : $"{Index}v{Generation}";
}

/// <summary>
/// Скалярное состояние проигрывателя (<see cref="MotionPlayers.State"/>): что показывает и сколько переходов было.
/// </summary>
/// <param name="Frame">Кадр базы; −1 — ещё не начат (первое задание с переходом ставит кадр без перехода).</param>
/// <param name="Alpha">Доля интервала <c>Frame → Next[Frame]</c>, [0, 1).</param>
/// <param name="Transitions">Переходов с инерциализацией (запрошенных и по рёбрам графа).</param>
/// <param name="HasOutput">Выход прошлого задания есть — скорости выхода считаются разностью с ним.</param>
/// <param name="RootStep">Перенос корня клипа за продвижение последнего задания — в системе корня до шага.</param>
public readonly record struct MotionPlayerState(int Frame, float Alpha, int Transitions, bool HasOutput, Pose2 RootStep);

/// <summary>
/// Шаг проигрывателя — параметры задания пула <see cref="PosePool.Motion"/>.
/// </summary>
/// <param name="Advance">Продвижение по графу, с клипа (dt × темп), ≥ 0.</param>
/// <param name="Dt">Время тика, с (&gt; 0): затухание смещений и скорости выхода — во времени игры, а не клипа.</param>
/// <param name="Transition">Запрошенный переход — кадр базы (итог поиска); −1 — без перехода.</param>
/// <param name="Halflife">Полупериод затухания смещений инерциализации, с (&gt; 0).</param>
public readonly record struct MotionStep(float Advance, float Dt, int Transition, float Halflife);
