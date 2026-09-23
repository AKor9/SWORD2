using System.Numerics;
using Sword.Engine.Client;
using Sword.Engine.Ecs;
using Sword.Game.Contracts;

namespace Sword.Game.Client;

/// <summary>
/// Боец в кадре: место на арене (<see cref="Slot"/>: игрок — 0, враги — 1…32; от него — цвет врага), корень, курс и
/// высота тела на рельефе — уже интерполированные по доле тика; поза последнего тика —
/// <see cref="PoseCount"/> модельных матриц с <see cref="PoseStart"/> в снимке кадра (<see cref="ArenaView.Pose"/>),
/// 0 — позы нет (рисуется поза привязки).
/// </summary>
public readonly record struct FighterView(Entity Entity, bool IsPlayer, Vector3 Root, float Facing, float Height,
    int PoseStart = 0, int PoseCount = 0, int Slot = 0)
{
    /// <summary>Мировая система скелета (<c>DuelArena.KinWorld</c> SWORD): поворот на курс, перенос в корень на высоте.</summary>
    public Matrix4x4 World
    {
        get
        {
            var m = Matrix4x4.CreateFromQuaternion(Quaternion.CreateFromAxisAngle(Vector3.UnitY, Facing));
            m.Translation = new Vector3(Root.X, Height, Root.Z);
            return m;
        }
    }
}

/// <summary>
/// Мир представления арены (Extract, раздел 7 плана): что рисует кадр. Бойцы появляются и уходят по экспорту тика
/// (<c>Added&lt;Fighter&gt;</c>/<c>Removed&lt;Fighter&gt;</c> из очереди кадра), их корни — из мира симуляции между
/// прошлым и последним тиком по доле тика (<c>SkinCutPose.DrawnJoints</c> SWORD: корень — линейно, курс — по короткой
/// дуге; высоты тела от постановки стоп ещё нет — высота рельефа под интерполированным корнем по функции рельефа, как
/// <c>DuelArena.KinWorld(root, facing)</c> с <c>Terrain.Height</c>). Рендер читает только этот мир.
/// </summary>
public sealed class ArenaView
{
    private readonly Func<float, float, float> _height;
    private readonly List<Entity> _fighters = [];
    private readonly List<FighterView> _views = [];
    /// <summary>Позы бойцов кадра подряд — снимок пула поз после тиков кадра (рендер читает только его).</summary>
    private Matrix4x4[] _poses = [];

    /// <param name="height">Высота рельефа в точке (x, z) — функция рельефа (<c>ArenaTerrain.Height</c>), не сетка рисования.</param>
    public ArenaView(Func<float, float, float> height) => _height = height;

    /// <summary>Бойцов, пропущенных кадром: у сущности нет корня или корня прошлого тика (ошибка симуляции).</summary>
    public int Skipped { get; private set; }

    /// <summary>Бойцы кадра.</summary>
    public IReadOnlyList<FighterView> Fighters => _views;

    /// <summary>Боец игрока в кадре; нет — до первого тика с его появлением.</summary>
    public FighterView? Player { get; private set; }

    /// <summary>
    /// Точка тени и кольца игрока на земле — показанный корень, как у Manny (иначе кольцо дрожит относительно бойца);
    /// нет игрока — null.
    /// </summary>
    public Vector3? PlayerMarker => Player is { } p ? p.Root with { Y = 0 } : null;

    /// <summary>Доля тика кадра.</summary>
    public float Alpha { get; private set; } = 1;

    /// <summary>Номер последнего тика, время симуляции на нём и шаг тика — снимок кадра для констант рендера.</summary>
    public ulong LastTick { get; private set; }
    public float Time { get; private set; }
    public float TickDt { get; private set; }

    /// <summary>
    /// Корень игрока в симуляции на последнем тике (не интерполированный) — итог Sword.exe, сверка с Headless; нет игрока
    /// в кадре — null.
    /// </summary>
    public Transform? PlayerAtTick { get; private set; }

