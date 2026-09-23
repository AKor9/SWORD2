using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Formats;
using static Sword.Engine.Animation.Tests.TestClips;

namespace Sword.Engine.Animation.Tests;

/// <summary>Пул поз и задания: итог — тот же, что у последовательных вызовов SWORD; хэндлы; память; поза привязки.</summary>
public sealed class PoseTests
{
    private static ClipLibrary TwoClips() => Library(Turning("a", "Walk", 12, 0.2f, loop: true), Turning("a", "Hit", 7, 1.1f, loop: false));

    /// <summary><c>SkeletalClips.ToComponent</c> SWORD: рекурсивно, родитель — по требованию.</summary>
    private static void SwordToComponent(int[] parents, ReadOnlySpan<Quaternion> rot, ReadOnlySpan<Vector3> pos, Matrix4x4[] component)
    {
        var done = new bool[parents.Length];
        void Resolve(int j, ReadOnlySpan<Quaternion> r, ReadOnlySpan<Vector3> p)
        {
            if (done[j]) return;
            var local = Matrix4x4.CreateFromQuaternion(r[j]);
            local.Translation = p[j];
            int parent = parents[j];
            if (parent >= 0)
            {
                Resolve(parent, r, p);
                local *= component[parent];
            }
            component[j] = local;
            done[j] = true;
        }
        for (int j = 0; j < parents.Length; j++) Resolve(j, rot, pos);
    }

    [Fact]
    public void Jobs_MatchSequentialCallsBitForBit()
    {
        var lib = TwoClips();
        var pool = new PosePool(lib);
        var main = pool.Alloc();
        var from = pool.Alloc();
        pool.Sample(from, 1, 0.13f);
        pool.Sample(main, 0, 0.371f);
        pool.Blend(main, from, 0.37f);
        pool.ToModel(main);
        var stats = pool.Tick();
        Assert.Equal(new PoseTickStats(2, 1, 0, 1, 0), stats);

        int jc = lib.JointCount;
        var rot = new Quaternion[jc];
        var pos = new Vector3[jc];
        var fromRot = new Quaternion[jc];
        var fromPos = new Vector3[jc];
        var model = new Matrix4x4[jc];
        lib.Sample(1, 0.13f, fromRot, fromPos);
        lib.Sample(0, 0.371f, rot, pos);
        ClipLibrary.Blend(rot, pos, fromRot, fromPos, 0.37f);
        SwordToComponent(lib.Skeleton.JointParents.ToArray(), rot, pos, model);

        Assert.Equal(rot, pool.LocalRot(main).ToArray());
        Assert.Equal(pos, pool.LocalPos(main).ToArray());
        Assert.Equal(model, pool.Model(main).ToArray());
    }

