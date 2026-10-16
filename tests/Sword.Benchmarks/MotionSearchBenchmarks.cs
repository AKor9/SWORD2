using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using Sword.Engine.Animation;
using Sword.Engine.Animation.Motion;

namespace Sword.Benchmarks;

/// <summary>
/// Поиск Motion Matching на синтетической базе размера пака (<see cref="SyntheticMotion"/>), запросы — <see cref="MotionQuery.Fill"/>
/// как в игре. Время — замер, не бюджет; цель — 0 байт на поиск.
/// </summary>
[MemoryDiagnoser]
public class MotionSearch
{
    private SyntheticMotion _data = null!;
    private int _next;

    [GlobalSetup]
    public void Setup() => _data = SyntheticMotion.Create(SyntheticMotion.Seed);

    [Benchmark]
    public int Search()
    {
        var q = _data.Queries[_next++ % _data.Queries.Length];
        return _data.Matcher.Search(q.Values, _data.Weights, q.Current, SyntheticMotion.Bias, SyntheticMotion.Skip).Frame;
    }
}

/// <summary>
/// Итог <c>--motion</c> в <c>artifacts/bench/motion.json</c>: медиана и байты, p50/p95/максимум поиска, сверка с полным
/// перебором. Код выхода 0 — 0 байт и поиск равен перебору (время не бюджет).
/// </summary>
public static class MotionSearchReport
{
    public static bool Run(IConfig config, string outDir, bool quick)
    {
        var summary = BenchmarkRunner.Run<MotionSearch>(config);
        var report = summary.Reports.Single();
        long bytes = report.GcStats.GetBytesAllocatedPerOperation(report.BenchmarkCase) ?? -1;
        double medianNs = report.ResultStatistics!.Median;

        var data = SyntheticMotion.Create(SyntheticMotion.Seed);
        var m = data.Matcher;
        var db = data.Database;
        var queries = data.Queries;
        for (int pass = 0; pass < 3; pass++)   // прогрев: многоуровневый JIT доводит поиск до Tier1
            foreach (var q in queries) m.Search(q.Values, data.Weights, q.Current, SyntheticMotion.Bias, SyntheticMotion.Skip);
        var us = new double[queries.Length];
        var blocks = new int[queries.Length];
        var frames = new int[queries.Length];
        for (int i = 0; i < queries.Length; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            var r = m.Search(queries[i].Values, data.Weights, queries[i].Current, SyntheticMotion.Bias, SyntheticMotion.Skip);
            us[i] = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
            blocks[i] = r.Blocks;
            frames[i] = r.Frames;
        }
        int bruteCount = quick ? 20 : 100, mismatches = 0;
        var bruteUs = new double[bruteCount];
        for (int i = 0; i < bruteCount; i++)
        {
            var q = queries[i];
            long t0 = Stopwatch.GetTimestamp();
            var brute = m.SearchBruteForce(q.Values, data.Weights, q.Current, SyntheticMotion.Bias, SyntheticMotion.Skip);
            bruteUs[i] = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
            var fast = m.Search(q.Values, data.Weights, q.Current, SyntheticMotion.Bias, SyntheticMotion.Skip);
            if (fast.Frame != brute.Frame || fast.Cost != brute.Cost) mismatches++;
        }
        bool ok = bytes == 0 && mismatches == 0;
        var result = new
        {
            ok, quick,
            database = new
            {
                clips = db.ClipCount, frames = db.FrameCount, rows = db.Rows.Length, blocks = db.BlockCount, features = db.FeatureCount,
                fingerprint = db.Fingerprint.ToString("x16"),
            },
            queries = queries.Length,
            searchUs = new { p50 = Percentile(us, 0.5), p95 = Percentile(us, 0.95), max = Round(us.Max()), mean = Round(us.Average()) },
            blocksVisited = new
            {
                p50 = Percentile([.. blocks.Select(b => (double)b)], 0.5), max = blocks.Max(), mean = Round(blocks.Average()),
                share = Round(blocks.Average() / db.BlockCount, 4),
            },
            framesComputed = new { p50 = Percentile([.. frames.Select(f => (double)f)], 0.5), max = frames.Max(), mean = Round(frames.Average()) },
            bruteForceUs = new { p50 = Percentile(bruteUs, 0.5), max = Round(bruteUs.Max()), searches = bruteCount, mismatches },
            benchmarkDotNet = new { medianNs = Math.Round(medianNs), bytesPerSearch = bytes },
            runtime = Environment.Version.ToString(), machine = Environment.MachineName, date = DateTime.UtcNow.ToString("u"),
        };
        string json = JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        File.WriteAllText(Path.Combine(outDir, "motion.json"), json);
        Console.WriteLine(json);
        return ok;
    }

