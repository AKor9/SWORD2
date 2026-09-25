using System.Numerics;
using Sword.Engine.Animation.Motion;

namespace Sword.Engine.Animation.Tests;

/// <summary>
/// Данные тестов поиска, запроса, модели и истории без постройки из клипов: библиотеки из таблиц с заданным ходом корня
/// (прямо, вбок, с поворотом), базы из массивов (граф и перенос корня — <see cref="MotionFixtures.Plain"/>) с признаками трёх
/// видов: случайными (как у <c>Plain</c>), гладкими по кадрам (узкие блоки — нижняя оценка отсекает) и «пройденными» —
/// траектория и корень по графу так, как их определяет схема, суставы случайные (запрос на ровном ходу). Плюс проверки, общие для
/// тестов: окно пропуска своими словами и побитовое сравнение.
/// </summary>
internal static class MotionSearchFixtures
{
    public const float Rate = 30;

    /// <summary>Клип таблицы: ход корня <paramref name="Speed"/> м/с в направлении <paramref name="Heading"/> от курса (+ — влево), поворот <paramref name="Turn"/> рад/с.</summary>
    public readonly record struct ClipSpec(string Name, int Frames, bool Loop, float Speed = 0, float Heading = 0, float Turn = 0);

    /// <summary>Библиотека 30 кадров/с на скелете тестов: позы единичные, корень — по <see cref="ClipSpec"/> от начала клипа.</summary>
    public static ClipLibrary Library(params ClipSpec[] specs)
    {
        var skeleton = TestClips.Skeleton();
        int joints = skeleton.JointCount, frames = specs.Sum(s => s.Frames);
        var clips = new ClipLibrary.Clip[specs.Length];
        var rotations = Enumerable.Repeat(Quaternion.Identity, frames * joints).ToArray();
        var positions = new Vector3[frames * joints];
        var motion = new Vector3[frames];
        var yaw = new float[frames];
        for (int c = 0, start = 0; c < specs.Length; start += specs[c].Frames, c++)
        {
            var s = specs[c];
            clips[c] = new ClipLibrary.Clip("locomotion/" + s.Name, start, s.Frames, s.Loop);
            var step = new Pose2(MathF.Sin(s.Heading) * s.Speed / Rate, MathF.Cos(s.Heading) * s.Speed / Rate, s.Turn / Rate);
            var root = Pose2.Identity;
            for (int i = 0; i < s.Frames; i++, root = Pose2.Compose(root, step))
            {
                motion[start + i] = root.Position;
                yaw[start + i] = root.Yaw;
            }
        }
        return new ClipLibrary(skeleton, Rate, clips, rotations, positions, motion, yaw);
    }

    /// <summary>Случайные клипы (2…60 кадров, циклы и нет, ход и поворот) — не меньше <paramref name="frames"/> кадров всего.</summary>
    public static ClipLibrary RandomLibrary(int seed, int frames)
    {
        var rng = new Random(seed);
        var specs = new List<ClipSpec>();
        for (int total = 0; total < frames;)
        {
            int n = Math.Min(rng.Next(2, 61), Math.Max(2, frames - total));
            specs.Add(new ClipSpec($"clip{specs.Count}", n, rng.Next(2) == 0, (float)rng.NextDouble() * 4,
                (float)(rng.NextDouble() * 2 - 1) * MathF.PI, (float)(rng.NextDouble() * 2 - 1) * 2));
            total += n;
        }
        return Library([.. specs]);
    }

    /// <summary>Все клипы библиотеки по порядку — список клипов базы.</summary>
    public static int[] All(ClipLibrary library) => [.. Enumerable.Range(0, library.ClipCount)];

