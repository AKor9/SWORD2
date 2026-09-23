using System.Globalization;
using System.Numerics;
using Sword.Engine.Ecs;
using Sword.Engine.Physics;
using Sword.Engine.Runtime;
using Sword.Game.Contracts;

namespace Sword.Game.Bodies;

/// <summary>
/// Инварианты физики (раздел 8 плана, этап 5), их регистрирует ядро рядом с участником хеша <c>physics</c>:
/// <c>physics.finite</c> — после стадии Physics (тела ставятся в PrePhysics — 5б, шаг физики — этап 7);
/// <c>physics.bodyRefs</c> — в конце End, когда хуки всех барьеров тика отработали. Проверки внутри шага — с шагом (этап 7).
/// </summary>
public static class PhysicsInvariants
{
    /// <summary>Отклонение квадрата длины поворота от 1, которое ещё не ошибка (как у поз).</summary>
    public const float UnitTolerance = 1e-3f;

    public static IEnumerable<IInvariant> For(IPhysicsWorld physics) => [new Finite(physics), new BodyRefs(physics)];

    /// <summary>
    /// <c>physics.finite</c> — у живых тел конечны положение, поворот, прошлая постановка, скорости и форма; поворот —
    /// единичной длины; форма не отрицательна.
    /// </summary>
    private sealed class Finite(IPhysicsWorld physics) : IInvariant
    {
        public string Id => "physics.finite";
        public InvariantCost Cost => InvariantCost.EveryTick;
        public InvariantScope Scope => InvariantScope.Subsystem;
        public string After => GameStages.Physics;

        public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
        {
            for (int i = 0, n = physics.BodyCapacity; i < n; i++)
            {
                var b = physics.BodyAt(i);
                if (b.IsNone) continue;
                var s = physics.Get(b);
                if (Problem(s) is not { } problem) continue;
                violations.Report($"тело {b} (сочленение {physics.ArticulationOf(b)}): {problem}");
                if (violations.Full) return;
            }
        }

        private static string? Problem(in BodyState s)
        {
            if (!Finite3(s.Position)) return $"положение {Text(s.Position)}";
            if (!Finite3(s.PrevPosition)) return $"прошлое положение {Text(s.PrevPosition)}";
            if (!Finite3(s.Velocity)) return $"скорость {Text(s.Velocity)}";
            if (!Finite3(s.AngularVelocity)) return $"угловая скорость {Text(s.AngularVelocity)}";
            if (Unit(s.Rotation) is { } rot) return $"поворот {rot}";
            if (Unit(s.PrevRotation) is { } prev) return $"прошлый поворот {prev}";
            if (!float.IsFinite(s.Radius) || !float.IsFinite(s.HalfLength) || s.Radius < 0 || s.HalfLength < 0)
                return string.Create(CultureInfo.InvariantCulture, $"форма: радиус {s.Radius}, полудлина {s.HalfLength}");
            return null;
        }

        private static bool Finite3(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

        private static string? Unit(Quaternion q)
        {
            float len = q.LengthSquared();
            return float.IsFinite(len) && MathF.Abs(len - 1) <= UnitTolerance ? null
                : string.Create(CultureInfo.InvariantCulture, $"({q.X}, {q.Y}, {q.Z}, {q.W}), |q|² = {len:G6}");
        }

        private static string Text(Vector3 v) => string.Create(CultureInfo.InvariantCulture, $"({v.X}, {v.Y}, {v.Z})");
    }

    /// <summary>
    /// <c>physics.bodyRefs</c> — тела и их владельцы согласованы: у каждого бойца есть тело (<see cref="BodyRef"/>) из
    /// <see cref="BodySegs.Bodies"/> капсул; каждый <see cref="BodyRef"/> указывает на живое сочленение; у сочленения ровно
    /// один владелец — нет ни общих, ни ничьих (тело, которое никто не освободит). В конце End.
    /// </summary>
    private sealed class BodyRefs(IPhysicsWorld physics) : IInvariant
    {
        private ReadQuery? _refs, _bare;
        private Entity[] _owner = [];

        public string Id => "physics.bodyRefs";
        public InvariantCost Cost => InvariantCost.EveryTick;
        public InvariantScope Scope => InvariantScope.Entity;
        public string After => GameStages.End;

        public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
        {
            var world = ctx.World;
            _refs ??= world.Query().Read<BodyRef>();
            _bare ??= world.Query().With<Fighter>().Without<BodyRef>();
            int capacity = physics.ArticulationCapacity;
            if (_owner.Length < capacity) _owner = new Entity[capacity];
            else Array.Clear(_owner, 0, capacity);

            foreach (var chunk in world.Iterate(_bare))
                foreach (var e in chunk.Entities)
                {
                    violations.Report(e, "боец без тела (BodyRef)");
                    if (violations.Full) return;
                }
            foreach (var chunk in world.Iterate(_refs))
            {
                var entities = chunk.Entities;
                var refs = chunk.Read<BodyRef>();
                for (int i = 0; i < entities.Length; i++)
                {
                    var a = FighterBodies.Handle(refs[i]);
                    if (!physics.IsAlive(a)) violations.Report(entities[i], $"BodyRef {a} — сочленение не живо");
                    else if (!_owner[a.Index].IsNull) violations.Report(entities[i], $"сочленение {a} — ещё и у {_owner[a.Index]}");
                    else
                    {
                        _owner[a.Index] = entities[i];
                        if (physics.BodyCount(a) == BodySegs.Bodies || !world.Has<Fighter>(entities[i])) continue;
                        violations.Report(entities[i], $"у бойца сочленение {a} из {physics.BodyCount(a)} тел, а нужно {BodySegs.Bodies}");
                    }
                    if (violations.Full) return;
                }
            }
            for (int i = 0; i < capacity; i++)
            {
                var a = physics.ArticulationAt(i);
                if (a.IsNone || !_owner[i].IsNull) continue;
                violations.Report($"сочленение {a} ({physics.BodyCount(a)} тел) без владельца — его никто не освободит");
                if (violations.Full) return;
            }
        }
    }
}
