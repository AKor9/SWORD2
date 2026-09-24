using System.Numerics;

namespace Sword.Engine.Physics;

// 7д: толчки живых (кинематических) тел — бюджет пары, проход, отдача, импульс удара (идеи — KinematicContacts, HitImpulse,
// PrepareKinematicContacts SWORD 2eab13b; свой путь — README «7д»). Крючки KinematicGrow, KinematicReset, BeginKinematicSubstep,
// PushKinematic, EndKinematicSubstep объявлены в PhysicsWorld.cs (контракт 7б).
public sealed partial class PhysicsWorld
{
    /// <summary>
    /// Состояние пары сочленений «толкающий → цель» (слоты): проход (номер удара, открыт, потенциал, остаток, пустые тики,
    /// отметки тика) и бюджет пары за подшаг (израсходовано, номер подшага, к которому оно относится).
    /// </summary>
    private struct KinematicPair
    {
        public int Strike, Number, EmptyTicks, Stamp;
        public bool Inside, ContactSeen, HitSeen;

        /// <summary>
        /// Закрытый проход — потенциал этого тика (<see cref="PreparePass"/>; конец тика обнуляет); открытый — бюджет прохода:
        /// наибольший потенциал с открытия. Потрачено за проход — <c>Potential − Remaining</c> (≥ 0).
        /// </summary>
        public float Potential, Remaining;

        /// <summary>Израсходовано бюджета пары в подшаге <see cref="Stamp"/>, кг·м.</summary>
        public float Used;
    }

    /// <summary>Пары по слотам: <c>толкающий · _kinematicCapacity + цель</c>; растёт вместе со слотами сочленений.</summary>
    private KinematicPair[] _kinematicPairs = [];
    private Vector3[] _kinematicReaction = [];
    private int _kinematicCapacity;

    /// <summary>Номер подшага толчков (растёт с каждым <see cref="BeginKinematicSubstep"/>; 0 — ни одного не было).</summary>
    private int _kinematicSubstep;

    /// <summary>Бюджет пары этого подшага, кг·м: <c>PushForce · h²</c>.</summary>
    private float _kinematicPairBudget;

    /// <summary>Итоги подшага для трассы: наибольшая доля бюджета пары и сколько пар толкало.</summary>
    private float _kinematicSubstepShare;
    private int _kinematicSubstepPairs;

    /// <summary>Израсходовано бюджета пары в последнем подшаге толчков, кг·м — тестам и диагностике (как <c>ContactsPairChecks</c>).</summary>
    internal float KinematicPairUsed(ArticulationHandle pusher, ArticulationHandle target)
    {
        ref readonly var p = ref KinematicPairOf(pusher, target);
        return p.Stamp == _kinematicSubstep ? p.Used : 0;
    }

    /// <summary>Бюджет пары последнего подшага толчков, кг·м (<c>PushForce · h²</c>) — тестам и диагностике.</summary>
    internal float KinematicPairBudget => _kinematicPairBudget;

    public void PreparePass(ArticulationHandle pusher, int strike, float mass, float speed)
    {
        Check(pusher);
        if (!float.IsFinite(mass) || mass < 0) throw new ArgumentOutOfRangeException(nameof(mass), mass, "масса удара — конечная, не меньше 0");
        if (!float.IsFinite(speed) || speed < 0) throw new ArgumentOutOfRangeException(nameof(speed), speed, "скорость — конечная, не меньше 0");
        var s = _settings;
        float potential = s.PassShare * mass * MathF.Min(speed, s.KinematicPointSpeedMax);
        int i = pusher.Index, row = i * _kinematicCapacity;
        for (int j = 0; j < _artCapacity; j++)
            if (j != i && _artGeneration[j] > 0) KinematicPrepare(ref _kinematicPairs[row + j], strike, potential);
    }