    /// <summary>
    /// Гладкие признаки: у признака i — синус номера кадра (медленный, своя частота и фаза) плюс сдвиг клипа и шум 0,05: соседние
    /// строки близки, блоки узкие, нижняя оценка отсекает большую часть базы.
    /// </summary>
    public static MotionDatabase Smooth(ClipLibrary library, MotionSchema schema, int seed)
    {
        var a = MotionFixtures.Plain(library, schema, All(library), seed);
        var rng = new Random(seed + 1000);
        int fc = schema.FeatureCount;
        var phase = Enumerable.Range(0, fc).Select(_ => rng.NextDouble() * 6).ToArray();
        var offsets = Enumerable.Range(0, library.ClipCount * fc).Select(_ => rng.NextDouble() - 0.5).ToArray();
        for (int c = 0; c < a.Clips.Length; c++)
            for (int f = a.Clips[c].Start; f < a.Clips[c].End; f++)
            {
                for (int i = 0; i < fc; i++)
                    a.Features[f * fc + i] = (float)(1.5 * Math.Sin(0.02 * (1 + i % 4) * f + phase[i]) + offsets[c * fc + i] + 0.05 * (rng.NextDouble() - 0.5));
            }
        return a.Build(library, schema);
    }

    /// <summary>
    /// «Пройденная» база: траектория будущего — по графу (<c>round(t·частота)</c> интервалов вперёд; нет полного горизонта — кадр
    /// не ищется), прошлое — назад по клипу (у цикла — через шов, у нециклического до начала — «стоял»), скорость и ω корня — по
    /// интервалу кадра, суставы — случайные; среднее — по искомым кадрам, разброс — средний по признакам группы, не меньше пола.
    /// Так определяет признаки схема (<see cref="FeatureKind"/>); это не постройка базы, а образец для запроса на ровном ходу.
    /// </summary>
    public static MotionDatabase Walked(ClipLibrary library, MotionSchema schema, int seed = 1)
    {
        var a = MotionFixtures.Plain(library, schema, All(library), seed);
        int frames = a.Next.Length, fc = schema.FeatureCount;
        var raw = new double[frames * fc];
        var rng = new Random(seed);
        for (int f = 0; f < frames; f++)
        {
            var clip = a.Clips.First(c => f >= c.Start && f < c.End);
            bool full = a.Searchable[f];
            foreach (var g in schema.Groups)
            {
                var row = raw.AsSpan(f * fc + g.Offset, g.Size);
                switch (g.Kind)
                {
                    case FeatureKind.TrajectoryPosition:
                    case FeatureKind.TrajectoryDirection:
                        for (int k = 0; k < schema.FutureTimes.Length; k++)
                        {
                            var p = Ahead(a, f, (int)MathF.Round(schema.FutureTimes[k] * Rate), out bool ok);
                            full &= ok;
                            if (g.Kind == FeatureKind.TrajectoryPosition) (row[2 * k], row[2 * k + 1]) = (p.X, p.Z);
                            else (row[2 * k], row[2 * k + 1]) = (MathF.Sin(p.Yaw), MathF.Cos(p.Yaw));
                        }
                        break;
                    case FeatureKind.PastPosition:
                        for (int k = 0; k < schema.PastTimes.Length; k++)
                        {
                            var p = Behind(a, clip, f, (int)MathF.Round(-schema.PastTimes[k] * Rate));
                            (row[2 * k], row[2 * k + 1]) = (p.X, p.Z);
                        }
                        break;
                    case FeatureKind.RootVelocity:
                        (row[0], row[1]) = (a.Interval[f].X * Rate, a.Interval[f].Z * Rate);
                        break;
                    case FeatureKind.RootYawRate:
                        row[0] = a.Interval[f].Yaw * Rate;
                        break;
                    default:
                        for (int i = 0; i < row.Length; i++) row[i] = rng.NextDouble() * 2 - 1;
                        break;
                }
            }
            a.Searchable[f] = full;
        }
        var rows = Enumerable.Range(0, frames).Where(f => a.Searchable[f]).ToArray();
        foreach (var g in schema.Groups)
        {
            double stdSum = 0;
            for (int i = g.Offset; i < g.End; i++)
            {
                double mean = rows.Average(f => raw[f * fc + i]);
                a.Mean[i] = (float)mean;
                stdSum += Math.Sqrt(rows.Average(f => (raw[f * fc + i] - mean) * (raw[f * fc + i] - mean)));
            }
            float std = MathF.Max(g.MinStd, (float)(stdSum / g.Size));
            for (int i = g.Offset; i < g.End; i++) a.Std[i] = std;
        }
        for (int f = 0; f < frames; f++)
            for (int i = 0; i < fc; i++)
                a.Features[f * fc + i] = (float)((raw[f * fc + i] - a.Mean[i]) / a.Std[i]);
        return a.Build(library, schema);
    }

