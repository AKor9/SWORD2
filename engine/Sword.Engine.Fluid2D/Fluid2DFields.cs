namespace Sword.Engine.Fluid2D;

/// <summary>Стадия шага (FSR §3.3) — для проверки стадий по отдельности.</summary>
public enum Fluid2DStage
{
    /// <summary>Composite and Gradient: A, P, кисть → C.</summary>
    Composite,

    /// <summary>C → A.</summary>
    Advection,

    /// <summary>A, P, кисть → S (P₀, дивергенция).</summary>
    Divergence,

    /// <summary>Проход давления по x: S → P.</summary>
    PressureX,

    /// <summary>Проход давления по y: P → S.</summary>
    PressureY,
}

/// <summary>
/// Поля сима в битах half, как на GPU (ряд за рядом, ряд 0 — v = 0): <see cref="Composite"/> C (vx, vy, ρ, wet) — состояние после
/// Composite, его экспортирует <c>RT_Composite</c>; <see cref="Advected"/> A (vx, vy, ρ, wet) — после адвекции (wet — копия C того же
/// текселя: Composite берёт прошлую мокрую карту из A); <see cref="Pressure"/> P (P, D) — итог давления прошлого шага;
/// <see cref="Scratch"/> S — второй буфер давления. Загрузка и чтение — <see cref="Fluid2DSim.Load"/>, <see cref="Fluid2DSim.ReadFields"/>.
/// </summary>
public sealed class Fluid2DFields
{
    public Fluid2DFields(int n)
    {
        if (n <= 0) throw new ArgumentOutOfRangeException(nameof(n), n, "сторона > 0");
        N = n;
        Composite = new Half[4 * n * n];
        Advected = new Half[4 * n * n];
        Pressure = new Half[2 * n * n];
        Scratch = new Half[2 * n * n];
    }

    public int N { get; }
    public Half[] Composite { get; private set; }
    public Half[] Advected { get; private set; }
    public Half[] Pressure { get; private set; }
    public Half[] Scratch { get; private set; }

    public Fluid2DFields Clone()
    {
        var c = new Fluid2DFields(N);
        Composite.CopyTo(c.Composite, 0);
        Advected.CopyTo(c.Advected, 0);
        Pressure.CopyTo(c.Pressure, 0);
        Scratch.CopyTo(c.Scratch, 0);
        return c;
    }

    /// <summary>Итог давления — в <see cref="Scratch"/>: буферы меняются ролями (как на GPU после двух проходов).</summary>
    internal void SwapPressure() => (Pressure, Scratch) = (Scratch, Pressure);

    /// <summary>Плотность C в текселе.</summary>
    public float Density(int x, int y) => (float)Composite[(y * N + x) * 4 + 2];

    public (float X, float Y) Velocity(int x, int y) => ((float)Composite[(y * N + x) * 4], (float)Composite[(y * N + x) * 4 + 1]);
}
