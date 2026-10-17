using System.Numerics;
using Sword.Engine.Animation.Motion;

namespace Sword.Engine.Animation;

// Исполнение задания проигрывателя Motion Matching. Вид задания, постановка в очередь (PosePool.Motion) и передача
// сюда — в PosePool.cs. Состояние проигрывателя — MotionPlayers (меняется только здесь).
public sealed partial class PosePool
{
    /// <summary>Предохранитель: интервалов графа за задание не больше; остаток продвижения отбрасывается (1e30 с не зацикливает тик).</summary>
    private const int MaxIntervalsPerStep = 1024;

    /// <summary>Предохранитель цепочки входов (кадр входа — сам кадр выхода): переходов по <see cref="MotionDatabase.Entry"/> подряд не больше.</summary>
    private const int MaxEntryChain = 16;

    /// <summary>
    /// Смещение и его скорость меньше 10⁻⁶ (квадраты длин) обнуляются: иначе хвост затухания уходит в денормализованные числа
    /// (медленная арифметика x86).
    /// </summary>
    private const float OffsetFlush = 1e-12f;

    /// <summary>
    /// Шаг проигрывателя и его выход — в позу <paramref name="dst"/>: продвижение по графу (на ребре — инерциализация в точке
    /// ребра, затухание делится по моменту ребра), затухание смещений, сэмпл кадра, скорости выхода; затем запрошенный переход
    /// (выход этого тика не меняется, разрыв берут смещения). Скорости — во времени игры (× темп <c>Advance / Dt</c>). На кадре
    /// выхода проигрыватель не стоит — всегда его вход.
    /// </summary>
    /// <returns>false — проигрыватель освобождён после постановки (задание пропущено).</returns>
    private partial bool RunMotion(in MotionJob job, int dst)
    {
        var players = job.Players;
        if (!players.IsAlive(job.Player)) return false;
        var p = players.SlotAt(job.Player.Index);
        var db = players.Database;
        var step = job.Step;
        var rot = _rot.AsSpan(dst, _jc);
        var pos = _pos.AsSpan(dst, _jc);
        float scale = db.SampleRate * (step.Advance / step.Dt);   // скорость клипа: за кадр → за секунду игры
        var s = p.State;
        if (s.Frame < 0)
        {
            if (step.Transition >= 0) StartPlayer(db, p, ResolveEntry(db, step.Transition), scale, rot, pos);
            return true;
        }

        int frame = s.Frame, transitions = s.Transitions;
        float alpha = s.Alpha;
        var root = Pose2.Identity;
        float decayed = AdvancePlayer(db, p, step, scale, ref frame, ref alpha, ref transitions, ref root);
        if (decayed < step.Dt) DecayOffsets(db, p, step.Halflife, step.Dt - decayed);
        SamplePlayer(db, p, frame, alpha, rot, pos);
        OutputVelocities(rot, pos, p.OutputRot, p.OutputPos, p.AngularVelocity, p.LinearVelocity, step.Dt);
        if (step.Transition >= 0)
        {
            frame = ResolveEntry(db, step.Transition);
            alpha = 0;
            InertializeRequested(db, p, frame, scale);
            transitions++;
        }
        p.State = new MotionPlayerState(frame, alpha, transitions, true, root);
        return true;
    }

    /// <summary>Первый переход не начатого проигрывателя: кадр без инерциализации (смещения нулевые), выход — кадр, скорости выхода — клипа в нём.</summary>
    private static void StartPlayer(MotionDatabase db, MotionPlayers.Slot p, int frame, float scale, Span<Quaternion> rot, Span<Vector3> pos)
    {
        SamplePlayer(db, p, frame, 0, rot, pos);
        FramePose(db, frame, out var r0, out var p0);
        int next = InClipNext(db, frame);
        if (next >= 0)
        {
            FramePose(db, next, out var r1, out var p1);
            ClipVelocities(r0, p0, r1, p1, scale, p.AngularVelocity, p.LinearVelocity);
        }
        else
        {
            p.AngularVelocity.Clear();
            p.LinearVelocity.Clear();
        }
        rot.CopyTo(p.OutputRot);
        pos.CopyTo(p.OutputPos);
        p.State = new MotionPlayerState(frame, 0, p.State.Transitions, true, Pose2.Identity);
    }

