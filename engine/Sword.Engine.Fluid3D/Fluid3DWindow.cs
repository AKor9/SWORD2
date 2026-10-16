using System.Numerics;

namespace Sword.Engine.Fluid3D;

/// <summary>
/// Окно сетки в мире: ячейка (x, y, z) сетки — ячейка (<see cref="X"/> + x, <see cref="Y"/> + y, <see cref="Z"/> + z) мировой решётки
/// шага <see cref="CellSize"/>, её центр — (индекс + ½)·<see cref="CellSize"/>. Окно едет за целью только целыми ячейками: содержимое
/// переносится без пересэмплирования; дробный остаток цели — <see cref="Offset"/> (для показа).
/// </summary>
public readonly record struct Fluid3DWindow(int X, int Y, int Z, int Nx, int Ny, int Nz, float CellSize, Vector3 Target)
{
    /// <summary>Окно с центром у <paramref name="target"/>: центр бокса — ближайший к цели узел решётки (для чётных сторон).</summary>
    public static Fluid3DWindow Around(Vector3 target, Fluid3DParams p)
    {
        double h = p.CellSize;
        return new Fluid3DWindow(Origin(target.X, h, p.Nx), Origin(target.Y, h, p.Ny), Origin(target.Z, h, p.Nz), p.Nx, p.Ny, p.Nz,
            p.CellSize, target);
    }

    private static int Origin(float target, double h, int n) =>
        (int)Math.Clamp(Math.Floor(target / h - n * 0.5 + 0.5), int.MinValue / 2, int.MaxValue / 2);

    /// <summary>Нижний угол бокса сетки в мире, м.</summary>
    public Vector3 Min => new((float)(X * (double)CellSize), (float)(Y * (double)CellSize), (float)(Z * (double)CellSize));

    /// <summary>Верхний угол бокса, м.</summary>
    public Vector3 Max => new((float)((X + Nx) * (double)CellSize), (float)((Y + Ny) * (double)CellSize), (float)((Z + Nz) * (double)CellSize));

    public Vector3 Center => new((float)((X + Nx * 0.5) * CellSize), (float)((Y + Ny * 0.5) * CellSize), (float)((Z + Nz * 0.5) * CellSize));

    /// <summary>Цель минус центр бокса — дробный остаток сдвига (по оси не больше ½ ячейки, пока окно следует за целью).</summary>
    public Vector3 Offset => Target - Center;

    /// <summary>Мировая точка → координата в индексах сетки (центры ячеек — целые).</summary>
    public Vector3 ToIndex(Vector3 world) => new((float)(world.X / (double)CellSize - X - 0.5), (float)(world.Y / (double)CellSize - Y - 0.5),
        (float)(world.Z / (double)CellSize - Z - 0.5));

    /// <summary>Координата в индексах (центры ячеек — целые) → мир.</summary>
    public Vector3 ToWorld(Vector3 index) => new((float)((X + index.X + 0.5) * CellSize), (float)((Y + index.Y + 0.5) * CellSize),
        (float)((Z + index.Z + 0.5) * CellSize));

    /// <summary>Сдвиг в ячейках от <paramref name="previous"/> к этому окну (ячейка x нового окна — ячейка x + сдвиг прошлого).</summary>
    public (int X, int Y, int Z) ShiftFrom(Fluid3DWindow previous) => (Clamp(X - (long)previous.X, Nx), Clamp(Y - (long)previous.Y, Ny),
        Clamp(Z - (long)previous.Z, Nz));

    private static int Clamp(long shift, int n) => (int)Math.Clamp(shift, -n, n);
}
