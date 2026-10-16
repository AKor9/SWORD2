using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sword.Engine.Animation.Motion;

/// <summary>
/// Граф переходов базы и траектории. Внутри клипа f → f + 1; цикл — последний кадр (= первый) идёт во второй. Рёбра
/// нециклических клипов строятся после связей внутри всех клипов (иначе ранний выход в цикл дальше по списку терялся); вход
/// никогда не бывает выходом. У клипа одно ребро: последний кадр или ранний выход, если его шов заметно дешевле
/// (<see cref="MotionBuildParams.SeamTie"/>); при ничьей — меньший номер кадра. Кадр, чей граф обрывается раньше горизонта, не ищется.
/// </summary>
public sealed partial class MotionDatabase
{
    private sealed partial class Builder
    {
        /// <summary>[клип] измеренное движение — вход политики.</summary>
        private MotionClipInfo[] _infos = [];

        /// <summary>Скорости начала, выхода и средняя, поворот — по корню кинематики.</summary>
        public void MeasureClips()
        {
            _infos = new MotionClipInfo[Clips.Length];
            int window = Math.Max(1, (int)MathF.Round(Params.VelocityWindow * Rate));
            for (int c = 0; c < Clips.Length; c++)
            {
                var clip = Clips[c];
                int n = clip.Count;
                var root = Kin.Root.Slice(clip.Start, n);
                if (n < 2)
                {
                    _infos[c] = new MotionClipInfo(clip.LibraryClip, clip.Loop, n, default, default, default, 0);
                    continue;
                }
                int k = Math.Min(window, n - 1);
                float windowSeconds = k / Rate, clipSeconds = (n - 1) / Rate;
                var start = Pose2.Relative(root[0], root[k]).Position / windowSeconds;
                var exit = -Pose2.Relative(root[n - 1], root[n - 1 - k]).Position / windowSeconds;
                var mean = Pose2.Relative(root[0], root[n - 1]).Position / clipSeconds;
                _infos[c] = new MotionClipInfo(clip.LibraryClip, clip.Loop, n, start, exit, mean, root[n - 1].Yaw - root[0].Yaw);
            }
        }

        /// <summary>Связи внутри клипов и шов цикла.</summary>
        public void LinkClips()
        {
            Array.Fill(Entry, -1);
            foreach (var clip in Clips) LinkClip(clip);
        }

        private void LinkClip(MotionClip clip)
        {
            int last = clip.End - 1;
            for (int f = clip.Start; f < last; f++)
            {
                Next[f] = f + 1;
                Interval[f] = Pose2.Relative(Kin.Root[f], Kin.Root[f + 1]);
            }
            if (clip.Loop && clip.Count >= 2)
            {
                Next[last] = clip.Start + 1;
                Interval[last] = Interval[clip.Start];
            }
            else
            {
                Next[last] = -1;
                Interval[last] = Pose2.Identity;
            }
        }

        /// <summary>Рёбра нециклических клипов: кандидаты от политики (по порядку клипов), вход — ценой шва, ранний выход.</summary>
        public void LinkEdges()
        {
            int window = (int)MathF.Round(Params.ExitWindow * Rate);
            var candidates = new List<int>();
            var entries = new List<int>();
            for (int c = 0; c < Clips.Length; c++)
            {
                if (Clips[c].Loop) continue;
                candidates.Clear();
                Policy.Continuations(c, _infos, candidates);
                EntryFrames(c, candidates, window, entries);
                if (entries.Count == 0) continue;
                var (exit, entry) = BestEdge(Clips[c], CollectionsMarshal.AsSpan(entries), window);
                Entry[exit] = entry;
                Next[exit] = Next[entry];
                Interval[exit] = Interval[entry];
            }
        }

        /// <summary>Кадры входа кандидатов — по возрастанию, без повторов.</summary>
        private void EntryFrames(int clip, List<int> candidates, int window, List<int> entries)
        {
            entries.Clear();
            candidates.Sort();
            for (int i = 0; i < candidates.Count; i++)
            {
                int d = candidates[i];
                if ((uint)d >= (uint)Clips.Length)
                    throw new ArgumentException($"политика графа: кандидат {d} продолжения клипа {ClipName(clip)} вне базы ({Clips.Length} клипов)", "policy");
                if (i > 0 && d == candidates[i - 1]) continue;
                var target = Clips[d];
                // у цикла — все, кроме последнего (он = первый); у нециклического — до окна выхода (вход не бывает выходом)
                int end = target.Loop ? (target.Count >= 2 ? target.End - 1 : target.Start) : target.End - 1 - window;
                for (int f = target.Start; f < end; f++) entries.Add(f);
            }
        }

        /// <summary>
        /// Выход и вход клипа — цепочка лучших пар: последний кадр со всеми входами, затем кадры окна по возрастанию; новая пара
        /// сменяет лучшую, только если дешевле больше чем на долю ничьей.
        /// </summary>
        private (int Exit, int Entry) BestEdge(MotionClip clip, ReadOnlySpan<int> entries, int window)
        {
            int exit = clip.End - 1, entry = -1;
            float cost = float.PositiveInfinity;
            Improve(exit, entries, ref entry, ref cost);
            for (int e = Math.Max(clip.Start, clip.End - 1 - window); e < clip.End - 1; e++)
                if (Improve(e, entries, ref entry, ref cost)) exit = e;
            return (exit, entry);
        }

