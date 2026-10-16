using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Formats;

namespace Sword.Engine.Animation.Tests;

/// <summary>
/// Скручивание (<see cref="PosePool.Twist"/>): поворот всего скелета вокруг вертикали через опору, затем корпус — обратно в своих
/// точках; угол 0 — поза побитово прежняя; 0 Б.
/// </summary>
public sealed class PoseTwistTests
{
    // Родитель бывает после ребёнка (экспорт UE); два корневых сустава (root и ik_root); корпус — spine_01…spine_03 (грудь).
    private static readonly string[] Names =
        ["thigh_l", "root", "pelvis", "spine_01", "spine_02", "spine_03", "head", "hand", "arm", "calf_l", "foot_l", "ik_root", "ik_foot"];
    private static readonly int[] Parents = [2, -1, 1, 2, 3, 4, 5, 8, 5, 0, 9, -1, 11];
    private const int Root = 1, Spine1 = 3, Spine2 = 4, Chest = 5;

    // Корпус в произвольном порядке — пул поворачивает предков раньше.
    private static readonly int[] Spine = [Spine2, Spine1, Chest];
    private static readonly float[] Weights = [0.3f, 0.2f, 0.5f];

    private static ClipLibrary Library()
    {
        int jc = Names.Length, frames = 4;
        var rng = new Random(7);
        float R() => (float)(rng.NextDouble() * 2 - 1);
        var rot = new Quaternion[frames * jc];
        var pos = new Vector3[frames * jc];
        for (int i = 0; i < rot.Length; i++)
        {
            rot[i] = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(R(), R(), R())), 1.2f * R());
            pos[i] = new Vector3(0.1f * R(), 0.2f + 0.1f * R(), 0.1f * R());   // горизонтальные плечи корпуса — сантиметры
        }
        var clip = new ClipFile
        {
            Skeleton = "twist", SkeletonHash = SkeletonFile.ComputeHash(Names, Parents), SampleRate = 30, Frames = frames, Joints = jc,
            SourcePath = "/Game/Twist", SourceHash = "h", LocalRot = rot, LocalPos = pos, RootMotion = new Vector3[frames], RootYaw = new float[frames],
        };
        return ClipLibrary.Load(new Skeleton("twist", Names, Parents), [TestClips.Source("a", "Twist", clip)])!;
    }

    private static (PosePool Pool, PoseTwist Twist, PoseHandle Pose) Rig(float time)
    {
        var pool = new PosePool(Library());
        var twist = pool.AddTwist(Root, Spine, Weights);
        var pose = pool.Alloc();
        pool.Sample(pose, 0, time);
        pool.ToModel(pose);
        pool.Tick();
        return (pool, twist, pose);
    }

    private static Matrix4x4 About(Vector3 origin, float yaw) =>
        Matrix4x4.CreateTranslation(-origin) * Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(origin);

    private static bool InSubtree(int joint, int root)
    {
        for (int a = joint; a >= 0; a = Parents[a])
            if (a == root) return true;
        return false;
    }

    /// <summary>Определение: модельные матрицы после скручивания — поворот всего тела, затем корпус предками вперёд, каждый в своей точке.</summary>
    private static Matrix4x4[] Expected(ReadOnlySpan<Matrix4x4> model, float yaw, float chest)
    {
        var m = model.ToArray();
        var w = About(m[Root].Translation, yaw);
        for (int j = 0; j < m.Length; j++) m[j] *= w;
        foreach (var (joint, weight) in new[] { (Spine1, 0.2f), (Spine2, 0.3f), (Chest, 0.5f) })
        {
            var turn = About(m[joint].Translation, -weight * (yaw - chest));
            for (int j = 0; j < m.Length; j++)
                if (InSubtree(j, joint)) m[j] *= turn;
        }
        return m;
    }

    private static void Near(in Matrix4x4 a, in Matrix4x4 b, float eps, string what)
    {
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
                Assert.True(MathF.Abs(a[r, c] - b[r, c]) <= eps, $"{what}: [{r},{c}] {a[r, c]} против {b[r, c]}");
    }

    [Fact]
    public void ZeroYaw_PoseBitForBit()
    {
        var (pool, twist, pose) = Rig(0.05f);
        var rot = pool.LocalRot(pose).ToArray();
        var pos = pool.LocalPos(pose).ToArray();
        pool.Twist(pose, twist, 0);
        Assert.Equal(new PoseTickStats(0, 0, 0, 0, 0, 0, 1), pool.Tick());
        Assert.Equal(rot, pool.LocalRot(pose).ToArray());
        Assert.Equal(pos, pool.LocalPos(pose).ToArray());
    }

    [Theory]
    [InlineData(0.7f, 0)]
    [InlineData(-1.6f, 0)]
    [InlineData(2.5f, 0)]
    [InlineData(0.9f, -0.3f)]
    [InlineData(0, 0.25f)]
    public void Twist_MatchesDefinition_LegsTurnChestKeepsOrientation(float yaw, float chest)
    {
        var (pool, twist, pose) = Rig(0.07f);
        var before = pool.Model(pose).ToArray();
        pool.Twist(pose, twist, yaw, chest);
        pool.ToModel(pose);
        pool.Tick();
        var after = pool.Model(pose).ToArray();
        var expected = Expected(before, yaw, chest);
        var legs = About(before[Root].Translation, yaw);
        for (int j = 0; j < Names.Length; j++)
        {
            Near(after[j], expected[j], 2e-5f, $"{Names[j]} по определению");
            if (!InSubtree(j, Spine1)) Near(after[j], before[j] * legs, 2e-5f, $"{Names[j]} — на угол вокруг опоры");
            if (!InSubtree(j, Chest)) continue;
            // грудь, голова, руки: поворот в модели — прежний и ещё на chest; положение — сдвиг на горизонтальные плечи корпуса
            var a = after[j];
            var b = before[j] * Matrix4x4.CreateRotationY(chest);
            a.Translation = b.Translation = Vector3.Zero;
            Near(a, b, 2e-5f, $"{Names[j]} — поворот в модели прежний (и на chest)");
            Assert.True(MathF.Abs(after[j].M42 - before[j].M42) < 1e-4f, $"{Names[j]}: высота не меняется");
        }
    }

    [Fact]
    public void Twist_Repeatable_AndInQueueOrder()
    {
        var (a, ta, pa) = Rig(0.09f);
        var (b, tb, pb) = Rig(0.09f);
        a.Twist(pa, ta, 0.4f);
        a.Twist(pa, ta, -1.1f);
        b.Twist(pb, tb, 0.4f);
        b.Twist(pb, tb, -1.1f);
        Assert.Equal(2, a.Tick().Twists);
        b.Tick();
        Assert.Equal(a.LocalRot(pa).ToArray(), b.LocalRot(pb).ToArray());
        Assert.Equal(a.LocalPos(pa).ToArray(), b.LocalPos(pb).ToArray());
    }

    [Fact]
    public void SteadyTwist_DoesNotAllocate()
    {
        var (pool, twist, pose) = Rig(0);
        void Tick(int t)
        {
            pool.Sample(pose, 0, t * 0.001f);
            pool.Twist(pose, twist, 0.01f * (t % 100) - 0.5f);
            pool.ToModel(pose);
            pool.Tick();
        }
        for (int t = 0; t < 10; t++) Tick(t);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int t = 0; t < 1000; t++) Tick(t);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void BadTwist_Rejected_StalePoseSkipped()
    {
        var pool = new PosePool(Library());
        var pose = pool.Alloc();
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Twist(pose, new PoseTwist(0), 0.1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.AddTwist(Names.Length, Spine, Weights));
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.AddTwist(Root, [Spine1, 99], [0.5f, 0.5f]));
        Assert.Throws<ArgumentException>(() => pool.AddTwist(Root, [], []));
        Assert.Throws<ArgumentException>(() => pool.AddTwist(Root, [Spine1, Spine1], [0.5f, 0.5f]));
        Assert.Throws<ArgumentException>(() => pool.AddTwist(Root, [Spine1, Chest], [0.5f]));
        Assert.Throws<ArgumentException>(() => pool.AddTwist(Root, [Spine1, Chest], [0.5f, 0.6f]));
        Assert.Throws<ArgumentException>(() => pool.AddTwist(Root, [Spine1, Chest], [-0.5f, 1.5f]));
        Assert.Equal(0, pool.TwistCount);
        var twist = pool.AddTwist(Root, Spine, Weights);
        Assert.Equal(1, pool.TwistCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Twist(pose, twist, float.NaN));
        pool.Twist(pose, twist, 0.3f);
        pool.Free(pose);   // поза освобождена до Tick — задание пропускается
        Assert.Equal(new PoseTickStats(0, 0, 0, 0, 1), pool.Tick());
        Assert.Throws<InvalidOperationException>(() => pool.Twist(pose, twist, 0.3f));
    }
}
