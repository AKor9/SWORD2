using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Sword.Engine.Animation;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Contracts;
using Sword.Game.Data;

namespace Sword.Game.Animation;

/// <summary>
/// Базовые инварианты анимации (раздел 8 плана), их регистрирует ядро рядом с участником хеша <c>animation</c>:
/// <c>animation.finite</c>, <c>animation.settled</c> — после стадии Animation; <c>animation.pool</c>,
/// <c>animation.owners</c>, <c>animation.actions</c> (слой действий, 4г) — в конце End, когда снятые позы уже возвращены в
/// пул. Позы слоёв модулей (локомоция) проверяют сами модули.
/// </summary>
public static class AnimationInvariants
{
    /// <summary>Отклонение квадрата длины поворота от 1, которое ещё не ошибка (Slerp float).</summary>
    public const float UnitTolerance = 1e-3f;

    public static IEnumerable<IInvariant> For(PosePool pool, ActionTable actions) =>
        [new Finite(pool), new Settled(pool), new Pool(pool), new Owners(pool), new Actions(pool, actions)];

    /// <summary>
    /// <c>animation.finite</c> — у живых поз локальные повороты конечны и единичной длины, смещения и модельные матрицы
    /// конечны: после стадии Animation (пул только что исполнил задания тика).
    /// </summary>
    private sealed class Finite(PosePool pool) : IInvariant
    {
        public string Id => "animation.finite";
        public InvariantCost Cost => InvariantCost.EveryTick;
        public InvariantScope Scope => InvariantScope.Subsystem;
        public string After => GameStages.Animation;

        public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
        {
            for (int i = 0, n = pool.Capacity; i < n; i++)
            {
                var pose = pool.HandleAt(i);
                if (pose.IsNone) continue;
                var rot = pool.LocalRot(pose);
                var pos = pool.LocalPos(pose);
                var model = pool.Model(pose);
                // Быстрый проход — векторами по битам экспоненты и длинам поворотов; сустав ищется, только если что-то не так.
                if (AllFinite(MemoryMarshal.Cast<Quaternion, float>(rot)) && AllFinite(MemoryMarshal.Cast<Vector3, float>(pos))
                    && AllFinite(MemoryMarshal.Cast<Matrix4x4, float>(model)) && AllUnit(rot))
                    continue;
                Locate(pose, rot, pos, model, ref violations);
                if (violations.Full) return;
            }
        }

        /// <summary>
        /// Нет NaN и бесконечностей: у них все биты экспоненты — единицы. Маска копится без ветвления на каждом векторе —
        /// проверка упирается в чтение памяти (поза из 161 сустава — 3700 чисел, ≈1,5 мкс).
        /// </summary>
        private static bool AllFinite(ReadOnlySpan<float> values)
        {
            var bits = MemoryMarshal.Cast<float, uint>(values);
            var vectors = MemoryMarshal.Cast<uint, Vector<uint>>(bits);
            var exponent = new Vector<uint>(0x7F80_0000u);
            var hit = Vector<uint>.Zero;
            foreach (var v in vectors) hit |= Vector.Equals(v & exponent, exponent);
            if (hit != Vector<uint>.Zero) return false;
            for (int i = vectors.Length * Vector<uint>.Count; i < bits.Length; i++)
                if ((bits[i] & 0x7F80_0000u) == 0x7F80_0000u) return false;
            return true;
        }

        private static bool AllUnit(ReadOnlySpan<Quaternion> rot)
        {
            foreach (var q in rot)
                if (MathF.Abs(q.LengthSquared() - 1) > UnitTolerance) return false;
            return true;
        }

        private void Locate(PoseHandle pose, ReadOnlySpan<Quaternion> rot, ReadOnlySpan<Vector3> pos, ReadOnlySpan<Matrix4x4> model,
            ref ViolationSink violations)
        {
            var names = pool.Skeleton.JointNames;
            for (int j = 0; j < rot.Length; j++)
            {
                var q = rot[j];
                float len = q.LengthSquared();
                if (!float.IsFinite(len) || MathF.Abs(len - 1) > UnitTolerance)
                    violations.Report($"поза {pose}, сустав {names[j]}: поворот {Text(q)} (|q|² = {len.ToString("G6", CultureInfo.InvariantCulture)})");
                else if (!AllFinite(MemoryMarshal.Cast<Vector3, float>(pos.Slice(j, 1))))
                    violations.Report($"поза {pose}, сустав {names[j]}: смещение {pos[j]}");
                else if (!AllFinite(MemoryMarshal.Cast<Matrix4x4, float>(model.Slice(j, 1))))
                    violations.Report($"поза {pose}, сустав {names[j]}: модельная матрица не конечна");
                else continue;
                if (violations.Full) return;
            }
        }