    /// <summary>
    /// Удар толкающего для одной пары: новый номер — проход «снаружи»; закрытому — потенциал этого тика; открытому бюджет растёт
    /// до нового потенциала, если тот больше (остаток — на ту же прибавку): проход, открытый медленной частью взмаха, получает
    /// бюджет быстрой (хрупкое место 10 SWORD — бюджет задавался раз при открытии).
    /// </summary>
    private static void KinematicPrepare(ref KinematicPair p, int strike, float potential)
    {
        if (p.Strike != strike)
        {
            p.Strike = strike;
            p.Inside = false;
            p.EmptyTicks = 0;
            p.Remaining = 0;
        }
        if (!p.Inside)
        {
            p.Potential = potential;
            return;
        }
        if (potential > p.Potential)
        {
            p.Remaining += potential - p.Potential;
            p.Potential = potential;
        }
    }

    public void PassHit(ArticulationHandle pusher, ArticulationHandle target)
    {
        ref var p = ref KinematicPairOf(pusher, target);
        KinematicOpen(ref p);
        p.HitSeen = true;
        SleepWake(target.Index, WakeReason.Hit);   // 7з-3: удар будит цель
    }

    public float SpendHit(ArticulationHandle pusher, ArticulationHandle target, float impulse)
    {
        ref var p = ref KinematicPairOf(pusher, target);
        float spent = KinematicSpend(ref p, impulse);
        if (spent > 0) _trace?.PassSpent(KinematicPassShare(in p));
        return spent;
    }

    public Vector3 ApplyHit(BodyHandle body, Vector3 point, Vector3 impulse, float impactSpeed, ArticulationHandle pusher)
    {
        Check(body);
        if (!Finite(point)) throw new ArgumentException("точка удара — конечная", nameof(point));
        if (!Finite(impulse)) throw new ArgumentException("импульс удара — конечный", nameof(impulse));
        if (float.IsNaN(impactSpeed)) throw new ArgumentOutOfRangeException(nameof(impactSpeed), impactSpeed, "скорость удара — число");
        ref var s = ref _bodies[body.Index];
        float j = impulse.Length();
        if (!(j > 0)) return Vector3.Zero;
        SleepWakeBody(body.Index, WakeReason.Hit);   // 7з-3: удар будит
        var dir = impulse / j;
        var r = point - s.Position;
        float w = s.InvMassAt(r, dir);

        // (1) потолок Δv точки вдоль удара — лёгкая часть не разгоняется до десятков м/с
        bool capped = false;
        if (impactSpeed > 0)
        {
            float dvMax = _settings.HitDvShare * impactSpeed;
            float dv = j * w;
            if (dv > dvMax)
            {
                j *= dvMax / dv;
                capped = true;
            }
        }

        // (2) часть на физике: импульс — из прохода пары, отдача (минус приложенный) — толкающему
        bool budgeted = false;
        int target = _bodyOwner[body.Index];
        float passShare = -1;
        if (s.Mode == BodyMode.Simulated && IsAlive(pusher) && pusher.Index != target)
        {
            ref var p = ref _kinematicPairs[pusher.Index * _kinematicCapacity + target];
            float spent = KinematicSpend(ref p, j);
            budgeted = spent < j;
            j = spent;
            _kinematicReaction[pusher.Index] -= dir * j;
            // закрытый проход ничего не тратил: его доля (потенциал тика при остатке 0) в трассу не идёт
            if (p.Inside) passShare = KinematicPassShare(in p);
        }

        // (3) импульс — в скорость (и кинематической части: тик смерти)
        var applied = dir * j;
        Vector3 v0 = s.Velocity, w0 = s.AngularVelocity;
        Dynamics.ApplyImpulse(ref s, applied, r);
        if (s.Mode == BodyMode.Simulated) StepMarkNonFinite(body.Index);   // 7з-2: счёт нечисловых — по записи тела
        if (_trace is { } trace)
        {
            float share = impactSpeed > 0 ? j * w / impactSpeed : 0;
            trace.Hit(body.Index, share, capped, budgeted, Vector3.Distance(s.Velocity, v0), Vector3.Distance(s.AngularVelocity, w0));
            if (passShare >= 0) trace.PassSpent(passShare);
        }
        return applied;
    }

