using System.Numerics;

namespace Sword.Engine.Animation.Motion;

/// <summary>
/// Проигрыватели Motion Matching — хранилище состояний за хэндлами (слоты — как у <see cref="PosePool"/>), у каждой симуляции
/// своё, привязано к одной базе. Состояние авторитетное (хешируется): кадр и доля, переходы, смещения инерциализации и их
/// скорости, скорости и выход прошлого задания. Меняет его только задание <see cref="PosePool.Motion"/> — в общей очереди
/// по порядку; открытые методы — выдача, освобождение и чтение.
/// </summary>
public sealed class MotionPlayers
{
    private readonly int _jc;
    private int[] _generation = [];
    private int[] _free = [];
    private int _freeCount, _capacity;
    private MotionPlayerState[] _state = [];
    private Quaternion[] _rotOffset = [];
    private Vector3[] _rotOffsetVelocity = [];
    private Vector3[] _posOffset = [];
    private Vector3[] _posOffsetVelocity = [];
    private Vector3[] _angularVelocity = [];
    private Vector3[] _linearVelocity = [];
    private Quaternion[] _outputRot = [];
    private Vector3[] _outputPos = [];

    /// <param name="database">База, в кадрах которой живут проигрыватели (скелет — её библиотеки).</param>
    /// <param name="capacity">Начальная ёмкость (растёт удвоением).</param>
    public MotionPlayers(MotionDatabase database, int capacity = 1)
    {
        ArgumentNullException.ThrowIfNull(database);
        Database = database;
        _jc = database.Library.JointCount;
        Grow(Math.Max(capacity, 1));
    }

    public MotionDatabase Database { get; }

    /// <summary>Суставов у проигрывателя — суставов скелета базы.</summary>
    public int JointCount => _jc;

    public int Count => _capacity - _freeCount;

    /// <summary>Слотов (живых и свободных) — логическая ёмкость, растёт удвоением.</summary>
    public int Capacity => _capacity;

    /// <summary>Слотов, под которые уже есть память (<see cref="Reserve"/>); не меньше <see cref="Capacity"/>.</summary>
    public int Reserved => _generation.Length;

    /// <summary>Память заранее под <paramref name="players"/> проигрывателей — как <see cref="PosePool.Reserve"/>, логика та же.</summary>
    public void Reserve(int players)
    {
        int slots = Math.Max(_capacity, 1);
        while (slots < players) slots *= 2;
        if (slots > _generation.Length) Resize(slots);
    }

    private void Resize(int slots)
    {
        Array.Resize(ref _generation, slots);
        Array.Resize(ref _free, slots);
        Array.Resize(ref _state, slots);
        int joints = slots * _jc;
        Array.Resize(ref _rotOffset, joints);
        Array.Resize(ref _rotOffsetVelocity, joints);
        Array.Resize(ref _posOffset, joints);
        Array.Resize(ref _posOffsetVelocity, joints);
        Array.Resize(ref _angularVelocity, joints);
        Array.Resize(ref _linearVelocity, joints);
        Array.Resize(ref _outputRot, joints);
        Array.Resize(ref _outputPos, joints);
    }

    private void Grow(int capacity)
    {
        int old = _capacity;
        if (capacity > _generation.Length) Resize(capacity);   // в пределах запаса (Reserve) — без выделений
        // свободные — с конца списка, чтобы первыми выдавались младшие номера
        for (int i = capacity - 1; i >= old; i--) _free[_freeCount++] = i;
        _capacity = capacity;
    }

    /// <summary>Новый проигрыватель — не начат (кадр −1): первое задание с переходом ставит кадр без инерциализации; смещения и скорости нулевые.</summary>
    public MotionPlayerHandle Alloc()
    {
        if (_freeCount == 0) Grow(_capacity * 2);
        int i = _free[--_freeCount];
        int gen = -_generation[i] + 1;   // у свободного слота поколение хранится со знаком минус
        _generation[i] = gen;
        _state[i] = new MotionPlayerState(-1, 0, 0, false, Pose2.Identity);
        int o = i * _jc;
        _rotOffset.AsSpan(o, _jc).Fill(Quaternion.Identity);
        _rotOffsetVelocity.AsSpan(o, _jc).Clear();
        _posOffset.AsSpan(o, _jc).Clear();
        _posOffsetVelocity.AsSpan(o, _jc).Clear();
        _angularVelocity.AsSpan(o, _jc).Clear();
        _linearVelocity.AsSpan(o, _jc).Clear();
        var skeleton = Database.Library.Skeleton;
        skeleton.ReferenceRot.CopyTo(_outputRot.AsSpan(o, _jc));
        skeleton.ReferencePos.CopyTo(_outputPos.AsSpan(o, _jc));
        return new MotionPlayerHandle(i, gen);
    }

    /// <exception cref="InvalidOperationException">Проигрыватель уже освобождён или чужой.</exception>
    public void Free(MotionPlayerHandle player)
    {
        Check(player);
        _generation[player.Index] = -_generation[player.Index];
        _free[_freeCount++] = player.Index;
    }

    public bool IsAlive(MotionPlayerHandle player) =>
        player.Generation > 0 && (uint)player.Index < (uint)_capacity && _generation[player.Index] == player.Generation;

    /// <summary>Живой проигрыватель в слоте <paramref name="index"/>; свободен — <see cref="MotionPlayerHandle.None"/> (обход — стабильный порядок).</summary>
    public MotionPlayerHandle HandleAt(int index) =>
        (uint)index < (uint)_capacity && _generation[index] > 0 ? new MotionPlayerHandle(index, _generation[index]) : MotionPlayerHandle.None;

