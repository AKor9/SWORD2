using System.Globalization;
using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Ecs;
using Sword.Engine.Physics;
using Sword.Engine.Runtime;
using Sword.Game.Bodies;
using Sword.Game.Contracts;

namespace Sword.Game.Ragdoll;

/// <summary>
/// Привязки скина мёртвых бойцов — данные симуляции вне ECS по слоту сочленения: <c>binding[j] = модельная[j] · мир бойца ·
/// (тело сегмента)⁻¹</c> в момент смерти, поэтому <c>binding[j] · тело</c> — сустав, куда бы тело ни ушло. Снимается один раз на
/// смерть, сбрасывается со снятием метки; снимок памяти не выделяет. Хеширует участник <c>skin</c>.
/// </summary>
public sealed class SkinBindings
{
    private readonly int[] _segments;
    private Matrix4x4[] _binding = [];
    /// <summary>Привязки кусков: слот × <see cref="Severance.MaxPieces"/> × суставы — сустав относительно тела куска.</summary>
    private Matrix4x4[] _ghost = [];
    private Entity[] _owner = [];
    private int[] _generation = [];
    private float[] _time = [];
    /// <summary>Сегменты, чья привязка снята при отделении у живого (смерть их не переснимает).</summary>
    private ushort[] _detached = [];

    /// <param name="jointSegments">Сегмент каждого сустава скелета (<see cref="JointSegments"/>).</param>
    /// <param name="slots">Слотов сочленений на старте (арена — 33 бойца).</param>
    public SkinBindings(int[] jointSegments, int slots)
    {
        _segments = jointSegments;
        Grow(slots);
    }

    /// <summary>Суставов скелета — матриц в привязке.</summary>
    public int JointCount => _segments.Length;

    /// <summary>Слотов привязок (по слотам сочленений).</summary>
    public int Capacity => _generation.Length;

    /// <summary>Сегмент тела сустава <paramref name="joint"/>.</summary>
    public int Segment(int joint) => _segments[joint];

    private void Grow(int slots)
    {
        if (slots <= _generation.Length) return;
        int n = Math.Max(slots, _generation.Length * 2);
        Array.Resize(ref _binding, n * _segments.Length);
        Array.Resize(ref _ghost, n * Severance.MaxPieces * _segments.Length);
        Array.Resize(ref _owner, n);
        Array.Resize(ref _generation, n);
        Array.Resize(ref _time, n);
        Array.Resize(ref _detached, n);
    }

    /// <summary>Привязка снята для этой смерти: слот тела, поколение, владелец и время смерти — те же.</summary>
    public bool Has(Entity owner, ArticulationHandle body, float time) =>
        (uint)body.Index < (uint)_generation.Length && _generation[body.Index] == body.Generation && _owner[body.Index] == owner
        && _time[body.Index].Equals(time);

    /// <summary>
    /// Снимок привязки (тик смерти) по телам <paramref name="body"/> и позе <paramref name="model"/> в мире бойца
    /// <paramref name="fighterWorld"/>. Сегменты, отделённые при жизни (их привязка снята при отделении), не переснимаются.
    /// </summary>
    public void Capture(IPhysicsWorld physics, Entity owner, ArticulationHandle body, ReadOnlySpan<Matrix4x4> model, in Matrix4x4 fighterWorld,
        float time)
    {
        if (model.Length < _segments.Length) throw new ArgumentException($"поза из {model.Length} суставов, а в скелете {_segments.Length}", nameof(model));
        Own(owner, body);
        ushort keep = _detached[body.Index];
        Span<Matrix4x4> inverse = stackalloc Matrix4x4[BodySegs.Bodies];
        for (int k = 0; k < BodySegs.Bodies; k++) inverse[k] = Inverse(physics.Get(physics.Body(body, k)));
        var dst = _binding.AsSpan(body.Index * _segments.Length, _segments.Length);
        for (int j = 0; j < dst.Length; j++)
            if ((keep & (1 << _segments[j])) == 0) dst[j] = model[j] * fighterWorld * inverse[BodySegs.Body(_segments[j])];
        _time[body.Index] = time;
    }