    [Fact]
    public void ToModel_ParentAfterChild_MatchesRecursiveOrder()
    {
        // экспорт UE: часть костей идёт раньше родителя
        string[] names = ["hand", "root", "arm", "finger"];
        int[] parents = [2, -1, 1, 0];
        var skeleton = new Skeleton("s", names, parents);
        var rot = Enumerable.Range(0, 4).Select(i => Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.3f * i + 0.1f)).ToArray();
        var pos = Enumerable.Range(0, 4).Select(i => new Vector3(0.1f * i, 0.2f, -0.05f * i)).ToArray();
        var expected = new Matrix4x4[4];
        var actual = new Matrix4x4[4];
        SwordToComponent(parents, rot, pos, expected);
        skeleton.ToModel(rot, pos, actual);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(new[] { -1, 2, 1 })]   // цикл
    [InlineData(new[] { 0 })]          // сам себе родитель
    [InlineData(new[] { -1, -2 })]     // родитель вне скелета
    [InlineData(new[] { -1, 5 })]
    public void Skeleton_BadParentsAreRejected(int[] parents)
    {
        var names = parents.Select((_, j) => $"j{j}").ToArray();
        Assert.Throws<InvalidDataException>(() => new Skeleton("s", names, parents));
    }

    [Fact]
    public void Handles_GenerationsAndStaleJobs()
    {
        var pool = new PosePool(TwoClips(), capacity: 1);
        var a = pool.Alloc();
        var b = pool.Alloc();   // пул растёт
        Assert.Equal(2, pool.Count);
        pool.Sample(a, 0, 0.1f);
        pool.Copy(b, a);
        pool.Free(a);
        Assert.False(pool.IsAlive(a));
        Assert.Throws<InvalidOperationException>(() => pool.Free(a));
        Assert.Throws<InvalidOperationException>(() => pool.Model(a));
        var stats = pool.Tick();
        Assert.Equal(2, stats.Stale);   // оба задания ссылались на освобождённую позу
        var c = pool.Alloc();
        Assert.Equal(a.Index, c.Index);
        Assert.NotEqual(a.Generation, c.Generation);
        Assert.False(pool.IsAlive(a));
        Assert.False(pool.IsAlive(PoseHandle.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Sample(c, 2, 0));
    }

    [Fact]
    public void NewPose_IsReferencePose()
    {
        var pool = new PosePool(TwoClips());
        var h = pool.Alloc();
        Assert.All(pool.LocalRot(h).ToArray(), q => Assert.Equal(Quaternion.Identity, q));
        Assert.All(pool.Model(h).ToArray(), m => Assert.Equal(Matrix4x4.Identity, m));
    }

    [Fact]
    public void BindPose_FromMesh_ReproducesBindMatrices()
    {
        var skeleton = Skeleton();
        var rot = new[] { Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.4f), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -0.7f) };
        var pos = new[] { Vector3.Zero, new Vector3(0, 0.95f, 0), new Vector3(0, 0.3f, 0.05f) };
        var model = new Matrix4x4[3];
        skeleton.ToModel(rot, pos, model);
        var mesh = new SkinnedMeshData
        {
            Vertices = [new SkinVertex(), new SkinVertex(), new SkinVertex()], Indices = [0, 1, 2],
            JointNames = Joints, JointParents = Parents,
            InverseBind = model.Select(m => Matrix4x4.Invert(m, out var inv) ? inv : throw new InvalidOperationException()).ToArray(),
        };
        using var s = new MemoryStream();
        mesh.Write(s);
        s.Position = 0;
        skeleton = skeleton.WithMeshBindPose(s, "mem");

        var pool = new PosePool(ClipLibrary.Load(skeleton, [Marked("a", "Idle", 2, 0)])!);
        var bind = pool.Model(pool.Alloc()).ToArray();
        for (int j = 0; j < 3; j++)
        {
            Assert.True(Vector3.Distance(model[j].Translation, bind[j].Translation) < 1e-5f, $"сустав {j}");
            Assert.True(Vector3.Distance(Vector3.TransformNormal(Vector3.UnitY, model[j]), Vector3.TransformNormal(Vector3.UnitY, bind[j])) < 1e-5f);
        }
    }

    [Fact]
    public void BindPose_ForeignMeshSkeleton_IsRejected()
    {
        var mesh = new SkinnedMeshData
        {
            Vertices = [new SkinVertex(), new SkinVertex(), new SkinVertex()], Indices = [0, 1, 2],
            JointNames = ["root", "pelvis", "head"], JointParents = Parents, InverseBind = [Matrix4x4.Identity, Matrix4x4.Identity, Matrix4x4.Identity],
        };
        using var s = new MemoryStream();
        mesh.Write(s);
        s.Position = 0;
        Assert.Throws<InvalidDataException>(() => Skeleton().WithMeshBindPose(s, "mem"));
    }

    [Fact]
    public void SteadyTick_DoesNotAllocate()
    {
        var pool = new PosePool(TwoClips());
        var poses = Enumerable.Range(0, 8).Select(_ => pool.Alloc()).ToArray();
        void Tick(int t)
        {
            foreach (var p in poses)
            {
                pool.Sample(p, 0, t * 0.01f);
                pool.Blend(p, poses[0], 0.5f);
                pool.ToModel(p);
            }
            pool.Tick();
        }
        for (int t = 0; t < 10; t++) Tick(t);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int t = 0; t < 1000; t++) Tick(t);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
