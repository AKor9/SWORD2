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
/// Привязки скина мёртвых бойцов (7ж; <c>CaptureSkin</c>, <c>JointRel</c> SWORD 2eab13b — идея): данные симуляции вне ECS (по матрице
/// на сустав — не компонент), по слоту сочленения тела бойца. Привязка сустава <c>j</c> — его мировая матрица по позе смерти в мире
/// бойца, умноженная на обратную систему тела его сегмента в момент смерти: <c>binding[j] = модельная[j] · мир бойца · (тело)⁻¹</c>,
/// поэтому <c>binding[j] · тело</c> — сустав, куда бы тело ни ушло (труп на физике, 7з; скин трупа, 7и). Запись — поколение
/// сочленения, владелец (сущность) и время смерти; снимается один раз на смерть (<see cref="Has"/>), сбрасывается, когда метку смерти
/// сняли (<see cref="Release"/>). Память — на слоты сочленений при сборке (33 бойца); рост — только при росте слотов физики. Снимок
/// не выделяет памяти. Состояние хеширует участник <c>skin</c> (<see cref="SkinState"/>).
/// </summary>
public sealed class SkinBindings
{
    private readonly int[] _segments;
    private Matrix4x4[] _binding = [];
    private Entity[] _owner = [];
    private int[] _generation = [];
    private float[] _time = [];

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
        Array.Resize(ref _owner, n);
        Array.Resize(ref _generation, n);
        Array.Resize(ref _time, n);
    }

    /// <summary>Привязка снята для этой смерти: слот тела, поколение, владелец и время смерти — те же.</summary>
    public bool Has(Entity owner, ArticulationHandle body, float time) =>
        (uint)body.Index < (uint)_generation.Length && _generation[body.Index] == body.Generation && _owner[body.Index] == owner
        && _time[body.Index].Equals(time);

    /// <summary>
    /// Снимок привязки (тик смерти): тела сочленения <paramref name="body"/> (все <see cref="BodySegs.Bodies"/>) и модельные матрицы
    /// позы <paramref name="model"/> в мире бойца <paramref name="fighterWorld"/> (<see cref="BodyPlacement.World"/>). Прежняя привязка
    /// слота заменяется.
    /// </summary>
    public void Capture(IPhysicsWorld physics, Entity owner, ArticulationHandle body, ReadOnlySpan<Matrix4x4> model, in Matrix4x4 fighterWorld,
        float time)
    {
        if (model.Length < _segments.Length) throw new ArgumentException($"поза из {model.Length} суставов, а в скелете {_segments.Length}", nameof(model));
        Grow(body.Index + 1);
        Span<Matrix4x4> inverse = stackalloc Matrix4x4[BodySegs.Bodies];
        for (int k = 0; k < BodySegs.Bodies; k++) inverse[k] = Inverse(physics.Get(physics.Body(body, k)));
        var dst = _binding.AsSpan(body.Index * _segments.Length, _segments.Length);
        for (int j = 0; j < dst.Length; j++) dst[j] = model[j] * fighterWorld * inverse[BodySegs.Body(_segments[j])];
        _owner[body.Index] = owner;
        _generation[body.Index] = body.Generation;
        _time[body.Index] = time;
    }

    /// <summary>Обратная система тела (вектор слева): перенос на минус центр, затем обратный поворот.</summary>
    private static Matrix4x4 Inverse(in BodyState s) =>
        Matrix4x4.CreateTranslation(-s.Position) * Matrix4x4.CreateFromQuaternion(Quaternion.Conjugate(s.Rotation));

    /// <summary>Привязка тела: есть и снята для этого сочленения (поколение то же) — матрицы и время смерти.</summary>
    public bool TryGet(ArticulationHandle body, out ReadOnlySpan<Matrix4x4> binding, out float time)
    {
        binding = default;
        time = float.NaN;
        if (body.IsNone || (uint)body.Index >= (uint)_generation.Length || _generation[body.Index] != body.Generation) return false;
        binding = _binding.AsSpan(body.Index * _segments.Length, _segments.Length);
        time = _time[body.Index];
        return true;
    }

    /// <summary>Слот <paramref name="slot"/>: поколение сочленения (0 — привязки нет), владелец, время смерти.</summary>
    public (int Generation, Entity Owner, float Time) At(int slot) => (_generation[slot], _owner[slot], _time[slot]);

    /// <summary>Матрицы привязки слота <paramref name="slot"/> (нет привязки — пусто).</summary>
    public ReadOnlySpan<Matrix4x4> BindingAt(int slot) =>
        _generation[slot] == 0 ? default : _binding.AsSpan(slot * _segments.Length, _segments.Length);

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
        _binding.AsSpan(slot * _segments.Length, _segments.Length).Clear();
    }
}

/// <summary>Привязка скина для наблюдателей (<see cref="ISkinView"/>) — чтение <see cref="SkinBindings"/>.</summary>
public sealed class SkinView(SkinBindings skins) : ISkinView
{
    public int JointCount => skins.JointCount;

    public bool TryGet(BodyRef body, out ReadOnlySpan<Matrix4x4> binding, out float time) =>
        skins.TryGet(FighterBodies.Handle(body), out binding, out time);
}

