using System.Numerics;
using Sword.Engine.Animation.Motion;

namespace Sword.Engine.Animation;

/// <summary>Поза в пуле: номер слота и поколение. Поколение 0 — «нет позы»; освобождённый слот меняет поколение.</summary>
public readonly record struct PoseHandle(int Index, int Generation)
{
    public static readonly PoseHandle None = default;
    public bool IsNone => Generation == 0;
    public override string ToString() => IsNone ? "нет" : $"{Index}v{Generation}";
}

/// <summary>Маска суставов в пуле (<see cref="PosePool.AddMask"/>): вес каждого сустава 0…1 для <see cref="PosePool.BlendMasked"/>.</summary>
public readonly record struct PoseMask(int Index)
{
    public override string ToString() => $"маска {Index}";
}

/// <summary>Скручивание в пуле (<see cref="PosePool.AddTwist"/>): опора поворота тела и суставы корпуса с долями для <see cref="PosePool.Twist"/>.</summary>
public readonly record struct PoseTwist(int Index)
{
    public override string ToString() => $"скручивание {Index}";
}

/// <summary>
/// Память смешивания по маске: итог прошлого тика (<see cref="Output"/>) и итог по кратчайшей дуге (<see cref="Shortest"/>);
/// <see cref="Continuous"/> — в них прошлый тик того же смешивания (иначе — только записать).
/// </summary>
public readonly record struct BlendMemory(PoseHandle Output, PoseHandle Shortest, bool Continuous)
{
    public bool IsNone => Output.IsNone || Shortest.IsNone;
}

/// <summary>
/// Итог тика пула (<see cref="PosePool.Tick"/> и проходы того же тика, <see cref="PosePool.Pass"/>): выполнено заданий каждого вида
/// и пропущено (<see cref="Stale"/>).
/// </summary>
public readonly record struct PoseTickStats(int Samples, int Blends, int Copies, int ToModel, int Stale, int Motions = 0, int Twists = 0, int LegIks = 0)
{
    public int Total => Samples + Blends + Copies + ToModel + Motions + Twists + LegIks;

    public static PoseTickStats operator +(PoseTickStats a, PoseTickStats b) => new(a.Samples + b.Samples, a.Blends + b.Blends,
        a.Copies + b.Copies, a.ToModel + b.ToModel, a.Stale + b.Stale, a.Motions + b.Motions, a.Twists + b.Twists, a.LegIks + b.LegIks);
}

/// <summary>
/// Пул поз одного скелета: локальные повороты, смещения и модельные матрицы за хэндлами. Позы меняются только заданиями
/// в очереди; <see cref="Tick"/> исполняет её пачкой в порядке постановки (порядок заданий одной позы сохраняется).
/// Задание к позе, освобождённой до <see cref="Tick"/>, пропускается. Установившийся тик не выделяет памяти.
/// </summary>
public sealed partial class PosePool
{
    private enum Op : byte { Sample, Blend, BlendMasked, AddMasked, Copy, ToModel, Motion, Twist, LegIk, FromModel }

    private struct Job
    {
        public Op Op;
        public PoseHandle Dst, Src;
        public PoseHandle Ref;   // добавка по маске: поза, от которой считается отклонение
        public int Clip;      // клип сэмпла, маска смешивания (добавки) по маске, скручивание; задание Motion, LegIk — номер в своей таблице
        public float Value;   // время клипа, вес смешивания или угол скручивания
        public float Stand;   // смешивание по маске: доля клипа в суставах вне маски; скручивание: поворот груди
        public BlendMemory Memory;   // смешивание по маске: память (None — без неё)
    }

    /// <summary>Задание проигрывателя (<see cref="Motion"/>): в очереди — номер в <see cref="_motionJobs"/> (поле <c>Clip</c> задания).</summary>
    private struct MotionJob
    {
        public MotionPlayers Players;
        public MotionPlayerHandle Player;
        public MotionStep Step;
    }

    /// <summary>
    /// Наибольший поворот сустава за задание, пока итог смешивания по маске догоняет кратчайшую дугу после перескока, рад
    /// (8° — 960°/с при 120 Гц).
    /// </summary>
    public const float MaxArcStep = 8 * MathF.PI / 180;

    /// <summary>Косинус половины <see cref="MaxArcStep"/>: итог дальше от прошлого — поворот больше шага.</summary>
    public static readonly float ArcStepCos = MathF.Cos(MaxArcStep / 2);

