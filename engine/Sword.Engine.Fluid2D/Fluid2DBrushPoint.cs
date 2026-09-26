using System.Numerics;
using System.Runtime.InteropServices;

namespace Sword.Engine.Fluid2D;

/// <summary>
/// Класс источника точки кисти (классы L2, BR §2.1): массив CPU-точек (кости, примитивы, мышь), частицы, обломки. От класса — джиттер
/// и проверка скачка скорости (только массивы), вес скорости (<see cref="Fluid2DParams.ArrayBrushVelocity"/>,
/// <see cref="Fluid2DParams.ParticleBrushVelocity"/>) и «только скорость» по <see cref="Fluid2DParams.SelectivelyKillBrushDensityKeepVelocity"/>.
/// Единицы скорости у всех классов одни (BR §8.2 (c)).
/// </summary>
public enum Fluid2DBrushSource : byte
{
    Array,
    Particle,
    Debris,
}

/// <summary>Флаги точки: «только скорость» (плотность не рисует) и экранная точка (мышь: отбора нет, prev — прошлая позиция с джиттером).</summary>
[Flags]
public enum Fluid2DBrushFlags : byte
{
    None = 0,
    VelocityOnly = 1,
    Screen = 2,
}

/// <summary>
/// Ключ точки кисти: класс источника, id сущности, индекс кости или сегмента. Состояние точки (прошлая позиция, скорость) хранится по ключу,
/// точки шага сортируются по <see cref="Packed"/> — порядок не зависит от порядка добавления.
/// </summary>
public readonly record struct Fluid2DBrushKey(Fluid2DBrushSource Source, uint Id, ushort Index) : IComparable<Fluid2DBrushKey>
{
    public ulong Packed => ((ulong)Source << 48) | ((ulong)Id << 16) | Index;

    public int CompareTo(Fluid2DBrushKey other) => Packed.CompareTo(other.Packed);

    public override string ToString() => $"{Source}:{Id}:{Index}";
}