    private static double Percentile(double[] values, double p)
    {
        var sorted = values.Order().ToArray();
        return Round(sorted[Math.Max(0, (int)Math.Ceiling(p * sorted.Length) - 1)]);
    }

    private static double Round(double v, int digits = 1) => Math.Round(v, digits);
}

/// <summary>
/// Синтетическая база размера пака: скелет из 10 суставов, 45 признаков, 30 кадров/с; клипы как в паке локомоции — циклы
/// шага и бега в 8 сторон, переходы с поворотом до 3,5 рад/с и медленное блуждание. Нормализация — как у базы.
/// </summary>
public sealed class SyntheticMotion
{
    public const int Seed = 8, ClipCount = 440, TargetFrames = 51_400, QueryCount = 1000;
    public const float Rate = 30, Bias = 0.1f, Tick = 1f / 120;
    public const int Skip = 7;

    private const int Root = 0, Pelvis = 1, ThighL = 2, CalfL = 3, FootL = 4, ThighR = 5, CalfR = 6, FootR = 7, ClavicleL = 8, ClavicleR = 9;

    public readonly record struct Query(float[] Values, int Current);

    public required MotionDatabase Database { get; init; }
    public required MotionMatcher Matcher { get; init; }
    public required float[] Weights { get; init; }
    public required Query[] Queries { get; init; }

    public static MotionSchema Schema() => new([1f / 3, 2f / 3, 1f], [-1f / 3],
    [
        new("trajectory", FeatureKind.TrajectoryPosition, 3.5f, 0.2f),
        new("trajectory-direction", FeatureKind.TrajectoryDirection, 1.5f, 0.2f),
        new("past", FeatureKind.PastPosition, 0.5f, 0.1f),
        new("root-velocity", FeatureKind.RootVelocity, 4f, 0.2f),
        new("root-yaw-rate", FeatureKind.RootYawRate, 0.5f, 0.5f),
        new("feet", FeatureKind.JointPosition, 0.4f, 0.03f, [FootL, FootR]),
        new("feet-velocity", FeatureKind.JointVelocity, 0.5f, 0.2f, [FootL, FootR]),
        new("knees", FeatureKind.JointPosition, 0.25f, 0.03f, [CalfL, CalfR]),
        new("pelvis", FeatureKind.JointPosition, 0.25f, 0.02f, [Pelvis]),
        new("pelvis-velocity", FeatureKind.JointVelocity, 0.5f, 0.1f, [Pelvis]),
        new("pelvis-facing", FeatureKind.JointPairFacing, 0.5f, 0.1f, [ThighL, ThighR]),
        new("chest-facing", FeatureKind.JointPairFacing, 0.25f, 0.1f, [ClavicleL, ClavicleR]),
    ], [FootL, FootR]);

    /// <summary>Профиль клипа: скорость, направление хода от курса и поворот в доле клипа u ∈ [0, 1]; фаза шага.</summary>
    private sealed record Profile(int Frames, bool Loop, Func<float, float> Speed, Func<float, float> Heading, Func<float, float> Turn,
        float Cadence, float Phase, float Amplitude, bool WalkedBefore);

