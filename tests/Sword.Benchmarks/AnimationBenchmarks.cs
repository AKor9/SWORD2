using System.Numerics;
using BenchmarkDotNet.Attributes;
using Sword.Engine.Animation;
using Sword.Engine.Formats;

namespace Sword.Benchmarks;

/// <summary>
/// Тик пула поз на самом дорогом пути — кроссфейд: сэмпл клипа, смешивание со снимком, копия в позу, FK; скелет — 161 сустав.
/// Цель — 0 байт на тик; время — для сравнения будущих SoA/SIMD-версий.
/// </summary>
[MemoryDiagnoser]
public class PoseSampling
{
    public const int Joints = 161, Frames = 31;

    /// <summary>Один боец и предельная толпа (33 бойца).</summary>
    [Params(1, 33)]
    public int Fighters { get; set; }

    private PosePool _pool = null!;
    private PoseHandle[] _pose = null!, _layer = null!, _from = null!;
    private float _time;

    [GlobalSetup]
    public void Setup()
    {
        var names = Enumerable.Range(0, Joints).Select(j => $"j{j}").ToArray();
        var parents = Enumerable.Range(0, Joints).Select(j => j == 0 ? -1 : (j - 1) / 2).ToArray();
        var skeleton = new Skeleton("bench", names, parents);
        ClipSource Clip(string name, float phase)
        {
            var file = new ClipFile
            {
                Skeleton = "bench", SkeletonHash = skeleton.Hash, SampleRate = 30, Frames = Frames, Joints = Joints, Loop = true,
                LocalRot = Enumerable.Range(0, Frames * Joints)
                    .Select(i => Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(1, i % 5, 0.3f)), phase + i * 0.01f)).ToArray(),
                LocalPos = Enumerable.Range(0, Frames * Joints).Select(i => new Vector3(0, 0.1f, 0.01f * (i % 7))).ToArray(),
                RootMotion = new Vector3[Frames], RootYaw = new float[Frames],
            };
            using var s = new MemoryStream();
            file.Write(s);
            byte[] bytes = s.ToArray();
            return new ClipSource($"animations/bench/{name}.swclip", () => new MemoryStream(bytes), name);
        }
        _pool = new PosePool(ClipLibrary.Load(skeleton, [Clip("a", 0), Clip("b", 1)])!);
        _pose = new PoseHandle[Fighters];
        _layer = new PoseHandle[Fighters];
        _from = new PoseHandle[Fighters];
        for (int i = 0; i < Fighters; i++)
        {
            _pose[i] = _pool.Alloc();
            _layer[i] = _pool.Alloc();
            _from[i] = _pool.Alloc();
            _pool.Sample(_from[i], 1, 0.2f);
        }
        _pool.Tick();
    }

    [Benchmark]
    public int CrossfadeTick()
    {
        _time += 1f / 120;
        for (int i = 0; i < _pose.Length; i++)
        {
            _pool.Sample(_layer[i], 0, _time + i * 0.01f);
            _pool.Blend(_layer[i], _from[i], 0.3f);
            _pool.Copy(_pose[i], _layer[i]);
            _pool.ToModel(_pose[i]);
        }
        return _pool.Tick().Total;
    }
}
