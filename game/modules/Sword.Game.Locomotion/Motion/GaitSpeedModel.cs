using System.Numerics;
using Sword.Engine.Animation.Motion;

namespace Sword.Game.Locomotion.Motion;

/// <summary>
/// Модель скоростей данных: средняя скорость циклов шага и бега по 8 секторам направления относительно лица (сектор k —
/// k·45°, + — влево); между секторами — линейно, пустой сектор — среднее ближайших заполненных. Нужна запросу: скорость
/// управления → скорость клипов — масштаб данных траектории. Неизменяема, общая для симуляций процесса.
/// </summary>
public sealed class GaitSpeedModel
{
    public const int Sectors = 8;

    /// <summary>Ширина сектора, рад (45°).</summary>
    public const float SectorAngle = MathF.PI / 4;

    /// <summary>Цикл для модели: походка, направление средней скорости (рад, относительно лица, + — влево), скорость, м/с.</summary>
    public readonly record struct Loop(GaspGait Gait, float Direction, float Speed);

    private readonly float[] _walk;
    private readonly float[] _run;
    private readonly int[] _walkLoops;
    private readonly int[] _runLoops;

    /// <param name="loops">Циклы хода; походка <see cref="GaspGait.Stand"/>, нулевая или не конечная скорость — ошибка.</param>
    /// <param name="runInputSpeed">Скорость ввода, выше которой — бег, м/с (≥ 0).</param>
    public GaitSpeedModel(ReadOnlySpan<Loop> loops, float runInputSpeed)
    {
        if (!float.IsFinite(runInputSpeed) || runInputSpeed < 0)
            throw new ArgumentException($"порог бега {runInputSpeed} м/с, а нужен конечный и не меньше 0", nameof(runInputSpeed));
        RunInputSpeed = runInputSpeed;
        var sum = new double[2, Sectors];
        _walkLoops = new int[Sectors];
        _runLoops = new int[Sectors];
        foreach (var loop in loops)
        {
            if (loop.Gait == GaspGait.Stand) throw new ArgumentException("цикл стойки в модели скоростей хода", nameof(loops));
            if (!float.IsFinite(loop.Direction) || !float.IsFinite(loop.Speed) || loop.Speed <= 0)
                throw new ArgumentException($"цикл {loop.Gait}: направление {loop.Direction}, скорость {loop.Speed}", nameof(loops));
            int g = loop.Gait == GaspGait.Walk ? 0 : 1;
            int s = SectorOf(loop.Direction);
            sum[g, s] += loop.Speed;
            (g == 0 ? _walkLoops : _runLoops)[s]++;
        }
        _walk = Fill(sum, 0, _walkLoops);
        _run = Fill(sum, 1, _runLoops);
    }

    /// <summary>Скорость ввода, выше которой — бег, м/с.</summary>
    public float RunInputSpeed { get; }

    public bool HasWalk => !float.IsNaN(_walk[0]);
    public bool HasRun => !float.IsNaN(_run[0]);

    /// <summary>Скорость шага по секторам, м/с (сектор k — направление k·45°); нет шага — NaN.</summary>
    public ReadOnlySpan<float> WalkSectors => _walk;

    /// <summary>Скорость бега по секторам, м/с; нет бега — NaN.</summary>
    public ReadOnlySpan<float> RunSectors => _run;

    /// <summary>Циклов шага в секторе по данным (0 — сектор достроен).</summary>
    public ReadOnlySpan<int> WalkLoops => _walkLoops;

    /// <summary>Циклов бега в секторе по данным (0 — сектор достроен).</summary>
    public ReadOnlySpan<int> RunLoops => _runLoops;

    /// <summary>Сектор направления <paramref name="direction"/> (рад): ближайший k·45°, k в [0, 8).</summary>
    public static int SectorOf(float direction)
    {
        int s = (int)MathF.Round(Pose2.Wrap(direction) / SectorAngle);
        return (s % Sectors + Sectors) % Sectors;
    }

    /// <summary>Скорость шага в направлении <paramref name="direction"/> (рад), линейно между секторами; нет шага — NaN.</summary>
    public float Walk(float direction) => Interpolate(_walk, direction);

