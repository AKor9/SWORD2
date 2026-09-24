using System.Numerics;
using Sword.Engine.Ecs;

namespace Sword.Game.Contracts;

/// <summary>
/// Анимация бойцов для наблюдателей симуляции (клиент — отрисовка, модули-представления, агент): вид только для чтения
/// (<c>WorldReader.View&lt;IAnimationView&gt;()</c>) на пул поз и библиотеку клипов подсистемы Animation. Не стратегия:
/// ядро его не спрашивает, а только отдаёт наружу то, что посчитал тик. Меняют позы только системы тика.
/// </summary>
public interface IAnimationView : IReadView
{
    /// <summary>Суставов скелета бойца.</summary>
    int JointCount { get; }

    /// <summary>Имя сустава <paramref name="joint"/>.</summary>
    string JointName(int joint);

    /// <summary>Номер сустава по имени; нет — −1.</summary>
    int FindJoint(string name);

    /// <summary>
    /// Итоговая поза последнего тика — модельные матрицы суставов в пространстве персонажа (вектор слева; мир бойца —
    /// поворот на курс и перенос в корень). Позы нет (не заведена или освобождена) — пусто.
    /// </summary>
    ReadOnlySpan<Matrix4x4> Model(PoseRef pose);

    /// <summary>Поза привязки — модельные матрицы, в которых меш рисуется без анимации (новая поза — такая).</summary>
    ReadOnlySpan<Matrix4x4> ReferenceModel();

    /// <summary>Клипов в библиотеке; номер клипа — в состояниях слоёв (например, у локомоции).</summary>
    int ClipCount { get; }

    /// <summary>ID клипа «источник/имя».</summary>
    string ClipName(int clip);

    /// <summary>Длительность клипа, с.</summary>
    float ClipDuration(int clip);

    bool ClipLoops(int clip);

    /// <summary>Скорость корневого движения клипа, м/с.</summary>
    float ClipRootSpeed(int clip);

    /// <summary>Клип по ID или имени (если оно одно); нет — −1.</summary>
    int FindClip(string name);

    /// <summary>Живых поз в пуле.</summary>
    int PoseCount { get; }

    /// <summary>Заданий поз, выполненных пулом в последнем тике, и пропущенных (к освобождённым позам).</summary>
    int LastTickJobs { get; }
    int LastTickStale { get; }
}

/// <summary>
/// Тела для наблюдателей симуляции (агент — 5б, клиент — капсулы на экране и ID-буфер снимка, 5в): вид только для чтения
/// (<c>WorldReader.View&lt;IBodyView&gt;()</c>) на тела подсистемы Physics по хэндлу сочленения из <see cref="BodyRef"/>.
/// Тело <c>i</c> сочленения бойца — сегмент <see cref="BodySegs.Segment"/>(i) (с 7ж — сегмент <c>i</c>, 11 — клинок). Прошлая и нынешняя постановка тела — в
/// <see cref="BodySnapshot"/>: по ним клиент интерполирует тело по доле тика. Меняют тела только системы тика.
/// </summary>
public interface IBodyView : IReadView
{
    /// <summary>Тел в сочленении; сочленения нет (пустой или освобождённый хэндл) — 0.</summary>
    int BodyCount(BodyRef body);

    /// <summary>Тело <paramref name="index"/> сочленения (0 … <see cref="BodyCount"/> − 1) — копия состояния.</summary>
    BodySnapshot Body(BodyRef body, int index);

    /// <summary>Живых сочленений и тел.</summary>
    int Articulations { get; }
    int Bodies { get; }

    /// <summary>
    /// Спит ли тело (7з-3; сочленение нет — false): труп, который физика не считает, пока его не разбудят (толчок живого, удар, касание
    /// движущегося трупа — цепочкой по куче, команда, ручки «Рэгдолл»); его тела стоят, скорости — 0, поза начала тика — поза тика.
    /// </summary>
    bool IsAsleep(BodyRef body);

