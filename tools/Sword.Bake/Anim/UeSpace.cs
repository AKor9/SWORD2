using System.Numerics;

namespace Sword.Bake.Anim;

/// <summary>
/// Перевод экспорта UE в пространство персонажа SWORD — единственное место, где записаны оси.
/// UE: X, Y, Z-вверх, левая система, единицы манифеста (см или м). SWORD: +Z вперёд, +X влево, +Y вверх,
/// правая система, метры. Смена рукости неизбежна. «Вперёд» персонажа в покое — rootForward манифеста
/// (у Manny +Y UE): <see cref="Dir"/> и <see cref="Point"/> поворачивают вокруг вертикали так, чтобы он стал +Z.
/// </summary>
public sealed class UeSpace
{
    /// <summary>Оси UE → оси SWORD, без масштаба и разворота.</summary>
    public static Vector3 ToSwordAxes(Vector3 ue) => new(-ue.Y, ue.Z, ue.X);
    /// <summary>Оси SWORD → оси UE, обратное к <see cref="ToSwordAxes"/>.</summary>
    public static Vector3 ToUeAxes(Vector3 c) => new(c.Z, -c.X, c.Y);

    /// <summary>Метров в единице экспорта.</summary>
    public static float UnitOf(RawManifest man) => man.Units == "m" ? 1 : 0.01f;

    /// <summary>Метров в единице экспорта.</summary>
    public readonly float Unit;
    /// <summary>Разворот вокруг вертикали: rootForward → +Z.</summary>
    public readonly Quaternion Facing;
    private readonly Quaternion _facingInv;

    public UeSpace(RawManifest man) : this(man.RootForward, UnitOf(man)) { }

    public UeSpace(ReadOnlySpan<float> rootForward, float unit)
    {
        if (rootForward.Length != 3) throw new InvalidDataException("rootForward — 3 числа");
        var f = ToSwordAxes(new Vector3(rootForward[0], rootForward[1], rootForward[2]));
        if (new Vector2(f.X, f.Z).LengthSquared() < 1e-6f)
            throw new InvalidDataException($"rootForward ({rootForward[0]}, {rootForward[1]}, {rootForward[2]}) вертикален — курс не определить");
        Facing = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.Atan2(-f.X, f.Z));
        _facingInv = Quaternion.Conjugate(Facing);
        Unit = unit;
    }

    public Vector3 Dir(Vector3 ue) => Vector3.Transform(ToSwordAxes(ue), Facing);
    public Vector3 Point(Vector3 ue) => Dir(ue) * Unit;
    private Vector3 DirToUe(Vector3 c) => ToUeAxes(Vector3.Transform(c, _facingInv));

    /// <summary>Преобразование UE (t в единицах экспорта, поворот q) → матрица персонажа (вектор слева: v·M).</summary>
    public Matrix4x4 Transform(Vector3 t, Quaternion q)
    {
        Vector3 Image(Vector3 e) => Dir(Vector3.Transform(DirToUe(e), q));
        var x = Image(Vector3.UnitX);
        var y = Image(Vector3.UnitY);
        var z = Image(Vector3.UnitZ);
        var p = Point(t);
        return new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, p.X, p.Y, p.Z, 1);
    }
}
