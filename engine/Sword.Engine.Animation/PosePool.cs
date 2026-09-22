using System.Numerics;

namespace Sword.Engine.Animation;

/// <summary>Поза в пуле: номер слота и поколение. Поколение 0 — «нет позы»; освобождённый слот меняет поколение.</summary>
public readonly record struct PoseHandle(int Index, int Generation)
{
    public static readonly PoseHandle None = default;
    public bool IsNone => Generation == 0;
    public override string ToString() => IsNone ? "нет" : $"{Index}v{Generation}";
}

/// <summary>Итог одного <see cref="PosePool.Tick"/>: сколько заданий каждого вида выполнено и сколько пропущено.</summary>
public readonly record struct PoseTickStats(int Samples, int Blends, int Copies, int ToModel, int Stale)
{
    public int Total => Samples + Blends + Copies + ToModel;
}

/// <summary>
/// Пул поз одного скелета (раздел 6 плана: подсистема владеет позами, наружу — хэндлы). Поза — локальные повороты
/// и смещения всех суставов и модельные матрицы (итог прямой кинематики). Слоты лежат подряд в общих массивах.
///
/// Позы меняются только заданиями: системы тика ставят <see cref="Sample"/>, <see cref="Blend"/>, <see cref="Copy"/>,
/// <see cref="ToModel"/> в очередь (Push), <see cref="Tick"/> исполняет всю очередь пачкой в порядке постановки (стадия
/// Animation), дальше позы читают (Pull, клиент). Порядок заданий одной позы сохраняется, поэтому итог совпадает
/// с последовательными вызовами <c>SkeletalClips</c> SWORD. Задание к позе, освобождённой до <see cref="Tick"/>,
/// пропускается (<see cref="PoseTickStats.Stale"/>). Установившийся тик не выделяет памяти.
/// </summary>
public sealed class PosePool
{
    private enum Op : byte { Sample, Blend, Copy, ToModel }

    private struct Job
    {
        public Op Op;
        public PoseHandle Dst, Src;
        public int Clip;
        public float Value;   // время клипа или вес смешивания
    }

    private readonly int _jc;
    private Quaternion[] _rot = [];
    private Vector3[] _pos = [];
    private Matrix4x4[] _model = [];
    private int[] _generation = [];
    private int[] _free = [];
    private int _freeCount, _capacity;
    private readonly Matrix4x4[] _referenceModel;
    private Job[] _jobs = new Job[64];
    private int _jobCount;

    public PosePool(ClipLibrary clips, int capacity = 16)
    {
        Clips = clips;
        _jc = clips.JointCount;
        _referenceModel = new Matrix4x4[_jc];
        Skeleton.ToModel(Skeleton.ReferenceRot, Skeleton.ReferencePos, _referenceModel);
        Grow(Math.Max(capacity, 1));
    }

    public ClipLibrary Clips { get; }
    public Skeleton Skeleton => Clips.Skeleton;
    public int JointCount => _jc;

    /// <summary>Модельные матрицы позы привязки — такой выдаётся новая поза.</summary>
    public ReadOnlySpan<Matrix4x4> ReferenceModel => _referenceModel;

    /// <summary>Живых поз.</summary>
    public int Count => _capacity - _freeCount;

    /// <summary>Заданий в очереди до следующего <see cref="Tick"/>.</summary>
    public int Pending => _jobCount;

    /// <summary>Итог последнего <see cref="Tick"/>.</summary>
    public PoseTickStats LastTick { get; private set; }

    /// <summary>Слотов в пуле (живых и свободных).</summary>
    public int Capacity => _capacity;

    /// <summary>Живая поза в слоте <paramref name="index"/>; слот свободен — <see cref="PoseHandle.None"/> (обход по слотам — стабильный порядок).</summary>
    public PoseHandle HandleAt(int index) =>
        (uint)index < (uint)_capacity && _generation[index] > 0 ? new PoseHandle(index, _generation[index]) : PoseHandle.None;

    /// <summary>
    /// Свободные слоты в порядке выдачи с конца: следующий <see cref="Alloc"/> берёт последний. От этого порядка зависят
    /// номера будущих поз — он часть состояния пула.
    /// </summary>
    public ReadOnlySpan<int> FreeSlots => _free.AsSpan(0, _freeCount);

    /// <summary>Поколение слота: у живого — поколение позы, у свободного — со знаком минус, у ни разу не выданного — 0.</summary>
    public int GenerationAt(int index) => _generation[index];

    private void Grow(int capacity)
    {
        int old = _capacity;
        Array.Resize(ref _rot, capacity * _jc);
        Array.Resize(ref _pos, capacity * _jc);
        Array.Resize(ref _model, capacity * _jc);
        Array.Resize(ref _generation, capacity);
        Array.Resize(ref _free, capacity);
        // свободные — с конца списка, чтобы первыми выдавались младшие номера
        for (int i = capacity - 1; i >= old; i--) _free[_freeCount++] = i;
        _capacity = capacity;
    }

