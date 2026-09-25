using System.Numerics;
using Sword.Engine.Animation.Motion;

namespace Sword.Engine.Animation.Tests;

/// <summary>
/// Плоская система <see cref="Pose2"/>: соглашение осей и порядок умножения — те же, что у матриц
/// <see cref="ClipLibrary.MotionMatrix"/> (вектор слева), <c>Compose∘Relative</c> — тождество, доля от тождества, заворот угла.
/// </summary>
public sealed class Pose2Tests
{
    private static readonly Pose2[] Poses =
    [
        new(0, 0, 0), new(1.5f, -2, 0.3f), new(-40, 25, 2.9f), new(3, 7, -3.1f), new(-0.2f, 0.1f, MathF.PI), new(12, -8, -MathF.PI / 2),
        new(100, 100, 6.5f), new(-5, 0.5f, -9.4f),
    ];

    private static void Near(Pose2 expected, Pose2 actual, float eps = 1e-4f)
    {
        Assert.True(MathF.Abs(expected.X - actual.X) < eps && MathF.Abs(expected.Z - actual.Z) < eps
            && MathF.Abs(Pose2.Wrap(expected.Yaw - actual.Yaw)) < eps, $"ожидалось {expected}, получено {actual}");
    }

    private static void Near(Vector3 expected, Vector3 actual, float eps = 1e-4f) =>
        Assert.True(Vector3.Distance(expected, actual) < eps, $"ожидалось {expected}, получено {actual}");

    [Fact]
    public void Relative_MatchesMotionMatrices()
    {
        // вектор слева: система b в системе a — M_b · M_a⁻¹; a ∘ (b в a) — M_rel · M_a = M_b
        foreach (var a in Poses)
            foreach (var b in Poses)
            {
                var ma = ClipLibrary.MotionMatrix(a.Position, a.Yaw);
                var mb = ClipLibrary.MotionMatrix(b.Position, b.Yaw);
                Assert.True(Matrix4x4.Invert(ma, out var maInv));
                var rel = Pose2.Relative(a, b);
                Near(Pose2.From(mb * maInv), rel, 2e-4f);
                Near(Pose2.From(ClipLibrary.MotionMatrix(new Vector3(rel.X, 0, rel.Z), rel.Yaw) * ma), b, 2e-4f);
                // точка системы b в родителе — та же, что через систему a
                var p = new Vector3(0.4f, 0, -1.3f);
                Near(Vector3.Transform(p, mb), a.ToParent(rel.ToParent(p)), 2e-4f);
            }
    }

    [Fact]
    public void ComposeOfRelative_IsIdentity()
    {
        foreach (var a in Poses)
            foreach (var b in Poses)
            {
                Near(b, Pose2.Compose(a, Pose2.Relative(a, b)));
                var local = Pose2.Relative(a, b);   // курс — в (−π, π]
                Near(local, Pose2.Relative(a, Pose2.Compose(a, local)));
                Near(Pose2.Identity, Pose2.Compose(a, a.Inverse()));
            }
    }

    [Fact]
    public void Relative_OnTurningRootFarFromOrigin_HasNoFalseShift()
    {
        // Корень идёт по дуге 2 м/с с поворотом 90°/с, кадры 30 Гц; та же дуга, унесённая далеко и повёрнутая. Перенос корня за
        // интервал — хорда ≈ 2/30 м вперёд; в обратном порядке умножения (M_a⁻¹ · M_b) он зависел бы от положения системы a.
        const float speed = 2, omega = MathF.PI / 2, rate = 30;
        var far = new Pose2(-40, 25, 2.5f);
        for (int f = 0; f < 60; f++)
        {
            var a = Arc(f / rate);
            var b = Arc((f + 1) / rate);
            var step = Pose2.Relative(a, b);
            Assert.InRange(MathF.Sqrt(step.X * step.X + step.Z * step.Z) * rate, speed - 1e-3f, speed + 1e-3f);
            Assert.True(step.Z > 0 && MathF.Abs(step.X) < step.Z * 0.05f, $"кадр {f}: {step}");   // вперёд, чуть влево по дуге
            Assert.InRange(step.Yaw * rate, omega - 1e-3f, omega + 1e-3f);
            Near(step, Pose2.Relative(Pose2.Compose(far, a), Pose2.Compose(far, b)), 1e-4f);
        }

        static Pose2 Arc(float t)
        {
            // интеграл скорости «вперёд» (sin курса, cos курса)·v при курсе ω·t, через матрицу клипа
            float r = speed / omega, yaw = omega * t;
            return Pose2.From(ClipLibrary.MotionMatrix(new Vector3(r * (1 - MathF.Cos(yaw)), 0, r * MathF.Sin(yaw)), yaw));
        }
    }