    public PassState Pass(ArticulationHandle pusher, ArticulationHandle target)
    {
        ref readonly var p = ref KinematicPairOf(pusher, target);
        return new PassState(p.Strike, p.Number, p.Inside, p.Potential, p.Remaining, p.EmptyTicks);
    }

    public Vector3 Reaction(ArticulationHandle articulation)
    {
        Check(articulation);
        return _kinematicReaction[articulation.Index];
    }

    public Vector3 TakeReaction(ArticulationHandle articulation)
    {
        Check(articulation);
        var j = _kinematicReaction[articulation.Index];
        _kinematicReaction[articulation.Index] = default;
        if (j != Vector3.Zero) _trace?.Reaction(articulation, j.Length());
        return j;
    }

    public void EndKinematicTick()
    {
        int exit = _settings.PassExitTicks, n = _kinematicCapacity;
        var pairs = _kinematicPairs;
        // строки мёртвых слотов сброшены KinematicReset — их пропускаем
        for (int i = 0; i < n; i++)
            if (_artGeneration[i] > 0) KinematicEndRow(pairs.AsSpan(i * n, n), exit);
    }

    private static void KinematicEndRow(Span<KinematicPair> row, int exit)
    {
        for (int j = 0; j < row.Length; j++) KinematicEndTick(ref row[j], exit);
    }

    /// <summary>Конец тика пары: открытый проход без контакта и попадания <paramref name="exit"/> тиков подряд — закрыт; у закрытого потенциала нет.</summary>
    private static void KinematicEndTick(ref KinematicPair p, int exit)
    {
        if (p.Inside)
        {
            p.EmptyTicks = p.ContactSeen || p.HitSeen ? 0 : p.EmptyTicks + 1;
            if (p.EmptyTicks >= exit)
            {
                p.Inside = false;
                p.EmptyTicks = 0;
                p.Remaining = 0;
            }
        }
        if (!p.Inside) p.Potential = 0;
        p.ContactSeen = p.HitSeen = false;
    }

    private static void KinematicOpen(ref KinematicPair p)
    {
        if (p.Inside) return;
        p.Inside = true;
        p.Number++;
        p.EmptyTicks = 0;
        p.Remaining = p.Potential;
    }

    /// <summary>Списать импульс из прохода: <c>min(impulse, остаток)</c>; закрыт или импульс не больше 0 (NaN) — 0.</summary>
    private static float KinematicSpend(ref KinematicPair p, float impulse)
    {
        if (!p.Inside || !(impulse > 0)) return 0;
        float spent = MathF.Min(impulse, p.Remaining);
        p.Remaining = MathF.Max(0, p.Remaining - spent);
        return spent;
    }

    /// <summary>Доля бюджета прохода, потраченная с открытия (0 — бюджета нет).</summary>
    private static float KinematicPassShare(in KinematicPair p) => p.Potential > 0 ? (p.Potential - p.Remaining) / p.Potential : 0;

    private ref KinematicPair KinematicPairOf(ArticulationHandle pusher, ArticulationHandle target)
    {
        Check(pusher);
        Check(target);
        if (pusher.Index == target.Index) throw new ArgumentException($"проход — между разными сочленениями ({pusher})", nameof(target));
        return ref _kinematicPairs[pusher.Index * _kinematicCapacity + target.Index];
    }

    partial void KinematicGrow(int articulations, int bodies)
    {
        if (articulations > _kinematicCapacity)
        {
            int old = _kinematicCapacity;
            var pairs = new KinematicPair[articulations * articulations];
            for (int i = 0; i < old; i++) Array.Copy(_kinematicPairs, i * old, pairs, i * articulations, old);
            _kinematicPairs = pairs;
            Array.Resize(ref _kinematicReaction, articulations);
            _kinematicCapacity = articulations;
        }
        _trace?.Grow(articulations, bodies);
    }

    partial void KinematicReset(int articulation)
    {
        int n = _kinematicCapacity, row = articulation * n;
        _kinematicPairs.AsSpan(row, n).Clear();
        for (int j = 0; j < n; j++) _kinematicPairs[j * n + articulation] = default;
        _kinematicReaction[articulation] = default;
        _trace?.ResetArticulation(articulation, _artBodies[articulation].AsSpan(0, _artCount[articulation]));
    }