    /// <summary>
    /// Продвижение на <c>Advance</c> по графу: перенос корня — композиция долей <see cref="MotionDatabase.Interval"/>. Кадр выхода
    /// или ребро в другой клип — переход с инерциализацией; смещения до него затухают до момента ребра, иначе скорость нового
    /// смещения действовала бы и до ребра. Кадр без следующего — проигрыватель стоит в нём.
    /// </summary>
    /// <returns>Время тика, до которого смещения уже затухли (момент последнего ребра; без рёбер — 0).</returns>
    private static float AdvancePlayer(MotionDatabase db, MotionPlayers.Slot p, in MotionStep step, float scale, ref int frame, ref float alpha,
        ref int transitions, ref Pose2 root)
    {
        var next = db.Next;
        var interval = db.Interval;
        float total = step.Advance * db.SampleRate, frames = total, decayed = 0;
        for (int n = 0; frames > 0 && n < MaxIntervalsPerStep; n++)
        {
            int to = next[frame];
            if (to < 0)
            {
                alpha = 0;
                break;
            }
            var span = interval[frame];
            float end = alpha + frames;
            if (end < 1)
            {
                root = Pose2.Compose(root, Pose2.Relative(Pose2.Lerp(span, alpha), Pose2.Lerp(span, end)));
                alpha = end;
                break;
            }
            root = Pose2.Compose(root, Pose2.Relative(Pose2.Lerp(span, alpha), span));
            frames = end - 1;
            bool inClip = db.ClipOf[to] == db.ClipOf[frame];
            int arrived = ResolveEntry(db, to);
            if (!inClip || arrived != to)
            {
                float at = MathF.Min(step.Dt, step.Dt * ((total - frames) / total));   // момент ребра в тике
                if (at > decayed)
                {
                    DecayOffsets(db, p, step.Halflife, at - decayed);
                    decayed = at;
                }
                EdgeTransition(db, p, frame, to, inClip, arrived, scale);
                transitions++;
            }
            frame = arrived;
            alpha = 0;
        }
        return decayed;
    }

    /// <summary>
    /// Переход на ребре <paramref name="from"/> → <paramref name="to"/>: выход — смещение ∘ кадр <paramref name="to"/> (внутри клипа)
    /// или <paramref name="from"/> (в другой клип), скорость — смещения + клипа на подходе.
    /// </summary>
    private static void EdgeTransition(MotionDatabase db, MotionPlayers.Slot p, int from, int to, bool inClip, int dst, float scale)
    {
        FramePose(db, inClip ? to : from, out var srcRot, out var srcPos);
        ReadOnlySpan<Quaternion> prevRot = default;
        ReadOnlySpan<Vector3> prevPos = default;
        if (inClip) FramePose(db, from, out prevRot, out prevPos);
        FramePose(db, dst, out var dstRot, out var dstPos);
        ReadOnlySpan<Quaternion> nextRot = default;
        ReadOnlySpan<Vector3> nextPos = default;
        int next = InClipNext(db, dst);
        if (next >= 0) FramePose(db, next, out nextRot, out nextPos);
        EdgeRotations(srcRot, prevRot, dstRot, nextRot, scale, p.RotOffset, p.RotOffsetVelocity);
        EdgePositions(db.MovingJoints, srcPos, prevPos, dstPos, nextPos, scale, p.PosOffset, p.PosOffsetVelocity);
    }

