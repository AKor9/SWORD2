using System.Numerics;
using Sword.Engine.Ecs;

namespace Sword.Game.Contracts;

/// <summary>
/// Анимация бойцов для наблюдателей симуляции: вид только для чтения на пул поз и библиотеку клипов. Ядро его не
/// спрашивает; меняют позы только системы тика.
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
    /// Поза последнего тика — модельные матрицы суставов (вектор слева; мир бойца — поворот на курс и перенос в корень).
    /// Позы нет — пусто.
    /// </summary>
    ReadOnlySpan<Matrix4x4> Model(PoseRef pose);

    /// <summary>
    /// Поза тика раньше последнего (пара к <see cref="Model"/> для показа между тиками). Пусто — позы нет или у неё нет прошлого тика
    /// (только заведена — постройка бойца — или в прошлом тике не считалась): показывать позу последнего тика.
    /// </summary>
    ReadOnlySpan<Matrix4x4> PreviousModel(PoseRef pose);

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
/// Тела для наблюдателей симуляции: вид только для чтения на тела Physics по хэндлу <see cref="BodyRef"/>. Тело <c>i</c>
/// бойца — сегмент <see cref="BodySegs.Segment"/>(i) (11 — клинок); прошлая и нынешняя постановка — в <see cref="BodySnapshot"/>.
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
    /// Спит ли тело (сочленения нет — false): труп, который физика не считает, пока его не разбудят; тела стоят, скорости 0.
    /// </summary>
    bool IsAsleep(BodyRef body);

    /// <summary>Счётчик покоя тела, с: сколько подряд труп медленнее порогов сна (у спящего — считает дальше; сочленения нет — 0).</summary>
    float RestTime(BodyRef body);

    /// <summary>Спящих тел (сочленений).</summary>
    int AsleepCount { get; }

    /// <summary>Суставов скелета бойца.</summary>
    int JointCount { get; }

    /// <summary>
    /// Сегмент тела (<see cref="BodySegs"/>), к которому относится сустав скелета <paramref name="joint"/>: скин трупа.
    /// </summary>
    int JointSegment(int joint);
}

/// <summary>
/// Капсула тела после тика (мир): постановка (ось капсулы — локальная Y), прошлая постановка, скорость, м/с, угловая,
/// рад/с, радиус и полудлина, м; поза начала тика (у тела на физике прошлую постановку перезаписывает каждый подшаг —
/// клиент интерполирует от неё), масса, кг (0 — нет), на физике ли (труп), профиль, касание земли, сон.
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
/// Привязка скина трупа для наблюдателей: снимок в тик смерти (по <c>Added&lt;Dead&gt;</c>). Для сустава <c>j</c>:
/// <c>binding[j] = модельная[j] · мир бойца · (система тела)⁻¹</c>, сустав в мире — <c>binding[j] · система тела</c>
/// (<see cref="BodySnapshot.World"/>): тела двинулись — скин идёт за ними. Пропадает, когда метку смерти сняла постройка заново.
/// </summary>
public interface ISkinView : IReadView
{
    /// <summary>Суставов скелета — матриц в привязке.</summary>
    int JointCount { get; }

    /// <summary>Привязка тела <paramref name="body"/> и время смерти, с; нет привязки — false.</summary>
    bool TryGet(BodyRef body, out ReadOnlySpan<Matrix4x4> binding, out float time);

    /// <summary>Привязка суставов куска <paramref name="piece"/> (<see cref="Severance"/>) к телу куска; нет — false.</summary>
    bool TryGetGhost(BodyRef body, int piece, out ReadOnlySpan<Matrix4x4> ghost);
}