    private readonly int _jc;
    private Quaternion[] _rot = [];
    private Vector3[] _pos = [];
    private Matrix4x4[] _model = [];
    private Matrix4x4[] _previousModel = [];
    private long[] _modelTick = [];   // номер Tick, в котором слот последний раз прошёл ToModel; новая поза — NoModelTick
    private bool[] _hasPrevious = [];  // _previousModel слота — модель тика прямо перед последним ToModel
    private long _ticks;   // исполненных Tick; проходы того же тика (Pass) его не меняют
    private const long NoModelTick = long.MinValue;
    private int[] _generation = [];
    private int[] _free = [];
    private int _freeCount, _capacity;
    private readonly Matrix4x4[] _referenceModel;
    private Job[] _jobs = new Job[64];
    private int _jobCount;
    private MotionJob[] _motionJobs = [];
    private int _motionCount;
    private readonly List<float[]> _masks = [];
    private readonly List<TwistSpec> _twists = [];
    private readonly float[] _layer;   // маска задания «смешать по маске» с долей stand — считается на задание

    public PosePool(ClipLibrary clips, int capacity = 16)
    {
        Clips = clips;
        _jc = clips.JointCount;
        _referenceModel = new Matrix4x4[_jc];
        _layer = new float[_jc];
        Skeleton.ToModel(Skeleton.ReferenceRot, Skeleton.ReferencePos, _referenceModel);
        Grow(Math.Max(capacity, 1));
    }

    public ClipLibrary Clips { get; }
    public Skeleton Skeleton => Clips.Skeleton;
    public int JointCount => _jc;

    /// <summary>Модельные матрицы позы привязки — такой выдаётся новая поза.</summary>
    public ReadOnlySpan<Matrix4x4> ReferenceModel => _referenceModel;

    public int Count => _capacity - _freeCount;

    public int Pending => _jobCount;

    public PoseTickStats LastTick { get; private set; }

    /// <summary>Слотов в пуле (живых и свободных); растёт удвоением, память может быть заведена заранее (<see cref="Reserve"/>).</summary>
    public int Capacity => _capacity;

    /// <summary>Живая поза в слоте <paramref name="index"/>; слот свободен — <see cref="PoseHandle.None"/> (обход по слотам — стабильный порядок).</summary>
    public PoseHandle HandleAt(int index) =>
        (uint)index < (uint)_capacity && _generation[index] > 0 ? new PoseHandle(index, _generation[index]) : PoseHandle.None;

    /// <summary>Свободные слоты в порядке выдачи с конца — часть состояния: от него зависят номера будущих поз.</summary>
    public ReadOnlySpan<int> FreeSlots => _free.AsSpan(0, _freeCount);

    /// <summary>Поколение слота: у живого — поколение позы, у свободного — со знаком минус, у ни разу не выданного — 0.</summary>
    public int GenerationAt(int index) => _generation[index];

    /// <summary>Слотов, под которые уже есть память (<see cref="Reserve"/>); не меньше <see cref="Capacity"/>.</summary>
    public int Reserved => _generation.Length;

    public int JobsReserved => _jobs.Length;

    /// <summary>
    /// Память заранее под <paramref name="poses"/> поз (с округлением до ёмкости удвоения) и <paramref name="jobs"/> заданий
    /// тика, чтобы рост посреди игры не выделял памяти. Логика и хеш не меняются: ёмкость, номера и поколения — те же, что без
    /// запаса. Меньше уже заведённого — ничего.
    /// </summary>
    public void Reserve(int poses, int jobs = 0)
    {
        int slots = Math.Max(_capacity, 1);
        while (slots < poses) slots *= 2;
        if (slots > _generation.Length) Resize(slots);
        if (jobs > _jobs.Length) Array.Resize(ref _jobs, jobs);
        if (jobs > _motionJobs.Length) Array.Resize(ref _motionJobs, jobs);
        if (jobs > _legJobs.Length) Array.Resize(ref _legJobs, jobs);
    }

    private void Resize(int slots)
    {
        Array.Resize(ref _rot, slots * _jc);
        Array.Resize(ref _pos, slots * _jc);
        Array.Resize(ref _model, slots * _jc);
        Array.Resize(ref _previousModel, slots * _jc);
        Array.Resize(ref _modelTick, slots);
        Array.Resize(ref _hasPrevious, slots);
        Array.Resize(ref _generation, slots);
        Array.Resize(ref _free, slots);
    }