    /// <summary>Запрошенный переход: смещение = выход ∘ сопряжённый кадр входа, скорость смещения = скорость выхода − скорость клипа во входе.</summary>
    private static void InertializeRequested(MotionDatabase db, MotionPlayers.Slot p, int dst, float scale)
    {
        FramePose(db, dst, out var dstRot, out var dstPos);
        ReadOnlySpan<Quaternion> nextRot = default;
        ReadOnlySpan<Vector3> nextPos = default;
        int next = InClipNext(db, dst);
        if (next >= 0) FramePose(db, next, out nextRot, out nextPos);
        RequestedRotations(p.OutputRot, p.AngularVelocity, dstRot, nextRot, scale, p.RotOffset, p.RotOffsetVelocity);
        RequestedPositions(db.MovingJoints, p.OutputPos, p.LinearVelocity, dstPos, nextPos, scale, p.PosOffset, p.PosOffsetVelocity);
    }

    /// <summary>Затухание смещений за <paramref name="dt"/> — выражения <see cref="AnimMath.DecaySpring(ref Quaternion, ref Vector3, float, float)"/>, коэффициенты раз на задание; малый хвост обнуляется.</summary>
    private static void DecayOffsets(MotionDatabase db, MotionPlayers.Slot p, float halflife, float dt)
    {
        float y = AnimMath.HalflifeToDamping(halflife) * 0.5f;
        float e = MathF.Exp(-y * dt);
        DecayRotations(p.RotOffset, p.RotOffsetVelocity, y, e, dt);
        DecayPositions(db.MovingJoints, p.PosOffset, p.PosOffsetVelocity, y, e, dt);
    }

    /// <summary>
    /// Выход проигрывателя: кадр с долей (только внутри клипа; сплайн по соседним кадрам клипа, как <see cref="ClipLibrary.Sample"/>),
    /// повороты — смещение · кадр, позиции движущихся суставов — кадр + смещение.
    /// </summary>
    private static void SamplePlayer(MotionDatabase db, MotionPlayers.Slot p, int frame, float alpha, Span<Quaternion> rot, Span<Vector3> pos)
    {
        FramePose(db, frame, out var r0, out var p0);
        int next = alpha > 0 ? InClipNext(db, frame) : -1;
        if (next >= 0)
        {
            FramePose(db, next, out var r1, out var p1);
            FramePose(db, InClipPrevious(db, frame), out var ra, out var pa);
            int after = InClipNext(db, next);
            FramePose(db, after >= 0 ? after : next, out var rd, out var pd);
            ClipLibrary.SampleKeys(ra, r0, r1, rd, pa, p0, p1, pd, alpha, rot, pos);
            ApplyOffsets(p.RotOffset, rot);
        }
        else SampleAt(r0, p0, p.RotOffset, rot, pos);
        AddPositionOffsets(db.MovingJoints, p.PosOffset, pos);
    }

    /// <summary>Прошлый кадр клипа: у начала цикла — предпоследний (последний кадр цикла = первый), у начала клипа — сам кадр.</summary>
    private static int InClipPrevious(MotionDatabase db, int frame)
    {
        var clip = db.Clips[db.ClipOf[frame]];
        if (frame > clip.Start) return frame - 1;
        return clip.Loop && clip.Count > 2 ? clip.End - 2 : frame;
    }

    /// <summary>Кадр входа, которым «становится» кадр выхода (цепочка — с предохранителем); не выход — сам кадр.</summary>
    private static int ResolveEntry(MotionDatabase db, int frame)
    {
        var entry = db.Entry;
        for (int n = 0; n < MaxEntryChain && entry[frame] >= 0; n++) frame = entry[frame];
        return frame;
    }

    /// <summary>Следующий кадр графа, если он в том же клипе (сэмпл с долей и скорость клипа — только внутри клипа); иначе −1.</summary>
    private static int InClipNext(MotionDatabase db, int frame)
    {
        int next = db.Next[frame];
        return next >= 0 && db.ClipOf[next] == db.ClipOf[frame] ? next : -1;
    }