    /// <summary>
    /// Отделение у живого: привязка суставов сегментов <paramref name="segments"/> к их телам по позе тика (дальше их рисуют тела);
    /// смерть их не переснимает.
    /// </summary>
    public void CaptureSegments(IPhysicsWorld physics, Entity owner, ArticulationHandle body, ReadOnlySpan<Matrix4x4> model,
        in Matrix4x4 fighterWorld, ushort segments)
    {
        Own(owner, body);
        var dst = _binding.AsSpan(body.Index * _segments.Length, _segments.Length);
        for (int j = 0; j < dst.Length; j++)
        {
            int s = _segments[j];
            if ((segments & (1 << s)) != 0) dst[j] = model[j] * fighterWorld * Inverse(physics.Get(physics.Body(body, BodySegs.Body(s))));
        }
        _detached[body.Index] |= segments;
    }

    /// <summary>
    /// Кусок <paramref name="piece"/> отрезан от сегмента <paramref name="segment"/>: суставы сегмента относительно тела куска. Живой —
    /// по позе тика; труп — из его привязки и тела сегмента до разреза (<paramref name="before"/>).
    /// </summary>
    public void CaptureGhost(IPhysicsWorld physics, Entity owner, ArticulationHandle body, int piece, int segment, ReadOnlySpan<Matrix4x4> model,
        in Matrix4x4 fighterWorld, bool corpse, in BodyState before)
    {
        Own(owner, body);
        var pieceInverse = Inverse(physics.Get(physics.Body(body, BodySegs.Bodies + piece)));
        var row = _binding.AsSpan(body.Index * _segments.Length, _segments.Length);
        var ghost = _ghost.AsSpan((body.Index * Severance.MaxPieces + piece) * _segments.Length, _segments.Length);
        var was = System(before);
        for (int j = 0; j < ghost.Length; j++)
        {
            if (_segments[j] != segment) continue;
            ghost[j] = corpse ? row[j] * was * pieceInverse : model[j] * fighterWorld * pieceInverse;
        }
    }

    /// <summary>
    /// Тело сегмента <paramref name="segment"/> трупа сдвинулось (культя): привязка его суставов — к новой системе тела, чтобы скин не
    /// сдвинулся (<c>binding · было · стало⁻¹</c>).
    /// </summary>
    public void Rebase(ArticulationHandle body, int segment, in BodyState before, in BodyState after)
    {
        if ((uint)body.Index >= (uint)_generation.Length || _generation[body.Index] != body.Generation) return;
        var row = _binding.AsSpan(body.Index * _segments.Length, _segments.Length);
        var move = System(before) * Inverse(after);
        for (int j = 0; j < row.Length; j++)
            if (_segments[j] == segment) row[j] *= move;
    }

    /// <summary>Слот за владельцем: чужой или другого поколения — сначала очистить.</summary>
    private void Own(Entity owner, ArticulationHandle body)
    {
        Grow(body.Index + 1);
        int slot = body.Index;
        if (_generation[slot] == body.Generation && _owner[slot] == owner) return;
        Clear(slot);
        _owner[slot] = owner;
        _generation[slot] = body.Generation;
        _time[slot] = float.NaN;
    }

    /// <summary>Обратная система тела (вектор слева): перенос на минус центр, затем обратный поворот.</summary>
    private static Matrix4x4 Inverse(in BodyState s) =>
        Matrix4x4.CreateTranslation(-s.Position) * Matrix4x4.CreateFromQuaternion(Quaternion.Conjugate(s.Rotation));

    /// <summary>Система тела (вектор слева): поворот, затем перенос в центр.</summary>
    private static Matrix4x4 System(in BodyState s)
    {
        var m = Matrix4x4.CreateFromQuaternion(s.Rotation);
        m.Translation = s.Position;
        return m;
    }