    /// <summary>Скорость бега в направлении <paramref name="direction"/> (рад); нет бега — NaN.</summary>
    public float Run(float direction) => Interpolate(_run, direction);

    /// <summary>Бег ли при скорости ввода <paramref name="inputSpeed"/>: быстрее порога и бег есть — да; шага нет — тоже да (если есть бег).</summary>
    public bool IsRun(float inputSpeed) => HasRun && (inputSpeed > RunInputSpeed || !HasWalk);

    /// <summary>
    /// Скорость данных для направления <paramref name="direction"/> (рад, относительно лица) и скорости ввода, м/с; нет
    /// ни шага, ни бега — <paramref name="inputSpeed"/>.
    /// </summary>
    public float Speed(float direction, float inputSpeed)
    {
        if (!HasWalk && !HasRun) return inputSpeed;
        return IsRun(inputSpeed) ? Run(direction) : Walk(direction);
    }

    /// <summary>
    /// Модель по циклам базы: скорость и направление — средняя скорость корня по кадрам оборота (без последнего — он равен
    /// первому); медленнее <see cref="GaspMotion.StandSpeed"/> — не ход.
    /// </summary>
    /// <param name="names">Имена клипов базы — по номеру клипа базы.</param>
    public static GaitSpeedModel From(MotionDatabase database, ReadOnlySpan<GaspClipName> names, float runInputSpeed)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (names.Length != database.ClipCount)
            throw new ArgumentException($"имён {names.Length}, а клипов базы {database.ClipCount}", nameof(names));
        var loops = new List<Loop>();
        var clips = database.Clips;
        for (int c = 0; c < clips.Length; c++)
        {
            var n = names[c];
            if (!clips[c].Loop || n.Kind != GaspKind.Loop || n.Gait == GaspGait.Stand) continue;
            var v = MeanVelocity(database, clips[c]);
            float speed = new Vector2(v.X, v.Z).Length();
            if (speed < GaspMotion.StandSpeed) continue;
            loops.Add(new Loop(n.Gait, MathF.Atan2(v.X, v.Z), speed));
        }
        return new GaitSpeedModel(loops.ToArray(), runInputSpeed);
    }

    /// <summary>Средняя скорость корня цикла по кадрам оборота (без последнего), в системе корня кадра, м/с.</summary>
    public static Vector3 MeanVelocity(MotionDatabase database, MotionClip clip)
    {
        var velocity = database.RootVelocity;
        int frames = clip.Loop && clip.Count > 1 ? clip.Count - 1 : clip.Count;
        double x = 0, z = 0;
        for (int f = clip.Start; f < clip.Start + frames; f++)
        {
            x += velocity[f].X;
            z += velocity[f].Z;
        }
        return new Vector3((float)(x / frames), 0, (float)(z / frames));
    }

    private static float[] Fill(double[,] sum, int gait, int[] count)
    {
        var sectors = new float[Sectors];
        bool any = false;
        for (int s = 0; s < Sectors; s++)
            if (count[s] > 0)
            {
                sectors[s] = (float)(sum[gait, s] / count[s]);
                any = true;
            }
        if (!any)
        {
            Array.Fill(sectors, float.NaN);
            return sectors;
        }
        var filled = (float[])sectors.Clone();
        for (int s = 0; s < Sectors; s++)
        {
            if (count[s] > 0) continue;
            int left = s, right = s;
            do left = (left + 1) % Sectors; while (count[left] == 0);
            do right = (right + Sectors - 1) % Sectors; while (count[right] == 0);
            filled[s] = (sectors[left] + sectors[right]) / 2;
        }
        return filled;
    }

    private static float Interpolate(float[] sectors, float direction)
    {
        if (float.IsNaN(sectors[0])) return float.NaN;
        float t = Pose2.Wrap(direction) / SectorAngle;
        float floor = MathF.Floor(t);
        int s0 = ((int)floor % Sectors + Sectors) % Sectors;
        int s1 = (s0 + 1) % Sectors;
        float k = t - floor;
        return sectors[s0] + (sectors[s1] - sectors[s0]) * k;
    }
}