    public static SyntheticMotion Create(int seed, int queries = QueryCount)
    {
        var rng = new Random(seed);
        var schema = Schema();
        var profiles = Profiles(rng);
        int frames = profiles.Sum(p => p.Frames), joints = 10;
        var skeleton = new Skeleton("mm-bench", ["root", "pelvis", "thigh_l", "calf_l", "foot_l", "thigh_r", "calf_r", "foot_r", "clavicle_l", "clavicle_r"],
            [-1, 0, 1, 2, 3, 1, 5, 6, 1, 1]);
        var libClips = new ClipLibrary.Clip[profiles.Length];
        var clips = new MotionClip[profiles.Length];
        var steps = new Pose2[frames];
        for (int c = 0, start = 0; c < profiles.Length; start += profiles[c].Frames, c++)
        {
            var p = profiles[c];
            libClips[c] = new ClipLibrary.Clip($"locomotion/clip{c:000}", start, p.Frames, p.Loop);
            clips[c] = new MotionClip(c, start, p.Frames, p.Loop);
            for (int i = 0; i < p.Frames; i++)
            {
                float u = p.Frames > 1 ? i / (p.Frames - 1f) : 0, s = p.Speed(u), h = p.Heading(u);
                steps[start + i] = new Pose2(MathF.Sin(h) * s / Rate, MathF.Cos(h) * s / Rate, p.Turn(u) / Rate);
            }
        }
        // корень библиотеки — интеграл шагов (базе он не нужен: перенос корня — в интервалах; библиотека должна сойтись по клипам)
        var motion = new Vector3[frames];
        var yaw = new float[frames];
        foreach (var clip in clips)
        {
            var root = Pose2.Identity;
            for (int i = 0; i < clip.Count; i++)
            {
                motion[clip.Start + i] = root.Position;
                yaw[clip.Start + i] = root.Yaw;
                root = Pose2.Compose(root, steps[clip.Start + i]);
            }
        }
        var library = new ClipLibrary(skeleton, Rate, libClips, Enumerable.Repeat(Quaternion.Identity, frames * joints).ToArray(),
            new Vector3[frames * joints], motion, yaw);

        int fc = schema.FeatureCount;
        var next = new int[frames];
        var interval = new Pose2[frames];
        var searchable = new bool[frames];
        var velocity = new Vector3[frames];
        var contacts = new byte[frames];
        var raw = new float[frames * fc];
        for (int c = 0; c < clips.Length; c++)
        {
            var clip = clips[c];
            var p = profiles[c];
            for (int i = 0; i < clip.Count; i++)
            {
                int f = clip.Start + i;
                bool last = i == clip.Count - 1;
                next[f] = !last ? f + 1 : clip.Loop ? clip.Start + 1 : -1;
                interval[f] = next[f] < 0 ? Pose2.Identity : Step(steps, clip, last ? 0 : i);
                searchable[f] = next[f] >= 0 && !(clip.Loop && last);
                velocity[f] = new Vector3(interval[f].X, 0, interval[f].Z) * Rate;
                Features(schema, steps, clip, p, i, raw.AsSpan(f * fc, fc), rng, out contacts[f]);
            }
        }
        var (mean, std) = Normalization(schema, raw, searchable);
        var features = new float[raw.Length];
        for (int f = 0; f < frames; f++)
            for (int i = 0; i < fc; i++) features[f * fc + i] = (raw[f * fc + i] - mean[i]) / std[i];
        var db = new MotionDatabase(library, schema, clips, next, interval, Enumerable.Repeat(-1, frames).ToArray(), searchable, features,
            mean, std, velocity, contacts, [Root, Pelvis]);
        return new SyntheticMotion
        {
            Database = db, Matcher = new MotionMatcher(db), Weights = schema.DefaultWeights.ToArray(), Queries = MakeQueries(db, rng, queries),
        };
    }

    private static Profile[] Profiles(Random rng)
    {
        float R(float a, float b) => a + (float)rng.NextDouble() * (b - a);
        var list = new List<Profile>();
        for (int c = 0; c < ClipCount; c++)
        {
            float phase = R(0, MathF.Tau);
            switch (c % 11)
            {
                case 0 or 1 or 2:   // цикл шага или бега в одну из 8 сторон, иногда по дуге
                {
                    bool run = rng.Next(2) == 0;
                    float speed = (run ? 3.6f : 1.6f) * R(0.9f, 1.1f), heading = rng.Next(8) * MathF.PI / 4 + R(-0.05f, 0.05f);
                    float turn = rng.Next(5) == 0 ? R(-1.2f, 1.2f) : 0;
                    list.Add(new Profile(rng.Next(30, 60), true, _ => speed, _ => heading, _ => turn, run ? 1.4f : 0.9f, phase,
                        run ? 1 : 0.55f, true));
                    break;
                }
                case 3 or 4 or 5 or 6:   // переход: старт, стоп, смена направления и скорости с поворотом
                {
                    float v0 = rng.Next(3) == 0 ? 0 : R(1.2f, 4), v1 = rng.Next(3) == 0 ? 0 : R(1.2f, 4);
                    float h0 = R(-MathF.PI, MathF.PI), h1 = R(-MathF.PI, MathF.PI), peak = R(-3.5f, 3.5f);
                    list.Add(new Profile(rng.Next(40, 130), false, u => v0 + (v1 - v0) * Smooth(u), u => h0 + (h1 - h0) * Smooth(u),
                        u => peak * MathF.Sin(MathF.PI * u), R(0.8f, 1.5f), phase, R(0.4f, 1), v0 > 0.3f));
                    break;
                }
                default:   // «танец»: медленное блуждание с поворотами
                {
                    float a = R(0.05f, 0.4f), w = R(0.5f, 3), turn = R(0.3f, 2);
                    list.Add(new Profile(rng.Next(150, 280), rng.Next(2) == 0, u => a * (1 + MathF.Sin(w * 10 * u)) / 2,
                        u => MathF.Sin(w * 7 * u) * 2, u => turn * MathF.Sin(w * 5 * u + 1), R(0.5f, 2), phase, R(0.2f, 0.7f), false));
                    break;
                }
            }
        }
        // до размера пака: длины клипов — в долю
        double scale = TargetFrames / (double)list.Sum(p => p.Frames);
        return [.. list.Select(p => p with { Frames = Math.Max(3, (int)Math.Round(p.Frames * scale)) })];
    }

