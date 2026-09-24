using System.Numerics;

namespace Sword.Engine.Animation;

/// <summary>Поза в пуле: номер слота и поколение. Поколение 0 — «нет позы»; освобождённый слот меняет поколение.</summary>
public readonly record struct PoseHandle(int Index, int Generation)
{
    public static readonly PoseHandle None = default;
    public bool IsNone => Generation == 0;
    public override string ToString() => IsNone ? "нет" : $"{Index}v{Generation}";
}

/// <summary>
/// Маска суставов, зарегистрированная в пуле (<see cref="PosePool.AddMask"/>): вес каждого сустава скелета 0…1 для
/// <see cref="PosePool.BlendMasked"/> (слой верха тела: ноги и таз — 0, позвоночник нарастает, корпус, руки и голова — 1).
/// </summary>
public readonly record struct PoseMask(int Index)
{
    public override string ToString() => $"маска {Index}";
}

/// <summary>
/// Память смешивания по маске (<see cref="PosePool.BlendMasked"/>): две позы — итог прошлого тика (<see cref="Output"/>) и итог по
/// кратчайшей дуге прошлого тика (<see cref="Shortest"/>); <see cref="Continuous"/> — в них прошлый тик того же смешивания
/// (иначе — только записать).
/// </summary>
public readonly record struct BlendMemory(PoseHandle Output, PoseHandle Shortest, bool Continuous)
{
    public bool IsNone => Output.IsNone || Shortest.IsNone;
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
/// Позы меняются только заданиями: системы тика ставят <see cref="Sample"/>, <see cref="Blend"/>, <see cref="BlendMasked"/>,
/// <see cref="AddMasked"/>, <see cref="Copy"/>, <see cref="ToModel"/> в очередь (Push), <see cref="Tick"/> исполняет всю очередь пачкой в порядке постановки (стадия
/// Animation), дальше позы читают (Pull, клиент). Порядок заданий одной позы сохраняется, поэтому итог совпадает
/// с последовательными вызовами <c>SkeletalClips</c> SWORD. Задание к позе, освобождённой до <see cref="Tick"/>,
/// пропускается (<see cref="PoseTickStats.Stale"/>). Установившийся тик не выделяет памяти.
/// </summary>
public sealed class PosePool
{
    private enum Op : byte { Sample, Blend, BlendMasked, AddMasked, Copy, ToModel }

    private struct Job
    {
        public Op Op;
        public PoseHandle Dst, Src;
        public PoseHandle Ref;   // добавка по маске: поза, от которой считается отклонение
        public int Clip;      // клип сэмпла или маска смешивания (добавки) по маске
        public float Value;   // время клипа или вес смешивания
        public float Stand;   // смешивание по маске: доля клипа в суставах вне маски
        public BlendMemory Memory;   // смешивание по маске: память (None — без неё)
    }

    /// <summary>
    /// Смешивание по маске с памятью: наибольший поворот сустава за задание, пока итог догоняет кратчайшую дугу после её
    /// перескока, рад (8° — 960°/с при 120 Гц: таз, перескочивший на 180°, доворачивается за 0,19 с, на 24° — за 3 тика).
    /// </summary>
    public const float MaxArcStep = 8 * MathF.PI / 180;

    /// <summary>Косинус половины <see cref="MaxArcStep"/>: итог дальше от прошлого — поворот больше шага.</summary>
    public static readonly float ArcStepCos = MathF.Cos(MaxArcStep / 2);

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
    private readonly List<float[]> _masks = [];
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

    /// <summary>Живых поз.</summary>
    public int Count => _capacity - _freeCount;

    /// <summary>Заданий в очереди до следующего <see cref="Tick"/>.</summary>
    public int Pending => _jobCount;

    /// <summary>Итог последнего <see cref="Tick"/>.</summary>
    public PoseTickStats LastTick { get; private set; }

    /// <summary>Слотов в пуле (живых и свободных) — логическая ёмкость: растёт удвоением, память под неё может быть заведена заранее (<see cref="Reserve"/>).</summary>
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

    /// <summary>Слотов, под которые уже есть память (<see cref="Reserve"/>); не меньше <see cref="Capacity"/>.</summary>
    public int Reserved => _generation.Length;

    /// <summary>Мест в очереди заданий без роста.</summary>
    public int JobsReserved => _jobs.Length;

    /// <summary>
    /// Память заранее (6ж): массивы — под <paramref name="poses"/> живых поз (с округлением до ёмкости, до которой пул дорастёт удвоением:
    /// 363 позы — 512 слотов) и <paramref name="jobs"/> заданий тика, чтобы рост пула посреди игры (действия, снимки, вздрагивания толпы)
    /// не выделял памяти. Логика пула не меняется: <see cref="Capacity"/>, номера будущих поз, порядок свободных слотов и поколения — те же,
    /// что без запаса (растут удвоением, как раньше), поэтому и хеш состояния тот же; удвоение в пределах запаса только открывает слоты.
    /// Меньше уже заведённого — ничего.
    /// </summary>
    public void Reserve(int poses, int jobs = 0)
    {
        int slots = Math.Max(_capacity, 1);
        while (slots < poses) slots *= 2;
        if (slots > _generation.Length) Resize(slots);
        if (jobs > _jobs.Length) Array.Resize(ref _jobs, jobs);
    }

    private void Resize(int slots)
    {
        Array.Resize(ref _rot, slots * _jc);
        Array.Resize(ref _pos, slots * _jc);
        Array.Resize(ref _model, slots * _jc);
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

    /// <summary>
    /// Маска суставов для <see cref="BlendMasked"/>: вес каждого сустава скелета, 0…1 (копируется). Маски регистрируются при
    /// настройке симуляции, номер — порядок регистрации.
    /// </summary>
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

    /// <summary>Масок в пуле.</summary>
    public int MaskCount => _masks.Count;

    /// <summary>Веса маски по суставам.</summary>
    public ReadOnlySpan<float> Mask(PoseMask mask) => _masks[mask.Index];

    /// <summary>
    /// Задание: смешать <paramref name="src"/> в <paramref name="dst"/> по маске — вес сустава j равен
    /// <c>weight·(mask[j] + (1 − mask[j])·stand)</c> (<c>CharacterRig.LayerMask</c> и <c>SkeletalClips.Blend</c> с маской SWORD
    /// 2eab13b: слой верха тела, <paramref name="stand"/> — доля клипа в суставах вне маски: стоя — 1, на ходу — 0). Дуга —
    /// кратчайшая (<c>Quaternion.Slerp</c>, как SWORD).
    ///
    /// С памятью (<paramref name="memory"/>, 6в): кратчайшая дуга перескакивает на другую сторону, когда повороты сустава в двух
    /// позах расходятся через 180° при частичном весе k (таз укола пака на полушаге: скачок min(k, 1 − k)·360° за тик). Перескок узнаётся
    /// по тому, что итог по другой дуге ближе к прошлому итогу по кратчайшей, чем новый; тогда (и пока итог не догонит кратчайшую
    /// дугу) итог сустава поворачивается к ней не больше чем на <see cref="MaxArcStep"/> за задание. Без перескока итог — побитово
    /// как без памяти (SWORD); быстрое, но непрерывное движение (вход действия) не ограничивается.
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
    /// Память смешивания ещё догоняет кратчайшую дугу (6ж): после последнего исполненного смешивания с ней итог хоть одного сустава не
    /// равен итогу по кратчайшей дуге. Владелец смешивания продолжает его с весом 0, пока догон не кончится — иначе конец смешивания
    /// (переход от снимка погас) вернул бы сустав на кратчайшую дугу скачком.
    /// </summary>
    public bool Following(BlendMemory memory)
    {
        if (memory.IsNone || !IsAlive(memory.Output) || !IsAlive(memory.Shortest)) return false;
        return Differ(_rot.AsSpan(memory.Output.Index * _jc, _jc), _rot.AsSpan(memory.Shortest.Index * _jc, _jc));
    }

    /// <summary>Повороты хоть одного сустава различаются (оператором, без упаковки — <c>SequenceEqual</c> в Debug упаковывал Quaternion).</summary>
    private static bool Differ(ReadOnlySpan<Quaternion> a, ReadOnlySpan<Quaternion> b)
    {
        for (int j = 0; j < a.Length; j++)
            if (a[j] != b[j]) return true;
        return false;
    }

    /// <summary>
    /// Задание (6г): добавка поворотов по маске — к позе <paramref name="dst"/> отклонение позы <paramref name="pose"/> от
    /// <paramref name="reference"/> с весом сустава <c>mask[j]·weight</c>: <c>rot[j] = normalize(rot[j]·slerp(identity,
    /// conj(ref[j])·pose[j], k))</c>; сустав с k ≤ 0 и смещения не трогаются (<c>CharacterRig.AddUpper</c> SWORD 2eab13b, побитово:
    /// вздрагивание игрока — первые кадры клипа реакции поверх любой позы). Вес не больше 0 — задание ничего не меняет.
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
                    Skeleton.ToModel(_rot.AsSpan(d, _jc), _pos.AsSpan(d, _jc), _model.AsSpan(d, _jc));
                    models++;
                    break;
            }
        }
        _jobCount = 0;
        return LastTick = new PoseTickStats(samples, blends, copies, models, stale);
    }

    /// <summary>
    /// Маска задания с долей <paramref name="stand"/>: <c>mask[j] + (1 − mask[j])·stand</c> — выражение
    /// <c>CharacterRig.LayerMask</c> SWORD (побитово). Цикл по суставам — свой метод: <see cref="Tick"/> зовётся раз в тик.
    /// </summary>
    private static void LayerMask(ReadOnlySpan<float> mask, float stand, Span<float> into)
    {
        for (int j = 0; j < mask.Length; j++) into[j] = mask[j] + (1 - mask[j]) * stand;
    }

    /// <summary>
    /// Добавка поворотов по маске (<see cref="AddMasked"/>, <c>CharacterRig.AddUpper</c> SWORD): выражения и порядок — SWORD. Цикл
    /// по суставам — свой метод: <see cref="Tick"/> зовётся раз в тик.
    /// </summary>
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

    /// <summary>
    /// <see cref="ClipLibrary.Blend"/> с маской и памятью (<see cref="BlendMasked"/>): итог сустава — по кратчайшей дуге, кроме
    /// сустава, у которого кратчайшая дуга перескочила или итог её ещё догоняет (<paramref name="output"/> прошлого тика не равен
    /// <paramref name="shortest"/>): тот поворачивается к ней не больше чем на <see cref="MaxArcStep"/>. Память переписывается.
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

    /// <summary>
    /// Сферическое смешивание <paramref name="a"/> → <paramref name="b"/> по другой (длинной) дуге — через −b, если кратчайшая идёт
    /// к b; повороты почти совпадают (длинная дуга — полный оборот) — кратчайшая.
    /// </summary>
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
