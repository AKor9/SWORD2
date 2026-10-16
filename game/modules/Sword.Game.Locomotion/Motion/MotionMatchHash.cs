using System.Numerics;
using Sword.Engine.Animation.Motion;
using Sword.Engine.Runtime;

namespace Sword.Game.Locomotion.Motion;

/// <summary>
/// Участник хеша <c>locomotion</c> (только при <c>locomotion.player = mm</c>): авторитетное состояние MM вне ECS —
/// хранилище проигрывателей (по слотам, затем порядок свободных) и истории корня живых проигрывателей. База в хеш не
/// входит — производная контента, кода и конфига; её отпечаток — в сведениях реплея. Компонент
/// <see cref="MotionMatchState"/> хеширует <c>ecs</c>. Память не выделяет.
/// </summary>
public sealed class MotionMatchHash(MotionMatchRuntime runtime) : IStateContributor
{
    public string Id => "locomotion";

    public void AppendHash(ref StateHasher h)
    {
        if (!runtime.Ready)
        {
            h.Add(-1);
            return;
        }
        var players = runtime.Players;
        int capacity = players.Capacity;
        h.Add(capacity);
        for (int i = 0; i < capacity; i++)
        {
            h.Add(players.GenerationAt(i));
            var player = players.HandleAt(i);
            if (!player.IsNone) AppendPlayer(ref h, player);
        }
        var free = players.FreeSlots;
        h.Add(free.Length);
        foreach (int slot in free) h.Add(slot);
    }

    /// <summary>Живой проигрыватель и его история (тело цикла по слотам — свой метод: правило OSR).</summary>
    private void AppendPlayer(ref StateHasher h, MotionPlayerHandle player)
    {
        var players = runtime.Players;
        var s = players.State(player);
        h.Add(s.Frame);
        h.Add(s.Alpha);
        h.Add(s.Transitions);
        h.Add(s.HasOutput);
        h.Add(s.RootStep.X);
        h.Add(s.RootStep.Z);
        h.Add(s.RootStep.Yaw);
        Add(ref h, players.RotationOffsets(player));
        Add(ref h, players.RotationOffsetVelocities(player));
        Add(ref h, players.PositionOffsets(player));
        Add(ref h, players.PositionOffsetVelocities(player));
        Add(ref h, players.AngularVelocities(player));
        Add(ref h, players.LinearVelocities(player));
        Add(ref h, players.OutputRotations(player));
        Add(ref h, players.OutputPositions(player));
        var history = runtime.HistoryAt(player.Index);
        int count = history?.Count ?? 0;
        h.Add(count);
        for (int k = 0; k < count; k++)
        {
            var r = history![k];
            h.Add(r.Tick);
            h.Add(r.Root.X);
            h.Add(r.Root.Z);
            h.Add(r.Root.Yaw);
        }
    }

    private static void Add(ref StateHasher h, ReadOnlySpan<Quaternion> values)
    {
        foreach (var q in values)
        {
            h.Add(q.X);
            h.Add(q.Y);
            h.Add(q.Z);
            h.Add(q.W);
        }
    }

    private static void Add(ref StateHasher h, ReadOnlySpan<Vector3> values)
    {
        foreach (var v in values)
        {
            h.Add(v.X);
            h.Add(v.Y);
            h.Add(v.Z);
        }
    }
}
