using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Formats;

namespace Sword.Engine.Animation.Tests;

/// <summary>
/// IK ноги (<see cref="PosePool.LegIk"/>): голеностоп в цели, длины звеньев и плоскость колена сохранены, стопа — поворот позы и
/// заданный курс, потомки едут жёстко, локальная поза не тронута, у предела без щелчка; второй проход тика (<see cref="PosePool.Pass"/>).
/// </summary>
public sealed class PoseLegIkTests
{
    // root → pelvis → thigh → calf → foot → ball; у бедра и голени — скрутка, у голеностопа — второй ребёнок.
    private static readonly string[] Names = ["root", "pelvis", "thigh", "calf", "foot", "ball", "thigh_twist", "calf_twist", "heel", "spine"];
    private static readonly int[] Parents = [-1, 0, 1, 2, 3, 4, 2, 3, 4, 1];
    private const int Thigh = 2, Calf = 3, Foot = 4, Ball = 5;

    private static ClipLibrary Library()
    {
        int jc = Names.Length;
        var rot = new Quaternion[jc];
        var pos = new Vector3[jc];
        Array.Fill(rot, Quaternion.Identity);
        pos[1] = new Vector3(0, 0.95f, 0);                    // таз
        pos[2] = new Vector3(0.1f, -0.05f, 0);                // бедро
        rot[2] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.25f);   // бедро вперёд
        pos[3] = new Vector3(0, -0.43f, 0);                   // колено
        rot[3] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.45f);    // голень назад — колено согнуто вперёд
        pos[4] = new Vector3(0, -0.42f, 0);                   // голеностоп
        rot[4] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.2f);
        pos[5] = new Vector3(0, -0.06f, 0.15f);               // носок
        pos[6] = new Vector3(0, -0.2f, 0);
        pos[7] = new Vector3(0, -0.2f, 0);
        pos[8] = new Vector3(0, -0.07f, -0.05f);
        pos[9] = new Vector3(0, 0.2f, 0);
        var clip = new ClipFile
        {
            Skeleton = "leg", SkeletonHash = SkeletonFile.ComputeHash(Names, Parents), SampleRate = 30, Frames = 1, Joints = jc,
            SourcePath = "/Game/Leg", SourceHash = "h", LocalRot = rot, LocalPos = pos, RootMotion = new Vector3[1], RootYaw = new float[1],
        };
        return ClipLibrary.Load(new Skeleton("leg", Names, Parents), [TestClips.Source("a", "Leg", clip)])!;
    }

    private static (PosePool Pool, PoseLeg Leg, PoseHandle Pose, Matrix4x4[] Before) Rig()
    {
        var pool = new PosePool(Library());
        var leg = pool.AddLeg(Thigh, Calf, Foot);
        var pose = pool.Alloc();
        pool.Sample(pose, 0, 0);
        pool.ToModel(pose);
        pool.Tick();
        return (pool, leg, pose, pool.Model(pose).ToArray());
    }

    private static Matrix4x4[] Solve(Vector3 target, float yaw = 0, float soft = 0.05f)
    {
        var (pool, leg, pose, _) = Rig();
        pool.LegIk(pose, leg, target, Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw), soft);
        Assert.Equal(1, pool.Pass().LegIks);
        return pool.Model(pose).ToArray();
    }

    private static Vector3 P(Matrix4x4[] m, int j) => m[j].Translation;

    /// <summary>Достижимая цель: голеностоп в ней, бедро не двигается, длины бедра и голени — прежние.</summary>
    [Fact]
    public void ReachableTarget_AnkleOnTarget_LengthsKept()
    {
        var before = Rig().Before;
        var target = P(before, Foot) + new Vector3(0.05f, 0.08f, -0.12f);
        var after = Solve(target);
        Assert.True(Vector3.Distance(P(after, Foot), target) < 1e-4f, $"голеностоп {P(after, Foot)}, цель {target}");
        Assert.Equal(P(before, Thigh), P(after, Thigh));
        Assert.Equal(Vector3.Distance(P(before, Thigh), P(before, Calf)), Vector3.Distance(P(after, Thigh), P(after, Calf)), 4);
        Assert.Equal(Vector3.Distance(P(before, Calf), P(before, Foot)), Vector3.Distance(P(after, Calf), P(after, Foot)), 4);
    }

    /// <summary>Колено — в плоскости колена позы (бедро, колено позы, цель), с той же стороны.</summary>
    [Fact]
    public void Knee_StaysInPoseKneePlane()
    {
        var before = Rig().Before;
        var target = P(before, Foot) + new Vector3(0, 0.1f, 0.05f);
        var after = Solve(target);
        var hip = P(before, Thigh);
        var normal = Vector3.Normalize(Vector3.Cross(P(before, Calf) - hip, target - hip));
        Assert.True(MathF.Abs(Vector3.Dot(P(after, Calf) - hip, normal)) < 1e-4f);
        var line = Vector3.Normalize(target - hip);
        var side = P(before, Calf) - hip - line * Vector3.Dot(P(before, Calf) - hip, line);
        Assert.True(Vector3.Dot(P(after, Calf) - hip, side) > 0, "колено ушло на другую сторону");
    }

    /// <summary>Стопа: её поворот в позе, затем заданный (курс вокруг вертикали); носок и пятка едут с ней жёстко.</summary>
    [Fact]
    public void Foot_KeepsPoseRotationPlusYaw_ChildrenRigid()
    {
        var before = Rig().Before;
        var target = P(before, Foot) + new Vector3(0.03f, 0.05f, -0.08f);
        const float yaw = 0.4f;
        var after = Solve(target, yaw);
        var expected = Matrix4x4.CreateFromQuaternion(Quaternion.CreateFromRotationMatrix(before[Foot])) * Matrix4x4.CreateRotationY(yaw);
        var actual = Matrix4x4.CreateFromQuaternion(Quaternion.CreateFromRotationMatrix(after[Foot]));
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                Assert.Equal(expected[r, c], actual[r, c], 4);
        foreach (int child in (int[])[Ball, 8])
        {
            var local = P(before, child) - P(before, Foot);
            var rotated = Vector3.Transform(local, Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw));
            Assert.True(Vector3.Distance(P(after, child) - P(after, Foot), rotated) < 1e-4f, Names[child]);
        }
    }

    /// <summary>Цель на месте голеностопа — модель та же (≤ 1e-5); локальная поза IK не трогает; у неноги (корпус) — бит в бит.</summary>
    [Fact]
    public void TargetAtAnkle_PoseUnchanged_LocalUntouched()
    {
        var (pool, leg, pose, before) = Rig();
        var local = pool.LocalRot(pose).ToArray();
        pool.LegIk(pose, leg, P(before, Foot), Quaternion.Identity);
        pool.Pass();
        var after = pool.Model(pose);
        for (int j = 0; j < before.Length; j++)
            Assert.True(Vector3.Distance(before[j].Translation, after[j].Translation) < 1e-5f, Names[j]);
        Assert.Equal(local, pool.LocalRot(pose).ToArray());
        Assert.Equal(before[9], after[9]);
        Assert.Equal(before[1], after[1]);
    }

    /// <summary>Цель уходит за предел ноги: угол колена и голеностоп меняются непрерывно (нет щелчка), всё конечно.</summary>
    [Fact]
    public void BeyondReach_ContinuousKnee_Finite()
    {
        var before = Rig().Before;
        var hip = P(before, Thigh);
        var dir = Vector3.Normalize(P(before, Foot) - hip);
        float lastKnee = float.NaN;
        Vector3 lastAnkle = default;
        for (int i = 0; i <= 200; i++)
        {
            float d = 0.70f + 0.002f * i;   // 0,70 … 1,10 м — ход ноги 0,85
            var after = Solve(hip + dir * d);
            var a = P(after, Thigh);
            var k = P(after, Calf);
            var f = P(after, Foot);
            Assert.True(float.IsFinite(f.X) && float.IsFinite(f.Y) && float.IsFinite(f.Z));
            float knee = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a - k), Vector3.Normalize(f - k)), -1, 1));
            if (i > 0)
            {
                Assert.True(MathF.Abs(knee - lastKnee) < 0.05f, $"d {d}: колено {lastKnee} → {knee}");
                Assert.True(Vector3.Distance(f, lastAnkle) < 0.0035f, $"d {d}: шаг голеностопа {Vector3.Distance(f, lastAnkle)}");
            }
            Assert.True(Vector3.Distance(a, f) <= 0.85f + 1e-4f);
            lastKnee = knee;
            lastAnkle = f;
        }
    }

    /// <summary>
    /// Второй проход того же тика: прошлая модель (показ между тиками) — модель тика до IK не становится; следующий тик видит
    /// итог с IK как прошлое; пустой проход — ничего. Задания IK — в итоге тика (<see cref="PosePool.LastTick"/>).
    /// </summary>
    [Fact]
    public void Pass_SameTick_PreviousModelIsLastFinal()
    {
        var (pool, leg, pose, before) = Rig();
        pool.ToModel(pose);
        pool.Tick();
        Assert.True(pool.HasPreviousModel(pose));
        var target = P(before, Foot) + new Vector3(0, 0.1f, 0);
        pool.LegIk(pose, leg, target, Quaternion.Identity);
        Assert.Equal(default, pool.Pass() with { LegIks = 0 });
        Assert.Equal(1, pool.LastTick.ToModel);
        Assert.Equal(1, pool.LastTick.LegIks);
        Assert.True(pool.HasPreviousModel(pose));
        var final = pool.Model(pose).ToArray();
        Assert.Equal(default, pool.Pass());
        pool.ToModel(pose);
        pool.Tick();
        Assert.True(pool.HasPreviousModel(pose));
        Assert.Equal(final, pool.PreviousModel(pose).ToArray());
        Assert.Equal(before, pool.Model(pose).ToArray());   // правка IK снята прямой кинематикой — не копится
    }

    /// <summary>Нога — не цепочка: ошибка; нет ноги, нечисловая цель — ошибка постановки.</summary>
    [Fact]
    public void BadLegAndTarget_Throw()
    {
        var pool = new PosePool(Library());
        Assert.Throws<ArgumentException>(() => pool.AddLeg(Thigh, Foot, Ball));
        var pose = pool.Alloc();
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.LegIk(pose, new PoseLeg(0), Vector3.Zero, Quaternion.Identity));
        var leg = pool.AddLeg(Thigh, Calf, Foot);
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.LegIk(pose, leg, new Vector3(float.NaN, 0, 0), Quaternion.Identity));
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.LegIk(pose, leg, Vector3.Zero, new Quaternion(float.PositiveInfinity, 0, 0, 1)));
    }

    /// <summary>Установившийся тик с IK ноги — 0 Б.</summary>
    [Fact]
    public void SteadyTickWithLegIk_DoesNotAllocate()
    {
        var (pool, leg, pose, before) = Rig();
        for (int i = 0; i < 4; i++)
        {
            pool.Sample(pose, 0, 0);
            pool.ToModel(pose);
            pool.Tick();
            pool.LegIk(pose, leg, P(before, Foot) + new Vector3(0, 0.05f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.1f));
            pool.Pass();
        }
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            pool.Sample(pose, 0, 0);
            pool.ToModel(pose);
            pool.Tick();
            pool.LegIk(pose, leg, P(before, Foot) + new Vector3(0, 0.05f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.1f));
            pool.Pass();
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - bytes);
    }
    /// <summary>Угол колена от стопы вокруг линии бедро — голеностоп, рад (как у <see cref="PosePool.KneeAlign"/>).</summary>
    private static float KneeToFoot(ReadOnlySpan<Matrix4x4> m)
    {
        var hip = m[Thigh].Translation;
        var axis = Vector3.Normalize(m[Foot].Translation - hip);
        var bend = m[Calf].Translation - hip;
        bend -= axis * Vector3.Dot(bend, axis);
        var toe = m[Ball].Translation - m[Foot].Translation;
        toe -= axis * Vector3.Dot(toe, axis);
        return MathF.Atan2(Vector3.Dot(Vector3.Cross(bend, toe), axis), Vector3.Dot(bend, toe));
    }

    /// <summary>
    /// Колено над стопой: стопа повёрнута IK на 60° от колена — после <see cref="PosePool.KneeAlign"/> (предел 30°, без мягкого входа)
    /// колено расходится со стопой ровно на предел; бедро, голеностоп и носок на месте, длины звеньев те же, стопа не повернулась; в
    /// пределе — поза бит в бит; мягкий вход — меньше жёсткого и гладко.
    /// </summary>
    [Fact]
    public void KneeAlign_TurnsKneeTowardFoot_KeepsHipAnkleFoot()
    {
        float limit = 30 * MathF.PI / 180;
        var (pool, leg, pose, _) = Rig();
        var ankle = pool.Model(pose)[Foot].Translation;
        pool.LegIk(pose, leg, ankle, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 60 * MathF.PI / 180));
        pool.Pass();
        var twisted = pool.Model(pose).ToArray();
        Assert.True(MathF.Abs(KneeToFoot(twisted)) > 50 * MathF.PI / 180, $"стопа не ушла от колена: {KneeToFoot(twisted) * 180 / MathF.PI}°");
        pool.KneeAlign(pose, leg, Ball, limit, 0, 1);
        Assert.Equal(1, pool.Pass().LegIks);
        var aligned = pool.Model(pose).ToArray();
        Assert.Equal(limit, MathF.Abs(KneeToFoot(aligned)), 3);
        foreach (int j in (int[])[Thigh, Foot, Ball])
            Assert.True(Vector3.Distance(twisted[j].Translation, aligned[j].Translation) < 1e-5f, $"сустав {Names[j]} сдвинулся");
        Assert.Equal(Vector3.Distance(twisted[Thigh].Translation, twisted[Calf].Translation), Vector3.Distance(aligned[Thigh].Translation, aligned[Calf].Translation), 5);
        Assert.Equal(Vector3.Distance(twisted[Calf].Translation, twisted[Foot].Translation), Vector3.Distance(aligned[Calf].Translation, aligned[Foot].Translation), 5);
        Assert.True(Vector3.Distance(twisted[Calf].Translation, aligned[Calf].Translation) > 0.005f, "колено не повернулось");
        var footBefore = Quaternion.CreateFromRotationMatrix(twisted[Foot]);
        var footAfter = Quaternion.CreateFromRotationMatrix(aligned[Foot]);
        Assert.True(MathF.Abs(Quaternion.Dot(footBefore, footAfter)) > 1 - 1e-5f, "стопа повернулась");

        // в пределе — без правки
        var (pool2, leg2, pose2, before) = Rig();
        pool2.KneeAlign(pose2, leg2, Ball, limit, 0, 1);
        pool2.Pass();
        Assert.Equal(before, pool2.Model(pose2).ToArray());

        // мягкий вход: избыток меньше жёсткого, без излома в начале
        Assert.Equal(0, PosePool.KneeExcess(0.4f, 0.5f, 0.2f));
        Assert.True(PosePool.KneeExcess(0.6f, 0.5f, 0.2f) is > 0 and < 0.1f);
        Assert.Equal(-(0.9f - 0.5f - 0.1f), PosePool.KneeExcess(-0.9f, 0.5f, 0.2f), 5);
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.KneeAlign(pose, leg, Ball, -1, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.KneeAlign(pose, leg, 99, limit, 0, 1));
    }
}