    private static void FramePose(MotionDatabase db, int frame, out ReadOnlySpan<Quaternion> rot, out ReadOnlySpan<Vector3> pos)
    {
        var lib = db.Library;
        int jc = lib.JointCount, o = db.LibraryFrame(frame) * jc;
        rot = lib.LocalRot.Slice(o, jc);
        pos = lib.LocalPos.Slice(o, jc);
    }

    // Циклы по суставам — свои методы (правило OSR): задание зовётся каждый тик.

    private static void ApplyOffsets(ReadOnlySpan<Quaternion> offset, Span<Quaternion> rot)
    {
        for (int j = 0; j < rot.Length; j++)
        {
            var o = offset[j];
            if (o != Quaternion.Identity) rot[j] = Quaternion.Normalize(o * rot[j]);
        }
    }

    private static void SampleAt(ReadOnlySpan<Quaternion> r0, ReadOnlySpan<Vector3> p0, ReadOnlySpan<Quaternion> offset, Span<Quaternion> rot,
        Span<Vector3> pos)
    {
        for (int j = 0; j < rot.Length; j++)
        {
            var o = offset[j];
            rot[j] = o == Quaternion.Identity ? r0[j] : Quaternion.Normalize(o * r0[j]);
        }
        p0.CopyTo(pos);
    }

    private static void AddPositionOffsets(ReadOnlySpan<int> moving, ReadOnlySpan<Vector3> offset, Span<Vector3> pos)
    {
        foreach (int j in moving)
            if (offset[j] != Vector3.Zero) pos[j] += offset[j];
    }

    /// <summary>Скорости выхода — разностью с прошлым выходом за <paramref name="dt"/>; выход этого задания становится прошлым.</summary>
    private static void OutputVelocities(ReadOnlySpan<Quaternion> rot, ReadOnlySpan<Vector3> pos, Span<Quaternion> prevRot, Span<Vector3> prevPos,
        Span<Vector3> angular, Span<Vector3> linear, float dt)
    {
        for (int j = 0; j < rot.Length; j++)
        {
            angular[j] = AnimMath.RotationVector(prevRot[j], rot[j]) / dt;
            linear[j] = (pos[j] - prevPos[j]) / dt;
            prevRot[j] = rot[j];
            prevPos[j] = pos[j];
        }
    }

    /// <summary>Скорости клипа между кадрами (<paramref name="scale"/> — кадров клипа в секунду игры).</summary>
    private static void ClipVelocities(ReadOnlySpan<Quaternion> r0, ReadOnlySpan<Vector3> p0, ReadOnlySpan<Quaternion> r1, ReadOnlySpan<Vector3> p1,
        float scale, Span<Vector3> angular, Span<Vector3> linear)
    {
        for (int j = 0; j < angular.Length; j++)
        {
            angular[j] = AnimMath.RotationVector(r0[j], r1[j]) * scale;
            linear[j] = (p1[j] - p0[j]) * scale;
        }
    }

    private static void DecayRotations(Span<Quaternion> offset, Span<Vector3> velocity, float y, float e, float dt)
    {
        for (int j = 0; j < offset.Length; j++)
        {
            if (offset[j] == Quaternion.Identity && velocity[j] == Vector3.Zero) continue;
            var x = AnimMath.ToRotationVector(offset[j]);
            var v = velocity[j];
            var j1 = v + x * y;
            x = e * (x + j1 * dt);
            v = e * (v - j1 * y * dt);
            if (x.LengthSquared() < OffsetFlush && v.LengthSquared() < OffsetFlush)
            {
                offset[j] = Quaternion.Identity;
                velocity[j] = Vector3.Zero;
            }
            else
            {
                offset[j] = Positive(AnimMath.FromRotationVector(x));   // вектор длиннее π (пружина с разгона) — та же ориентация
                velocity[j] = v;
            }
        }
    }