        /// <summary>
        /// Входы для кадра выхода — продолжение цепочки: вход сменяет лучший, только если дешевле <see cref="Beat"/>. Шов с отсечкой
        /// по ней — итог тот же (сумма квадратов не убывает). true — цепочка сменилась.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private bool Improve(int exit, ReadOnlySpan<int> entries, ref int entry, ref float cost)
        {
            bool improved = false;
            foreach (int x in entries)
            {
                float bound = Beat(cost);
                float c = Seam(exit, x, bound);
                if (c < bound)
                {
                    cost = c;
                    entry = x;
                    improved = true;
                }
            }
            return improved;
        }

        /// <summary>
        /// Граница «заметно дешевле» цены <paramref name="cost"/>: разница меньше доли SeamTie от max(цена, 1) — ничья. Пол «1»: у
        /// почти нулевых цен (~10⁻⁹) разница — шум округления, и без AVX2 менялся ранний выход.
        /// </summary>
        private float Beat(float cost) => float.IsPositiveInfinity(cost) ? cost : cost - Params.SeamTie * MathF.Max(cost, 1);

        /// <summary>Траектории будущего и прошлого всех кадров; искомые кадры — с полным горизонтом будущего по графу.</summary>
        public void FillTrajectories()
        {
            var future = Schema.FutureTimes;
            var pastTimes = Schema.PastTimes;
            var futureSteps = new int[future.Length];
            for (int k = 0; k < future.Length; k++) futureSteps[k] = (int)MathF.Round(future[k] * Rate);
            var pastSteps = new int[pastTimes.Length];
            for (int k = 0; k < pastTimes.Length; k++) pastSteps[k] = (int)MathF.Round(-pastTimes[k] * Rate);
            var past = new MotionPast[Clips.Length];
            if (pastSteps.Length > 0)
                for (int c = 0; c < Clips.Length; c++)
                {
                    if (Clips[c].Loop) continue;
                    past[c] = Policy.Past(c, _infos);
                    if (!Enum.IsDefined(past[c]))
                        throw new ArgumentException($"политика графа: прошлое {past[c]} клипа {ClipName(c)} — неизвестное", "policy");
                }
            int position = Schema.Find(FeatureKind.TrajectoryPosition), direction = Schema.Find(FeatureKind.TrajectoryDirection),
                pastGroup = Schema.Find(FeatureKind.PastPosition);
            int positionAt = position < 0 ? -1 : Schema.Groups[position].Offset;
            int directionAt = direction < 0 ? -1 : Schema.Groups[direction].Offset;
            int pastAt = pastGroup < 0 ? -1 : Schema.Groups[pastGroup].Offset;
            for (int c = 0; c < Clips.Length; c++)
            {
                var clip = Clips[c];
                for (int i = 0; i < clip.Count; i++)
                {
                    int f = clip.Start + i;
                    bool complete = Future(f, futureSteps, positionAt, directionAt);
                    if (pastAt >= 0) Past(c, i, pastSteps, past[c], pastAt);
                    Searchable[f] = complete && Next[f] >= 0 && !(clip.Loop && i == clip.Count - 1);
                }
            }
        }

        /// <summary>Будущее кадра по графу — в признаки; false — граф оборвался раньше горизонта.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private bool Future(int f, int[] steps, int positionAt, int directionAt)
        {
            var row = Raw.AsSpan(f * Fc, Fc);
            var at = Pose2.Identity;
            int current = f, walked = 0;
            bool complete = true;
            for (int k = 0; k < steps.Length; k++)
            {
                while (complete && walked < steps[k])
                {
                    if (Next[current] < 0)
                    {
                        complete = false;
                        break;
                    }
                    at = Pose2.Compose(at, Interval[current]);
                    current = Next[current];
                    walked++;
                }
                if (positionAt >= 0)
                {
                    row[positionAt + 2 * k] = at.X;
                    row[positionAt + 2 * k + 1] = at.Z;
                }
                if (directionAt >= 0)
                {
                    row[directionAt + 2 * k] = MathF.Sin(at.Yaw);
                    row[directionAt + 2 * k + 1] = MathF.Cos(at.Yaw);
                }
            }
            return complete;
        }

        /// <summary>Прошлое кадра <paramref name="i"/> клипа <paramref name="clip"/> — положения корня в его системе.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void Past(int clip, int i, int[] steps, MotionPast policy, int pastAt)
        {
            var c = Clips[clip];
            var here = Kin.Root[c.Start + i];
            var row = Raw.AsSpan((c.Start + i) * Fc, Fc);
            for (int k = 0; k < steps.Length; k++)
            {
                int j = i - steps[k];
                Pose2 then;
                if (!c.Loop && j < 0 && policy == MotionPast.StartVelocity)
                {
                    // шёл к первому кадру со скоростью начала: назад от него на (−j) кадров
                    var first = Kin.Root[c.Start];
                    then = new Pose2(first.ToParent(-_infos[clip].StartVelocity * (-j / Rate)), first.Yaw);
                }
                else then = Kin.RootAt(clip, j);   // у цикла — по оборотам; у нециклического до начала — «стоял» в первом кадре
                var local = Pose2.Relative(here, then);
                row[pastAt + 2 * k] = local.X;
                row[pastAt + 2 * k + 1] = local.Z;
            }
        }
    }
}