    private static float Smooth(float u) => u * u * (3 - 2 * u);

    /// <summary>Шаг корня из кадра i клипа (у цикла — по кругу, период «кадров − 1»; после конца нециклического — последний шаг).</summary>
    private static Pose2 Step(Pose2[] steps, MotionClip clip, int i)
    {
        if (clip.Loop) return steps[clip.Start + (clip.Count > 1 ? ((i % (clip.Count - 1)) + clip.Count - 1) % (clip.Count - 1) : 0)];
        return steps[clip.Start + Math.Clamp(i, 0, clip.Count - 2 >= 0 ? clip.Count - 2 : 0)];
    }

    private static void Features(MotionSchema schema, Pose2[] steps, MotionClip clip, Profile p, int i, Span<float> row, Random rng, out byte contact)
    {
        float Noise(float a) => ((float)rng.NextDouble() - 0.5f) * 2 * a;
        var future = schema.FutureTimes;
        int k = 0;
        // траектория будущего: интервалы вперёд
        Span<Pose2> ahead = stackalloc Pose2[future.Length];
        var pose = Pose2.Identity;
        for (int n = 0; k < future.Length;)
        {
            int target = (int)MathF.Round(future[k] * Rate);
            for (; n < target; n++) pose = Pose2.Compose(pose, Step(steps, clip, i + n));
            ahead[k++] = pose;
        }
        for (k = 0; k < future.Length; k++)
        {
            row[2 * k] = ahead[k].X;
            row[2 * k + 1] = ahead[k].Z;
            row[6 + 2 * k] = MathF.Sin(ahead[k].Yaw);
            row[6 + 2 * k + 1] = MathF.Cos(ahead[k].Yaw);
        }
        // прошлое: назад; у нециклического до начала — стоял или шёл первым шагом
        var back = Pose2.Identity;
        int past = (int)MathF.Round(-schema.PastTimes[0] * Rate);
        for (int n = 1; n <= past; n++)
        {
            int j = i - n;
            if (j < 0 && !clip.Loop && !p.WalkedBefore) break;
            back = Pose2.Compose(back, Step(steps, clip, clip.Loop ? j : Math.Max(j, 0)).Inverse());
        }
        row[12] = back.X;
        row[13] = back.Z;
        var step = Step(steps, clip, i);
        float vx = step.X * Rate, vz = step.Z * Rate;
        row[14] = vx;
        row[15] = vz;
        row[16] = step.Yaw * Rate;
        // суставы: фаза шага, амплитуда по скорости, направление хода
        float t = i / Rate, phi = MathF.Tau * p.Cadence * t + p.Phase, w = MathF.Tau * p.Cadence;
        float speed = MathF.Sqrt(vx * vx + vz * vz), amp = p.Amplitude * Math.Clamp(speed / 3.5f, 0.15f, 1);
        float dx = speed > 1e-4f ? vx / speed : 0, dz = speed > 1e-4f ? vz / speed : 1;
        contact = 0;
        for (int side = 0; side < 2; side++)
        {
            float ph = phi + side * MathF.PI, lateral = side == 0 ? 0.12f : -0.12f;
            float lift = MathF.Max(0, MathF.Sin(ph)), stride = 0.35f * amp * MathF.Cos(ph);
            float h = 0.02f + 0.1f * amp * lift, dh = MathF.Sin(ph) > 0 ? 0.1f * amp * w * MathF.Cos(ph) : 0, ds = -0.35f * amp * w * MathF.Sin(ph);
            if (h < 0.05f) contact |= (byte)(1 << side);
            int foot = 17 + 3 * side, footVel = 23 + 3 * side, knee = 29 + 3 * side;
            row[foot] = lateral + stride * dx + Noise(0.004f);
            row[foot + 1] = h + Noise(0.003f);
            row[foot + 2] = stride * dz + Noise(0.004f);
            row[footVel] = vx + ds * dx + Noise(0.03f);
            row[footVel + 1] = dh + Noise(0.02f);
            row[footVel + 2] = vz + ds * dz + Noise(0.03f);
            row[knee] = lateral * 0.9f + 0.5f * stride * dx + Noise(0.004f);
            row[knee + 1] = 0.48f + 0.5f * h + Noise(0.003f);
            row[knee + 2] = 0.5f * stride * dz + 0.05f + Noise(0.004f);
        }
        row[35] = 0.02f * amp * MathF.Sin(phi) + Noise(0.002f);
        row[36] = 0.95f - 0.03f * amp + 0.015f * MathF.Cos(2 * phi) + Noise(0.002f);
        row[37] = 0.03f * amp + Noise(0.002f);
        row[38] = vx + 0.02f * amp * w * MathF.Cos(phi) + Noise(0.02f);
        row[39] = -0.03f * w * MathF.Sin(2 * phi) + Noise(0.02f);
        row[40] = vz + Noise(0.02f);
        float twist = 0.25f * amp * MathF.Sin(phi) + 0.3f * dx;
        row[41] = MathF.Sin(twist);
        row[42] = MathF.Cos(twist);
        row[43] = MathF.Sin(-0.5f * twist);
        row[44] = MathF.Cos(-0.5f * twist);
    }