    /// <summary>Свободные слоты в порядке выдачи с конца — часть состояния (от него зависят номера будущих проигрывателей).</summary>
    public ReadOnlySpan<int> FreeSlots => _free.AsSpan(0, _freeCount);

    /// <summary>Поколение слота: у живого — его, у свободного — со знаком минус, у ни разу не выданного — 0.</summary>
    public int GenerationAt(int index) => _generation[index];

    private void Check(MotionPlayerHandle player)
    {
        if (!IsAlive(player)) throw new InvalidOperationException($"проигрыватель {player} не жив (хранилище из {_capacity} слотов)");
    }

    private ReadOnlySpan<T> Joints<T>(T[] data, MotionPlayerHandle player)
    {
        Check(player);
        return data.AsSpan(player.Index * _jc, _jc);
    }

    public MotionPlayerState State(MotionPlayerHandle player)
    {
        Check(player);
        return _state[player.Index];
    }

    /// <summary>Ближний к положению кадр — текущий для поиска: доля ≥ 0,5 — следующий кадр клипа (последний кадр цикла — его начало); не начат — −1.</summary>
    public int NearestFrame(MotionPlayerHandle player)
    {
        var s = State(player);
        if (s.Frame < 0) return -1;
        var db = Database;
        int frame = s.Frame, next = db.Next[frame];
        if (s.Alpha >= 0.5f && next >= 0 && db.ClipOf[next] == db.ClipOf[frame]) frame = next;
        var clip = db.Clips[db.ClipOf[frame]];
        return clip.Loop && frame == clip.End - 1 ? clip.Start : frame;   // последний кадр цикла = первый
    }

    /// <summary>[сустав] смещение поворота инерциализации (выход = смещение · поворот кадра).</summary>
    public ReadOnlySpan<Quaternion> RotationOffsets(MotionPlayerHandle player) => Joints(_rotOffset, player);

    /// <summary>[сустав] скорость смещения поворота — вектор поворота, рад/с.</summary>
    public ReadOnlySpan<Vector3> RotationOffsetVelocities(MotionPlayerHandle player) => Joints(_rotOffsetVelocity, player);

    /// <summary>[сустав] смещение позиции инерциализации, м (не ноль — только у движущихся суставов).</summary>
    public ReadOnlySpan<Vector3> PositionOffsets(MotionPlayerHandle player) => Joints(_posOffset, player);

    /// <summary>[сустав] скорость смещения позиции, м/с.</summary>
    public ReadOnlySpan<Vector3> PositionOffsetVelocities(MotionPlayerHandle player) => Joints(_posOffsetVelocity, player);

    /// <summary>[сустав] угловая скорость выхода (локальный поворот), рад/с.</summary>
    public ReadOnlySpan<Vector3> AngularVelocities(MotionPlayerHandle player) => Joints(_angularVelocity, player);

    /// <summary>[сустав] линейная скорость выхода (локальное смещение), м/с.</summary>
    public ReadOnlySpan<Vector3> LinearVelocities(MotionPlayerHandle player) => Joints(_linearVelocity, player);

    /// <summary>[сустав] локальные повороты выхода прошлого задания.</summary>
    public ReadOnlySpan<Quaternion> OutputRotations(MotionPlayerHandle player) => Joints(_outputRot, player);

    /// <summary>[сустав] локальные смещения выхода прошлого задания.</summary>
    public ReadOnlySpan<Vector3> OutputPositions(MotionPlayerHandle player) => Joints(_outputPos, player);

    /// <summary>Изменяемое состояние живого проигрывателя — только для задания пула; проверка «жив» — у вызывающего.</summary>
    internal Slot SlotAt(int index)
    {
        int o = index * _jc;
        return new Slot(ref _state[index], _rotOffset.AsSpan(o, _jc), _rotOffsetVelocity.AsSpan(o, _jc), _posOffset.AsSpan(o, _jc),
            _posOffsetVelocity.AsSpan(o, _jc), _angularVelocity.AsSpan(o, _jc), _linearVelocity.AsSpan(o, _jc), _outputRot.AsSpan(o, _jc),
            _outputPos.AsSpan(o, _jc));
    }

    /// <summary>Состояние одного проигрывателя для задания пула: скаляры по ссылке и данные суставов.</summary>
    internal readonly ref struct Slot
    {
        public readonly ref MotionPlayerState State;
        public readonly Span<Quaternion> RotOffset;
        public readonly Span<Vector3> RotOffsetVelocity;
        public readonly Span<Vector3> PosOffset;
        public readonly Span<Vector3> PosOffsetVelocity;
        public readonly Span<Vector3> AngularVelocity;
        public readonly Span<Vector3> LinearVelocity;
        public readonly Span<Quaternion> OutputRot;
        public readonly Span<Vector3> OutputPos;

        public Slot(ref MotionPlayerState state, Span<Quaternion> rotOffset, Span<Vector3> rotOffsetVelocity, Span<Vector3> posOffset,
            Span<Vector3> posOffsetVelocity, Span<Vector3> angularVelocity, Span<Vector3> linearVelocity, Span<Quaternion> outputRot,
            Span<Vector3> outputPos)
        {
            State = ref state;
            RotOffset = rotOffset;
            RotOffsetVelocity = rotOffsetVelocity;
            PosOffset = posOffset;
            PosOffsetVelocity = posOffsetVelocity;
            AngularVelocity = angularVelocity;
            LinearVelocity = linearVelocity;
            OutputRot = outputRot;
            OutputPos = outputPos;
        }
    }
}