        private static string Text(Quaternion q) => string.Create(CultureInfo.InvariantCulture, $"({q.X}, {q.Y}, {q.Z}, {q.W})");
    }

    /// <summary>
    /// <c>animation.settled</c> — после стадии Animation заданий в очереди пула нет: пул исполняет их пачкой последним в
    /// стадии, задание, поставленное позже, ждало бы следующего тика (поза отставала бы на тик).
    /// </summary>
    private sealed class Settled(PosePool pool) : IInvariant
    {
        public string Id => "animation.settled";
        public InvariantCost Cost => InvariantCost.EveryTick;
        public InvariantScope Scope => InvariantScope.Subsystem;
        public string After => GameStages.Animation;

        public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
        {
            if (pool.Pending != 0)
                violations.Report($"в очереди пула {pool.Pending} заданий после стадии Animation — поставлены после {GameLabels.AnimationTick.Name}");
        }
    }

    /// <summary>
    /// <c>animation.pool</c> — пул поз согласован: свободные слоты не повторяются и не живые (поколение ≤ 0), остальные —
    /// живые (поколение &gt; 0), живых — <see cref="PosePool.Count"/>. В конце тика.
    /// </summary>
    private sealed class Pool(PosePool pool) : IInvariant
    {
        private bool[] _free = [];

        public string Id => "animation.pool";
        public InvariantCost Cost => InvariantCost.EveryTick;
        public InvariantScope Scope => InvariantScope.Subsystem;
        public string After => GameStages.End;

        public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
        {
            int capacity = pool.Capacity;
            if (_free.Length < capacity) _free = new bool[capacity];
            else Array.Clear(_free, 0, capacity);
            foreach (int slot in pool.FreeSlots)
            {
                if ((uint)slot >= (uint)capacity) violations.Report($"свободный слот {slot} вне пула ({capacity})");
                else if (_free[slot]) violations.Report($"свободный слот {slot} в списке дважды");
                else if (pool.GenerationAt(slot) > 0) violations.Report($"слот {slot} в списке свободных, а поза {pool.HandleAt(slot)} жива");
                else
                {
                    _free[slot] = true;
                    continue;
                }
                if (violations.Full) return;
            }
            int alive = 0;
            for (int i = 0; i < capacity; i++)
            {
                if (_free[i]) continue;
                if (pool.GenerationAt(i) > 0) alive++;
                else
                {
                    violations.Report($"слот {i} не жив и не в списке свободных — потерян");
                    if (violations.Full) return;
                }
            }
            if (alive != pool.Count) violations.Report($"живых поз {alive}, а пул считает {pool.Count}");
        }
    }

    /// <summary>
    /// <c>animation.owners</c> — у каждого бойца есть поза (<see cref="PoseRef"/>), каждая <see cref="PoseRef"/> указывает на
    /// живую позу, одна поза — у одной сущности. В конце тика: поза снятого <see cref="PoseRef"/> к этому времени возвращена.
    /// </summary>
    private sealed class Owners(PosePool pool) : IInvariant
    {
        private ReadQuery? _refs, _bare;
        private Entity[] _owner = [];

        public string Id => "animation.owners";
        public InvariantCost Cost => InvariantCost.EveryTick;
        public InvariantScope Scope => InvariantScope.Entity;
        public string After => GameStages.End;

        public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
        {
            var world = ctx.World;
            _refs ??= world.Query().Read<PoseRef>();
            _bare ??= world.Query().With<Fighter>().Without<PoseRef>();
            int capacity = pool.Capacity;
            if (_owner.Length < capacity) _owner = new Entity[capacity];
            else Array.Clear(_owner, 0, capacity);

            foreach (var chunk in world.Iterate(_bare))
                foreach (var e in chunk.Entities)
                {
                    violations.Report(e, "боец без позы (PoseRef)");
                    if (violations.Full) return;
                }
            foreach (var chunk in world.Iterate(_refs))
            {
                var entities = chunk.Entities;
                var refs = chunk.Read<PoseRef>();
                for (int i = 0; i < entities.Length; i++)
                {
                    var h = new PoseHandle(refs[i].Index, refs[i].Generation);
                    if (!pool.IsAlive(h)) violations.Report(entities[i], $"PoseRef {h} — поза не жива");
                    else if (!_owner[h.Index].IsNull) violations.Report(entities[i], $"поза {h} — ещё и у {_owner[h.Index]}");
                    else
                    {
                        _owner[h.Index] = entities[i];
                        continue;
                    }
                    if (violations.Full) return;
                }
            }
        }
    }
    /// <summary>
    /// <c>animation.actions</c> (4г) — слой действий аниматора согласован: у бойца не-<c>Agile</c> (без <see cref="PlayerTag"/>)
    /// он есть (<see cref="AnimatorAction"/> и <see cref="ActionPoses"/>) — иначе боец не поворачивает и не разгоняется
    /// (<c>RootSteerSystem</c> ведёт не-<c>Agile</c> только со слоем); состояние и поза — вместе; вес в 0…1, числа конечны;
    /// клип задан тогда и только тогда, когда действие идёт; без действия вес 0; поворот — клипом поворота с растяжением
    /// 0,5…1,6; поза слоя — живая, своя и не итоговая поза бойца; идёт действие — поза уже взята. В конце тика.
    /// </summary>
    private sealed class Actions(PosePool pool, ActionTable actions) : IInvariant
    {
        private ReadQuery? _steady, _states;
        private Entity[] _owner = [];

