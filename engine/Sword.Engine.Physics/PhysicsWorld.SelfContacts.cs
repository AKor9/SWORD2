using System.Numerics;

namespace Sword.Engine.Physics;

// Самоколлизия — пары тел одного сочленения (таблица владельца), решаются после пар сочленений тем же контактом тело ↔ тело
// на физике (ContactsSimulatedPair). Члены — с приставкой Self; README физики, «Самоколлизия».
public sealed partial class PhysicsWorld
{
    /// <summary>Пары самоколлизии по слотам сочленений (массив переиспользуется — место под все пары с создания сочленения) и их число.</summary>
    private BodyPair[]?[] _selfPairs = [];
    private int[] _selfCount = [];

    /// <summary>Толщина частей в самоколлизии — из настроек на шаг (<see cref="PhysicsSettings.SelfContactScale"/>).</summary>
    private float _selfScale = 1;

    /// <summary>Пар самоколлизии, дошедших до ближайших точек осей, за последний шаг — тестам.</summary>
    internal int SelfNarrowChecks { get; private set; }

    /// <summary>Контактов самоколлизии за последний шаг — тестам.</summary>
    internal int SelfTouches { get; private set; }


    public void SetSelfContacts(ArticulationHandle articulation, ReadOnlySpan<BodyPair> pairs)
    {
        Check(articulation);
        int a = articulation.Index, n = _artCount[a];
        for (int i = 0; i < pairs.Length; i++)
            if (SelfProblem(pairs, i, n) is { } problem)
                throw new ArgumentException($"пара {i} самоколлизии сочленения {articulation}: {problem}", nameof(pairs));
        if (pairs.SequenceEqual(_selfPairs[a].AsSpan(0, _selfCount[a]))) return;
        SleepWake(a, WakeReason.Command);   // другие пары — другая физика сочленения
        var stored = _selfPairs[a];
        if (stored is null || stored.Length < pairs.Length) _selfPairs[a] = stored = new BodyPair[Math.Max(pairs.Length, SelfCapacity(n))];
        pairs.CopyTo(stored);
        _selfCount[a] = pairs.Length;
    }

    public ReadOnlySpan<BodyPair> SelfContacts(ArticulationHandle articulation)
    {
        Check(articulation);
        int a = articulation.Index;
        return _selfPairs[a].AsSpan(0, _selfCount[a]);
    }

    /// <summary>Всех пар тел сочленения из <paramref name="bodies"/> тел — <c>n(n − 1)/2</c>.</summary>
    private static int SelfCapacity(int bodies) => bodies * (bodies - 1) / 2;

    private static string? SelfProblem(ReadOnlySpan<BodyPair> pairs, int i, int bodies)
    {
        var p = pairs[i];
        if ((uint)p.A >= (uint)bodies || (uint)p.B >= (uint)bodies) return $"тела ({p.A}, {p.B}) — номера тел сочленения 0…{bodies - 1}";
        if (p.A == p.B) return $"тела ({p.A}, {p.B}) — разные";
        for (int k = 0; k < i; k++)
            if (pairs[k] == p || pairs[k] == new BodyPair(p.B, p.A)) return $"пара ({p.A}, {p.B}) — уже есть (пара {k})";
        return null;
    }

    /// <summary>Слоты выросли (зовёт <see cref="ContactsGrow"/>): таблицы пар по слотам сочленений.</summary>
    private void SelfGrow(int articulations)
    {
        if (_selfCount.Length >= articulations) return;
        Array.Resize(ref _selfPairs, articulations);
        Array.Resize(ref _selfCount, articulations);
    }

    /// <summary>Сочленение в слоте создано или удаляется: пар нет, место под все пары — за слотом (таблица трупа посреди игры — без выделения памяти).</summary>
    private void SelfReset(int a)
    {
        _selfCount[a] = 0;
        int need = SelfCapacity(_artCount[a]);
        if (need > 0 && (_selfPairs[a] is not { } stored || stored.Length < need)) _selfPairs[a] = new BodyPair[need];
    }

    /// <summary>Самоколлизия подшага: неспящие сочленения с телами на физике по слотам, пары — по порядку таблицы. Толщина 0 — пропуск.</summary>
    private void SelfPairs(float h)
    {
        float scale = _selfScale;
        if (!(scale > 0) || _contactsSimulatedArticulations == 0) return;
        for (int a = 0; a < _artCapacity; a++)
            if (_selfCount[a] > 0 && _contactsSimulated[a] > 1) SelfArticulation(a, scale, h);
    }

    private void SelfArticulation(int a, float scale, float h)
    {
        var pairs = _selfPairs[a]!;
        var members = _artBodies[a]!;
        for (int k = 0, n = _selfCount[a]; k < n; k++)
        {
            var p = pairs[k];
            SelfPair(members[p.A], members[p.B], scale, h);
        }
    }

    /// <summary>Пара самоколлизии: оба тела на физике, коробки (радиусы × толщина) пересеклись — контакт с источником <see cref="PhysicsSource.Self"/>.</summary>
    private void SelfPair(int ia, int ib, float scale, float h)
    {
        ref readonly var qa = ref _contactsProbe[ia];
        ref readonly var qb = ref _contactsProbe[ib];
        if (!(qa.Simulated && qb.Simulated)) return;
        float ra = qa.Radius * scale, rb = qb.Radius * scale;
        if (!ContactsOverlap(qa.Center, qa.Extent + new Vector3(ra - qa.Radius), qb.Center, qb.Extent + new Vector3(rb - qb.Radius))) return;
        SelfNarrowChecks++;
        float rs = ra + rb;
        float d2 = ContactsClosest(qa.Center, qa.Axis, _bodies[ia].HalfLength, qb.Center, qb.Axis, _bodies[ib].HalfLength, out var c1, out var c2);
        if (!(d2 < rs * rs)) return;
        SelfTouches++;
        float d = MathF.Sqrt(d2);
        var n = d > ContactsDirectionEpsilon ? (c1 - c2) / d : ContactsFallbackNormal(in _bodies[ia], in _bodies[ib]);
        ContactsSimulatedPair(ia, ib, c1 - n * ra, c2 + n * rb, n, rs - d, h, PhysicsSource.Self, scale);
    }
}
