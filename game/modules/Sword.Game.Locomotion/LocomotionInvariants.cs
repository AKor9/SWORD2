using Sword.Engine.Animation;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Contracts;

namespace Sword.Game.Locomotion;

/// <summary>
/// <c>locomotion.poses</c> — позы слоя и снимка локомоции (<see cref="LegacyLocomotionState.Layer"/>,
/// <see cref="LegacyLocomotionState.From"/>) живые, свои у каждого бойца и не совпадают с его итоговой позой
/// (<see cref="PoseRef"/>). В конце тика: поза снятого состояния к этому времени возвращена. Пул модуль видит только
/// через вид <see cref="IAnimationView"/> (живая поза — непустые модельные матрицы). Позы ядра проверяет
/// <c>animation.owners</c>.
/// </summary>
public sealed class LocomotionPosesInvariant : IInvariant
{
    private ReadQuery? _query;
    private Entity[] _owner = new Entity[16];

    public string Id => "locomotion.poses";
    public InvariantCost Cost => InvariantCost.EveryTick;
    public InvariantScope Scope => InvariantScope.Entity;
    public string After => GameStages.End;

    public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
    {
        var world = ctx.World;
        var view = world.View<IAnimationView>();
        _query ??= world.Query().Read<LegacyLocomotionState>();
        Array.Clear(_owner);
        foreach (var chunk in world.Iterate(_query))
        {
            var entities = chunk.Entities;
            var states = chunk.Read<LegacyLocomotionState>();
            for (int i = 0; i < entities.Length; i++)
            {
                var e = entities[i];
                var s = states[i];
                if (s.Layer.IsNone && s.From.IsNone) continue;   // позы берёт первый тик локомоции
                var core = world.TryGet<PoseRef>(e, out var r) ? new PoseHandle(r.Index, r.Generation) : PoseHandle.None;
                Check(e, s.Layer, "слоя", core, view, ref violations);
                if (violations.Full) return;
                Check(e, s.From, "снимка", core, view, ref violations);
                if (violations.Full) return;
            }
        }
    }

    private void Check(Entity e, PoseHandle h, string what, PoseHandle core, IAnimationView view, ref ViolationSink violations)
    {
        if (h.IsNone || view.Model(new PoseRef { Index = h.Index, Generation = h.Generation }).IsEmpty)
        {
            violations.Report(e, $"поза {what} {h} не жива");
            return;
        }
        if (h == core)
        {
            violations.Report(e, $"поза {what} {h} — это итоговая поза бойца");
            return;
        }
        if (h.Index >= _owner.Length) Array.Resize(ref _owner, Math.Max(_owner.Length * 2, h.Index + 1));
        if (!_owner[h.Index].IsNull) violations.Report(e, $"поза {what} {h} — ещё и у {_owner[h.Index]}");
        else _owner[h.Index] = e;
    }
}
