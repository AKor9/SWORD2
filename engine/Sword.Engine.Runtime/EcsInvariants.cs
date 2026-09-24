using System.Globalization;
using Sword.Engine.Ecs;

namespace Sword.Engine.Runtime;

/// <summary>
/// Базовые инварианты движка (раздел 8 плана) — их заводит Runtime в каждой симуляции, по листьям полей
/// (<see cref="FieldLayout"/>), как хеш: <c>ecs.finite</c> и <c>ecs.entityRefs</c>.
/// </summary>
internal static class EcsInvariants
{
    public static IEnumerable<IInvariant> For(WorldReader world) => [new Finite(world), new EntityRefs(world)];

    private readonly record struct Data(int Id, string Title, FieldLayout Layout, int[] Leaves);

    /// <summary>Компоненты и ресурсы-значения, у которых есть листья вида <paramref name="kind"/>.</summary>
    private static (Data[] Components, Data[] Resources) Prepare(WorldReader world, Func<FieldLayout.Kind, bool> kind)
    {
        Data[] Of(IEnumerable<(int Id, string Name, Type Type)> items) =>
        [
            .. items.Select(x => (x, Layout: FieldLayout.Of(x.Type)))
                .Select(p => new Data(p.x.Id, p.x.Type.Name, p.Layout,
                    [.. Enumerable.Range(0, p.Layout.Leaves.Count).Where(i => kind(p.Layout.Leaves[i].Kind))]))
                .Where(d => d.Leaves.Length > 0),
        ];
        return (Of(world.Components.Where(c => !c.IsTag).Select(c => (c.Id, c.Name, c.Type))),
            Of(world.Resources.Where(r => r.Size > 0).Select(r => (r.Id, r.Name, r.Type))));
    }

    private static string Path(Data d, int leaf) => d.Layout.Leaves[leaf].Path is { Length: > 0 } p ? $"{d.Title}.{p}" : d.Title;

    /// <summary>
    /// <c>ecs.finite</c> — все числа с плавающей точкой в компонентах живых сущностей и в ресурсах-значениях конечны (нет NaN
    /// и бесконечностей): в конце каждого тика. Нарушение — сущность и путь поля (<c>Transform.Position.X = NaN</c>).
    /// </summary>
    private sealed class Finite(WorldReader world) : IInvariant
    {
        private (Data[] Components, Data[] Resources)? _data;

        public string Id => "ecs.finite";
        public InvariantCost Cost => InvariantCost.Heavy;   // в живой игре — раз в 4 тика (исправления после Пд)
        public InvariantScope Scope => InvariantScope.World;
        public string After => Simulation.ExportStage;

        public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
        {
            var (components, resources) = _data ??= Prepare(world, k => k is FieldLayout.Kind.F32 or FieldLayout.Kind.F64);
            // Тело обхода по сущности — свой метод: иначе долгий цикл (сущности × компоненты × листья) в методе, который зовётся
            // раз в тик, многоуровневый JIT заменял на ходу (OSR 2,2…3,5 мс на главном потоке — docs/history/profiler.md,
            // «Разбор Пд», вопрос 3).
            for (int i = 0, n = world.EntitySlots; i < n; i++)
            {
                var e = world.EntityAt(i);
                if (!e.IsNull && !Scan(e, components, ref violations)) return;
            }
            foreach (var d in resources)
            {
                if (!world.TryReadResource(d.Id, out var bytes)) continue;
                foreach (int leaf in d.Leaves)
                {
                    if (IsFinite(d.Layout, bytes, leaf)) continue;
                    violations.Report($"ресурс {Path(d, leaf)} = {Text(d.Layout, bytes, leaf)}");
                    if (violations.Full) return;
                }
            }
        }

        /// <summary>Листья компонентов живой сущности; ложь — приёмник нарушений полон, дальше не искать.</summary>
        private bool Scan(Entity e, Data[] components, ref ViolationSink violations)
        {
            foreach (var d in components)
            {
                if (!world.TryRead(e, d.Id, out var bytes)) continue;
                foreach (int leaf in d.Leaves)
                {
                    if (IsFinite(d.Layout, bytes, leaf)) continue;
                    violations.Report(e, $"{Path(d, leaf)} = {Text(d.Layout, bytes, leaf)}");
                    if (violations.Full) return false;
                }
            }
            return true;
        }

        private static bool IsFinite(FieldLayout layout, ReadOnlySpan<byte> bytes, int leaf)
        {
            ulong bits = layout.Bits(bytes, leaf);
            return layout.Leaves[leaf].Kind == FieldLayout.Kind.F32
                ? (bits & 0x7F80_0000UL) != 0x7F80_0000UL
                : (bits & 0x7FF0_0000_0000_0000UL) != 0x7FF0_0000_0000_0000UL;
        }

        private static string Text(FieldLayout layout, ReadOnlySpan<byte> bytes, int leaf)
        {
            ulong bits = layout.Bits(bytes, leaf);
            return layout.Leaves[leaf].Kind == FieldLayout.Kind.F32
                ? BitConverter.UInt32BitsToSingle((uint)bits).ToString(CultureInfo.InvariantCulture)
                : BitConverter.UInt64BitsToDouble(bits).ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// <c>ecs.entityRefs</c> — поля-сущности в компонентах живых сущностей и в ресурсах-значениях указывают на живую сущность
    /// или пусты. По запросу (<c>invariant.run</c>): ссылка на только что умершую сущность бывает законной, пока система её не
    /// сняла, — это совет искать висячие ссылки, а не правило каждого тика.
    /// </summary>
    private sealed class EntityRefs(WorldReader world) : IInvariant
    {
        private (Data[] Components, Data[] Resources)? _data;

        public string Id => "ecs.entityRefs";
        public InvariantCost Cost => InvariantCost.OnDemand;
        public InvariantScope Scope => InvariantScope.Entity;
        public string After => Simulation.ExportStage;

        public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
        {
            var (components, resources) = _data ??= Prepare(world, k => k == FieldLayout.Kind.Entity);
            // Тело обхода по сущности — свой метод (включённый явно проверяется каждый тик — без замены на ходу, как ecs.finite).
            for (int i = 0, n = world.EntitySlots; i < n; i++)
            {
                var e = world.EntityAt(i);
                if (!e.IsNull && !Scan(e, components, ref violations)) return;
            }
            foreach (var d in resources)
            {
                if (!world.TryReadResource(d.Id, out var bytes)) continue;
                foreach (int leaf in d.Leaves)
                {
                    var target = Target(d.Layout, bytes, leaf);
                    if (target.IsNull || world.IsAlive(target)) continue;
                    violations.Report($"ресурс {Path(d, leaf)} = {target} — не жива");
                    if (violations.Full) return;
                }
            }
        }

        /// <summary>Ссылки в компонентах живой сущности; ложь — приёмник полон.</summary>
        private bool Scan(Entity e, Data[] components, ref ViolationSink violations)
        {
            foreach (var d in components)
            {
                if (!world.TryRead(e, d.Id, out var bytes)) continue;
                foreach (int leaf in d.Leaves)
                {
                    var target = Target(d.Layout, bytes, leaf);
                    if (target.IsNull || world.IsAlive(target)) continue;
                    violations.Report(e, $"{Path(d, leaf)} = {target} — не жива");
                    if (violations.Full) return false;
                }
            }
            return true;
        }

        private static Entity Target(FieldLayout layout, ReadOnlySpan<byte> bytes, int leaf)
        {
            ulong bits = layout.Bits(bytes, leaf);
            return new Entity(unchecked((int)(uint)bits), unchecked((int)(bits >> 32)));
        }
    }
}