    /// <summary>Привязка тела: есть и снята для этого сочленения (поколение то же) — матрицы и время смерти (NaN — снята при отделении у живого).</summary>
    public bool TryGet(ArticulationHandle body, out ReadOnlySpan<Matrix4x4> binding, out float time)
    {
        binding = default;
        time = float.NaN;
        if (body.IsNone || (uint)body.Index >= (uint)_generation.Length || _generation[body.Index] != body.Generation) return false;
        binding = _binding.AsSpan(body.Index * _segments.Length, _segments.Length);
        time = _time[body.Index];
        return true;
    }

    /// <summary>Привязка суставов куска <paramref name="piece"/> к его телу (суставы сегмента, от которого он отрезан).</summary>
    public bool TryGetGhost(ArticulationHandle body, int piece, out ReadOnlySpan<Matrix4x4> ghost)
    {
        ghost = default;
        if (body.IsNone || (uint)body.Index >= (uint)_generation.Length || _generation[body.Index] != body.Generation
            || (uint)piece >= Severance.MaxPieces) return false;
        ghost = _ghost.AsSpan((body.Index * Severance.MaxPieces + piece) * _segments.Length, _segments.Length);
        return true;
    }

    /// <summary>Слот <paramref name="slot"/>: поколение сочленения (0 — привязки нет), владелец, время смерти.</summary>
    public (int Generation, Entity Owner, float Time) At(int slot) => (_generation[slot], _owner[slot], _time[slot]);

    /// <summary>Сегменты слота, снятые при отделении у живого.</summary>
    public ushort DetachedAt(int slot) => _detached[slot];

    /// <summary>Матрицы привязки слота <paramref name="slot"/> (нет привязки — пусто).</summary>
    public ReadOnlySpan<Matrix4x4> BindingAt(int slot) =>
        _generation[slot] == 0 ? default : _binding.AsSpan(slot * _segments.Length, _segments.Length);

    /// <summary>Привязки кусков слота подряд (нет привязки — пусто).</summary>
    public ReadOnlySpan<Matrix4x4> GhostsAt(int slot) =>
        _generation[slot] == 0 ? default : _ghost.AsSpan(slot * Severance.MaxPieces * _segments.Length, Severance.MaxPieces * _segments.Length);

    /// <summary>Сбросить привязки владельца <paramref name="owner"/> (метку смерти сняли или боец ушёл).</summary>
    public void Release(Entity owner)
    {
        for (int i = 0; i < _generation.Length; i++)
            if (_generation[i] != 0 && _owner[i] == owner) Clear(i);
    }

    private void Clear(int slot)
    {
        _generation[slot] = 0;
        _owner[slot] = Entity.Null;
        _time[slot] = 0;
        _detached[slot] = 0;
        _binding.AsSpan(slot * _segments.Length, _segments.Length).Clear();
        _ghost.AsSpan(slot * Severance.MaxPieces * _segments.Length, Severance.MaxPieces * _segments.Length).Clear();
    }
}

// <summary>Привязка скина для наблюдателей (<see cref="ISkinView"/>) — чтение <see cref="SkinBindings"/>.</summary>
public sealed class SkinView(SkinBindings skins) : ISkinView
{
    public int JointCount => skins.JointCount;

    public bool TryGet(BodyRef body, out ReadOnlySpan<Matrix4x4> binding, out float time) =>
        skins.TryGet(FighterBodies.Handle(body), out binding, out time);

    public bool TryGetGhost(BodyRef body, int piece, out ReadOnlySpan<Matrix4x4> ghost) =>
        skins.TryGetGhost(FighterBodies.Handle(body), piece, out ghost);
}