    partial void BeginKinematicSubstep(float h)
    {
        _kinematicSubstep++;
        _kinematicPairBudget = _settings.PushForce * h * h;
        _kinematicSubstepShare = 0;
        _kinematicSubstepPairs = 0;
    }

    partial void PushKinematic(int kinematic, int body, Vector3 kinematicPoint, Vector3 point, Vector3 normal, float depth, float h) =>
        KinematicPush(kinematic, body, kinematicPoint, point, normal, depth, h);

    /// <summary>
    /// Толчок тела на физике <paramref name="body"/> кинематическим телом <paramref name="kinematic"/> (слоты тел разных
    /// сочленений): кинематическая сторона не двигается (для решателя — мир). Предел λ — остаток бюджета пары за подшаг (скаляр
    /// на все контакты пары: первый берёт сколько может), у <see cref="KinematicSource.Pass"/> — ещё остаток прохода (проход
    /// открывается контактом), и предел скорости: точка тела вдоль нормали — не быстрее толкающей точки
    /// (<c>0 … KinematicPointSpeedMax</c>) плюс <see cref="PhysicsSettings.PopSpeed"/>. Глубина решается полная (непогашенная
    /// — на следующие подшаги). Отдача (<c>−λn/h</c>) — толкающему, кроме <see cref="KinematicSource.NoReaction"/>.
    /// Возвращает применённую λ, кг·м.
    /// </summary>
    private float KinematicPush(int kinematic, int body, Vector3 kinematicPoint, Vector3 point, Vector3 normal, float depth, float h)
    {
        int pusher = _bodyOwner[kinematic], target = _bodyOwner[body];
        ref var pair = ref _kinematicPairs[pusher * _kinematicCapacity + target];
        if (pair.Stamp != _kinematicSubstep)
        {
            pair.Stamp = _kinematicSubstep;
            pair.Used = 0;
        }
        var settings = _settings;
        ref readonly var kin = ref _bodies[kinematic];
        ref var phys = ref _bodies[body];
        var source = kin.Source;

        float max = MathF.Max(0, _kinematicPairBudget - pair.Used);
        if (source == KinematicSource.Pass)
        {
            KinematicOpen(ref pair);
            pair.ContactSeen = true;
            max = MathF.Min(max, pair.Remaining * h);
        }

        // Предел скорости: бюджет пары ограничивает силу, а не скорость — лёгкую часть он разогнал бы за подшаг на метры в секунду.
        float w = phys.InvMassAt(point - phys.Position, normal);
        var prevPoint = phys.PrevPoint(point);
        float vKin = Math.Clamp(Vector3.Dot(kin.PointVelocity(kinematicPoint), normal), 0, settings.KinematicPointSpeedMax);
        float limit = 0;
        if (w > 0)
        {
            float vNow = Vector3.Dot(point - prevPoint, normal) / h;
            limit = MathF.Max(0, vKin + settings.PopSpeed - vNow) * h / w;
        }
        max = MathF.Min(max, limit);

        var trace = _trace;
        PhysicsTrace.Snap snap = default;
        if (trace is not null) snap = new PhysicsTrace.Snap(phys.Position, phys.PrevPosition, phys.Rotation);
        var local = phys.ToLocal(point);   // материальная точка толчка — до поправки
        var saved = phys;
        ContactsPoseBeforePair(body);      // 7з-2: мир после пар решает от позы до пар

        float lambda = Xpbd.Contact(ref phys, point, normal, -depth, settings.ContactCompliance, h, max);
        if (lambda > 0 && w > 0) lambda = KinematicExactLimit(ref phys, in saved, local, point, prevPoint, normal, depth, (vKin + settings.PopSpeed) * h, lambda, h);
        if (lambda > 0)
        {
            ContactsProbeOf(body);   // 7з-2: толчок сдвинул и повернул тело — щуп пар подшага нынешний
            ContactsPress(body);     // 7з-2: толчок мог вдавить тело в землю — мир после пар
            if (pair.Used == 0) _kinematicSubstepPairs++;
            pair.Used = MathF.Min(_kinematicPairBudget, pair.Used + lambda);
            if (_kinematicPairBudget > 0) _kinematicSubstepShare = MathF.Max(_kinematicSubstepShare, pair.Used / _kinematicPairBudget);
            if (source == KinematicSource.Pass) pair.Remaining = MathF.Max(0, pair.Remaining - lambda / h);
            // отдача — против поправки физической стороны (+λn)
            if (source != KinematicSource.NoReaction) _kinematicReaction[pusher] -= normal * (lambda / h);
        }
        if (trace is not null) KinematicTrace(trace, kinematic, body, in snap, local, prevPoint, point, normal, depth, vKin, lambda, h, in pair, source);
        return lambda;
    }

