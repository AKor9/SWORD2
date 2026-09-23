using System.Numerics;

namespace Sword.Engine.Physics;

/// <summary>
/// Эталонная реализация <see cref="IPhysicsWorld"/>: логика <c>RigidBody</c> SWORD 2eab13b за хэндлами, тела — массив
/// состояний по слотам (AoS). Оптимизированная раскладка (SoA, острова) — отдельной реализацией рядом, эта остаётся
/// оракулом в тестах (раздел 6 плана).
///
/// Слоты тел и сочленений — как у пула поз: поколение слота (у свободного — со знаком минус), список свободных, из
/// которого берут с конца; рост — удвоением, новые свободные кладутся так, чтобы первыми выдавались младшие номера.
/// Удалённое сочленение возвращает тела с конца, поэтому следующее того же размера получает те же слоты тем же порядком.
/// Массив тел сочленения остаётся за его слотом и переиспользуется — создание и удаление сочленений прежних размеров
/// памяти не выделяет.
/// </summary>
public sealed class PhysicsWorld : IPhysicsWorld
{
    private BodyState[] _bodies = [];
    private int[] _bodyGeneration = [], _bodyOwner = [], _freeBodies = [];
    private int _freeBodyCount, _bodyCapacity;

    private int[] _artGeneration = [], _artCount = [], _freeArts = [];
    private int[]?[] _artBodies = [];
    private int _freeArtCount, _artCapacity;

    /// <param name="articulations">Слотов сочленений на старте (растёт удвоением).</param>
    /// <param name="bodies">Слотов тел на старте (растёт удвоением).</param>
    public PhysicsWorld(int articulations = 16, int bodies = 256)
    {
        GrowArticulations(Math.Max(articulations, 1));
        GrowBodies(Math.Max(bodies, 1));
    }

    public int ArticulationCount => _artCapacity - _freeArtCount;
    public int LiveBodyCount => _bodyCapacity - _freeBodyCount;
    public int ArticulationCapacity => _artCapacity;
    public int BodyCapacity => _bodyCapacity;
    public ReadOnlySpan<int> FreeArticulations => _freeArts.AsSpan(0, _freeArtCount);
    public ReadOnlySpan<int> FreeBodies => _freeBodies.AsSpan(0, _freeBodyCount);

    public int ArticulationGenerationAt(int index) => _artGeneration[index];
    public int BodyGenerationAt(int index) => _bodyGeneration[index];

    public ArticulationHandle ArticulationAt(int index) =>
        (uint)index < (uint)_artCapacity && _artGeneration[index] > 0 ? new ArticulationHandle(index, _artGeneration[index]) : ArticulationHandle.None;

    public BodyHandle BodyAt(int index) =>
        (uint)index < (uint)_bodyCapacity && _bodyGeneration[index] > 0 ? new BodyHandle(index, _bodyGeneration[index]) : BodyHandle.None;

    public bool IsAlive(ArticulationHandle a) =>
        a.Generation > 0 && (uint)a.Index < (uint)_artCapacity && _artGeneration[a.Index] == a.Generation;

    public bool IsAlive(BodyHandle b) =>
        b.Generation > 0 && (uint)b.Index < (uint)_bodyCapacity && _bodyGeneration[b.Index] == b.Generation;

    public ArticulationHandle CreateArticulation(int bodies)
    {
        if (bodies < 1) throw new ArgumentOutOfRangeException(nameof(bodies), bodies, "в сочленении — от одного тела");
        if (_freeArtCount == 0) GrowArticulations(_artCapacity * 2);
        int a = _freeArts[--_freeArtCount];
        int gen = -_artGeneration[a] + 1;   // у свободного слота поколение хранится со знаком минус
        _artGeneration[a] = gen;
        var members = _artBodies[a];
        if (members is null || members.Length < bodies) _artBodies[a] = members = new int[bodies];
        _artCount[a] = bodies;
        for (int k = 0; k < bodies; k++)
        {
            if (_freeBodyCount == 0) GrowBodies(_bodyCapacity * 2);
            int b = _freeBodies[--_freeBodyCount];
            _bodyGeneration[b] = -_bodyGeneration[b] + 1;
            _bodyOwner[b] = a;
            _bodies[b] = BodyState.New(BodyMode.Kinematic);
            members[k] = b;
        }
        return new ArticulationHandle(a, gen);
    }