/// <summary>
/// Gameplay и Settle, реактивная по <c>Added&lt;Dead&gt;</c>: снимок привязки скина мёртвого (<see cref="SkinBindings.Capture"/>) по
/// позе и телам момента смерти (мёртвый замирает). Каждая смерть снимается один раз (<see cref="SkinBindings.Has"/>); ожившие, без
/// позы или тела — пропускаются. Памяти не выделяет.
/// </summary>
public sealed class SkinCaptureSystem(IPhysicsWorld physics, SkinBindings skins, bool gameplay) : IReactiveSystem<Added<Dead>>
{
    public void Setup(SystemSetup s)
    {
        s.Label(GameLabels.SkinCapture).Reads<Dead>().Reads<Transform>().Reads<PoseRef>().Reads<BodyRef>();
        // В Gameplay — смерти командой сцены этого тика и (уже снятые) попадания и кровопотеря прошлого; после постройки тел и клинка
        // (поставленный и убитый в одном тике — по телам постройки и позе прошлого тика: мёртвый не анимируется).
        if (gameplay) s.After(GameLabels.BladeSetup).Before(GameLabels.RootSteer).NextTickOk<Added<Dead>>();
    }

    public void React(in SystemContext ctx, ReadOnlySpan<Added<Dead>> added)
    {
        var world = ctx.World;
        var pool = world.Resource<PosePool>();
        foreach (var a in added)
        {
            var e = a.Entity;
            if (!world.IsAlive(e) || !world.TryGet<Dead>(e, out var dead) || !world.TryGet<BodyRef>(e, out var bodyRef)
                || !world.TryGet<PoseRef>(e, out var poseRef) || !world.TryGet<Transform>(e, out var root))
                continue;
            var body = FighterBodies.Handle(bodyRef);
            var pose = new PoseHandle(poseRef.Index, poseRef.Generation);
            if (!pool.IsAlive(pose) || !BodyPlacement.Placeable(physics, body) || skins.Has(e, body, dead.Time)) continue;
            skins.Capture(physics, e, body, pool.Model(pose), BodyPlacement.World(root.Position, root.Facing), dead.Time);
        }
    }
}

/// <summary>
/// End, реактивная по <c>Removed&lt;Dead&gt;</c>: метку смерти сняли — привязка скина сбрасывается; умер снова в том же тике —
/// привязка уже новая, её не трогать.
/// </summary>
public sealed class SkinReleaseSystem(SkinBindings skins) : IReactiveSystem<Removed<Dead>>
{
    public void Setup(SystemSetup s) => s.Label(GameLabels.SkinRelease).Reads<Dead>();

    public void React(in SystemContext ctx, ReadOnlySpan<Removed<Dead>> removed)
    {
        var world = ctx.World;
        foreach (var r in removed)
            if (!world.IsAlive(r.Entity) || !world.Has<Dead>(r.Entity)) skins.Release(r.Entity);
    }
}

/// <summary>
/// Участник хеша <c>skin</c>: привязки по слотам сочленений — поколение, владелец, время смерти и матрицы суставов.
/// </summary>
public sealed class SkinState(SkinBindings skins) : IStateContributor
{
    public string Id => "skin";

    public void AppendHash(ref StateHasher h)
    {
        h.Add(skins.JointCount);
        h.Add(skins.Capacity);
        for (int i = 0; i < skins.Capacity; i++) Slot(ref h, i);
    }

    /// <summary>Слот привязки (тело цикла по слотам — свой метод: матриц в слоте — по суставу).</summary>
    private void Slot(ref StateHasher h, int i)
    {
        var (generation, owner, time) = skins.At(i);
        h.Add(generation);
        if (generation == 0) return;
        var binding = skins.BindingAt(i);
        h.Add(owner.Index);
        h.Add(owner.Generation);
        h.Add(time);
        h.Add(skins.DetachedAt(i));
        Matrices(ref h, binding);
        Matrices(ref h, skins.GhostsAt(i));
    }

