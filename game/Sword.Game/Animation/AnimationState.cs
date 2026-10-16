using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Runtime;

namespace Sword.Game.Animation;

/// <summary>
/// Участник хеша <c>animation</c>: пул поз — поколения слотов, у живой позы повороты, смещения и модельные матрицы, порядок
/// свободных слотов и заданий. Ассеты в хеш не входят; состояния слоёв — компоненты, их хеширует <c>ecs</c>.
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
    /// Живая поза: повороты, смещения, модельные матрицы. Тело цикла по слотам — свой метод (хеш каждый тик, иначе OSR на главном потоке).
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
}