    public void DestroyArticulation(ArticulationHandle articulation)
    {
        Check(articulation);
        int a = articulation.Index;
        var members = _artBodies[a]!;
        // С конца: следующее сочленение того же размера возьмёт те же слоты тем же порядком.
        for (int k = _artCount[a] - 1; k >= 0; k--)
        {
            int b = members[k];
            _bodyGeneration[b] = -_bodyGeneration[b];
            _bodyOwner[b] = -1;
            _bodies[b] = default;
            _freeBodies[_freeBodyCount++] = b;
        }
        _artCount[a] = 0;
        _artGeneration[a] = -_artGeneration[a];
        _freeArts[_freeArtCount++] = a;
    }

    public int BodyCount(ArticulationHandle articulation)
    {
        Check(articulation);
        return _artCount[articulation.Index];
    }

    public BodyHandle Body(ArticulationHandle articulation, int index)
    {
        Check(articulation);
        if ((uint)index >= (uint)_artCount[articulation.Index])
            throw new ArgumentOutOfRangeException(nameof(index), index, $"в сочленении {articulation} тел {_artCount[articulation.Index]}");
        int b = _artBodies[articulation.Index]![index];
        return new BodyHandle(b, _bodyGeneration[b]);
    }

    public ArticulationHandle ArticulationOf(BodyHandle body)
    {
        Check(body);
        int a = _bodyOwner[body.Index];
        return new ArticulationHandle(a, _artGeneration[a]);
    }

    public BodyState Get(BodyHandle body)
    {
        Check(body);
        return _bodies[body.Index];
    }

    public void SetShape(BodyHandle body, float radius, float halfLength)
    {
        Check(body);
        if (!float.IsFinite(radius) || radius < 0) throw new ArgumentOutOfRangeException(nameof(radius), radius, "радиус — конечный, не меньше 0");
        if (!float.IsFinite(halfLength) || halfLength < 0)
            throw new ArgumentOutOfRangeException(nameof(halfLength), halfLength, "полудлина — конечная, не меньше 0");
        ref var s = ref _bodies[body.Index];
        s.Radius = radius;
        s.HalfLength = halfLength;
    }

    public void Place(BodyHandle body, Vector3 position, Quaternion rotation)
    {
        Check(body);
        ref var s = ref _bodies[body.Index];
        s.Position = s.PrevPosition = position;
        s.Rotation = s.PrevRotation = rotation;
        s.Velocity = s.AngularVelocity = Vector3.Zero;
    }

    public void SetKinematicTarget(BodyHandle body, Vector3 position, Quaternion rotation, float dt)
    {
        Check(body);
        if (!float.IsFinite(dt) || dt <= 0) throw new ArgumentOutOfRangeException(nameof(dt), dt, "шаг — конечный и больше 0");
        ref var s = ref _bodies[body.Index];
        if (s.Mode != BodyMode.Kinematic) throw new InvalidOperationException($"тело {body} не кинематическое ({s.Mode})");
        // Порядок и арифметика — кинематическая ветка Place в PlaceBodies SWORD.
        s.PrevPosition = s.Position;
        s.PrevRotation = s.Rotation;
        s.Velocity = (position - s.Position) / dt;
        s.AngularVelocity = PhysicsMath.RotationVector(s.Rotation, rotation) / dt;
        s.Position = position;
        s.Rotation = rotation;
    }

    private void Check(ArticulationHandle a)
    {
        if (!IsAlive(a)) throw new InvalidOperationException($"сочленение {a} не живо (слотов {_artCapacity})");
    }

    private void Check(BodyHandle b)
    {
        if (!IsAlive(b)) throw new InvalidOperationException($"тело {b} не живо (слотов {_bodyCapacity})");
    }

    private void GrowArticulations(int capacity)
    {
        int old = _artCapacity;
        Array.Resize(ref _artGeneration, capacity);
        Array.Resize(ref _artCount, capacity);
        Array.Resize(ref _artBodies, capacity);
        Array.Resize(ref _freeArts, capacity);
        // свободные — с конца списка, чтобы первыми выдавались младшие номера
        for (int i = capacity - 1; i >= old; i--) _freeArts[_freeArtCount++] = i;
        _artCapacity = capacity;
    }

    private void GrowBodies(int capacity)
    {
        int old = _bodyCapacity;
        Array.Resize(ref _bodies, capacity);
        Array.Resize(ref _bodyGeneration, capacity);
        Array.Resize(ref _bodyOwner, capacity);
        Array.Resize(ref _freeBodies, capacity);
        for (int i = old; i < capacity; i++) _bodyOwner[i] = -1;
        for (int i = capacity - 1; i >= old; i--) _freeBodies[_freeBodyCount++] = i;
        _bodyCapacity = capacity;
    }
}
