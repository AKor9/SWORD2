using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Formats;
using static Sword.Engine.Animation.Tests.TestClips;

namespace Sword.Engine.Animation.Tests;

/// <summary>Пул поз и задания: итог — тот же, что у последовательных вызовов библиотеки клипов; хэндлы; память; поза привязки.</summary>
public sealed class PoseTests
{
    private static ClipLibrary TwoClips() => Library(Turning("a", "Walk", 12, 0.2f, loop: true), Turning("a", "Hit", 7, 1.1f, loop: false));

    /// <summary>Модельные матрицы по определению: <c>model[j] = local[j] · model[parent]</c>, рекурсивно, родитель — по требованию.</summary>
    private static void RecursiveToModel(int[] parents, ReadOnlySpan<Quaternion> rot, ReadOnlySpan<Vector3> pos, Matrix4x4[] component)
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
        RecursiveToModel(lib.Skeleton.JointParents.ToArray(), rot, pos, model);

        Assert.Equal(rot, pool.LocalRot(main).ToArray());
        Assert.Equal(pos, pool.LocalPos(main).ToArray());
        Assert.Equal(model, pool.Model(main).ToArray());
    }

    /// <summary>
    /// Смешивание по маске: вес сустава — <c>w·(mask[j] + (1 − mask[j])·stand)</c>, итог — как <see cref="ClipLibrary.Blend"/>
    /// с этими весами суставов, побитово; стоя (stand = 1) — всё тело, на ходу (0) — только маска.
    /// </summary>
    [Fact]
    public void BlendMasked_EqualsBlendWithJointWeights_BitForBit()
    {
        var lib = TwoClips();
        var pool = new PosePool(lib);
        int jc = lib.JointCount;
        float[] weights = [.. Enumerable.Range(0, jc).Select(j => (j % 5) * 0.25f)];   // 0, 0,25, 0,5, 0,75, 1 — частичная маска верха
        var mask = pool.AddMask(weights);
        Assert.Equal(new PoseMask(0), mask);
        Assert.Equal(weights, pool.Mask(mask).ToArray());
        foreach (float stand in new[] { 0f, 0.37f, 1f })
        {
            var main = pool.Alloc();
            var layer = pool.Alloc();
            pool.Sample(main, 0, 0.29f);
            pool.Sample(layer, 1, 0.41f);
            pool.BlendMasked(main, layer, 0.8f, mask, stand);
            Assert.Equal(new PoseTickStats(2, 1, 0, 0, 0), pool.Tick());

            var rot = new Quaternion[jc];
            var pos = new Vector3[jc];
            var layerRot = new Quaternion[jc];
            var layerPos = new Vector3[jc];
            var layerMask = new float[jc];
            lib.Sample(0, 0.29f, rot, pos);
            lib.Sample(1, 0.41f, layerRot, layerPos);
            for (int j = 0; j < jc; j++) layerMask[j] = weights[j] + (1 - weights[j]) * stand;   // вес сустава слоя
            ClipLibrary.Blend(rot, pos, layerRot, layerPos, 0.8f, layerMask);
            Assert.Equal(rot, pool.LocalRot(main).ToArray());
            Assert.Equal(pos, pool.LocalPos(main).ToArray());
            if (stand == 1)
            {
                // стоя — всё тело: то же, что смешивание без маски
                lib.Sample(0, 0.29f, rot, pos);
                ClipLibrary.Blend(rot, pos, layerRot, layerPos, 0.8f);
                Assert.Equal(rot, pool.LocalRot(main).ToArray());
            }
            if (stand == 0)
            {
                // на ходу — суставы вне маски (вес 0) остаются позой локомоции
                lib.Sample(0, 0.29f, rot, pos);
                for (int j = 0; j < jc; j++)
                    if (weights[j] == 0) Assert.Equal(rot[j], pool.LocalRot(main)[j]);
            }
            pool.Free(main);
            pool.Free(layer);
        }
    }

    /// <summary>
    /// Память смешивания по маске: повороты сустава в двух позах расходятся через 180° при частичном весе — кратчайшая дуга
    /// (без памяти) перескакивает ((1 − вес)·360° = 36° при весе 0,9 за задание), с памятью итог доворачивает не больше
    /// <see cref="PosePool.MaxArcStep"/> за задание и догоняет кратчайшую дугу; до перескока и при быстром, но непрерывном движении
    /// (вход веса от 0 до 1 за 4 задания) — побитово как без памяти.
    /// </summary>
    [Fact]
    public void BlendMasked_Memory_ArcFlipFollowedByStep_SameOtherwise()
    {
        const int frames = 41;
        int jc = Joints.Length;
        var spin = Source("a", "Spin", new ClipFile
        {
            Skeleton = "test", SkeletonHash = SkeletonFile.ComputeHash(Joints, Parents), SampleRate = 30, Frames = frames, Joints = jc,
            LocalRot = [.. Enumerable.Range(0, frames * jc).Select(i => Quaternion.CreateFromAxisAngle(Vector3.UnitY, (160 + i / jc) * MathF.PI / 180))],
            LocalPos = new Vector3[frames * jc], RootMotion = new Vector3[frames], RootYaw = new float[frames],
        });
        var lib = Library(spin, Marked("a", "Still", frames, 0));
        int spinClip = lib.FindClip("Spin"), still = lib.FindClip("Still");
        Quaternion[] Run(bool memory, Func<int, float> weight)
        {
            var pool = new PosePool(lib);
            var mask = pool.AddMask(new float[jc]);   // все суставы вне маски: вес — weight·stand
            var (a, b, output, shortest) = (pool.Alloc(), pool.Alloc(), pool.Alloc(), pool.Alloc());
            var result = new Quaternion[frames];
            for (int f = 0; f < frames; f++)
            {
                pool.Sample(a, still, 0);
                pool.Sample(b, spinClip, f / 30f);
                pool.BlendMasked(a, b, weight(f), mask, 1, memory ? new BlendMemory(output, shortest, f > 0) : default);
                pool.Tick();
                result[f] = pool.LocalRot(a)[0];
            }
            return result;
        }
        static float Step(Quaternion p, Quaternion q) => 2 * MathF.Acos(Math.Min(1, MathF.Abs(Quaternion.Dot(p, q))));
        var plain = Run(false, _ => 0.9f);
        var kept = Run(true, _ => 0.9f);
        float plainJump = 0, keptJump = 0;
        for (int f = 1; f < frames; f++)
        {
            plainJump = MathF.Max(plainJump, Step(plain[f - 1], plain[f]));
            keptJump = MathF.Max(keptJump, Step(kept[f - 1], kept[f]));
        }
        Assert.True(plainJump > 0.5f, $"кратчайшая дуга: скачок {plainJump} рад");
        Assert.True(keptJump <= PosePool.MaxArcStep + 0.02f, $"с памятью: скачок {keptJump} рад");
        Assert.Equal(plain[..20], kept[..20]);   // до 180° (кадр 20) — побитово
        Assert.Equal(plain[^1], kept[^1]);       // догнал кратчайшую дугу
        // Вход веса за 4 задания (быстро, но непрерывно) — без ограничения, побитово.
        Func<int, float> fadeIn = f => MathF.Min(1, f * 0.25f);
        Assert.Equal(Run(false, fadeIn)[..8], Run(true, fadeIn)[..8]);

        // Смешивание погасло посреди догона (переход от снимка кончился) — PosePool.Following: догон идёт; владелец продолжает с весом 0,
        // и сустав доходит до позы без смешивания шагами не больше 8°, а не скачком; догнал — Following ложно.
        {
            var pool = new PosePool(lib);
            var mask = pool.AddMask(new float[jc]);
            var (a, b, output, shortest) = (pool.Alloc(), pool.Alloc(), pool.Alloc(), pool.Alloc());
            var memory = new BlendMemory(output, shortest, true);
            Quaternion prev = default;
            bool followed = false;
            float jump = 0;
            for (int f = 0; f < frames + 30; f++)
            {
                bool tail = f >= 22;   // перескок — у кадра 20; с 22-го смешивания «нет»: вес 0, пока идёт догон
                if (tail && !pool.Following(memory)) break;
                followed |= tail;
                pool.Sample(a, still, 0);
                pool.Sample(b, spinClip, Math.Min(f, frames - 1) / 30f);
                pool.BlendMasked(a, b, tail ? 0 : 0.9f, mask, 1, memory with { Continuous = f > 0 });
                pool.Tick();
                var q = pool.LocalRot(a)[0];
                if (f > 0) jump = MathF.Max(jump, Step(prev, q));
                prev = q;
            }
            Assert.True(followed, "после перескока догон не шёл — тест ничего не проверял бы");
            Assert.False(pool.Following(memory));
            Assert.True(jump <= PosePool.MaxArcStep + 0.02f, $"конец смешивания посреди догона: скачок {jump} рад");
            var plainPose = pool.Alloc();
            pool.Sample(plainPose, still, 0);
            pool.Tick();
            Assert.Equal(pool.LocalRot(plainPose)[0], prev);   // догнал: сустав — поза без смешивания
        }
    }

    /// <summary>
    /// Переход от снимка — смешивание по маске всего тела (вес 1 у каждого сустава) с памятью: без перескока дуги — побитово как
    /// <see cref="PosePool.Blend"/> на гаснущем весе перехода.
    /// </summary>
    [Fact]
    public void BlendMasked_FullMaskWithMemory_EqualsBlend_BitForBit()
    {
        var lib = TwoClips();
        var pool = new PosePool(lib);
        var full = pool.AddMask([.. Enumerable.Repeat(1f, pool.JointCount)]);
        var (a, b, snap, output, shortest) = (pool.Alloc(), pool.Alloc(), pool.Alloc(), pool.Alloc(), pool.Alloc());
        pool.Sample(snap, 1, 0.4f);
        for (int t = 0; t < 18; t++)
        {
            float w = 1 - t / 18f;
            w = w * w * (3 - 2 * w);
            pool.Sample(a, 0, t / 60f);
            pool.Sample(b, 0, t / 60f);
            pool.Blend(a, snap, w);
            pool.BlendMasked(b, snap, w, full, 1, new BlendMemory(output, shortest, t > 0));
            pool.Tick();
            Assert.Equal(pool.LocalRot(a).ToArray(), pool.LocalRot(b).ToArray());
            Assert.Equal(pool.LocalPos(a).ToArray(), pool.LocalPos(b).ToArray());
        }
    }

    [Fact]
    public void BlendMasked_BadMaskOrStaleSource()
    {
        var pool = new PosePool(TwoClips());
        int jc = pool.JointCount;
        Assert.Throws<ArgumentException>(() => pool.AddMask(new float[jc + 1]));
        var bad = new float[jc];
        bad[1] = 1.5f;
        Assert.Throws<ArgumentException>(() => pool.AddMask(bad));
        bad[1] = float.NaN;
        Assert.Throws<ArgumentException>(() => pool.AddMask(bad));
        var a = pool.Alloc();
        var b = pool.Alloc();
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.BlendMasked(a, b, 1, new PoseMask(0), 1));
        var mask = pool.AddMask(new float[jc]);
        pool.BlendMasked(a, b, 1, mask, 1);
        pool.Free(b);   // источник освобождён до Tick — задание пропускается
        Assert.Equal(new PoseTickStats(0, 0, 0, 0, 1), pool.Tick());
    }

    /// <summary>
    /// Добавка поворотов по маске (вздрагивание игрока): <c>rot[j] = normalize(rot[j]·slerp(identity, conj(ref[j])·pose[j],
    /// mask[j]·вес))</c> — побитово по этой формуле; суставы с весом маски 0 и смещения не меняются; вес 0 и меньше — ничего; две
    /// добавки подряд — по порядку заданий (два слота вздрагивания).
    /// </summary>
    [Fact]
    public void AddMasked_FormulaBitForBit_MaskZeroAndOffsetsUntouched()
    {
        var lib = TwoClips();
        int jc = lib.JointCount;
        float[] weights = [.. Enumerable.Range(0, jc).Select(j => (j % 5) * 0.25f)];
        // Формула добавки по суставам — ожидание теста.
        static void AddMasked(Quaternion[] rot, Quaternion[] reference, Quaternion[] pose, float[] mask, float weight)
        {
            if (weight <= 0) return;
            for (int j = 0; j < rot.Length; j++)
            {
                float k = mask[j] * weight;
                if (k <= 0) continue;
                var delta = Quaternion.Conjugate(reference[j]) * pose[j];
                rot[j] = Quaternion.Normalize(rot[j] * Quaternion.Slerp(Quaternion.Identity, delta, k));
            }
        }
        foreach (float weight in new[] { 0.8f, 0.37f, 0f, -0.2f })
        {
            var pool = new PosePool(lib);
            var mask = pool.AddMask(weights);
            var (main, reference, pose) = (pool.Alloc(), pool.Alloc(), pool.Alloc());
            pool.Sample(main, 0, 0.29f);
            // два слота подряд: первый — клип 1 от 0 к 0,13 с, второй — к 0,41 с с другим весом
            pool.Sample(reference, 1, 0);
            pool.Sample(pose, 1, 0.13f);
            pool.AddMasked(main, reference, pose, weight, mask);
            pool.Sample(reference, 1, 0);
            pool.Sample(pose, 1, 0.41f);
            pool.AddMasked(main, reference, pose, weight * 0.5f, mask);
            Assert.Equal(new PoseTickStats(5, 2, 0, 0, 0), pool.Tick());

            var rot = new Quaternion[jc];
            var pos = new Vector3[jc];
            var refRot = new Quaternion[jc];
            var poseRot = new Quaternion[jc];
            var scratch = new Vector3[jc];
            lib.Sample(0, 0.29f, rot, pos);
            var before = (Quaternion[])rot.Clone();
            lib.Sample(1, 0, refRot, scratch);
            lib.Sample(1, 0.13f, poseRot, scratch);
            AddMasked(rot, refRot, poseRot, weights, weight);
            lib.Sample(1, 0.41f, poseRot, scratch);
            AddMasked(rot, refRot, poseRot, weights, weight * 0.5f);
            Assert.Equal(rot, pool.LocalRot(main).ToArray());
            Assert.Equal(pos, pool.LocalPos(main).ToArray());   // смещения не трогаются
            for (int j = 0; j < jc; j++)
                if (weights[j] == 0 || weight <= 0) Assert.Equal(before[j], pool.LocalRot(main)[j]);
        }
    }

    [Fact]
    public void AddMasked_BadMaskOrStalePoses()
    {
        var pool = new PosePool(TwoClips());
        int jc = pool.JointCount;
        var (a, r, p) = (pool.Alloc(), pool.Alloc(), pool.Alloc());
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.AddMasked(a, r, p, 1, new PoseMask(0)));
        var mask = pool.AddMask(new float[jc]);
        pool.AddMasked(a, r, p, 1, mask);
        pool.Free(r);   // поза отсчёта освобождена до Tick — задание пропускается
        Assert.Equal(new PoseTickStats(0, 0, 0, 0, 1), pool.Tick());
        Assert.Throws<InvalidOperationException>(() => pool.AddMasked(a, r, p, 1, mask));
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
        RecursiveToModel(parents, rot, pos, expected);
        skeleton.ToModel(rot, pos, actual);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SubtreeMask_RootAndAllDescendants_ParentAfterChild()
    {
        // экспорт UE: часть костей идёт раньше родителя; поддерево arm — arm, hand, finger (не root и не соседняя ветка)
        string[] names = ["hand", "root", "arm", "finger", "leg"];
        int[] parents = [2, -1, 1, 0, 1];
        var skeleton = new Skeleton("s", names, parents);
        float[] Mask(int root)
        {
            var mask = new float[names.Length];
            Array.Fill(mask, 7f);   // всё место переписывается
            skeleton.SubtreeMask(root, mask);
            return mask;
        }
        Assert.Equal([1f, 0, 1, 1, 0], Mask(2));
        Assert.Equal([1f, 1, 1, 1, 1], Mask(1));
        Assert.Equal([0f, 0, 0, 1, 0], Mask(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => Mask(5));
        Assert.Throws<ArgumentOutOfRangeException>(() => Mask(-1));
        Assert.Throws<ArgumentException>(() => skeleton.SubtreeMask(2, new float[4]));
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
        var mask = pool.AddMask([.. Enumerable.Range(0, pool.JointCount).Select(j => j % 2 * 1f)]);
        void Tick(int t)
        {
            foreach (var p in poses)
            {
                pool.Sample(p, 0, t * 0.01f);
                pool.Blend(p, poses[0], 0.5f);
                pool.BlendMasked(p, poses[1], 0.5f, mask, t * 0.001f);
                pool.AddMasked(p, poses[2], poses[3], 0.4f, mask);
                pool.ToModel(p);
            }
            pool.Tick();
        }
        for (int t = 0; t < 10; t++) Tick(t);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int t = 0; t < 1000; t++) Tick(t);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>
    /// Запас памяти пула (<see cref="PosePool.Reserve"/>) — рост посреди игры (выдача, освобождение, удвоение ёмкости, очередь заданий) не
    /// выделяет памяти, а логика та же, что без запаса: ёмкость, номера и поколения поз, порядок свободных слотов — шаг в шаг.
    /// </summary>
    [Fact]
    public void Reserve_GrowthWithoutAllocations_SameLogicAsWithout()
    {
        var plain = new PosePool(TwoClips());
        var reserved = new PosePool(TwoClips());
        reserved.Reserve(300, jobs: 4096);
        Assert.Equal(plain.Capacity, reserved.Capacity);
        Assert.True(reserved.Reserved >= 300 && reserved.JobsReserved >= 4096);
        var rng = new Random(5);
        var a = new List<PoseHandle>();
        var b = new List<PoseHandle>(512);   // список теста не растёт в замере
        long allocated = 0;
        for (int step = 0; step < 4000; step++)
        {
            bool alloc = a.Count == 0 || a.Count < 250 && rng.Next(3) > 0;
            int victim = alloc ? -1 : rng.Next(a.Count);
            if (alloc) a.Add(plain.Alloc());
            else
            {
                plain.Free(a[victim]);
                a.RemoveAt(victim);
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            if (alloc)
            {
                var h = reserved.Alloc();
                reserved.Sample(h, 0, 0.1f);
                b.Add(h);
            }
            else
            {
                reserved.Free(b[victim]);
                b.RemoveAt(victim);
            }
            if (step % 50 == 0) reserved.Tick();
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            if (!alloc) continue;
            Assert.Equal(a[^1], b[^1]);
            Assert.Equal(plain.Capacity, reserved.Capacity);
            Assert.Equal(plain.FreeSlots.ToArray(), reserved.FreeSlots.ToArray());
        }
        Assert.Equal(256, reserved.Capacity);   // ёмкость росла удвоением, как без запаса
        Assert.Equal(0, allocated);
        for (int i = 0; i < plain.Capacity; i++) Assert.Equal(plain.GenerationAt(i), reserved.GenerationAt(i));
    }
}