    [Fact]
    public void Lerp_ZeroIsIdentity_OneIsItself()
    {
        foreach (var a in Poses)
        {
            Assert.Equal(Pose2.Identity, Pose2.Lerp(a, 0));
            Assert.Equal(a, Pose2.Lerp(a, 1));
            var half = Pose2.Lerp(a, 0.5f);
            Assert.Equal(new Pose2(a.X / 2, a.Z / 2, a.Yaw / 2), half);
        }
    }

    [Fact]
    public void Wrap_IsInHalfOpenInterval()
    {
        Assert.Equal(MathF.PI, Pose2.Wrap(MathF.PI));
        Assert.Equal(MathF.PI, Pose2.Wrap(-MathF.PI));
        Assert.Equal(0f, Pose2.Wrap(0));
        Assert.Equal(0f, Pose2.Wrap(MathF.Tau));
        for (float a = -50; a <= 50; a += 0.037f)
        {
            float w = Pose2.Wrap(a);
            Assert.True(w > -MathF.PI && w <= MathF.PI, $"Wrap({a}) = {w}");
            Assert.True(MathF.Abs(MathF.Sin(w) - MathF.Sin(a)) < 1e-4f && MathF.Abs(MathF.Cos(w) - MathF.Cos(a)) < 1e-4f, $"Wrap({a}) = {w}");
        }
    }

    [Fact]
    public void From_InvertsMatrixConstruction()
    {
        foreach (var p in Poses)
        {
            var fromClip = Pose2.From(ClipLibrary.MotionMatrix(new Vector3(p.X, 0.7f, p.Z), p.Yaw));   // высота отбрасывается
            Near(p, fromClip, 1e-5f);
            Assert.True(fromClip.Yaw > -MathF.PI && fromClip.Yaw <= MathF.PI);
            Near(p, Pose2.From(p.ToMatrix()), 1e-5f);
            Assert.Equal(ClipLibrary.MotionMatrix(p.Position, p.Yaw), p.ToMatrix());
        }
    }

    [Fact]
    public void RotateAndPoints_MatchMatrixConvention()
    {
        var v = new Vector3(0.3f, 1.2f, -2);
        foreach (var p in Poses)
        {
            Near(Vector3.TransformNormal(v, Matrix4x4.CreateRotationY(p.Yaw)), Pose2.Rotate(v, p.Yaw), 1e-5f);
            Near(Vector3.Transform(v, p.ToMatrix()), p.ToParent(v), 1e-4f);
            Near(v, p.ToLocal(p.ToParent(v)), 1e-4f);
            Near(p.Forward, Pose2.Rotate(Vector3.UnitZ, p.Yaw), 1e-6f);
        }
        // «+ — от +Z к +X»: положительный курс уводит «вперёд» к +X (влево персонажа)
        Assert.True(Pose2.Rotate(Vector3.UnitZ, 0.1f).X > 0);
        Near(new Vector3(1, 0, 0), new Pose2(0, 0, MathF.PI / 2).Forward, 1e-6f);
    }
}
