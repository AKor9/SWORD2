using System.Globalization;
using System.Numerics;
using Sword.Engine.Animation.Motion;
using Sword.Engine.Runtime;

namespace Sword.Game.Locomotion.Motion;

/// <summary>
/// Участник хеша состояния <c>locomotion</c> (8е; только при <c>locomotion.player = mm</c>): авторитетное состояние Motion Matching вне ECS —
/// хранилище проигрывателей (по слотам по возрастанию: поколение, у живого — кадр, доля, переходов, есть ли выход, перенос корня последнего
/// шага и восемь массивов суставов: смещения поворота и позиции и их скорости, скорости выхода, выход прошлого шага; затем порядок свободных
/// слотов) и истории корня живых проигрывателей (записи по порядку: тик и корень). База в хеш не входит — производная контента, кода и
/// конфига; её отпечаток — в описании (<see cref="Describe"/>) и в строке старта журнала. Скалярное состояние бойца (компонент
/// <see cref="MotionMatchState"/>) хеширует участник <c>ecs</c>. Память не выделяет.
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

    /// <summary>
    /// Сводка хранилища и части: <c>base</c> — база (сводка, отпечаток, отбор, кадр позы постройки; в хеш не входит; время постройки — в
    /// строке старта журнала), проигрыватели <c>0v1</c> —
    /// состояние, история корня и суставы (смещения, скорости, выход). Отбор: проигрыватель <c>0v1</c>, часть имени сустава (<c>foot</c>) или
    /// оба через двоеточие (<c>0v1:foot</c>); <c>base</c> — только база.
    /// </summary>
    public void Describe(StateWriter w, in DescribeQuery q)
    {
        if (!runtime.Ready)
        {
            w.Summary("players", 0);
            return;
        }
        string? playerFilter = null, jointFilter = null;
        bool onlyBase = false;
        if (q.Filter is { Length: > 0 } f)
        {
            int colon = f.IndexOf(':');
            if (colon >= 0) (playerFilter, jointFilter) = (f[..colon], f[(colon + 1)..]);
            else if (f == "base") onlyBase = true;
            else if (f.Length > 1 && char.IsDigit(f[0]) && f.Contains('v')) playerFilter = f;
            else jointFilter = f;
        }
        var players = runtime.Players;
        w.Summary("players", players.Count);
        w.Summary("capacity", players.Capacity);
        w.Summary("reserved", players.Reserved);
        w.Summary("freeSlots", players.FreeSlots.Length);
        int matched = 0, skip = q.Skip, take = q.Take;
        bool OnPage()
        {
            int at = matched++;
            return at >= skip && at < skip + take;
        }
        if (playerFilter is null && jointFilter is null && OnPage()) DescribeBase(w);
        if (!onlyBase)
            for (int i = 0; i < players.Capacity; i++)
            {
                var player = players.HandleAt(i);
                if (player.IsNone || playerFilter is not null && player.ToString() != playerFilter || !OnPage()) continue;
                DescribePlayer(w, player, jointFilter);
            }
        w.Matched = matched;
    }

    private void DescribeBase(StateWriter w)
    {
        var db = runtime.Database;
        var s = db.Summary;
        w.BeginItem("base");
        w.Value("clips", s.Clips);
        w.Value("frames", s.Frames);
        w.Value("searchable", s.Searchable);
        w.Value("edges", s.Edges);
        w.Value("earlyExits", s.EarlyExits);
        w.Value("fingerprint", db.Fingerprint.ToString("X16", CultureInfo.InvariantCulture));
        w.Value("pattern", runtime.Base.Pattern);
        w.Value("startFrame", runtime.StartFrame);
        w.EndItem();
    }

    private void DescribePlayer(StateWriter w, MotionPlayerHandle player, string? jointFilter)
    {
        var players = runtime.Players;
        var db = runtime.Database;
        var s = players.State(player);
        w.BeginItem(player.ToString());
        w.Value("frame", s.Frame);
        if (s.Frame >= 0)
        {
            w.Value("clip", db.Library.Clips[db.Clips[db.ClipOf[s.Frame]].LibraryClip].Name);
            w.Value("clipFrame", db.FrameInClip(s.Frame));
        }
        w.Value("alpha", s.Alpha);
        w.Value("transitions", s.Transitions);
        w.Value("hasOutput", s.HasOutput);
        w.BeginObject("rootStep");
        w.Value("x", s.RootStep.X);
        w.Value("z", s.RootStep.Z);
        w.Value("yaw", s.RootStep.Yaw);
        w.EndObject();
        var history = runtime.HistoryAt(player.Index);
        w.BeginArray("history");
        for (int k = 0, n = history?.Count ?? 0; k < n; k++)
        {
            var r = history![k];
            w.BeginElement();
            w.Value("tick", r.Tick);
            w.Value("x", r.Root.X);
            w.Value("z", r.Root.Z);
            w.Value("yaw", r.Root.Yaw);
            w.EndObject();
        }
        w.EndArray();
        var names = db.Library.Skeleton.JointNames;
        var rotOffset = players.RotationOffsets(player);
        var rotOffsetVelocity = players.RotationOffsetVelocities(player);
        var posOffset = players.PositionOffsets(player);
        var posOffsetVelocity = players.PositionOffsetVelocities(player);
        var angular = players.AngularVelocities(player);
        var linear = players.LinearVelocities(player);
        var outRot = players.OutputRotations(player);
        var outPos = players.OutputPositions(player);
        for (int j = 0; j < names.Length; j++)
        {
            if (jointFilter is not null && !names[j].Contains(jointFilter, StringComparison.OrdinalIgnoreCase)) continue;
            w.BeginObject(names[j]);
            Write(w, "rotOffset", rotOffset[j]);
            Write(w, "rotOffsetVelocity", rotOffsetVelocity[j]);
            Write(w, "posOffset", posOffset[j]);
            Write(w, "posOffsetVelocity", posOffsetVelocity[j]);
            Write(w, "angularVelocity", angular[j]);
            Write(w, "linearVelocity", linear[j]);
            Write(w, "outRot", outRot[j]);
            Write(w, "outPos", outPos[j]);
            w.EndObject();
        }
        w.EndItem();
    }

    private static void Write(StateWriter w, string name, Quaternion q)
    {
        w.BeginObject(name);
        w.Value("x", q.X);
        w.Value("y", q.Y);
        w.Value("z", q.Z);
        w.Value("w", q.W);
        w.EndObject();
    }

    private static void Write(StateWriter w, string name, Vector3 v)
    {
        w.BeginObject(name);
        w.Value("x", v.X);
        w.Value("y", v.Y);
        w.Value("z", v.Z);
        w.EndObject();
    }
}