    private static void DecayPositions(ReadOnlySpan<int> moving, Span<Vector3> offset, Span<Vector3> velocity, float y, float e, float dt)
    {
        foreach (int j in moving)
        {
            var x = offset[j];
            var v = velocity[j];
            if (x == Vector3.Zero && v == Vector3.Zero) continue;
            var j1 = v + x * y;
            x = e * (x + j1 * dt);
            v = e * (v - j1 * y * dt);
            bool flush = x.LengthSquared() < OffsetFlush && v.LengthSquared() < OffsetFlush;
            offset[j] = flush ? Vector3.Zero : x;
            velocity[j] = flush ? Vector3.Zero : v;
        }
    }

    private static void EdgeRotations(ReadOnlySpan<Quaternion> src, ReadOnlySpan<Quaternion> srcPrev, ReadOnlySpan<Quaternion> dst,
        ReadOnlySpan<Quaternion> dstNext, float scale, Span<Quaternion> offset, Span<Vector3> velocity)
    {
        for (int j = 0; j < offset.Length; j++)
        {
            var vs = srcPrev.IsEmpty ? Vector3.Zero : AnimMath.RotationVector(srcPrev[j], src[j]) * scale;
            var vd = dstNext.IsEmpty ? Vector3.Zero : AnimMath.RotationVector(dst[j], dstNext[j]) * scale;
            offset[j] = Positive(Quaternion.Normalize(offset[j] * src[j] * Quaternion.Conjugate(dst[j])));
            velocity[j] += vs - vd;
        }
    }

    private static void EdgePositions(ReadOnlySpan<int> moving, ReadOnlySpan<Vector3> src, ReadOnlySpan<Vector3> srcPrev, ReadOnlySpan<Vector3> dst,
        ReadOnlySpan<Vector3> dstNext, float scale, Span<Vector3> offset, Span<Vector3> velocity)
    {
        foreach (int j in moving)
        {
            var vs = srcPrev.IsEmpty ? Vector3.Zero : (src[j] - srcPrev[j]) * scale;
            var vd = dstNext.IsEmpty ? Vector3.Zero : (dstNext[j] - dst[j]) * scale;
            offset[j] += src[j] - dst[j];
            velocity[j] += vs - vd;
        }
    }

    private static void RequestedRotations(ReadOnlySpan<Quaternion> output, ReadOnlySpan<Vector3> outputVelocity, ReadOnlySpan<Quaternion> dst,
        ReadOnlySpan<Quaternion> dstNext, float scale, Span<Quaternion> offset, Span<Vector3> velocity)
    {
        for (int j = 0; j < offset.Length; j++)
        {
            var vd = dstNext.IsEmpty ? Vector3.Zero : AnimMath.RotationVector(dst[j], dstNext[j]) * scale;
            offset[j] = Positive(Quaternion.Normalize(output[j] * Quaternion.Conjugate(dst[j])));
            velocity[j] = outputVelocity[j] - vd;
        }
    }

    private static void RequestedPositions(ReadOnlySpan<int> moving, ReadOnlySpan<Vector3> output, ReadOnlySpan<Vector3> outputVelocity,
        ReadOnlySpan<Vector3> dst, ReadOnlySpan<Vector3> dstNext, float scale, Span<Vector3> offset, Span<Vector3> velocity)
    {
        foreach (int j in moving)
        {
            var vd = dstNext.IsEmpty ? Vector3.Zero : (dstNext[j] - dst[j]) * scale;
            offset[j] = output[j] - dst[j];
            velocity[j] = outputVelocity[j] - vd;
        }
    }

    /// <summary>Смещение с W ≥ 0: выход <c>смещение · кадр</c> остаётся в полусфере кадра, знак поворотов не скачет.</summary>
    private static Quaternion Positive(Quaternion q) => q.W < 0 ? Quaternion.Negate(q) : q;
}