    private void Grow(int capacity)
    {
        int old = _capacity;
        if (capacity > _generation.Length) Resize(capacity);   // в пределах запаса (Reserve) — без выделений
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
        _referenceModel.CopyTo(_previousModel.AsSpan(i * _jc, _jc));
        _modelTick[i] = NoModelTick;
        _hasPrevious[i] = false;
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

    /// <summary>
    /// Модельные матрицы позы на <see cref="ToModel"/> прошлого <see cref="Tick"/> — пара к <see cref="Model"/> для скоростей суставов
    /// и показа между тиками. Прошлой модели нет (<see cref="HasPreviousModel"/>: поза только заведена или стояла) — то же, что
    /// <see cref="Model"/>. Не состояние пула: в логике и хеше не участвует.
    /// </summary>
    public ReadOnlySpan<Matrix4x4> PreviousModel(PoseHandle h)
    {
        Check(h);
        return (HasPreviousModel(h) ? _previousModel : _model).AsSpan(h.Index * _jc, _jc);
    }

    /// <summary>У позы есть модель прошлого тика: прямая кинематика была и в последнем <see cref="Tick"/>, и раньше.</summary>
    public bool HasPreviousModel(PoseHandle h)
    {
        Check(h);
        return _modelTick[h.Index] == _ticks - 1 && _hasPrevious[h.Index];
    }

    /// <summary>Задание: поза клипа <paramref name="clip"/> в момент <paramref name="time"/>, с, — в <paramref name="dst"/>.</summary>
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

    /// <summary>Маска суставов для <see cref="BlendMasked"/>: вес сустава 0…1 (копируется); регистрируется при настройке, номер — порядок.</summary>
    /// <exception cref="ArgumentException">Длина — не число суставов скелета или вес вне 0…1.</exception>
    public PoseMask AddMask(ReadOnlySpan<float> weights)
    {
        if (weights.Length != _jc) throw new ArgumentException($"маска на {weights.Length} суставов, а в скелете {_jc}", nameof(weights));
        for (int j = 0; j < weights.Length; j++)
            if (!(weights[j] is >= 0 and <= 1))
                throw new ArgumentException($"вес сустава {Skeleton.JointNames[j]} в маске — {weights[j]}, а не 0…1", nameof(weights));
        _masks.Add(weights.ToArray());
        return new PoseMask(_masks.Count - 1);
    }

    public int MaskCount => _masks.Count;

    public ReadOnlySpan<float> Mask(PoseMask mask) => _masks[mask.Index];

    /// <summary>
    /// Задание: смешать по маске — вес сустава <c>weight·(mask[j] + (1 − mask[j])·stand)</c>, <paramref name="stand"/> — доля
    /// клипа вне маски (стоя — 1, на ходу — 0). Дуга — кратчайшая. С памятью: если кратчайшая дуга перескочила через 180°
    /// (скачок min(k, 1 − k)·360° за тик), итог доворачивается к ней не больше <see cref="MaxArcStep"/> за задание; без
    /// перескока — побитово как без памяти.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Маски нет в пуле.</exception>
    public void BlendMasked(PoseHandle dst, PoseHandle src, float weight, PoseMask mask, float stand, BlendMemory memory = default)
    {
        Check(dst);
        Check(src);
        if (!memory.IsNone)
        {
            Check(memory.Output);
            Check(memory.Shortest);
        }
        if ((uint)mask.Index >= (uint)_masks.Count) throw new ArgumentOutOfRangeException(nameof(mask), mask, $"масок в пуле {_masks.Count}");
        Push(new Job { Op = Op.BlendMasked, Dst = dst, Src = src, Clip = mask.Index, Value = weight, Stand = stand, Memory = memory });
    }

    /// <summary>
    /// Память ещё догоняет кратчайшую дугу. Владелец продолжает смешивание с весом 0, пока догон не кончится, — иначе конец
    /// смешивания вернул бы сустав на кратчайшую дугу скачком.
    /// </summary>
    public bool Following(BlendMemory memory)
    {
        if (memory.IsNone || !IsAlive(memory.Output) || !IsAlive(memory.Shortest)) return false;
        return Differ(_rot.AsSpan(memory.Output.Index * _jc, _jc), _rot.AsSpan(memory.Shortest.Index * _jc, _jc));
    }

    /// <summary>Повороты хоть одного сустава различаются (оператором: <c>SequenceEqual</c> в Debug упаковывал Quaternion).</summary>
    private static bool Differ(ReadOnlySpan<Quaternion> a, ReadOnlySpan<Quaternion> b)
    {
        for (int j = 0; j < a.Length; j++)
            if (a[j] != b[j]) return true;
        return false;
    }

    /// <summary>
    /// Задание: добавка к <paramref name="dst"/> отклонения <paramref name="pose"/> от <paramref name="reference"/> с весом
    /// <c>mask[j]·weight</c>: <c>rot[j] = normalize(rot[j]·slerp(identity, conj(ref[j])·pose[j], k))</c>; смещения не
    /// трогаются. Вес ≤ 0 — ничего.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Маски нет в пуле.</exception>
    public void AddMasked(PoseHandle dst, PoseHandle reference, PoseHandle pose, float weight, PoseMask mask)
    {
        Check(dst);
        Check(reference);
        Check(pose);
        if ((uint)mask.Index >= (uint)_masks.Count) throw new ArgumentOutOfRangeException(nameof(mask), mask, $"масок в пуле {_masks.Count}");
        Push(new Job { Op = Op.AddMasked, Dst = dst, Src = pose, Ref = reference, Clip = mask.Index, Value = weight });
    }

    /// <summary>
    /// Скручивание для <see cref="Twist"/>: <paramref name="bodyRoot"/> — опора вертикали поворота тела; <paramref name="spine"/>
    /// — суставы корпуса с долями обратного поворота (≥ 0, сумма 1), поворачиваются «предок раньше потомка».
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Сустава нет в скелете.</exception>
    /// <exception cref="ArgumentException">Суставов корпуса нет, они повторяются, их не столько, сколько долей, или доли не ≥ 0 и не в сумме 1.</exception>
    public PoseTwist AddTwist(int bodyRoot, ReadOnlySpan<int> spine, ReadOnlySpan<float> weights)
    {
        if ((uint)bodyRoot >= (uint)_jc) throw new ArgumentOutOfRangeException(nameof(bodyRoot), bodyRoot, $"суставов в скелете {_jc}");
        if (spine.Length == 0) throw new ArgumentException("скручиванию нужны суставы корпуса", nameof(spine));
        if (spine.Length != weights.Length) throw new ArgumentException($"суставов корпуса {spine.Length}, а долей {weights.Length}", nameof(weights));
        var parents = Skeleton.JointParents;
        var depth = new int[spine.Length];
        float sum = 0;
        for (int k = 0; k < spine.Length; k++)
        {
            int j = spine[k];
            if ((uint)j >= (uint)_jc) throw new ArgumentOutOfRangeException(nameof(spine), j, $"суставов в скелете {_jc}");
            if (spine[..k].Contains(j)) throw new ArgumentException($"сустав {Skeleton.JointNames[j]} в корпусе дважды", nameof(spine));
            if (!(float.IsFinite(weights[k]) && weights[k] >= 0))
                throw new ArgumentException($"доля сустава {Skeleton.JointNames[j]} — {weights[k]}, а не конечное ≥ 0", nameof(weights));
            sum += weights[k];
            for (int a = parents[j]; a >= 0; a = parents[a]) depth[k]++;
        }
        if (MathF.Abs(sum - 1) > 1e-4f) throw new ArgumentException($"доли корпуса в сумме {sum}, а не 1", nameof(weights));
        int[] order = [.. Enumerable.Range(0, spine.Length).OrderBy(k => depth[k])];
        int[] spineJoints = spine.ToArray(), spineOrdered = new int[order.Length];
        float[] weightArray = weights.ToArray(), weightOrdered = new float[order.Length];
        for (int k = 0; k < order.Length; k++)
        {
            spineOrdered[k] = spineJoints[order[k]];
            weightOrdered[k] = weightArray[order[k]];
        }
        var tops = new List<int>();
        for (int j = 0; j < _jc; j++)
            if (parents[j] < 0) tops.Add(j);
        _twists.Add(new TwistSpec(bodyRoot, spineOrdered, weightOrdered, [.. tops]));
        return new PoseTwist(_twists.Count - 1);
    }

    public int TwistCount => _twists.Count;

    /// <summary>
    /// Задание: скручивание на <paramref name="yaw"/>, рад (+ от +Z к +X): всё тело — вокруг вертикали через опору, затем
    /// суставы корпуса — на −доля·(yaw − <paramref name="chest"/>): ноги и таз уходят на yaw, верх тела — на chest. Меняет
    /// локальную позу; оба угла 0 — поза побитово прежняя.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Скручивания нет в пуле или угол не конечен.</exception>
    public void Twist(PoseHandle dst, PoseTwist twist, float yaw, float chest = 0)
    {
        Check(dst);
        if ((uint)twist.Index >= (uint)_twists.Count) throw new ArgumentOutOfRangeException(nameof(twist), twist, $"скручиваний в пуле {_twists.Count}");
        if (!float.IsFinite(yaw)) throw new ArgumentOutOfRangeException(nameof(yaw), yaw, "угол скручивания — конечный");
        if (!float.IsFinite(chest)) throw new ArgumentOutOfRangeException(nameof(chest), chest, "поворот груди — конечный");
        Push(new Job { Op = Op.Twist, Dst = dst, Clip = twist.Index, Value = yaw, Stand = chest });
    }

    public void Copy(PoseHandle dst, PoseHandle src)
    {
        Check(dst);
        Check(src);
        Push(new Job { Op = Op.Copy, Dst = dst, Src = src });
    }

    /// <summary>Модельные матрицы заданий <see cref="FromModel"/> тика: у задания — с номера <c>Clip × суставов</c>; растут удвоением.</summary>
    private Matrix4x4[] _staged = [];
    private int _stagedCount;

    /// <summary>
    /// Задание: локальная поза <paramref name="dst"/> — из модельных матриц <paramref name="model"/> (вектор слева, без масштаба):
    /// <c>local = model · model(родителя)⁻¹</c>. Матрицы копируются при постановке. Поза тела, лёгшего на физику, — обратно в анимацию.
    /// </summary>
    /// <exception cref="ArgumentException">Матриц не столько, сколько суставов.</exception>
    public void FromModel(PoseHandle dst, ReadOnlySpan<Matrix4x4> model)
    {
        Check(dst);
        if (model.Length != _jc) throw new ArgumentException($"матриц {model.Length}, суставов {_jc}", nameof(model));
        if (_staged.Length < (_stagedCount + 1) * _jc) Array.Resize(ref _staged, Math.Max((_stagedCount + 1) * _jc, _staged.Length * 2));
        model.CopyTo(_staged.AsSpan(_stagedCount * _jc, _jc));
        Push(new Job { Op = Op.FromModel, Dst = dst, Clip = _stagedCount++ });
    }

    /// <summary>Задание: модельные матрицы <paramref name="dst"/> — прямой кинематикой из её локальной позы.</summary>
    public void ToModel(PoseHandle dst)
    {
        Check(dst);
        Push(new Job { Op = Op.ToModel, Dst = dst });
    }

    /// <summary>
    /// Задание: шаг проигрывателя Motion Matching и его выход — в <paramref name="dst"/>. Состояние проигрывателя меняется только
    /// так, в общей очереди по порядку; освобождённый до <see cref="Tick"/> пропускается.
    /// </summary>
    /// <exception cref="ArgumentException">Хранилище — другой библиотеки клипов.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Шаг вне допустимого (продвижение &lt; 0, dt или полупериод ≤ 0, переход вне базы).</exception>
    /// <exception cref="InvalidOperationException">Поза или проигрыватель не живы.</exception>
    public void Motion(PoseHandle dst, MotionPlayers players, MotionPlayerHandle player, in MotionStep step)
    {
        Check(dst);
        ArgumentNullException.ThrowIfNull(players);
        if (players.Database.Library != Clips) throw new ArgumentException("проигрыватели — другой библиотеки клипов", nameof(players));
        if (!players.IsAlive(player)) throw new InvalidOperationException($"проигрыватель {player} не жив");
        if (!(step.Advance >= 0) || !float.IsFinite(step.Advance))
            throw new ArgumentOutOfRangeException(nameof(step), step.Advance, "продвижение — конечное и не меньше 0");
        if (!(step.Dt > 0) || !float.IsFinite(step.Dt)) throw new ArgumentOutOfRangeException(nameof(step), step.Dt, "dt — конечное и больше 0");
        if (!(step.Halflife > 0) || !float.IsFinite(step.Halflife))
            throw new ArgumentOutOfRangeException(nameof(step), step.Halflife, "полупериод — конечный и больше 0");
        if (step.Transition < -1 || step.Transition >= players.Database.FrameCount)
            throw new ArgumentOutOfRangeException(nameof(step), step.Transition, $"переход — кадр базы (0…{players.Database.FrameCount - 1}) или −1");
        if (_motionCount == _motionJobs.Length) Array.Resize(ref _motionJobs, Math.Max(4, _motionJobs.Length * 2));
        _motionJobs[_motionCount] = new MotionJob { Players = players, Player = player, Step = step };
        Push(new Job { Op = Op.Motion, Dst = dst, Clip = _motionCount });
        _motionCount++;
    }

    private void Push(in Job job)
    {
        if (_jobCount == _jobs.Length) Array.Resize(ref _jobs, _jobs.Length * 2);
        _jobs[_jobCount++] = job;
    }

    /// <summary>Исполнить все задания очереди по порядку и очистить её; тик пула — следующий.</summary>
    public PoseTickStats Tick()
    {
        var stats = Run(_ticks);
        _ticks++;
        return LastTick = stats;
    }

    /// <summary>
    /// Ещё один проход очереди в том же тике (задания, поставленные после <see cref="Tick"/>, — например, IK ноги по окончательному
    /// корню): исполнить и очистить; тик пула не меняется, итог прибавляется к <see cref="LastTick"/>. Пустая очередь — ничего.
    /// </summary>
    public PoseTickStats Pass()
    {
        if (_jobCount == 0) return default;
        var stats = Run(_ticks - 1);
        LastTick += stats;
        return stats;
    }

    /// <summary>Очередь по порядку в тике пула <paramref name="tick"/> (номер для прошлой модели, <see cref="ToModel(int, int, long)"/>).</summary>
    private PoseTickStats Run(long tick)
    {
        int samples = 0, blends = 0, copies = 0, models = 0, stale = 0, motions = 0, twists = 0, legs = 0;
        var clips = Clips;
        for (int n = 0; n < _jobCount; n++)
        {
            ref readonly var job = ref _jobs[n];
            if (!IsAlive(job.Dst) || job.Op is Op.Blend or Op.BlendMasked or Op.AddMasked or Op.Copy && !IsAlive(job.Src)
                || job.Op == Op.AddMasked && !IsAlive(job.Ref))
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
                case Op.BlendMasked:
                {
                    int s = job.Src.Index * _jc;
                    LayerMask(_masks[job.Clip], job.Stand, _layer);
                    var m = job.Memory;
                    if (!m.IsNone && IsAlive(m.Output) && IsAlive(m.Shortest))
                        BlendFollowingArc(_rot.AsSpan(d, _jc), _pos.AsSpan(d, _jc), _rot.AsSpan(s, _jc), _pos.AsSpan(s, _jc), job.Value, _layer,
                            _rot.AsSpan(m.Output.Index * _jc, _jc), _rot.AsSpan(m.Shortest.Index * _jc, _jc), m.Continuous);
                    else
                        ClipLibrary.Blend(_rot.AsSpan(d, _jc), _pos.AsSpan(d, _jc), _rot.AsSpan(s, _jc), _pos.AsSpan(s, _jc), job.Value, _layer);
                    blends++;
                    break;
                }
                case Op.AddMasked:
                    if (job.Value > 0)
                        AddRotations(_rot.AsSpan(d, _jc), _rot.AsSpan(job.Ref.Index * _jc, _jc), _rot.AsSpan(job.Src.Index * _jc, _jc),
                            _masks[job.Clip], job.Value);
                    blends++;
                    break;
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
                    ToModel(job.Dst.Index, d, tick);
                    models++;
                    break;
                case Op.Motion:
                    if (RunMotion(in _motionJobs[job.Clip], d)) motions++;
                    else stale++;
                    break;
                case Op.Twist:
                    if (job.Value != 0 || job.Stand != 0) ApplyTwist(_twists[job.Clip], _rot.AsSpan(d, _jc), _pos.AsSpan(d, _jc), job.Value, job.Stand);
                    twists++;
                    break;
                case Op.LegIk:
                    if (_legJobs[job.Clip].Align) ApplyKneeAlign(in _legJobs[job.Clip], d);
                    else ApplyLegIk(in _legJobs[job.Clip], d);
                    legs++;
                    break;
                case Op.FromModel:
                    Locals(_staged.AsSpan(job.Clip * _jc, _jc), _rot.AsSpan(d, _jc), _pos.AsSpan(d, _jc));
                    copies++;
                    break;
            }
        }
        _jobCount = 0;
        _stagedCount = 0;
        if (_motionCount > 0)
        {
            Array.Clear(_motionJobs, 0, _motionCount);   // хранилища не держатся очередью после тика
            _motionCount = 0;
        }
        _legCount = 0;
        return new PoseTickStats(samples, blends, copies, models, stale, motions, twists, legs);
    }

