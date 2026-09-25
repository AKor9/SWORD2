using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Sword.Engine.Formats;

namespace Sword.Bake.Engine.Anim;

/// <summary>
/// Сравнение клипов <c>.swclip</c> по содержимому без <see cref="ClipFile.SourceHash"/>: хеш исходника — сведения для
/// инкрементальной выпечки, и у клипов, испечённых разными сборками пекарни (эталон SWORD — с MVID конвертера в хеше,
/// SWORD2 — без), он разный по устройству. Всё остальное — заголовок и массивы — сравнивается побайтно (−0 ≠ +0, NaN
/// равен себе же с теми же битами). Преемник <c>inspect compare</c> SWORD; сравнитель <c>.swclip</c> для <c>bake diff</c> —
/// на нём (Б5).
/// </summary>
internal static class ClipContent
{
    /// <summary>null — клипы одинаковы; иначе — первое различие с подробностями (сколько значений, наибольшая разница, где).</summary>
    public static string? Diff(ClipFile a, ClipFile b)
    {
        var inv = CultureInfo.InvariantCulture;
        if (a.FileVersion != b.FileVersion) return $"версия {a.FileVersion} ≠ {b.FileVersion}";
        if (a.Skeleton != b.Skeleton || a.SkeletonHash != b.SkeletonHash)
            return $"скелет {a.Skeleton}:{a.SkeletonHash:X8} ≠ {b.Skeleton}:{b.SkeletonHash:X8}";
        if (BitConverter.SingleToInt32Bits(a.SampleRate) != BitConverter.SingleToInt32Bits(b.SampleRate) || a.Frames != b.Frames
            || a.Joints != b.Joints || a.Loop != b.Loop)
            return string.Create(inv, $"заголовок: {a.SampleRate} Гц, {a.Frames}×{a.Joints}, цикл {a.Loop} ≠ {b.SampleRate} Гц, {b.Frames}×{b.Joints}, цикл {b.Loop}");
        if (a.SourcePath != b.SourcePath) return $"путь исходника {a.SourcePath} ≠ {b.SourcePath}";
        return Floats("повороты", MemoryMarshal.Cast<Quaternion, float>(a.LocalRot.AsSpan()), MemoryMarshal.Cast<Quaternion, float>(b.LocalRot.AsSpan()), 4 * a.Joints)
            ?? Floats("смещения", MemoryMarshal.Cast<Vector3, float>(a.LocalPos.AsSpan()), MemoryMarshal.Cast<Vector3, float>(b.LocalPos.AsSpan()), 3 * a.Joints)
            ?? Floats("root motion", MemoryMarshal.Cast<Vector3, float>(a.RootMotion.AsSpan()), MemoryMarshal.Cast<Vector3, float>(b.RootMotion.AsSpan()), 3)
            ?? Floats("поворот root motion", a.RootYaw, b.RootYaw, 1);
    }

    /// <param name="perFrame">Чисел на кадр — для места первого различия.</param>
    private static string? Floats(string what, ReadOnlySpan<float> a, ReadOnlySpan<float> b, int perFrame)
    {
        if (a.Length != b.Length) return $"{what}: длина {a.Length} ≠ {b.Length}";
        if (MemoryMarshal.AsBytes(a).SequenceEqual(MemoryMarshal.AsBytes(b))) return null;
        int count = 0, first = -1;
        float max = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (BitConverter.SingleToInt32Bits(a[i]) == BitConverter.SingleToInt32Bits(b[i])) continue;
            count++;
            if (first < 0) first = i;
            max = MathF.Max(max, MathF.Abs(a[i] - b[i]));
        }
        return string.Create(CultureInfo.InvariantCulture,
            $"{what}: различаются {count} из {a.Length} чисел, наибольшая разница {max:G3}, первое — кадр {first / perFrame}, число {first % perFrame}");
    }
}
