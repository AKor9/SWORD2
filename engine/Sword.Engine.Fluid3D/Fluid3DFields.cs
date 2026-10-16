namespace Sword.Engine.Fluid3D;

/// <summary>Стадия шага — для проверки стадий по отдельности (буферы — <see cref="Fluid3DFields"/>).</summary>
public enum Fluid3DStage
{
    /// <summary>Сдвиг окна и источники: V, S, Aux → V₁, S₁, новый Aux (давление — сдвинутое или 0; буферы Aux меняются ролями).</summary>
    Sources,

    /// <summary>V₁ → t в Aux (|ω|).</summary>
    Curl,

    /// <summary>V₁, S₁, t в Aux → V (плавучесть и вихревое удержание).</summary>
    Forces,

    /// <summary>V → V₁ (полулагранжева).</summary>
    AdvectVelocity,

    /// <summary>V₁ → t в Aux (дивергенция; давление — прошлое или 0).</summary>
    Divergence,

    /// <summary>Полупроход SOR по «красным» ячейкам ((x + y + z) чётно), Aux на месте.</summary>
    PressureRed,

    /// <summary>Полупроход SOR по «чёрным» ячейкам.</summary>
    PressureBlack,

    /// <summary>V₁, давление Aux → V (проекция и предел скорости).</summary>
    Project,

    /// <summary>V, S₁ → S (полулагранжева с затуханием; при MacCormack — итог по S₁ и S₂).</summary>
    AdvectScalars,

    /// <summary>MacCormack, прямой полушаг: V, S₁ → S₂.</summary>
    AdvectForward,
}

/// <summary>
/// Поля сима в битах half, как на GPU (ячейка x + Nx·(y + Ny·z), y — вверх): <see cref="Velocity"/> V (vx, vy, vz, 0) и <see cref="Scalars"/>
/// S (ρ, T) — состояние шага; <see cref="VelocityScratch"/> V₁, <see cref="ScalarScratch"/> S₁, <see cref="ScalarForward"/> S₂ — промежуточные;
/// <see cref="Aux"/> (p, t) — давление шага и служебное t (|ω| после Curl, дивергенция после Divergence); <see cref="AuxPrevious"/> — второй
/// буфер Aux. Загрузка и чтение — <see cref="Fluid3DSim.Load"/>, <see cref="Fluid3DSim.ReadFields"/>.
/// </summary>
public sealed class Fluid3DFields
{
    public Fluid3DFields(int nx, int ny, int nz)
    {
        Nx = nx;
        Ny = ny;
        Nz = nz;
        int cells = nx * ny * nz;
        Velocity = new Half[4 * cells];
        VelocityScratch = new Half[4 * cells];
        Scalars = new Half[2 * cells];
        ScalarScratch = new Half[2 * cells];
        ScalarForward = new Half[2 * cells];
        Aux = new Half[2 * cells];
        AuxPrevious = new Half[2 * cells];
    }

    public int Nx { get; }
    public int Ny { get; }
    public int Nz { get; }
    public int Cells => Nx * Ny * Nz;

    public Half[] Velocity { get; private set; }
    public Half[] VelocityScratch { get; private set; }
    public Half[] Scalars { get; private set; }
    public Half[] ScalarScratch { get; private set; }
    public Half[] ScalarForward { get; private set; }
    public Half[] Aux { get; private set; }
    public Half[] AuxPrevious { get; private set; }

    public int Index(int x, int y, int z) => x + Nx * (y + Ny * z);

    /// <summary>Aux меняются ролями (стадия Sources пишет новый Aux из прошлого).</summary>
    internal void SwapAux() => (Aux, AuxPrevious) = (AuxPrevious, Aux);

    public Fluid3DFields Clone()
    {
        var f = new Fluid3DFields(Nx, Ny, Nz);
        Velocity.CopyTo(f.Velocity, 0);
        VelocityScratch.CopyTo(f.VelocityScratch, 0);
        Scalars.CopyTo(f.Scalars, 0);
        ScalarScratch.CopyTo(f.ScalarScratch, 0);
        ScalarForward.CopyTo(f.ScalarForward, 0);
        Aux.CopyTo(f.Aux, 0);
        AuxPrevious.CopyTo(f.AuxPrevious, 0);
        return f;
    }
}