    private static void Matrices(ref StateHasher h, ReadOnlySpan<Matrix4x4> matrices)
    {
        foreach (var m in matrices)
        {
            h.Add(m.M11); h.Add(m.M12); h.Add(m.M13); h.Add(m.M14);
            h.Add(m.M21); h.Add(m.M22); h.Add(m.M23); h.Add(m.M24);
            h.Add(m.M31); h.Add(m.M32); h.Add(m.M33); h.Add(m.M34);
            h.Add(m.M41); h.Add(m.M42); h.Add(m.M43); h.Add(m.M44);
        }
    }
}

/// <summary>
/// Инвариант <c>ragdoll.skin</c> (в конце End): у мёртвого с годным телом есть привязка (время — смерти), у живого — нет; привязка
/// без мёртвого владельца — ошибка; матрицы конечны (проверяются один раз на привязку — после снимка не меняются).
/// </summary>
public sealed class SkinInvariant(IPhysicsWorld physics, SkinBindings skins) : IInvariant
{
    private ReadQuery? _dead;
    private (int Generation, Entity Owner, float Time)[] _finite = [];

    public string Id => "ragdoll.skin";
    public InvariantCost Cost => InvariantCost.EveryTick;
    public InvariantScope Scope => InvariantScope.Entity;
    public string After => GameStages.End;

    public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
    {
        var world = ctx.World;
        _dead ??= world.Query().With<Fighter>().Read<Dead>().Read<BodyRef>();
        foreach (var chunk in world.Iterate(_dead))
        {
            var entities = chunk.Entities;
            var dead = chunk.Read<Dead>();
            var bodies = chunk.Read<BodyRef>();
            for (int i = 0; i < entities.Length; i++)
            {
                var body = FighterBodies.Handle(bodies[i]);
                if (!BodyPlacement.Placeable(physics, body) || skins.Has(entities[i], body, dead[i].Time)) continue;
                violations.Report(entities[i], string.Create(CultureInfo.InvariantCulture,
                    $"мёртвый (с {dead[i].Time} с, {dead[i].Cause}) без привязки скина тела {body}"));
                if (violations.Full) return;
            }
        }
        if (_finite.Length < skins.Capacity) Array.Resize(ref _finite, skins.Capacity);
        for (int i = 0; i < skins.Capacity; i++)
        {
            if (Problem(world, i) is not { } problem) continue;
            violations.Report(problem);
            if (violations.Full) return;
        }
    }

    /// <summary>Что не так с привязкой слота <paramref name="slot"/> (тело цикла по слотам — свой метод); нет привязки или годна — null.</summary>
    private string? Problem(WorldReader world, int slot)
    {
        var (generation, owner, time) = skins.At(slot);
        if (generation == 0) return null;
        var binding = skins.BindingAt(slot);
        var body = new ArticulationHandle(slot, generation);
        if (!world.IsAlive(owner) || !world.TryGet<BodyRef>(owner, out var r) || FighterBodies.Handle(r) != body)
            return $"привязка скина тела {body} у {owner}, а он не боец с этим телом";
        if (world.TryGet<Dead>(owner, out var dead))
        {
            if (!dead.Time.Equals(time) && !float.IsNaN(time))
                return string.Create(CultureInfo.InvariantCulture, $"привязка скина {owner} снята в {time} с, а умер он в {dead.Time} с");
        }
        else if (!world.Has<Severance>(owner)) return $"привязка скина тела {body} у живого {owner} без отделённых частей";
        if (_finite[slot] == (generation, owner, time)) return null;   // эту привязку уже проверили: после снимка она не меняется
        foreach (var m in binding)
            if (!float.IsFinite(m.M11 + m.M12 + m.M13 + m.M21 + m.M22 + m.M23 + m.M31 + m.M32 + m.M33 + m.M41 + m.M42 + m.M43))
                return $"привязка скина {owner}: нечисловая матрица";
        _finite[slot] = (generation, owner, time);
        return null;
    }
}