    private static (float[] Mean, float[] Std) Normalization(MotionSchema schema, float[] raw, bool[] searchable)
    {
        int fc = schema.FeatureCount, frames = searchable.Length, rows = searchable.Count(s => s);
        var mean = new float[fc];
        var std = new float[fc];
        var m = new double[fc];
        for (int f = 0; f < frames; f++)
            if (searchable[f])
                for (int i = 0; i < fc; i++) m[i] += raw[f * fc + i];
        for (int i = 0; i < fc; i++) m[i] /= rows;
        var v = new double[fc];
        for (int f = 0; f < frames; f++)
            if (searchable[f])
                for (int i = 0; i < fc; i++) v[i] += (raw[f * fc + i] - m[i]) * (raw[f * fc + i] - m[i]);
        foreach (var g in schema.Groups)
        {
            double sum = 0;
            for (int i = g.Offset; i < g.End; i++) sum += Math.Sqrt(v[i] / rows);
            float s = MathF.Max(g.MinStd, (float)(sum / g.Size));
            for (int i = g.Offset; i < g.End; i++)
            {
                mean[i] = (float)m[i];
                std[i] = s;
            }
        }
        return (mean, std);
    }

    /// <summary>
    /// Запросы как в игре: текущий кадр — случайный искомый, скорость до 4,2 м/с в любую сторону, поворот до полуоборота;
    /// модель — разгон 14 1/с, поворот 12 рад/с, тик 120 Гц.
    /// </summary>
    private static Query[] MakeQueries(MotionDatabase db, Random rng, int count)
    {
        float R(float a, float b) => a + (float)rng.NextDouble() * (b - a);
        var query = new MotionQuery(db);
        var history = new RootHistory(Tick, db.Schema.PastDepth);
        var rows = db.Rows;
        var result = new Query[count];
        for (int q = 0; q < count; q++)
        {
            int current = rows[rng.Next(rows.Length)];
            float yaw = R(-MathF.PI, MathF.PI);
            var root = new Pose2(R(-50, 50), R(-50, 50), yaw);
            var local = rng.Next(2) == 0
                ? db.RootVelocity[current]
                : Pose2.Rotate(Vector3.UnitZ * R(0, 4.2f), R(-MathF.PI, MathF.PI));
            var velocity = Pose2.Rotate(local, yaw);
            var goal = rng.Next(3) == 0 ? Pose2.Rotate(Vector3.UnitZ * R(0, 4.2f), R(-MathF.PI, MathF.PI)) : velocity;
            float goalYaw = rng.Next(5) < 3 ? yaw : yaw + R(-MathF.PI, MathF.PI);
            history.Clear();
            long now = 10_000 + q * 100;
            for (long t = now - 50; t < now; t++)
            {
                float back = (now - t) * Tick;
                history.Add(t, new Pose2(root.X - velocity.X * back, root.Z - velocity.Z * back, yaw));
            }
            query.Fill(new TrajectoryState(root, velocity), new TrajectoryModel(14, 12, goal, goalYaw), history, now, current, 1, 1,
                query.DataYawRate);
            result[q] = new Query(query.Values.ToArray(), current);
        }
        return result;
    }
}