/// <summary>
/// Точка шага для Painter (<c>fluid2d.hlsl</c> <c>CsPainter</c>, 20 слов в буфере кисти): всё, что не зависит от текселя (BR §4.2 до цикла по
/// текселям), посчитано на CPU один раз — GPU и CPU-эталон берут одни и те же числа.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Fluid2DBrushPoint
{
    /// <summary>Голова мазка (UV, с джиттером) и хвост prev − 0.01·WOD.</summary>
    public Vector2 Pos, Prev;

    /// <summary>Направление и длина отрезка prev→pos в UV; длина &lt; 0 — только штамп в голове.</summary>
    public Vector2 Dir;

    public float SegLen, C2;

    /// <summary>Толчок sign·|vc|^Pow, прокол pz, множители скорости радиального члена (str0 + 1 − k) и итога (min(strp^0.25, 1) + 1 − k).</summary>
    public Vector2 Vb;

    public float Pz, KVel, Mv, Strp, Hs, Zf;

    /// <summary>AABB штампа ±4σ·stretch вокруг отрезка (UV): вне него точка текселю не рисует.</summary>
    public Vector2 Min, Max;

    public const int Words = 20;

    /// <summary>
    /// Числа точки по BR §4.2: скорость (проверка скачка и вес класса), зажим, толчок, растяжение и K, отрезок, прокол, сила, жёсткость.
    /// <paramref name="position"/> — UV головы (z — высота/глубина, 0.5 — плоскость), <paramref name="prev"/> — UV хвоста до сдвига площадки,
    /// <paramref name="velocity"/>/<paramref name="prevVelocity"/> — UV-скорость шага и прошлого шага, <paramref name="age"/> — с с активации.
    /// </summary>
    public static Fluid2DBrushPoint Build(Fluid2DParams p, Fluid2DBrushSource source, bool velocityOnly, Vector3 position, Vector2 prev,
        Vector3 velocity, Vector3 prevVelocity, float scale, float age, Vector2 wod, out float sigma)
    {
        float k = velocityOnly ? 0 : DensityFactor(p.SelectivelyKillBrushDensityKeepVelocity, source);
        float vx = velocity.X, vy = velocity.Y, vz = velocity.Z;
        if (source == Fluid2DBrushSource.Array)
        {
            // Скачок |ΔUV| ≥ 1 за шаг (телепорт) обнуляет скорость — отрезка через прыжок нет.
            float dx = vx - prevVelocity.X, dy = vy - prevVelocity.Y, dz = vz - prevVelocity.Z;
            float d = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            float g = p.ArrayBrushVelocity * (d < 1f || d * d < 1e-10f ? 1f : 0f);
            vx *= g;
            vy *= g;
            vz *= g;
        }
        else if (source == Fluid2DBrushSource.Particle)
        {
            vx *= p.ParticleBrushVelocity;
            vy *= p.ParticleBrushVelocity;
            vz *= p.ParticleBrushVelocity;
        }

        float b = p.BrushVelocityClamp;
        float cx = MathF.Min(b, MathF.Max(-b, vx)), cy = MathF.Min(b, MathF.Max(-b, vy)), cz = MathF.Min(b, MathF.Max(-b, vz));
        var vb = new Vector2(Sign(cx) * Pow(MathF.Abs(cx), p.BrushVelocityPow), Sign(cy) * Pow(MathF.Abs(cy), p.BrushVelocityPow));
        float zf = p.FadeBrushByZPos ? 1f - Saturate(MathF.Abs(position.Z - 0.5f) * (10f / p.FadeBrushByZPosMult)) : 1f;
        bool interp = age > 0.25f && p.PosInterpolation;
        float sp = MathF.Sqrt(vx * vx + vy * vy + vz * vz);
        bool noInterp = sp == 0 || p.KillInterpolatorAboveThisVelocity < sp;
        float vzs = p.NegativePuncture ? MathF.Min(cz, 0.1f) : cz;
        float puncture = Saturate(0.1f * age) * p.BrushPuncture;
        float stretch = vzs * vzs * Math.Clamp(20f * puncture, 1f, 10f) * MathF.Min(MathF.Abs(10f * puncture), 1f) + 1f;
        float maxE = 100f * MathF.Max(p.Extents.X, p.Extents.Y);
        float kk = MathF.Max(1f / (0.002f * maxE) * scale * stretch * p.BrushSize, 0f);
        float c2 = -2000f / (kk * kk);

        var tail = prev - 0.01f * wod;
        float sgx = position.X - tail.X, sgy = position.Y - tail.Y;
        float segLen = MathF.Sqrt(sgx * sgx + sgy * sgy);
        bool segment = interp && !noInterp && segLen > 0;

        float mz = p.BrushVelocityMultZ * cz;
        float pz = (p.NegativePuncture ? -mz : MathF.Abs(mz)) * 10f + puncture;
        float kv = sp < p.KillBrushBelowThisVelocity ? Pow(sp, 1.5f) : 1f;
        float str0 = kv * (p.BrushStrength * k);
        float strp = pz * str0 * 0.1f + str0;

        // σ в UV длинной стороны: f = 2^(c2·d²) = exp(−d²/2σ²); за 4σ f < 3.4·10⁻⁴ (BR §8.2 (d)).
        sigma = (float)(kk / Math.Sqrt(4000.0 * Math.Log(2.0)));
        float r = 4f * sigma;
        var head = new Vector2(position.X, position.Y);
        var lo = segment ? Vector2.Min(head, tail) : head;
        var hi = segment ? Vector2.Max(head, tail) : head;
        return new Fluid2DBrushPoint
        {
            Pos = head,
            Prev = tail,
            Dir = segment ? new Vector2(sgx / segLen, sgy / segLen) : Vector2.Zero,
            SegLen = segment ? segLen : -1f,
            C2 = c2,
            Vb = vb,
            Pz = pz,
            KVel = str0 + (1f - k),
            Mv = MathF.Min(Pow(MathF.Max(strp, 0f), 0.25f), 1f) + (1f - k),
            Strp = strp,
            Hs = Math.Clamp(MathF.Min(p.BrushHardnessScaledByObjSize * scale, 0.5f) + p.BrushHardness, 0f, 0.95f),
            Zf = zf,
            Min = lo - new Vector2(r),
            Max = hi + new Vector2(r),
        };
    }

    /// <summary>k класса по SelectivelyKill: 1 — массивы, 2 — частицы, 3 — обломки, 4 — массивы и обломки, 5 — все (k = 0 — только скорость).</summary>
    internal static float DensityFactor(int selectivelyKill, Fluid2DBrushSource source)
    {
        int s = selectivelyKill;
        if (s == 5) return 0;
        return source switch
        {
            Fluid2DBrushSource.Array => s is 1 or 4 ? 0 : 1,
            Fluid2DBrushSource.Particle => s == 2 ? 0 : 1,
            _ => s is 3 or 4 ? 0 : 1,
        };
    }

    private static float Sign(float x) => (x > 0 ? 1f : 0f) - (x < 0 ? 1f : 0f);

    private static float Pow(float x, float y) => x <= 0 ? 0f : MathF.Pow(x, y);

    private static float Saturate(float x) => x >= 0 ? MathF.Min(x, 1f) : 0f;
}