    /// <summary>Мир представления кадра — всё, что читает рендер, снимается здесь; <c>BuildFrame</c> симуляцию не читает.</summary>
    public void Extract(SimulationLink simulation, float alpha)
    {
        var world = simulation.World;
        ApplyFighterRecords(simulation.Frame<Added<Fighter>>(), simulation.Frame<Removed<Fighter>>());
        // Итог сверяется с миром: взаимный порядок снятия и добавления в стадиях одного тика двумя очередями не передаётся,
        // а мир после тиков кадра — правда о том, кто сейчас боец. Циклом, не RemoveAll с лямбдой: кадр без выделений.
        for (int i = _fighters.Count - 1; i >= 0; i--)
            if (!world.Has<Fighter>(_fighters[i])) _fighters.RemoveAt(i);

        Alpha = alpha;
        LastTick = simulation.Tick > 0 ? simulation.Tick - 1 : 0;
        Time = simulation.Time;
        TickDt = simulation.Dt;
        Player = null;
        PlayerAtTick = null;
        Skipped = 0;
        _views.Clear();
        world.TryView<IAnimationView>(out var poses);
        int posed = 0;
        foreach (var e in _fighters)
        {
            if (!world.TryGet<Transform>(e, out var cur) || !world.TryGet<PrevTransform>(e, out var prev))
            {
                Skipped++;
                continue;
            }
            var view = Interpolate(e, world.Has<PlayerTag>(e), prev, cur, alpha, _height);
            if (world.TryGet<FighterSlot>(e, out var slot)) view = view with { Slot = slot.Index };
            // Поза — последнего тика, без интерполяции между тиками (как SkinnedCharacters SWORD): плавно идёт корень.
            if (poses is not null && world.TryGet<PoseRef>(e, out var poseRef) && poses.Model(poseRef) is { Length: > 0 } model)
            {
                if (_poses.Length < posed + model.Length) Array.Resize(ref _poses, Math.Max(_poses.Length * 2, posed + model.Length));
                model.CopyTo(_poses.AsSpan(posed));
                view = view with { PoseStart = posed, PoseCount = model.Length };
                posed += model.Length;
            }
            _views.Add(view);
            if (view.IsPlayer)
            {
                Player = view;
                PlayerAtTick = cur;
            }
        }
    }

    /// <summary>Поза бойца кадра — модельные матрицы суставов последнего тика; пусто — позы нет.</summary>
    public ReadOnlySpan<Matrix4x4> Pose(in FighterView fighter) => _poses.AsSpan(fighter.PoseStart, fighter.PoseCount);

    /// <summary>
    /// Появления и уходы бойцов — по тикам кадра по порядку, внутри тика уход раньше появления (нормализация барьера:
    /// снят и добавлен заново — <c>Removed</c>, затем <c>Added</c>).
    /// </summary>
    private void ApplyFighterRecords(ReadOnlySpan<(ulong Tick, Added<Fighter> Record)> added,
        ReadOnlySpan<(ulong Tick, Removed<Fighter> Record)> removed)
    {
        int a = 0, r = 0;
        while (a < added.Length || r < removed.Length)
        {
            ulong tick = Math.Min(a < added.Length ? added[a].Tick : ulong.MaxValue, r < removed.Length ? removed[r].Tick : ulong.MaxValue);
            for (; r < removed.Length && removed[r].Tick == tick; r++) _fighters.Remove(removed[r].Record.Entity);
            for (; a < added.Length && added[a].Tick == tick; a++)
                if (!_fighters.Contains(added[a].Record.Entity)) _fighters.Add(added[a].Record.Entity);
        }
    }

    /// <summary>Интерполяция корня между прошлым и последним тиком (<c>SkinCutPose.DrawnJoints</c> SWORD).</summary>
    public static FighterView Interpolate(Entity e, bool isPlayer, in PrevTransform prev, in Transform cur, float alpha,
        Func<float, float, float> height)
    {
        var root = Vector3.Lerp(prev.Position, cur.Position, alpha);
        float facing = prev.Facing + MathF.IEEERemainder(cur.Facing - prev.Facing, MathF.Tau) * alpha;
        return new FighterView(e, isPlayer, root, facing, height(root.X, root.Z));
    }
}