    /// <summary>Допуск точного предела скорости толчка, м/с: точка тела после толчка не быстрее толкающей плюс <c>PopSpeed</c> — с этим запасом.</summary>
    private const float KinematicLimitSlack = 1e-4f;

    /// <summary>
    /// Точный предел скорости толчка (7з-2): λ предела считается линейно (<c>сдвиг = λ·w</c>), а поворот от поправки — нелинеен: точка
    /// лёгкого тела (стопа, кисть) уходила за предел до 2,6 % (замер 7к: выталкивание 1,026 м/с при <c>PopSpeed</c> 1, инвариант
    /// <c>physics.pushes</c>). После поправки мерится настоящий сдвиг материальной точки вдоль нормали от прошлой позы; больше
    /// <paramref name="allowed"/> — тело возвращается в позу до толчка, и λ урезается в долю лишнего от собственного сдвига поправки (до
    /// трёх раз: ошибка — второго порядка, сходится сразу). Возвращает применённую λ.
    /// </summary>
    private float KinematicExactLimit(ref BodyState phys, in BodyState saved, Vector3 local, Vector3 point, Vector3 prevPoint, Vector3 normal,
        float depth, float allowed, float lambda, float h)
    {
        float before = Vector3.Dot(point - prevPoint, normal);
        for (int k = 0; k < 3; k++)
        {
            float moved = Vector3.Dot(phys.ToWorld(local) - prevPoint, normal);
            float over = moved - allowed;
            float own = moved - before;
            if (!(over > KinematicLimitSlack * h) || !(own > 0)) return lambda;
            float scaled = lambda * MathF.Max(0, (own - over) / own);
            phys = saved;
            lambda = scaled > 0 ? Xpbd.Contact(ref phys, point, normal, -depth, _settings.ContactCompliance, h, scaled) : 0;
            if (!(lambda > 0)) return 0;
        }
        return lambda;
    }

    /// <summary>Трасса толчка: поправка источника <c>PushBody + source</c>, контакт, точный импульс, доли бюджетов, выталкивание.</summary>
    private void KinematicTrace(PhysicsTrace trace, int kinematic, int body, in PhysicsTrace.Snap snap, Vector3 local, Vector3 prevPoint,
        Vector3 point, Vector3 normal, float depth, float vKin, float lambda, float h, in KinematicPair pair, KinematicSource source)
    {
        ref readonly var phys = ref _bodies[body];
        var src = PhysicsSource.PushBody + (byte)source;
        trace.Correction(body, src, h, in snap, phys.Position, phys.PrevPosition, phys.Rotation, phys.Mass);
        var moved = phys.ToWorld(local);
        float solved = Vector3.Dot(moved - point, normal);
        trace.RecordContact(src, TraceBody(body), TraceBody(kinematic), point, normal, depth, solved);
        if (!(lambda > 0)) return;
        // скорость точки вдоль нормали после толчка сверх толкающей точки — против PopSpeed
        float pop = Vector3.Dot(moved - prevPoint, normal) / h - vKin;
        float passShare = source == KinematicSource.Pass ? KinematicPassShare(in pair) : -1;
        trace.Push(src, _bodyOwner[body], lambda / h, pop, passShare);
    }

    partial void EndKinematicSubstep()
    {
        _trace?.PairBudget(_kinematicSubstepShare, _kinematicSubstepPairs);
    }
}