    /// <summary>Счётчик покоя тела, с (7з-3): сколько подряд труп медленнее порогов сна (у спящего — считает дальше; сочленения нет — 0).</summary>
    float RestTime(BodyRef body);

    /// <summary>Спящих тел (сочленений) — 7з-3.</summary>
    int AsleepCount { get; }

    /// <summary>Суставов скелета бойца.</summary>
    int JointCount { get; }

    /// <summary>
    /// Сегмент тела (<see cref="BodySegs"/>), к которому относится сустав скелета <paramref name="joint"/>
    /// (<c>CharacterRig.JointSegment</c> SWORD): ID-буфер снимка — сегмент пикселя по суставу вершины (5в), позже — разрезы.
    /// </summary>
    int JointSegment(int joint);
}

/// <summary>
/// Капсула тела после тика: постановка (центр, поворот; ось капсулы — локальная Y), прошлая постановка (для скорости и
/// интерполяции), скорость, м/с, угловая скорость, рад/с (мир), радиус и полудлина оси, м. С 7ж — динамика тела: поза начала
/// тика (у тела на физике прошлую постановку перезаписывает каждый подшаг — клиент интерполирует от позы начала тика, 7и),
/// масса, кг (0 — без массы), на физике ли тело (труп, 7з; живое — кинематическое), номер профиля динамики, касание земли; с 7з-3 — спит ли
/// сочленение тела (спящее физика не считает: поза стоит, скорости 0).
/// </summary>
public readonly record struct BodySnapshot(Vector3 Position, Quaternion Rotation, Vector3 PrevPosition, Quaternion PrevRotation,
    Vector3 Velocity, Vector3 AngularVelocity, float Radius, float HalfLength, Vector3 StartPosition, Quaternion StartRotation,
    float Mass, bool Simulated, int Profile, bool Grounded, bool Asleep = false)
{
    /// <summary>Ось капсулы в мире.</summary>
    public Vector3 Axis => Vector3.Transform(Vector3.UnitY, Rotation);

    /// <summary>Концы оси капсулы: нижний (A) и верхний (B).</summary>
    public Vector3 EndA => Position - Axis * HalfLength;
    public Vector3 EndB => Position + Axis * HalfLength;

    /// <summary>Система тела в мире (вектор слева): поворот, затем перенос в центр.</summary>
    public Matrix4x4 World
    {
        get
        {
            var m = Matrix4x4.CreateFromQuaternion(Rotation);
            m.Translation = Position;
            return m;
        }
    }
}

/// <summary>
/// Привязка скина трупа для наблюдателей (7ж; клиент — скин трупа по телам, 7и; агент): вид только для чтения
/// (<c>WorldReader.View&lt;ISkinView&gt;()</c>) на снимок привязки, который симуляция делает в тик смерти бойца (реактивно по
/// <c>Added&lt;Dead&gt;</c>; <c>CaptureSkin</c> SWORD 2eab13b). Для каждого сустава скелета <c>j</c> — матрица сустава относительно
/// тела его сегмента (<see cref="IBodyView.JointSegment"/>) по позе и телам момента смерти:
/// <c>binding[j] = модельная[j] · мир бойца · (система тела)⁻¹</c>, и потому сустав в мире — <c>binding[j] · система тела</c>
/// (<see cref="BodySnapshot.World"/>): пока тела трупа стоят, это поза смерти; тела двинулись (7з) — скин идёт за ними. Привязка
/// есть у мёртвого бойца с телом и пропадает, когда метку смерти сняла постройка заново (возрождение, сброс, команда сцены).
/// </summary>
public interface ISkinView : IReadView
{
    /// <summary>Суставов скелета — матриц в привязке.</summary>
    int JointCount { get; }

    /// <summary>
    /// Привязка тела <paramref name="body"/>: матрицы суставов относительно тел их сегментов и время смерти, с, когда её сняли;
    /// нет привязки (боец жив, тело не живо) — false.
    /// </summary>
    bool TryGet(BodyRef body, out ReadOnlySpan<Matrix4x4> binding, out float time);
}
