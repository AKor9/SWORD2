using System.Numerics;
using System.Runtime.InteropServices;
using Sword.Engine.Animation.Motion;
using static Sword.Engine.Animation.Tests.MotionBuildFixtures;

namespace Sword.Engine.Animation.Tests;

/// <summary>
/// Кинематика кадров базы: скорость корня, непрерывность скоростей на шве цикла, перенос корня по дуге без ложного сдвига,
/// FK в системе корня, стопа в опоре стоит; параллельно = последовательно побитово.
/// </summary>
public sealed class ClipKinematicsTests
{
    private static ClipKinematics Compute(ClipLibrary library, bool parallel = true) =>
        ClipKinematics.Compute(library, All(library), Schema(), Contacts, parallel);

    private static void Near(Vector3 expected, Vector3 actual, float eps, string what) =>
        Assert.True(Vector3.Distance(expected, actual) < eps, $"{what}: ожидалось {expected}, получено {actual}");

    private static int Slot(int joint) => Schema().JointSlot(joint);

    [Fact]
    public void Layout_IsCompact_LocalFramesInLibraryOrder()
    {
        var lib = Library(IdleLoop(), WalkLoop(), WalkStop());
        var kin = ClipKinematics.Compute(lib, [0, 2], Schema(), Contacts);
        Assert.Equal([new MotionClip(0, 0, 61, true), new MotionClip(2, 61, 46, false)], kin.Clips.ToArray());
        Assert.Equal(61 + 46, kin.FrameCount);
        Assert.Equal(Schema().Joints.Length, kin.JointCount);
        Assert.Equal(kin.FrameCount * kin.JointCount, kin.Positions.Length);
        // первый кадр клипа — начало его системы
        Assert.Equal(Pose2.Identity, kin.Root[61]);
    }

    [Fact]
    public void StraightLoop_RootVelocityIsGivenSpeed()
    {
        var lib = Library(WalkLoop(), RunLoop());
        var kin = Compute(lib);
        for (int f = 0; f < kin.FrameCount; f++)
        {
            float speed = f < 31 ? 1.5f : 3.5f;
            Near(new Vector3(0, 0, speed), kin.RootVelocity[f], 1e-4f, $"кадр {f}");
            Assert.Equal(0, kin.YawRate[f], 1e-5f);
        }
    }

    [Fact]
    public void Loop_VelocitiesAreContinuousAcrossSeam()
    {
        var lib = Library(WalkLoop());
        var kin = Compute(lib);
        int last = 30, jc = kin.JointCount;
        // первый и последний кадр цикла — один кадр: скорости те же
        Assert.Equal(kin.RootVelocity[0], kin.RootVelocity[last]);
        Assert.Equal(kin.Velocities.Slice(0, jc).ToArray(), kin.Velocities.Slice(last * jc, jc).ToArray());
        // таз качается cos(4π·t): на шве (пик) вертикальная скорость ≈ 0, а не односторонняя разность (−0,1 м/с)
        var pelvis0 = kin.Velocities[Slot(Pelvis)];
        Assert.True(MathF.Abs(pelvis0.Y) < 1e-4f, $"скорость таза на шве {pelvis0}");
        // соседние кадры — симметрично (± одна и та же скорость, не ноль)
        Assert.Equal(-kin.Velocities[jc + Slot(Pelvis)].Y, kin.Velocities[(last - 1) * jc + Slot(Pelvis)].Y, 1e-4f);
        Assert.True(MathF.Abs(kin.Velocities[jc + Slot(Pelvis)].Y) > 0.05f);
    }

    [Fact]
    public void TurningClip_IntervalIsConstantArc_NoFalseShiftFarFromStart()
    {
        const double speed = 1.5, omega = 2 * Math.PI / 8, dt = 1 / 30.0;
        var lib = Library(CircleLoop());
        var kin = Compute(lib);
        double radius = speed / omega;
        var step = new Pose2((float)(radius * (1 - Math.Cos(omega * dt))), (float)(radius * Math.Sin(omega * dt)), (float)(omega * dt));
        float chord = (float)(2 * radius * Math.Sin(omega * dt) / (2 * dt));
        float farthest = 0;
        for (int f = 0; f + 1 < kin.FrameCount; f++)
        {
            var interval = Pose2.Relative(kin.Root[f], kin.Root[f + 1]);
            Assert.True(MathF.Abs(interval.X - step.X) < 1e-5f && MathF.Abs(interval.Z - step.Z) < 1e-5f
                && MathF.Abs(interval.Yaw - step.Yaw) < 1e-5f, $"кадр {f}: {interval}, а дуга {step}");
            // скорость корня в его системе — вперёд по хорде, ω — заданная
            Near(new Vector3(0, 0, chord), kin.RootVelocity[f], 1e-4f, $"скорость корня, кадр {f}");
            Assert.Equal((float)omega, kin.YawRate[f], 1e-4f);
            farthest = MathF.Max(farthest, kin.Root[f].Position.Length());
        }
        Assert.True(farthest > 3.5f, $"клип не уходит от начала: {farthest} м");
    }

    [Fact]
    public void Positions_AreForwardKinematicsInRootFrame()
    {
        var lib = Library(WalkStart(), WalkTurnLeft());
        var kin = Compute(lib);
        var schema = Schema();
        int jc = lib.JointCount;
        var model = new Matrix4x4[jc];
        foreach (int f in new[] { 0, 7, 30, 31, 45, 61 })
        {
            lib.Skeleton.ToModel(lib.LocalRot.Slice(f * jc, jc), lib.LocalPos.Slice(f * jc, jc), model);
            for (int s = 0; s < kin.JointCount; s++)
                Assert.Equal(model[schema.Joints[s]].Translation, kin.Positions[f * kin.JointCount + s]);
        }
    }

