using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Runtime;

namespace Sword.Game.Animation;

/// <summary>
/// Участник хеша состояния <c>animation</c> (раздел 7 плана): авторитетное состояние пула поз симуляции. По слотам
/// по возрастанию — поколение (у свободного — со знаком минус: от него зависит поколение следующей позы), у живой позы —
/// локальные повороты и смещения всех суставов и модельные матрицы; затем порядок свободных слотов (номера будущих поз)
/// и заданий в очереди (между тиками — 0). Ассеты (клипы, скелет) неизменяемы и в хеш не входят: их задают пак и
/// реплей. Состояния слоёв (локомоция) — компоненты, их хеширует участник <c>ecs</c>.
/// </summary>
public sealed class AnimationState(PosePool pool) : IStateContributor
{
    public string Id => "animation";

    public void AppendHash(ref StateHasher h)
    {
        int capacity = pool.Capacity;
        h.Add(capacity);
        for (int i = 0; i < capacity; i++)
        {
            h.Add(pool.GenerationAt(i));
            var pose = pool.HandleAt(i);
            if (!pose.IsNone) AppendPose(ref h, pose);
        }
        var free = pool.FreeSlots;
        h.Add(free.Length);
        foreach (int slot in free) h.Add(slot);
        h.Add(pool.Pending);
    }

    /// <summary>
    /// Сводка пула и позы по слотам (часть — <c>2v1</c>: суставы с локальным поворотом, смещением и модельной матрицей).
    /// Отбор: поза <c>2v1</c>, часть имени сустава (<c>foot</c>) или оба через двоеточие (<c>2v1:foot</c>).
    /// </summary>
    public void Describe(StateWriter w, in DescribeQuery q)
    {
        string? poseFilter = null, jointFilter = null;
        if (q.Filter is { Length: > 0 } f)
        {
            int colon = f.IndexOf(':');
            if (colon >= 0) (poseFilter, jointFilter) = (f[..colon], f[(colon + 1)..]);
            else if (f.Length > 1 && char.IsDigit(f[0]) && f.Contains('v')) poseFilter = f;
            else jointFilter = f;
        }
        w.Summary("capacity", pool.Capacity);
        // Запас памяти (на арену из конфига и прибавку врагов — GameCore.ReservePoses): слотов и мест очереди без выделений;
        // в хеш не входит.
        w.Summary("reserved", pool.Reserved);
        w.Summary("jobsReserved", pool.JobsReserved);
        w.Summary("poses", pool.Count);
        w.Summary("pending", pool.Pending);
        w.Summary("freeSlots", pool.FreeSlots.Length);
        var names = pool.Skeleton.JointNames;
        int matched = 0;
        for (int i = 0; i < pool.Capacity; i++)
        {
            var pose = pool.HandleAt(i);
            if (pose.IsNone || poseFilter is not null && pose.ToString() != poseFilter) continue;
            int at = matched++;
            if (at < q.Skip || at >= q.Skip + q.Take) continue;
            w.BeginItem(pose.ToString());
            var rot = pool.LocalRot(pose);
            var pos = pool.LocalPos(pose);
            var model = pool.Model(pose);
            for (int j = 0; j < rot.Length; j++)
            {
                if (jointFilter is not null && !names[j].Contains(jointFilter, StringComparison.OrdinalIgnoreCase)) continue;
                w.BeginObject(names[j]);
                Write(w, "rot", rot[j]);
                Write(w, "pos", pos[j]);
                Write(w, "model", model[j]);
                w.EndObject();
            }
            w.EndItem();
        }
        w.Matched = matched;
    }

    /// <summary>
    /// Живая поза: повороты, смещения, модельные матрицы. Тело цикла по слотам — свой метод: хеш каждый тик (реплей, агент)
    /// — долгий цикл (слоты × суставы) в методе раз в тик многоуровневый JIT заменял на ходу (OSR на главном потоке).
    /// Порядок листьев тот же — хеш не меняется.
    /// </summary>
    private void AppendPose(ref StateHasher h, PoseHandle pose)
    {
        foreach (var q in pool.LocalRot(pose))
        {
            h.Add(q.X);
            h.Add(q.Y);
            h.Add(q.Z);
            h.Add(q.W);
        }
        foreach (var p in pool.LocalPos(pose)) Add(ref h, p);
        foreach (ref readonly var m in pool.Model(pose)) Add(ref h, m);
    }

    private static void Add(ref StateHasher h, Vector3 v)
    {
        h.Add(v.X);
        h.Add(v.Y);
        h.Add(v.Z);
    }

    private static void Add(ref StateHasher h, in Matrix4x4 m)
    {
        h.Add(m.M11); h.Add(m.M12); h.Add(m.M13); h.Add(m.M14);
        h.Add(m.M21); h.Add(m.M22); h.Add(m.M23); h.Add(m.M24);
        h.Add(m.M31); h.Add(m.M32); h.Add(m.M33); h.Add(m.M34);
        h.Add(m.M41); h.Add(m.M42); h.Add(m.M43); h.Add(m.M44);
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

    private static void Write(StateWriter w, string name, in Matrix4x4 m)
    {
        w.BeginObject(name);
        w.Value("m11", m.M11); w.Value("m12", m.M12); w.Value("m13", m.M13); w.Value("m14", m.M14);
        w.Value("m21", m.M21); w.Value("m22", m.M22); w.Value("m23", m.M23); w.Value("m24", m.M24);
        w.Value("m31", m.M31); w.Value("m32", m.M32); w.Value("m33", m.M33); w.Value("m34", m.M34);
        w.Value("m41", m.M41); w.Value("m42", m.M42); w.Value("m43", m.M43); w.Value("m44", m.M44);
        w.EndObject();
    }
}