    private static Pose2 Ahead(MotionFixtures.Arrays a, int frame, int steps, out bool ok)
    {
        var p = Pose2.Identity;
        for (int s = 0; s < steps; s++)
        {
            if (a.Next[frame] < 0)
            {
                ok = false;
                return p;
            }
            p = Pose2.Compose(p, a.Interval[frame]);
            frame = a.Next[frame];
        }
        ok = true;
        return p;
    }

    private static Pose2 Behind(MotionFixtures.Arrays a, MotionClip clip, int frame, int steps)
    {
        var p = Pose2.Identity;
        int i = frame - clip.Start;
        for (int s = 0; s < steps; s++)
        {
            // прежний кадр: у цикла последний кадр = первый, перед первым — предпоследний; нециклический до начала — стоял
            int prev = i > 0 ? i - 1 : clip.Loop ? clip.Count - 2 : -1;
            if (prev < 0) break;
            p = Pose2.Compose(p, a.Interval[clip.Start + prev].Inverse());
            i = prev;
        }
        return p;
    }

    /// <summary>Случайный конечный запрос: около строки кадра (шум <paramref name="noise"/>) или равномерно в [−3, 3].</summary>
    public static float[] RandomQuery(Random rng, MotionDatabase db, float noise)
    {
        var q = new float[db.FeatureCount];
        if (noise < 0)
        {
            for (int i = 0; i < q.Length; i++) q[i] = (float)(rng.NextDouble() * 6 - 3);
            return q;
        }
        var row = db.FeatureRow(rng.Next(db.FrameCount));
        for (int i = 0; i < q.Length; i++) q[i] = row[i] + (float)(rng.NextDouble() * 2 - 1) * noise;
        return q;
    }

    /// <summary>Случайные веса групп: 0…4, каждый пятый — 0.</summary>
    public static float[] RandomWeights(Random rng, MotionSchema schema) =>
        [.. Enumerable.Range(0, schema.GroupCount).Select(_ => rng.Next(5) == 0 ? 0f : (float)(rng.NextDouble() * 4))];

    /// <summary>
    /// Окно пропуска своими словами (не кодом поиска): кадр в клипе текущего и ближе <paramref name="skip"/> кадров к нему;
    /// у цикла место последнего кадра — 0 и расстояние по кругу с периодом «кадров − 1».
    /// </summary>
    public static bool InWindow(MotionDatabase db, int current, int skip, int frame)
    {
        if (current < 0 || db.ClipOf[frame] != db.ClipOf[current]) return false;
        var clip = db.Clips[db.ClipOf[current]];
        int Place(int f) => clip.Loop && f == clip.End - 1 ? 0 : f - clip.Start;
        int d = Math.Abs(Place(frame) - Place(current));
        if (clip.Loop) d = Math.Min(d, clip.Count - 1 - d);
        return d < skip;
    }

    public static void AssertSameBits(float expected, float actual, string what) =>
        Assert.True(BitConverter.SingleToInt32Bits(expected) == BitConverter.SingleToInt32Bits(actual), $"{what}: {expected:R} ≠ {actual:R}");

    public static void AssertSameBits(in TrajectoryState expected, in TrajectoryState actual, string what)
    {
        AssertSameBits(expected.Root.X, actual.Root.X, what + " x");
        AssertSameBits(expected.Root.Z, actual.Root.Z, what + " z");
        AssertSameBits(expected.Root.Yaw, actual.Root.Yaw, what + " курс");
        AssertSameBits(expected.Velocity.X, actual.Velocity.X, what + " vx");
        AssertSameBits(expected.Velocity.Y, actual.Velocity.Y, what + " vy");
        AssertSameBits(expected.Velocity.Z, actual.Velocity.Z, what + " vz");
    }

    /// <summary>Байт, выделенных <paramref name="action"/> на этом потоке за <paramref name="times"/> вызовов после прогрева.</summary>
    public static long Allocated(int times, Action action)
    {
        for (int i = 0; i < 5; i++) action();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < times; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