    /// <summary>Локальная поза из модельных матриц: <c>local = model · model(родителя)⁻¹</c>.</summary>
    private void Locals(ReadOnlySpan<Matrix4x4> model, Span<Quaternion> rot, Span<Vector3> pos)
    {
        var parents = Skeleton.JointParents;
        for (int j = 0; j < model.Length; j++)
        {
            var local = model[j];
            if (parents[j] >= 0 && Matrix4x4.Invert(model[parents[j]], out var up)) local *= up;
            rot[j] = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(local));
            pos[j] = local.Translation;
        }
    }

    /// <summary>
    /// Прямая кинематика слота: первая за <see cref="Tick"/> сохраняет прежнюю модель (<see cref="PreviousModel"/>) — модель прошлого
    /// тика, если прямая кинематика была и в нём (<see cref="HasPreviousModel"/>); у только что заведённой позы прошлое — она же.
    /// </summary>
    private void ToModel(int slot, int d, long tick)
    {
        long last = _modelTick[slot];
        if (last != tick)
        {
            if (last != NoModelTick) _model.AsSpan(d, _jc).CopyTo(_previousModel.AsSpan(d, _jc));
            _hasPrevious[slot] = last == tick - 1;
        }
        Skeleton.ToModel(_rot.AsSpan(d, _jc), _pos.AsSpan(d, _jc), _model.AsSpan(d, _jc));
        if (last == NoModelTick) _model.AsSpan(d, _jc).CopyTo(_previousModel.AsSpan(d, _jc));
        _modelTick[slot] = tick;
    }

    /// <summary>Исполнить задание проигрывателя (Motion/PosePool.Motion.cs); false — проигрыватель освобождён, задание пропущено.</summary>
    private partial bool RunMotion(in MotionJob job, int dst);

    /// <summary>Маска задания с долей <paramref name="stand"/>: <c>mask[j] + (1 − mask[j])·stand</c>. Цикл — свой метод: Tick зовётся раз в тик.</summary>
    private static void LayerMask(ReadOnlySpan<float> mask, float stand, Span<float> into)
    {
        for (int j = 0; j < mask.Length; j++) into[j] = mask[j] + (1 - mask[j]) * stand;
    }

    /// <summary>Добавка поворотов по маске (<see cref="AddMasked"/>). Цикл — свой метод: Tick зовётся раз в тик.</summary>
    private static void AddRotations(Span<Quaternion> rot, ReadOnlySpan<Quaternion> reference, ReadOnlySpan<Quaternion> pose,
        ReadOnlySpan<float> mask, float weight)
    {
        for (int j = 0; j < rot.Length; j++)
        {
            float k = mask[j] * weight;
            if (k <= 0) continue;
            var delta = Quaternion.Conjugate(reference[j]) * pose[j];
            rot[j] = Quaternion.Normalize(rot[j] * Quaternion.Slerp(Quaternion.Identity, delta, k));
        }
    }

    /// <summary>Скручивание (<see cref="AddTwist"/>): опора, суставы корпуса предками вперёд, их доли, корневые суставы скелета.</summary>
    private sealed record TwistSpec(int BodyRoot, int[] Spine, float[] Weights, int[] Tops);

    /// <summary>
    /// Скручивание: всё тело W = T(−c)·Ry·T(c) — справа к локальной матрице корневых суставов; сустав корпуса — локально
    /// <c>r' = conj(P)·y·P·r</c> (P — модельный поворот родителя), θ = −доля·(yaw − chest).
    /// </summary>
    private void ApplyTwist(TwistSpec twist, Span<Quaternion> rot, Span<Vector3> pos, float yaw, float chest)
    {
        if (yaw != 0)
        {
            var c = ModelOrigin(twist.BodyRoot, rot, pos);
            var q = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
            foreach (int t in twist.Tops)
            {
                rot[t] = Quaternion.Normalize(q * rot[t]);
                pos[t] = Vector3.Transform(pos[t] - c, q) + c;
            }
        }
        var parents = Skeleton.JointParents;
        for (int k = 0; k < twist.Spine.Length; k++)
        {
            int j = twist.Spine[k];
            var p = ModelRotation(parents[j], rot);
            var y = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -twist.Weights[k] * (yaw - chest));
            rot[j] = Quaternion.Normalize(Quaternion.Conjugate(p) * y * p * rot[j]);
        }
    }

    /// <summary>Начало сустава <paramref name="joint"/> в модели — прямой кинематикой по цепочке его предков.</summary>
    private Vector3 ModelOrigin(int joint, ReadOnlySpan<Quaternion> rot, ReadOnlySpan<Vector3> pos)
    {
        var parents = Skeleton.JointParents;
        var v = pos[joint];
        for (int a = parents[joint]; a >= 0; a = parents[a]) v = Vector3.Transform(v, rot[a]) + pos[a];
        return v;
    }

    /// <summary>Поворот сустава <paramref name="joint"/> в модели (−1 — тождество): <c>Q = Q родителя · r</c>.</summary>
    private Quaternion ModelRotation(int joint, ReadOnlySpan<Quaternion> rot)
    {
        var parents = Skeleton.JointParents;
        var q = Quaternion.Identity;
        for (int a = joint; a >= 0; a = parents[a]) q = rot[a] * q;
        return q;
    }

    /// <summary>
    /// Смешивание с памятью: итог — по кратчайшей дуге, кроме сустава, у которого она перескочила или итог её ещё догоняет:
    /// тот поворачивается не больше <see cref="MaxArcStep"/>. Память переписывается.
    /// </summary>
    private static void BlendFollowingArc(Span<Quaternion> aRot, Span<Vector3> aPos, ReadOnlySpan<Quaternion> bRot, ReadOnlySpan<Vector3> bPos,
        float w, ReadOnlySpan<float> mask, Span<Quaternion> output, Span<Quaternion> shortest, bool continuous)
    {
        for (int j = 0; j < aRot.Length; j++)
        {
            float k = w * mask[j];
            var target = k > 0 ? Quaternion.Slerp(aRot[j], bRot[j], k) : aRot[j];
            var result = target;
            if (continuous)
            {
                bool follow = output[j] != shortest[j];   // прошлый итог ещё догонял кратчайшую дугу
                if (!follow && k > 0 && k < 1)
                {
                    float near = MathF.Abs(Quaternion.Dot(target, shortest[j]));
                    if (near < ArcStepCos) follow = MathF.Abs(Quaternion.Dot(OtherArc(aRot[j], bRot[j], k), shortest[j])) > near;
                }
                if (follow) result = StepToward(output[j], target, MaxArcStep);
            }
            output[j] = result;
            shortest[j] = target;
            aRot[j] = result;
            if (k > 0) aPos[j] = Vector3.Lerp(aPos[j], bPos[j], k);
        }
    }

    /// <summary>Слерп <paramref name="a"/> → <paramref name="b"/> по длинной дуге (через −b); почти совпадающие — по кратчайшей.</summary>
    public static Quaternion OtherArc(Quaternion a, Quaternion b, float t)
    {
        float d = Quaternion.Dot(a, b);
        var c = d >= 0 ? -b : b;
        float omega = MathF.Acos(Math.Clamp(-MathF.Abs(d), -1, 1)), sin = MathF.Sin(omega);
        if (sin < 1e-4f) return Quaternion.Slerp(a, b, t);
        return Quaternion.Normalize(a * (MathF.Sin((1 - t) * omega) / sin) + c * (MathF.Sin(t * omega) / sin));
    }

    /// <summary>Поворот <paramref name="from"/> к <paramref name="to"/> не больше чем на <paramref name="maxAngle"/> рад; ближе — ровно <paramref name="to"/>.</summary>
    public static Quaternion StepToward(Quaternion from, Quaternion to, float maxAngle)
    {
        float angle = 2 * MathF.Acos(Math.Min(1, MathF.Abs(Quaternion.Dot(from, to))));
        return angle <= maxAngle ? to : Quaternion.Normalize(Quaternion.Slerp(from, to, maxAngle / angle));
    }
}
