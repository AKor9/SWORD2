using System.Runtime.CompilerServices;

namespace Sword.Engine.Animation.Motion;

/// <summary>
/// Нормализация признаков базы: среднее по признаку и разброс, общий на группу (не меньше <see cref="FeatureGroup.MinStd"/>);
/// суммы в double, по <c>N</c>, порядок сложения возрастающий — итог от потоков не зависит. Дважды: поза по всем кадрам (для
/// цены шва), затем все признаки по искомым кадрам.
/// </summary>
public sealed partial class MotionDatabase
{
    private sealed partial class Builder
    {
        /// <summary>Номера признаков позы (не траектории) в строке — по порядку.</summary>
        private int[] _poseFeatures = [];

        /// <summary>[кадр·признак позы] нормализованные признаки позы — для цены шва.</summary>
        private float[] _pose = [];

        private int PoseCount => _poseFeatures.Length;

        /// <summary>Нормализация позы по всем кадрам базы — для цены шва (<see cref="Seam"/>).</summary>
        public void NormalizePose()
        {
            var features = new List<int>();
            foreach (var g in Schema.Groups)
                if (!MotionSchema.IsTrajectory(g.Kind))
                    for (int i = g.Offset; i < g.End; i++) features.Add(i);
            _poseFeatures = [.. features];
            var (mean, std) = Stats(null);
            int pc = PoseCount;
            _pose = new float[Frames * pc];
            for (int f = 0; f < Frames; f++) NormalizePoseFrame(f, mean, std);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void NormalizePoseFrame(int f, float[] mean, float[] std)
        {
            var row = Raw.AsSpan(f * Fc, Fc);
            var into = _pose.AsSpan(f * PoseCount, PoseCount);
            for (int k = 0; k < into.Length; k++)
            {
                int i = _poseFeatures[k];
                into[k] = (row[i] - mean[i]) / std[i];
            }
        }

        /// <summary>Итоговая нормализация по искомым кадрам; признаки всех кадров — на месте (<see cref="Raw"/> становится нормализованным).</summary>
        /// <exception cref="InvalidDataException">Нет ни одного искомого кадра; признак — не число (с клипом и группой).</exception>
        public (float[] Mean, float[] Std) Normalize()
        {
            var (mean, std) = Stats(Searchable);
            for (int f = 0; f < Frames; f++) NormalizeFrame(f, mean, std);
            return (mean, std);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void NormalizeFrame(int f, float[] mean, float[] std)
        {
            var row = Raw.AsSpan(f * Fc, Fc);
            for (int i = 0; i < row.Length; i++)
            {
                if (!float.IsFinite(row[i])) throw NotFinite(f, i, row[i]);
                row[i] = (row[i] - mean[i]) / std[i];
            }
        }

        private InvalidDataException NotFinite(int f, int feature, float value)
        {
            int clip = 0;
            while (Clips[clip].End <= f) clip++;
            string group = "";
            foreach (var g in Schema.Groups)
                if (feature >= g.Offset && feature < g.End) group = g.Name;
            return new InvalidDataException(
                $"клип {ClipName(clip)}, кадр {f - Clips[clip].Start}: признак {feature} (группа «{group}») = {value} — данные клипа испорчены");
        }

        /// <summary>Среднее по признаку и разброс группы по кадрам <paramref name="mask"/> (null — все).</summary>
        /// <exception cref="InvalidDataException">По маске нет ни одного кадра.</exception>
        private (float[] Mean, float[] Std) Stats(bool[]? mask)
        {
            var sum = new double[Fc];
            int n = 0;
            for (int f = 0; f < Frames; f++)
                if (mask is null || mask[f])
                {
                    AddRow(f, sum);
                    n++;
                }
            if (n == 0)
                throw new InvalidDataException($"в базе ни одного искомого кадра (клипов {Clips.Length}, кадров {Frames}): ни у одного кадра " +
                    $"нет полного горизонта будущего {Schema.Horizon} с по графу — клипы короче горизонта и без продолжения по политике");
            var mean = new double[Fc];
            for (int i = 0; i < Fc; i++) mean[i] = sum[i] / n;
            var squares = new double[Fc];
            for (int f = 0; f < Frames; f++)
                if (mask is null || mask[f]) AddSquares(f, mean, squares);

            var meanF = new float[Fc];
            var stdF = new float[Fc];
            for (int i = 0; i < Fc; i++) meanF[i] = (float)mean[i];
            foreach (var g in Schema.Groups)
            {
                double groupStd = 0;
                for (int i = g.Offset; i < g.End; i++) groupStd += Math.Sqrt(squares[i] / n);
                groupStd /= g.Size;
                stdF.AsSpan(g.Offset, g.Size).Fill(MathF.Max((float)groupStd, g.MinStd));
            }
            return (meanF, stdF);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void AddRow(int f, double[] sum)
        {
            var row = Raw.AsSpan(f * Fc, Fc);
            for (int i = 0; i < row.Length; i++) sum[i] += row[i];
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void AddSquares(int f, double[] mean, double[] squares)
        {
            var row = Raw.AsSpan(f * Fc, Fc);
            for (int i = 0; i < row.Length; i++)
            {
                double d = row[i] - mean[i];
                squares[i] += d * d;
            }
        }

        /// <summary>Цена шва кадров <paramref name="a"/> и <paramref name="b"/> по признакам позы; дошла до <paramref name="bound"/> — счёт обрывается.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private float Seam(int a, int b, float bound)
        {
            var pa = _pose.AsSpan(a * PoseCount, PoseCount);
            var pb = _pose.AsSpan(b * PoseCount, PoseCount);
            float sum = 0;
            for (int k = 0; k < pa.Length; k++)
            {
                float d = pa[k] - pb[k];
                sum += d * d;
                if (sum >= bound) break;
            }
            return sum;
        }
    }
}