    /// <summary>Новая поза — в позе привязки скелета (локальной и модельной).</summary>
    public PoseHandle Alloc()
    {
        if (_freeCount == 0) Grow(_capacity * 2);
        int i = _free[--_freeCount];
        int gen = -_generation[i] + 1;   // у свободного слота поколение хранится со знаком минус
        _generation[i] = gen;
        Skeleton.ReferenceRot.CopyTo(_rot.AsSpan(i * _jc, _jc));
        Skeleton.ReferencePos.CopyTo(_pos.AsSpan(i * _jc, _jc));
        _referenceModel.CopyTo(_model.AsSpan(i * _jc, _jc));
        return new PoseHandle(i, gen);
    }

    /// <exception cref="InvalidOperationException">Поза уже освобождена или чужая.</exception>
    public void Free(PoseHandle h)
    {
        Check(h);
        _generation[h.Index] = -_generation[h.Index];
        _free[_freeCount++] = h.Index;
    }

    public bool IsAlive(PoseHandle h) => h.Generation > 0 && (uint)h.Index < (uint)_capacity && _generation[h.Index] == h.Generation;

    private void Check(PoseHandle h)
    {
        if (!IsAlive(h)) throw new InvalidOperationException($"поза {h} не жива (пул из {_capacity} слотов)");
    }

    public ReadOnlySpan<Quaternion> LocalRot(PoseHandle h)
    {
        Check(h);
        return _rot.AsSpan(h.Index * _jc, _jc);
    }

    public ReadOnlySpan<Vector3> LocalPos(PoseHandle h)
    {
        Check(h);
        return _pos.AsSpan(h.Index * _jc, _jc);
    }

    /// <summary>Модельные матрицы позы на последнем <see cref="ToModel"/> (вектор слева, пространство персонажа).</summary>
    public ReadOnlySpan<Matrix4x4> Model(PoseHandle h)
    {
        Check(h);
        return _model.AsSpan(h.Index * _jc, _jc);
    }

    /// <summary>Задание: локальная поза клипа <paramref name="clip"/> в момент <paramref name="time"/> — в <paramref name="dst"/>.</summary>
    public void Sample(PoseHandle dst, int clip, float time)
    {
        Check(dst);
        if ((uint)clip >= (uint)Clips.Clips.Length) throw new ArgumentOutOfRangeException(nameof(clip), clip, "нет такого клипа");
        Push(new Job { Op = Op.Sample, Dst = dst, Clip = clip, Value = time });
    }

    /// <summary>Задание: смешать <paramref name="src"/> в <paramref name="dst"/> с весом <paramref name="weight"/> (<see cref="ClipLibrary.Blend"/>).</summary>
    public void Blend(PoseHandle dst, PoseHandle src, float weight)
    {
        Check(dst);
        Check(src);
        Push(new Job { Op = Op.Blend, Dst = dst, Src = src, Value = weight });
    }

    /// <summary>Задание: локальная поза <paramref name="src"/> — в <paramref name="dst"/>.</summary>
    public void Copy(PoseHandle dst, PoseHandle src)
    {
        Check(dst);
        Check(src);
        Push(new Job { Op = Op.Copy, Dst = dst, Src = src });
    }

    /// <summary>Задание: модельные матрицы <paramref name="dst"/> — прямой кинематикой из её локальной позы.</summary>
    public void ToModel(PoseHandle dst)
    {
        Check(dst);
        Push(new Job { Op = Op.ToModel, Dst = dst });
    }

    private void Push(in Job job)
    {
        if (_jobCount == _jobs.Length) Array.Resize(ref _jobs, _jobs.Length * 2);
        _jobs[_jobCount++] = job;
    }

    /// <summary>Исполнить все задания очереди по порядку и очистить её.</summary>
    public PoseTickStats Tick()
    {
        int samples = 0, blends = 0, copies = 0, models = 0, stale = 0;
        var clips = Clips;
        for (int n = 0; n < _jobCount; n++)
        {
            ref readonly var job = ref _jobs[n];
            if (!IsAlive(job.Dst) || job.Op is Op.Blend or Op.Copy && !IsAlive(job.Src))
            {
                stale++;
                continue;
            }
            int d = job.Dst.Index * _jc;
            switch (job.Op)
            {
                case Op.Sample:
                    clips.Sample(job.Clip, job.Value, _rot.AsSpan(d, _jc), _pos.AsSpan(d, _jc));
                    samples++;
                    break;
                case Op.Blend:
                {
                    int s = job.Src.Index * _jc;
                    ClipLibrary.Blend(_rot.AsSpan(d, _jc), _pos.AsSpan(d, _jc), _rot.AsSpan(s, _jc), _pos.AsSpan(s, _jc), job.Value);
                    blends++;
                    break;
                }
                case Op.Copy:
                {
                    int s = job.Src.Index * _jc;
                    if (s != d)
                    {
                        _rot.AsSpan(s, _jc).CopyTo(_rot.AsSpan(d, _jc));
                        _pos.AsSpan(s, _jc).CopyTo(_pos.AsSpan(d, _jc));
                    }
                    copies++;
                    break;
                }
                case Op.ToModel:
                    Skeleton.ToModel(_rot.AsSpan(d, _jc), _pos.AsSpan(d, _jc), _model.AsSpan(d, _jc));
                    models++;
                    break;
            }
        }
        _jobCount = 0;
        return LastTick = new PoseTickStats(samples, blends, copies, models, stale);
    }
}
