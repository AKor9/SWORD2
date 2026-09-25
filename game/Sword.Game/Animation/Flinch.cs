using System.Diagnostics.CodeAnalysis;
using Sword.Engine.Animation;

namespace Sword.Game.Animation;

/// <summary>Слот вздрагивания: клип реакции (−1 — слот свободен), время от начала и длина, с (<c>FighterAnimator.Flinch</c> SWORD).</summary>
public struct FlinchSlot
{
    public int Clip;
    public float Time, Length;

    public static FlinchSlot Free => new() { Clip = -1 };
}

/// <summary>
/// Вздрагивание игрока от попадания (<c>FighterAnimator.StartFlinch</c>, <c>_flinch</c>, <c>_flinchLast</c> SWORD 2eab13b — реакция
/// <c>Agile</c>): первые 0,55 с клипа реакции — добавкой поворотов к верху тела (отклонение от первого кадра клипа, сила 0,8) поверх
/// удара, бега и рывка; управление не отнимается (цель SWORD2 — отзывчивый бой). Два слота: новое попадание гасит прошлое с текущего
/// веса, а не обрывает. Компонент игрока; хешируется, виден агенту (<c>entity.get player</c>).
/// </summary>
public struct Flinch
{
    public FlinchSlot First, Second;

    /// <summary>Слот последнего вздрагивания (0 — <see cref="First"/>, 1 — <see cref="Second"/>).</summary>
    public int Last;

    /// <summary>Вздрагиваний за жизнь бойца.</summary>
    public int Count;

    public static Flinch None => new() { First = FlinchSlot.Free, Second = FlinchSlot.Free };

    [UnscopedRef]
    public ref FlinchSlot Slot(int i) => ref i == 0 ? ref First : ref Second;
}

/// <summary>Константы и логика вздрагивания — <c>CharacterRig.Flinch*</c> и <c>FighterAnimator</c> SWORD 2eab13b.</summary>
public static class FlinchLayer
{
    /// <summary>Длина вздрагивания, с; вход и выход огибающей, с; сила добавки (<c>FlinchLength</c>, <c>FlinchIn</c>, <c>FlinchOut</c>, <c>FlinchStrength</c>).</summary>
    public const float Length = 0.55f, In = 0.06f, Out = 0.2f, Strength = 0.8f;

    /// <summary>Вес вздрагивания в момент <paramref name="t"/> при длине <paramref name="length"/>: быстрый вход, мягкий выход (<c>FlinchEnvelope</c>).</summary>
    public static float Envelope(float t, float length) => Math.Clamp(t / In, 0, 1) * Math.Clamp((length - t) / Out, 0, 1);

    /// <summary>
    /// Новое вздрагивание клипом <paramref name="clip"/> длительностью <paramref name="duration"/> (<c>StartFlinch</c>): прошлый слот гаснет с
    /// текущего веса, новый — в другом слоте с начала. Отличия от SWORD (оба — скачки позы при частых попаданиях, их ловит
    /// <c>physics.kinematicSpeed</c>: предплечье до 41 м/с): прошлый гаснет от своего веса выхода — длина не больше «сейчас + выход·доля
    /// выхода» (в SWORD — «сейчас + выход·огибающая»: на входе, первые 0,06 с, вес падал скачком до квадрата входа); другой слот ещё виден
    /// (третье попадание за время угасания) — нового вздрагивания нет, идущие доигрывают (в SWORD другой слот обрывался). false — не начато.
    /// </summary>
    public static bool Start(ref Flinch f, int clip, float duration)
    {
        int next = f.Last ^ 1;
        ref var target = ref f.Slot(next);
        if (target.Clip >= 0 && Envelope(target.Time, target.Length) > 0) return false;
        ref var prev = ref f.Slot(f.Last);
        if (prev.Clip >= 0) prev.Length = MathF.Min(prev.Length, prev.Time + Out * Math.Clamp((prev.Length - prev.Time) / Out, 0, 1));
        f.Last = next;
        target.Clip = clip;
        target.Time = 0;
        target.Length = MathF.Min(Length, duration);
        f.Count++;
        return true;
    }

    /// <summary>Шаг на тик (начало цикла вздрагиваний <c>TickActions</c>): время слотов; вышло — слот свободен.</summary>
    public static void Tick(ref Flinch f, float dt)
    {
        for (int i = 0; i < 2; i++)
        {
            ref var s = ref f.Slot(i);
            if (s.Clip < 0) continue;
            s.Time += dt;
            if (s.Time >= s.Length) s.Clip = -1;
        }
    }

    /// <summary>
    /// Задания пулу поз (конец цикла <c>TickActions</c>: после действия и снимка, до прямой кинематики): каждый занятый слот по порядку —
    /// первый кадр клипа и кадр слота, добавка по маске верха <paramref name="upper"/> с весом силы × огибающей. Позы берутся при
    /// первом вздрагивании.
    /// </summary>
    public static void Pose(in Flinch f, ref ActionPoses poses, PoseHandle pose, PosePool pool, PoseMask upper)
    {
        for (int i = 0; i < 2; i++)
        {
            var s = i == 0 ? f.First : f.Second;
            if (s.Clip < 0) continue;
            if (poses.FlinchRef.IsNone)
            {
                poses.FlinchRef = pool.Alloc();
                poses.FlinchPose = pool.Alloc();
            }
            pool.Sample(poses.FlinchRef, s.Clip, 0);
            pool.Sample(poses.FlinchPose, s.Clip, s.Time);
            pool.AddMasked(pose, poses.FlinchRef, poses.FlinchPose, Strength * Envelope(s.Time, s.Length), upper);
        }
    }
}