    [Fact]
    public void PlantedFoot_StandsStillInWorld_AndIsInContact()
    {
        var lib = Library(WalkLoop(), IdleLoop());
        var kin = Compute(lib);
        int jc = kin.JointCount, left = Slot(FootL);
        // шаг: левая стопа в опоре на фазе (0; 0,5) — кадры 1…14, в переносе — 16…29 (середина — поднята)
        for (int f = 2; f <= 13; f++)
        {
            Assert.True(kin.Velocities[f * jc + left].Length() < 1e-3f, $"кадр {f}: стопа в опоре едет {kin.Velocities[f * jc + left]}");
            Assert.True((kin.Contacts[f] & 1) != 0, $"кадр {f}: опора без контакта");
        }
        for (int f = 18; f <= 27; f++) Assert.True((kin.Contacts[f] & 1) == 0, $"кадр {f}: перенос с контактом");
        // правая — в противофазе
        Assert.True((kin.Contacts[22] & 2) != 0);
        Assert.True((kin.Contacts[7] & 2) == 0);
        // стойка — обе стопы на земле все кадры
        for (int f = 31; f < kin.FrameCount; f++) Assert.Equal(3, kin.Contacts[f]);
    }

    [Fact]
    public void RootAt_WalksLoopTurns_ClampsNonLoop()
    {
        var lib = Library(WalkLoop(), WalkStop());
        var kin = Compute(lib);
        // шаг 1,5 м/с: кадр −1 — предпоследний кадр прошлого оборота (1/30 с назад), кадр n − 1 + 3 — третий кадр следующего
        Near(new Vector3(0, 0, -1.5f / 30), kin.RootAt(0, -1).Position, 1e-5f, "кадр −1");
        Near(new Vector3(0, 0, 1.5f * 33 / 30), kin.RootAt(0, 33).Position, 1e-5f, "кадр 33");
        Near(new Vector3(0, 0, -1.5f * 70 / 30), kin.RootAt(0, -70).Position, 1e-4f, "кадр −70");
        Assert.Equal(kin.Root[31], kin.RootAt(1, -5));
        Assert.Equal(kin.Root[31 + 45], kin.RootAt(1, 50));
        Assert.Equal(kin.Root[31 + 7], kin.RootAt(1, 7));

        // на круге оборот цикла — поворот на 2π: кадр следующего оборота совпадает с кадром этого
        var circle = Compute(Library(CircleLoop()));
        var next = circle.RootAt(0, 240 + 60);
        var same = circle.Root[60];
        Assert.True(Vector3.Distance(same.Position, next.Position) < 1e-3f && MathF.Abs(Pose2.Wrap(same.Yaw - next.Yaw)) < 1e-4f,
            $"{next} ≠ {same}");
    }

    [Fact]
    public void Parallel_IsBitwiseSequential()
    {
        var lib = Library([.. Standard(), CircleLoop(), WalkLoop("Walk_Loop_B", 0.01)]);
        var parallel = Compute(lib, parallel: true);
        var sequential = Compute(lib, parallel: false);
        static void Same<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b) where T : unmanaged =>
            Assert.True(MemoryMarshal.AsBytes(a).SequenceEqual(MemoryMarshal.AsBytes(b)), typeof(T).Name);
        Same(parallel.Root, sequential.Root);
        Same(parallel.RootVelocity, sequential.RootVelocity);
        Same(parallel.YawRate, sequential.YawRate);
        Same(parallel.Positions, sequential.Positions);
        Same(parallel.Velocities, sequential.Velocities);
        Same(parallel.Contacts, sequential.Contacts);
        Same(parallel.OffsetDrift, sequential.OffsetDrift);
    }

    [Fact]
    public void OffsetDrift_IsLargestLocalOffsetChange()
    {
        var lib = Library(IdleLoop(), WalkLoop());
        var kin = Compute(lib);
        Assert.Equal(0, kin.OffsetDrift[Root]);
        Assert.Equal(0, kin.OffsetDrift[ThighL]);
        Assert.Equal(0, kin.OffsetDrift[Spine]);
        // таз качается на ±0,02 м — уход до 0,04 м
        Assert.Equal(0.04f, kin.OffsetDrift[Pelvis], 1e-3f);
        Assert.True(kin.OffsetDrift[FootL] > 0.5f);
    }

    [Theory]
    [InlineData("не по возрастанию")]
    [InlineData("вне библиотеки")]
    [InlineData("нет клипов")]
    [InlineData("сустав схемы вне скелета")]
    [InlineData("пороги контактов")]
    public void RejectsBadInput(string what)
    {
        var lib = Library(IdleLoop(), WalkLoop());
        int[] clips = [0, 1];
        var schema = Schema();
        var contacts = Contacts;
        switch (what)
        {
            case "не по возрастанию": clips = [1, 0]; break;
            case "вне библиотеки": clips = [0, 2]; break;
            case "нет клипов": clips = []; break;
            case "сустав схемы вне скелета": schema = new MotionSchema([], [], [new("j", FeatureKind.JointPosition, 1, 0.1f, [7])]); break;
            case "пороги контактов": contacts = contacts with { MinRun = 0 }; break;
            default: throw new ArgumentOutOfRangeException(nameof(what), what, "нет такого случая");
        }
        Assert.ThrowsAny<ArgumentException>(() => ClipKinematics.Compute(lib, clips, schema, contacts));
    }
}
