using System.Numerics;
using Sword.Engine.Animation;

namespace Sword.Engine.Animation.Tests;

/// <summary>
/// Математика позы — <c>Sword.Anim.Tests/MathTests</c> SWORD 2eab13b с теми же ожиданиями: соглашение курса, IK,
/// пружины. Тесты осей UE и переноса пальцев (<c>UeSpace</c>, <c>FingerTransfer</c>) — пекарня, переносятся с ней.
/// </summary>
public sealed class MathTests
{
    private static void Near(Vector3 expected, Vector3 actual, float eps = 1e-4f) =>
        Assert.True(Vector3.Distance(expected, actual) < eps, $"ожидалось {expected}, получено {actual}");

    [Fact]
    public void FacingConvention_MatchesMotionMatrix()
    {
        // Боец: курс F — поворот вокруг Y, вперёд = (sin F, 0, cos F), вправо = −X, повёрнутый на F
        // (DuelArena.KinWorld, поворот RootDelta). Клипы запекаются по MotionMatrix — она обязана жить в той же системе.
        float facing = 0.7f;
        var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, facing);
        var m = ClipLibrary.MotionMatrix(new Vector3(1, 0, 2), facing);
        Near(new Vector3(MathF.Sin(facing), 0, MathF.Cos(facing)), Vector3.TransformNormal(Vector3.UnitZ, m));
        Near(Vector3.Transform(new Vector3(-1, 0, 0), yaw), Vector3.TransformNormal(new Vector3(-1, 0, 0), m));
        Near(new Vector3(1, 0, 2), Vector3.Transform(Vector3.Zero, m));
        // «+ — от +Z к +X»: положительный поворот уводит взгляд влево бойца
        Assert.True(Vector3.TransformNormal(Vector3.UnitZ, ClipLibrary.MotionMatrix(default, 0.1f)).X > 0);
    }

    [Theory]
    [InlineData(0f)]      // шаг вперёд
    [InlineData(0.35f)]   // стопа вбок
    [InlineData(-0.35f)]
    public void Ik2Bone_ReachesTargetKeepsLengthsAndBendsToPole(float side)
    {
        // нога фиксации стопы: бедро 0.45, голень 0.45, полюс — впереди колена
        var hip = new Vector3(0.09f, 0.88f, 0);
        var target = new Vector3(hip.X + side, 0.2f, 0.25f);   // в пределах ноги (дальше 0.898 м — прямая нога, ниже)
        var pole = hip + new Vector3(0, -0.3f, 0.6f);
        var (knee, end) = AnimMath.Ik2Bone(hip, target, pole, 0.45f, 0.45f);
        Near(target, end);
        Assert.Equal(0.45f, Vector3.Distance(hip, knee), 3);
        Assert.Equal(0.45f, Vector3.Distance(knee, end), 3);
        // колено уходит к полюсу, а не назад
        var mid = (hip + end) * 0.5f;
        Assert.True(Vector3.Dot(knee - mid, pole - mid) > 0);
        // недостижимая цель — нога почти прямая в её сторону
        var (_, far) = AnimMath.Ik2Bone(hip, hip + new Vector3(0, -2, 0), pole, 0.45f, 0.45f);
        Assert.Equal(0.898f, Vector3.Distance(hip, far), 3);
    }

    [Fact]
    public void DecaySpring_ReachesZero()
    {
        var x = new Vector3(1, -2, 0.5f);
        var v = Vector3.Zero;
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1);
        var w = Vector3.Zero;
        for (int i = 0; i < 120; i++)
        {
            AnimMath.DecaySpring(ref x, ref v, 0.1f, 1f / 120);
            AnimMath.DecaySpring(ref q, ref w, 0.1f, 1f / 120);
        }
        Assert.True(x.Length() < 1e-3f);
        Assert.True(AnimMath.ToRotationVector(q).Length() < 1e-3f);
    }
}
