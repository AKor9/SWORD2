using System.Numerics;
using Sword.Engine.Animation.Motion;
using static Sword.Engine.Animation.Tests.MotionPlayerFixtures;

namespace Sword.Engine.Animation.Tests;

/// <summary>Хранилище проигрывателей: хэндлы с поколением, свободные слоты, запас памяти, ближний кадр.</summary>
public sealed class MotionPlayersTests
{
    [Fact]
    public void Handles_GenerationsFreeSlotsAndStaleReads()
    {
        var lib = Library();
        var db = Database(lib);
        var players = new MotionPlayers(db, capacity: 1);
        Assert.Same(db, players.Database);
        Assert.Equal(lib.JointCount, players.JointCount);
        var a = players.Alloc();
        var b = players.Alloc();   // хранилище растёт удвоением
        Assert.Equal((0, 1), (a.Index, b.Index));
        Assert.Equal((2, 2), (players.Count, players.Capacity));
        var c = players.Alloc();
        Assert.Equal(2, c.Index);
        Assert.Equal(4, players.Capacity);
        Assert.Equal([3], players.FreeSlots.ToArray());

        players.Free(a);
        Assert.False(players.IsAlive(a));
        Assert.Equal(-1, players.GenerationAt(0));
        Assert.Equal(0, players.GenerationAt(3));   // ни разу не выдан
        Assert.Equal(MotionPlayerHandle.None, players.HandleAt(0));
        Assert.Equal(b, players.HandleAt(1));
        Assert.Equal(MotionPlayerHandle.None, players.HandleAt(4));
        Assert.Equal([3, 0], players.FreeSlots.ToArray());
        Assert.Throws<InvalidOperationException>(() => players.Free(a));
        Assert.Throws<InvalidOperationException>(() => players.State(a));
        Assert.Throws<InvalidOperationException>(() => players.NearestFrame(a));
        Assert.Throws<InvalidOperationException>(() => { players.RotationOffsets(a); });
        Assert.Throws<InvalidOperationException>(() => { players.OutputPositions(a); });

        var d = players.Alloc();   // последний свободный — освобождённый слот 0, новое поколение
        Assert.Equal(new MotionPlayerHandle(0, 2), d);
        Assert.False(players.IsAlive(a));
        Assert.False(players.IsAlive(MotionPlayerHandle.None));
        Assert.False(players.IsAlive(new MotionPlayerHandle(99, 1)));
        Assert.Equal(3, players.Count);
    }

    /// <summary>Новый проигрыватель — не начат: кадр −1, смещения и скорости нулевые, выход — поза привязки; слот после работы сбрасывается.</summary>
    [Fact]
    public void Alloc_NotStarted_ReusedSlotIsReset()
    {
        var rig = new PlayerRig(Database(Library()));
        rig.Step(0, Stop + 2);
        for (int t = 0; t < 5; t++) rig.Tick(t == 2 ? Idle + 7 : -1);
        Assert.NotEqual(0, rig.State.Transitions);
        rig.Players.Free(rig.Player);
        var p = rig.Players.Alloc();
        Assert.Equal(rig.Player.Index, p.Index);
        Assert.Equal(new MotionPlayerState(-1, 0, 0, false, Pose2.Identity), rig.Players.State(p));
        Assert.Equal(-1, rig.Players.NearestFrame(p));
        Assert.All(rig.Players.RotationOffsets(p).ToArray(), q => Assert.Equal(Quaternion.Identity, q));
        foreach (var span in new[]
        {
            rig.Players.RotationOffsetVelocities(p).ToArray(), rig.Players.PositionOffsets(p).ToArray(),
            rig.Players.PositionOffsetVelocities(p).ToArray(), rig.Players.AngularVelocities(p).ToArray(), rig.Players.LinearVelocities(p).ToArray(),
        })
            Assert.All(span, v => Assert.Equal(Vector3.Zero, v));
        var skeleton = rig.Database.Library.Skeleton;
        Assert.Equal(skeleton.ReferenceRot.ToArray(), rig.Players.OutputRotations(p).ToArray());
        Assert.Equal(skeleton.ReferencePos.ToArray(), rig.Players.OutputPositions(p).ToArray());
    }

