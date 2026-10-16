using System.Numerics;
using Sword.Bake.Anim;

namespace Sword.Bake.Tests;

/// <summary>Оси UE → пространство персонажа (<see cref="UeSpace"/>) и окно кадров переноса пальцев (<see cref="FingerTransfer"/>).</summary>
public sealed class MathTests
{
    private static void Near(Vector3 expected, Vector3 actual, float eps = 1e-4f) =>
        Assert.True(Vector3.Distance(expected, actual) < eps, $"ожидалось {expected}, получено {actual}");

    [Fact]
    public void UeAxes_MapToCharacterSpace()
    {
        // UE: X вперёд, Y вправо, Z вверх. SWORD: +Z вперёд, −X вправо, +Y вверх.
        Near(Vector3.UnitZ, UeSpace.ToSwordAxes(Vector3.UnitX));
        Near(-Vector3.UnitX, UeSpace.ToSwordAxes(Vector3.UnitY));
        Near(Vector3.UnitY, UeSpace.ToSwordAxes(Vector3.UnitZ));
        var p = new Vector3(1.5f, -2, 3);
        Near(p, UeSpace.ToSwordAxes(UeSpace.ToUeAxes(p)));
    }

    [Theory]
    [InlineData(0f, 1f, 0f)]   // UE Manny: вперёд +Y
    [InlineData(1f, 0f, 0f)]   // синтетика: вперёд +X
    [InlineData(-0.6f, -0.8f, 0f)]
    public void UeSpace_RootForwardBecomesPlusZ(float x, float y, float z)
    {
        var space = new UeSpace([x, y, z], 0.01f);
        Near(Vector3.UnitZ, space.Dir(new Vector3(x, y, z)));
        Near(Vector3.UnitY, space.Dir(Vector3.UnitZ));
        // матрица кости согласована с переводом точек: FK в персонаже = перевод точки UE
        var q = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(0.3f, -0.5f, 1)), 0.9f);
        var t = new Vector3(10, -20, 30);
        var v = new Vector3(3, 4, 5);
        Near(space.Point(Vector3.Transform(v, q) + t), Vector3.Transform(space.Point(v), space.Transform(t, q)));
    }

    [Fact]
    public void UeSpace_VerticalRootForward_IsAnError() =>
        Assert.Throws<InvalidDataException>(() => new UeSpace([0, 0, 1], 0.01f));

    [Fact]
    public void FingerTransfer_SourceFrameSpansWholeClip()
    {
        Assert.Equal(0, FingerTransfer.SourceFrame(0, 31, 30));
        Assert.Equal(29, FingerTransfer.SourceFrame(30, 31, 30));
        Assert.Equal(30, FingerTransfer.SourceFrame(29, 30, 31));
        for (int f = 0; f < 30; f++) Assert.Equal(f, FingerTransfer.SourceFrame(f, 30, 30));
    }
}