/// <summary>
/// Gameplay и Settle (<see cref="GameLabels.SkinCapture"/>; 7ж), реактивная по <c>Added&lt;Dead&gt;</c>: снимок привязки скина мёртвого
/// бойца (<see cref="SkinBindings.Capture"/>) по его позе и телам сейчас — момента смерти: мёртвый замирает (поза, корень и тела не
/// ставятся), поэтому в Gameplay у убитого командой сцены (Intent) — поза и тела прошлого тика, в Settle у убитого попаданием
/// (Resolve) или кровопотерей (Aftermath) — этого тика. Каждая смерть снимается один раз (<see cref="SkinBindings.Has"/>: регистрация
/// в Gameplay видит и смерти прошлого тика — их уже сняла регистрация в Settle). Уже ожившие, без живой позы или тела — пропускаются
/// (их ловят <c>ragdoll.skin</c>, <c>animation.*</c>, <c>physics.bodyRefs</c>). Памяти не выделяет.
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
/// End (<see cref="GameLabels.SkinRelease"/>; 7ж), реактивная по <c>Removed&lt;Dead&gt;</c>: метку смерти сняла постройка заново
/// (возрождение, сброс, команда сцены) или боец ушёл вместе с ней — привязка скина сбрасывается. Умер снова в том же тике (ожил в
/// Intent, убит в Resolve) — привязка уже новая, её не трогать.
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
/// Участник хеша состояния <c>skin</c> (7ж): привязки скина по слотам сочленений — поколение (0 — нет), у снятой — владелец (номер и
/// поколение сущности), время смерти и матрицы суставов (16 чисел на сустав). Привязка — данные симуляции вне ECS: по ней рисуется
/// труп (7и), и её можно сверить реплеем. <see cref="Describe"/> — те же поля: часть на привязку (<c>3v1</c> — сочленение), отбор — она.
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
        foreach (var m in binding)
        {
            h.Add(m.M11); h.Add(m.M12); h.Add(m.M13); h.Add(m.M14);
            h.Add(m.M21); h.Add(m.M22); h.Add(m.M23); h.Add(m.M24);
            h.Add(m.M31); h.Add(m.M32); h.Add(m.M33); h.Add(m.M34);
            h.Add(m.M41); h.Add(m.M42); h.Add(m.M43); h.Add(m.M44);
        }
    }

    public void Describe(StateWriter w, in DescribeQuery q)
    {
        string? filter = q.Filter is { Length: > 0 } f ? f : null;
        int captured = 0;
        for (int i = 0; i < skins.Capacity; i++)
            if (skins.At(i).Generation != 0) captured++;
        w.Summary("joints", skins.JointCount);
        w.Summary("slots", skins.Capacity);
        w.Summary("captured", captured);
        int matched = 0;
        for (int i = 0; i < skins.Capacity; i++)
        {
            var (generation, owner, time) = skins.At(i);
            if (generation == 0) continue;
            var binding = skins.BindingAt(i);
            string id = new ArticulationHandle(i, generation).ToString();
            if (filter is not null && id != filter) continue;
            int at = matched++;
            if (at < q.Skip || at >= q.Skip + q.Take) continue;
            w.BeginItem(id);
            w.Value("owner", owner.ToString());
            w.Value("time", time);
            for (int j = 0; j < binding.Length; j++)
            {
                var m = binding[j];
                w.BeginObject(j.ToString(CultureInfo.InvariantCulture));
                w.Value("segment", skins.Segment(j));
                Row(w, "row1", m.M11, m.M12, m.M13, m.M14);
                Row(w, "row2", m.M21, m.M22, m.M23, m.M24);
                Row(w, "row3", m.M31, m.M32, m.M33, m.M34);
                Row(w, "row4", m.M41, m.M42, m.M43, m.M44);
                w.EndObject();
            }
            w.EndItem();
        }
        w.Matched = matched;
    }

    private static void Row(StateWriter w, string name, float x, float y, float z, float t)
    {
        w.BeginObject(name);
        w.Value("x", x);
        w.Value("y", y);
        w.Value("z", z);
        w.Value("w", t);
        w.EndObject();
    }
}

/// <summary>
/// Инвариант <c>ragdoll.skin</c> (7ж; в конце End): у каждого мёртвого бойца с годным телом есть привязка скина его тела (снята в тик
/// смерти, время — смерти), у живого — нет; привязка без мёртвого владельца — ошибка; матрицы привязок конечны. Ловит пропущенный
/// снимок (новый путь смерти мимо <c>Added&lt;Dead&gt;</c>), несброшенную привязку ожившего, чужую привязку. Каждый тик: матрицы
/// после снимка не меняются, поэтому они проверяются один раз на привязку (память — слот, поколение, владелец, время), а каждый тик —
/// только владельцы: O(слотов) без матриц.
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
        if (!world.IsAlive(owner) || !world.TryGet<Dead>(owner, out var dead) || !world.TryGet<BodyRef>(owner, out var r)
            || FighterBodies.Handle(r) != body)
            return $"привязка скина тела {body} у {owner}, а он не мёртвый боец с этим телом";
        if (!dead.Time.Equals(time)) return string.Create(CultureInfo.InvariantCulture, $"привязка скина {owner} снята в {time} с, а умер он в {dead.Time} с");
        if (_finite[slot] == (generation, owner, time)) return null;   // эту привязку уже проверили: после снимка она не меняется
        foreach (var m in binding)
            if (!float.IsFinite(m.M11 + m.M12 + m.M13 + m.M21 + m.M22 + m.M23 + m.M31 + m.M32 + m.M33 + m.M41 + m.M42 + m.M43))
                return $"привязка скина {owner}: нечисловая матрица";
        _finite[slot] = (generation, owner, time);
        return null;
    }
}