    /// <summary>Как у пула поз: рост с запасом не выделяет, а ёмкость, номера, поколения и свободные слоты — те же, что без него.</summary>
    [Fact]
    public void Reserve_GrowthWithoutAllocations_SameLogicAsWithout()
    {
        var db = Database(Library());
        var plain = new MotionPlayers(db);
        var reserved = new MotionPlayers(db);
        reserved.Reserve(100);
        Assert.Equal(plain.Capacity, reserved.Capacity);
        Assert.Equal(128, reserved.Reserved);
        reserved.Reserve(10);   // меньше заведённого — ничего
        Assert.Equal(128, reserved.Reserved);
        var rng = new Random(7);
        var a = new List<MotionPlayerHandle>(128);
        var b = new List<MotionPlayerHandle>(128);   // списки теста не растут в замере
        long allocated = 0;
        for (int step = 0; step < 3000; step++)
        {
            bool alloc = a.Count == 0 || a.Count < 90 && rng.Next(3) > 0;
            int victim = alloc ? -1 : rng.Next(a.Count);
            if (alloc) a.Add(plain.Alloc());
            else
            {
                plain.Free(a[victim]);
                a.RemoveAt(victim);
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            if (alloc) b.Add(reserved.Alloc());
            else
            {
                reserved.Free(b[victim]);
                b.RemoveAt(victim);
            }
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            if (!alloc) continue;
            Assert.Equal(a[^1], b[^1]);
            Assert.Equal(plain.Capacity, reserved.Capacity);
            Assert.Equal(plain.FreeSlots.ToArray(), reserved.FreeSlots.ToArray());
        }
        Assert.Equal(128, reserved.Capacity);   // ёмкость росла удвоением, как без запаса
        Assert.Equal(0, allocated);
        for (int i = 0; i < plain.Capacity; i++) Assert.Equal(plain.GenerationAt(i), reserved.GenerationAt(i));
    }

    /// <summary>Ближний кадр: доля ≥ 0,5 — следующий кадр клипа; последний кадр цикла — начало цикла; конец нециклического — он сам.</summary>
    [Fact]
    public void NearestFrame_RoundsWithinClip_LoopLastIsStart()
    {
        var rig = new PlayerRig(Database(Library()));
        rig.Step(0, Walk + 5);
        Assert.Equal(Walk + 5, rig.Players.NearestFrame(rig.Player));
        rig.Step(0.4f);
        Assert.Equal((Walk + 5, 0.4f), (rig.State.Frame, rig.State.Alpha), new NearAlpha());
        Assert.Equal(Walk + 5, rig.Players.NearestFrame(rig.Player));
        rig.Step(0.2f);
        Assert.Equal(Walk + 6, rig.Players.NearestFrame(rig.Player));

        rig.Step(0, Walk + 23);
        rig.Step(1.2f);   // последний кадр цикла (24) с долей 0,2 — это начало цикла
        Assert.Equal(Walk + 24, rig.State.Frame);
        Assert.Equal(Walk, rig.Players.NearestFrame(rig.Player));
        rig.Step(0.4f);   // доля 0,6 от последнего — второй кадр (шов цикла)
        Assert.Equal(Walk + 1, rig.Players.NearestFrame(rig.Player));

        rig.Step(0, StopLast - 1);
        rig.Step(5);   // конец нециклического клипа без продолжения — стоит в последнем кадре
        Assert.Equal((StopLast, 0f), (rig.State.Frame, rig.State.Alpha));
        Assert.Equal(StopLast, rig.Players.NearestFrame(rig.Player));
    }

    private sealed class NearAlpha : IEqualityComparer<(int Frame, float Alpha)>
    {
        public bool Equals((int Frame, float Alpha) x, (int Frame, float Alpha) y) => x.Frame == y.Frame && MathF.Abs(x.Alpha - y.Alpha) < 1e-5f;
        public int GetHashCode((int Frame, float Alpha) obj) => obj.Frame;
    }
}