        public string Id => "animation.actions";
        public InvariantCost Cost => InvariantCost.EveryTick;
        public InvariantScope Scope => InvariantScope.Entity;
        public string After => GameStages.End;

        public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
        {
            var world = ctx.World;
            _steady ??= world.Query().With<Fighter>().Without<PlayerTag>();
            _states ??= world.Query().Read<AnimatorAction>();
            int capacity = pool.Capacity;
            if (_owner.Length < capacity) _owner = new Entity[capacity];
            else Array.Clear(_owner, 0, capacity);

            foreach (var chunk in world.Iterate(_steady))
                foreach (var e in chunk.Entities)
                {
                    if (world.Has<AnimatorAction>(e) && world.Has<ActionPoses>(e)) continue;
                    violations.Report(e, "боец не-Agile без слоя действий (AnimatorAction и ActionPoses) — курс и скорость ему не считаются");
                    if (violations.Full) return;
                }
            foreach (var chunk in world.Iterate(_states))
            {
                var entities = chunk.Entities;
                var states = chunk.Read<AnimatorAction>();
                for (int i = 0; i < entities.Length; i++)
                {
                    var e = entities[i];
                    if (Problem(world, e, states[i]) is not { } problem) continue;
                    violations.Report(e, problem);
                    if (violations.Full) return;
                }
            }
        }

        private string? Problem(WorldReader world, Entity e, in AnimatorAction a)
        {
            if (!world.TryGet<ActionPoses>(e, out var poses)) return "слой действий без позы (ActionPoses)";
            if (!float.IsFinite(a.Weight) || a.Weight is < 0 or > 1) return $"вес действия {a.Weight} вне 0…1";
            if (!float.IsFinite(a.Time) || !float.IsFinite(a.Duration) || !float.IsFinite(a.TurnScale) || !float.IsFinite(a.YawDelta)
                || !float.IsFinite(a.RootDelta.X) || !float.IsFinite(a.RootDelta.Y) || !float.IsFinite(a.RootDelta.Z))
                return $"нечисловое время {a.Time}, длительность {a.Duration}, растяжение {a.TurnScale}, поворот {a.YawDelta} или сдвиг {a.RootDelta}";
            switch (a.Kind)
            {
                case ActionKind.None:
                    if (a.Clip != -1) return $"действия нет, а клип {a.Clip}";
                    if (a.Weight != 0) return $"действия нет, а вес {a.Weight}";
                    break;
                case ActionKind.Turn:
                    if ((uint)a.Clip >= (uint)pool.Clips.ClipCount) return $"поворот: клипа {a.Clip} нет в библиотеке";
                    if (a.Clip != actions.TurnLeft && a.Clip != actions.TurnRight && a.Clip != actions.Turn180)
                        return $"поворот клипом {pool.Clips.Clips[a.Clip].Name} — не клип поворота";
                    if (MathF.Abs(a.TurnScale) is < ActionRules.TurnScaleMin or > ActionRules.TurnScaleMax)
                        return $"поворот: растяжение yaw клипа {a.TurnScale} вне ±0,5…1,6";
                    break;
                default:
                    return $"неизвестное действие {(byte)a.Kind}";
            }
            var core = world.TryGet<PoseRef>(e, out var r) ? new PoseHandle(r.Index, r.Generation) : PoseHandle.None;
            var h = poses.Layer;
            if (h.IsNone) return a.Kind != ActionKind.None && !core.IsNone ? $"действие {a.Kind} идёт, а позы слоя действий нет" : null;
            if (!pool.IsAlive(h)) return $"поза слоя действий {h} не жива";
            if (h == core) return $"поза слоя действий {h} — это итоговая поза бойца";
            if (!_owner[h.Index].IsNull) return $"поза слоя действий {h} — ещё и у {_owner[h.Index]}";
            _owner[h.Index] = e;
            return null;
        }
    }
}
