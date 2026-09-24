using System.Numerics;

namespace Sword.Engine.Physics;

// 7в: суставы сочленений — описания, привязка по текущей позе, проход решателя в подшаге (точка + предел по порядку
// суставов), журнал срабатываний пределов, расслабление скоростей, включение, замер. Крючки JointsGrow, JointsReset,
// SolveJoints, RelaxJoints (объявлены в PhysicsWorld.cs); внутренние типы и математика — Joints.cs. Идеи — Fighter.Joints,
// RebindJoints, цикл суставов SolveConstraints SWORD 2eab13b (источник, не образец; отличия — README «7в»).
public sealed partial class PhysicsWorld
{
    // По слотам сочленений: записи суставов (массив переиспользуется), их число, журнал срабатываний пределов за шаг.
    private JointRecord[]?[] _joints = [];
    private int[] _jointCount = [];
    private JointLimitHit[]?[] _jointHits = [];
    private int[] _jointHitCount = [];

    public void SetJoints(ArticulationHandle articulation, ReadOnlySpan<JointDesc> joints)
    {
        Check(articulation);
        int a = articulation.Index, n = joints.Length;
        for (int j = 0; j < n; j++)
            if (JointsProblem(joints, j, _artCount[a]) is { } problem)
                throw new ArgumentException($"сустав {j} сочленения {articulation}: {problem}", nameof(joints));
        SleepWake(a, WakeReason.Command);   // 7з-3: суставы будят
        var records = _joints[a];
        if (n > 0 && (records is null || records.Length < n)) _joints[a] = records = new JointRecord[n];
        for (int j = 0; j < n; j++) records![j] = JointRecord.Of(joints[j]);
        _jointCount[a] = n;
        _jointHitCount[a] = 0;
        // Журнал: за подшаг — не больше двух срабатываний на сустав (шарнир: ось и сгиб; наклон-скручивание: наклон и
        // скручивание), подшагов — из настроек.
        JointsEnsureLog(a, 2 * n * _settings.Substeps);
        JointsRebind(a);
    }

    public int JointCount(ArticulationHandle articulation)
    {
        Check(articulation);
        return _jointCount[articulation.Index];
    }

    public JointDesc Joint(ArticulationHandle articulation, int joint) => JointsAt(articulation, joint).Desc;

    public void RebindJoints(ArticulationHandle articulation)
    {
        Check(articulation);
        SleepWake(articulation.Index, WakeReason.Command);   // 7з-3
        JointsRebind(articulation.Index);
    }

    public void SetJointEnabled(ArticulationHandle articulation, int joint, bool enabled)
    {
        ref var r = ref JointsAt(articulation, joint);
        if (r.Enabled == enabled) return;
        SleepWake(articulation.Index, WakeReason.Command);   // 7з-3: включение и выключение сустава будят
        r.Enabled = enabled;
    }

    public bool IsJointEnabled(ArticulationHandle articulation, int joint) => JointsAt(articulation, joint).Enabled;

    public JointBinding Binding(ArticulationHandle articulation, int joint)
    {
        ref var r = ref JointsAt(articulation, joint);
        return new JointBinding(r.ParentAnchor, r.Rest);
    }

    public ReadOnlySpan<JointLimitHit> JointLimitHits(ArticulationHandle articulation)
    {
        Check(articulation);
        int a = articulation.Index;
        return _jointHits[a].AsSpan(0, _jointHitCount[a]);
    }

    public JointMeasure MeasureJoint(ArticulationHandle articulation, int joint)
    {
        ref var r = ref JointsAt(articulation, joint);
        var members = _artBodies[articulation.Index]!;
        return Joints.Measure(r, _bodies[members[r.Desc.Parent]], _bodies[members[r.Desc.Child]]);
    }

    // ——— Крючки (контракт 7б) ———

    partial void JointsGrow(int articulations, int bodies)
    {
        if (_jointCount.Length >= articulations) return;
        Array.Resize(ref _joints, articulations);
        Array.Resize(ref _jointCount, articulations);
        Array.Resize(ref _jointHits, articulations);
        Array.Resize(ref _jointHitCount, articulations);
    }

    partial void JointsReset(int articulation)
    {
        // Массивы остаются за слотом (следующее сочленение того же размера — без выделения памяти).
        _jointCount[articulation] = 0;
        _jointHitCount[articulation] = 0;
        JointsReserve(articulation, _artCount[articulation] - 1);
    }

    /// <summary>
    /// Место под суставы дерева сочленения (тел − 1) и их журнал — за слотом с его создания (7з): первый <see cref="SetJoints"/> дерева
    /// посреди игры (переход бойца в труп) памяти не выделяет. Слот, где место уже есть, — ничего.
    /// </summary>
    private void JointsReserve(int a, int joints)
    {
        if (joints <= 0) return;
        var records = _joints[a];
        if (records is null || records.Length < joints) _joints[a] = new JointRecord[joints];
        JointsEnsureLog(a, 2 * joints * _settings.Substeps);
    }

    partial void SolveJoints(float h, int substep)
    {
        float compliance = _settings.JointLimitCompliance;
        _jointsPopDepth = _settings.JointPopSpeed * h;
        for (int a = 0; a < _artCapacity; a++)
            if (_jointCount[a] > 0) JointsSolveArticulation(a, h, substep, compliance);
    }

    /// <summary>
    /// Сколько унаследованного разрыва точки сустава сводится за подшаг, м (7з-2): <c>JointPopSpeed · h</c> (∞ — весь); пишет
    /// <see cref="SolveJoints"/>.
    /// </summary>
    private float _jointsPopDepth = float.PositiveInfinity;

    partial void RelaxJoints(float h, int substep)
    {
        for (int a = 0; a < _artCapacity; a++)
            if (_jointCount[a] > 0) JointsRelaxArticulation(a, substep);
    }

    // ——— Проход решателя ———

    /// <summary>
    /// Суставы сочленения в подшаге — по порядку описания, один проход Гаусса — Зейделя: предел (податливость описания или
    /// настроек), затем точка сустава (жёстко). Подшаг 0 — начало шага: журнал прошлого шага очищается (и у сочленения без тел
    /// на физике). Кинематическое тело — неподвижная сторона; сустав между двумя кинематическими и выключенный — пропускаются.
    /// </summary>
    private void JointsSolveArticulation(int a, float h, int substep, float compliance)
    {
        if (substep == 0) _jointHitCount[a] = 0;
        if (_contactsSimulated[a] == 0) return;   // тел на физике нет (счёт шага — режим внутри шага не меняется)
        int n = _jointCount[a];
        JointsEnsureLog(a, _jointHitCount[a] + 2 * n);
        if (_trace is not null) JointsTraceBegin(a);
        var records = _joints[a]!;
        var members = _artBodies[a]!;
        for (int j = 0; j < n; j++)
            if (records[j].Enabled) JointsSolveOne(a, j, ref records[j], members, h, substep, compliance);
        if (_trace is not null) JointsTraceEnd(a, h);
    }

    private void JointsSolveOne(int a, int j, ref JointRecord r, int[] members, float h, int substep, float defaultCompliance)
    {
        ref var c = ref _bodies[members[r.Desc.Child]];
        ref var p = ref _bodies[members[r.Desc.Parent]];
        bool cs = c.Mode == BodyMode.Simulated, ps = p.Mode == BodyMode.Simulated;
        if (!cs && !ps) return;

        // Сначала предел (поворот вокруг центров масс), затем точка (у SWORD — наоборот; порядок Мюллера и др., 2020): поворот
        // предела уводит точку сустава, и при обратном порядке её сводит только следующий подшаг. Так после сустава его точка
        // сведена — ошибка точек в 5–10 раз меньше; за это у жёсткого упора остаётся малый заход (≈ 0,008 рад при 6 рад/с),
        // который гаснет за 2–3 тика (README «7в», замеры).
        float alpha = r.Desc.LimitCompliance ?? defaultCompliance;
        if (r.Desc.Kind == JointKind.Hinge)
        {
            // Ось шарнира ребёнка — к оси, которую держит родитель, сверх люфта; затем сгиб — в [BendMin, BendMax].
            var ap = Joints.ParentHingeAxis(r.Desc, p.Rotation, r.Rest);
            var ac = Joints.ChildHingeAxis(r.Desc, c.Rotation);
            var cross = Vector3.Cross(ac, ap);
            float dot = Vector3.Dot(ac, ap);
            // 7з-2: быстрое «в люфте» — atan2(|ac × ap|, ac·ap) ≤ люфт ⇔ |ac × ap|² ≤ (ac·ap)²·tg²(люфт) при ac·ap > 0
            if (!(dot > 0 && cross.LengthSquared() <= dot * dot * r.SlackTan2))
            {
                float len = cross.Length();
                float off = MathF.Atan2(len, dot);
                if (off > r.Desc.AxisSlack)
                {
                    if (len > Joints.AxisEpsilon) JointsLimit(a, j, ref c, cs, ref p, ps, cross * ((off - r.Desc.AxisSlack) / len), alpha, h, substep);
                    // Ось родителя — по позе после поправки оси (у SWORD сгиб мерился вокруг оси до неё).
                    ap = Joints.ParentHingeAxis(r.Desc, p.Rotation, r.Rest);
                }
            }
            float bend = Joints.Bend(r.Desc, p.Rotation, c.Rotation, ap);
            float clamped = Math.Clamp(bend, r.Desc.BendMin, r.Desc.BendMax);
            if (clamped != bend)
            {
                // вдоль градиента сгиба: δ = ∇·(обрезанный − сгиб)/|∇|² (при оси капсулы ⟂ оси шарнира — ровно вокруг оси)
                var grad = Joints.BendGradient(r.Desc, c.Rotation, ap);
                JointsLimit(a, j, ref c, cs, ref p, ps, grad * ((clamped - bend) / grad.LengthSquared()), alpha, h, substep);
            }
        }
        else
        {
            // Наклон, затем скручивание — каждое вдоль градиента своей меры, левым поворотом в системе покоя (в мир — P·Rest).
            var q = Joints.Deviation(p.Rotation, r.Rest, c.Rotation);
            if (Joints.SwingCorrection(r.Desc, q, out var swing, r.SwingTanHalf2))
            {
                JointsLimit(a, j, ref c, cs, ref p, ps, Vector3.Transform(swing, p.Rotation * r.Rest), alpha, h, substep);
                q = Joints.Deviation(p.Rotation, r.Rest, c.Rotation);
            }
            if (Joints.TwistCorrection(r.Desc, q, out var twist, r.TwistTanHalf))
                JointsLimit(a, j, ref c, cs, ref p, ps, Vector3.Transform(twist, p.Rotation * r.Rest), alpha, h, substep);
        }

        // Точка сустава — жёстко (α = 0); разрыв, унаследованный от прошлого подшага, — не быстрее JointPopSpeed (7з-2).
        var pc = c.ToWorld(r.Desc.ChildAnchor);
        var pp = p.ToWorld(r.ParentAnchor);
        var gap = pc - pp;
        float distance = gap.Length();
        if (distance < Xpbd.AttachEpsilon) return;
        float solve = distance > _jointsPopDepth ? JointsSolvedGap(ref r, in c, in p, distance, h) : distance;
        if (!(solve > 0)) return;
        var n = gap / distance;
        if (cs && ps) Xpbd.Along(ref c, pc, ref p, pp, n, solve, 0, h);
        else if (cs) Xpbd.Along(ref c, pc, n, solve, 0, h);
        else Xpbd.Along(ref p, pp, -n, solve, 0, h);
    }

    /// <summary>
    /// Сколько разрыва точки сустава сводить в этом подшаге (7з-2; как <see cref="ContactsSolvedDepth"/> для глубины): старый разрыв —
    /// та же пара точек в прошлых позах (начало подшага: его оставили контакты прошлого подшага, решаемые после суставов) — уходит не
    /// быстрее <see cref="PhysicsSettings.JointPopSpeed"/>; новый разрыв этого подшага (интеграция разнесла тела) — целиком. Без предела
    /// одна жёсткая поправка переводила разрыв 5…7 мм после удара трупа о землю в скорость ~3 м/с — всплеск энергии до 10 Дж за подшаг.
    /// Зовётся, только когда разрыв больше <c>JointPopSpeed · h</c> (меньший решается целиком при любой старой глубине).
    /// </summary>
    private float JointsSolvedGap(ref JointRecord r, in BodyState c, in BodyState p, float len, float h)
    {
        var pc0 = c.PrevPosition + Vector3.Transform(r.Desc.ChildAnchor, c.PrevRotation);
        var pp0 = p.PrevPosition + Vector3.Transform(r.ParentAnchor, p.PrevRotation);
        float solved = ContactsSolvedDepth(len, Vector3.Distance(pc0, pp0), _settings.JointPopSpeed, h);
        return MathF.Max(solved, len - JointsPopLag);
    }

    /// <summary>
    /// Наибольший разрыв точки сустава, который сводится с ограничением скорости, м (7з-2): больше — лишнее сводится сразу. Удар о землю
    /// рвёт сустав на 5…7 мм — это сводится не быстрее <see cref="PhysicsSettings.JointPopSpeed"/>; труп, зажатый в куче (пары решаются после
    /// суставов), без предела отставания рвал сустав на сантиметры (замер: куча тестов — до 361 мм при падении). Замер (толпа 32 трупов на
    /// паке): 0,5 см — всплески s8 до 8 Дж; 1 см — до 2,5 Дж, разрыв при падении толпы ≤ 16 мм; 2 см — ≤ 25 мм.
    /// </summary>
    internal const float JointsPopLag = 0.01f;

    /// <summary>
    /// Поправка предела: повернуть ребёнка относительно родителя на <paramref name="theta"/> (мир, рад) —
    /// <see cref="Xpbd.Angular(ref BodyState, ref BodyState, Vector3, float, float)"/>; кинематическая сторона неподвижна
    /// (кинематический ребёнок — родитель поворачивается на −θ). Применённая поправка — в журнал: сустав, подшаг, ось θ.
    /// </summary>
    private void JointsLimit(int a, int j, ref BodyState c, bool cs, ref BodyState p, bool ps, Vector3 theta, float alpha, float h, int substep)
    {
        float lambda = cs && ps ? Xpbd.Angular(ref c, ref p, theta, alpha, h)
            : cs ? Xpbd.Angular(ref c, theta, alpha, h)
            : Xpbd.Angular(ref p, -theta, alpha, h);
        if (!(lambda > 0)) return;
        _jointHits[a]![_jointHitCount[a]++] = new JointLimitHit(j, substep, Vector3.Normalize(theta));
    }

    // ——— Расслабление скоростей ———

    /// <summary>
    /// Расслабление скоростей сочленения в подшаге (после скоростного прохода контактов): (1) у каждого включённого сустава
    /// относительная скорость точек сустава гасится целиком импульсом в точку (<c>K j = −Δv</c>, K — полная 3×3: вращение
    /// перекладывает скорость в поперечные оси); (2) по срабатываниям пределов этого подшага, по порядку, гасится угловая
    /// скорость ребёнка относительно родителя «от предела» (вдоль оси поправки, если положительна) — вместе с точкой сустава
    /// (<see cref="JointsRelaxLimit"/>): предел, как и контакт, не отскакивает. Каждый импульс — неупругий по своим
    /// скоростям: энергии не добавляет.
    /// </summary>
    private void JointsRelaxArticulation(int a, int substep)
    {
        if (_contactsSimulated[a] == 0) return;
        var records = _joints[a]!;
        var members = _artBodies[a]!;
        int n = _jointCount[a];
        for (int j = 0; j < n; j++)
            if (records[j].Enabled) JointsRelaxPoint(ref records[j], members);
        // Срабатывания этого подшага — хвост журнала (журнал упорядочен по подшагам).
        var log = _jointHits[a];
        int end = _jointHitCount[a], first = end;
        while (first > 0 && log![first - 1].Substep == substep) first--;
        for (int k = first; k < end; k++) JointsRelaxLimit(records, members, log![k]);
    }

    private void JointsRelaxPoint(ref JointRecord r, int[] members)
    {
        ref var c = ref _bodies[members[r.Desc.Child]];
        ref var p = ref _bodies[members[r.Desc.Parent]];
        bool cs = c.Mode == BodyMode.Simulated, ps = p.Mode == BodyMode.Simulated;
        if (!cs && !ps) return;
        var pc = c.ToWorld(r.Desc.ChildAnchor);
        var pp = p.ToWorld(r.ParentAnchor);
        var dv = c.PointVelocity(pc) - p.PointVelocity(pp);
        if (dv.LengthSquared() < Joints.RelaxEpsilonSquared) return;
        Vector3 rc = pc - c.Position, rp = pp - p.Position;
        Vector3 kx = default, ky = default, kz = default;
        // обратные инерции — матрицы подшага (7з-2: поза после обновления скоростей не меняется)
        ref readonly var wc = ref _stepInertia[members[r.Desc.Child]];
        ref readonly var wp = ref _stepInertia[members[r.Desc.Parent]];
        if (cs) Joints.AddPointMass(in wc, rc, ref kx, ref ky, ref kz);
        if (ps) Joints.AddPointMass(in wp, rp, ref kx, ref ky, ref kz);
        if (!Joints.Solve(kx, ky, kz, -dv, out var impulse)) return;
        if (cs) wc.Impulse(ref c, impulse, rc);
        if (ps) wp.Impulse(ref p, -impulse, rp);
    }

    /// <summary>
    /// Отскок от предела: относительная угловая скорость вдоль оси поправки <c>u = (ω_c − ω_p)·n</c>, если положительна, и
    /// относительная скорость точек сустава гасятся вместе — импульс в точку <c>j</c> и угловой <c>λn</c> из системы 4×4
    /// <c>[K B; Bᵀ w]·[j; λ] = −[Δv; u]</c> (дополнение Шура: две системы 3×3 с K). У SWORD гасилась только угловая
    /// скорость: у тела на точке сустава оставалась скорость центра, и следующий подшаг возвращал её вращением вокруг точки —
    /// конечность отскакивала от упора (README «7в»). Импульс — проекция в метрике масс: энергии не добавляет.
    /// </summary>
    private void JointsRelaxLimit(JointRecord[] records, int[] members, in JointLimitHit hit)
    {
        ref var r = ref records[hit.Joint];
        if (!r.Enabled) return;
        ref var c = ref _bodies[members[r.Desc.Child]];
        ref var p = ref _bodies[members[r.Desc.Parent]];
        bool cs = c.Mode == BodyMode.Simulated, ps = p.Mode == BodyMode.Simulated;
        if (!cs && !ps) return;
        var n = hit.Axis;
        float u = Vector3.Dot(c.AngularVelocity - p.AngularVelocity, n);
        if (!(u > 0)) return;

        var pc = c.ToWorld(r.Desc.ChildAnchor);
        var pp = p.ToWorld(r.ParentAnchor);
        Vector3 rc = pc - c.Position, rp = pp - p.Position;
        var dv = c.PointVelocity(pc) - p.PointVelocity(pp);
        ref readonly var wc = ref _stepInertia[members[r.Desc.Child]];
        ref readonly var wp = ref _stepInertia[members[r.Desc.Parent]];
        Vector3 kx = default, ky = default, kz = default, b = default;
        float w = 0;
        if (cs)
        {
            Joints.AddPointMass(in wc, rc, ref kx, ref ky, ref kz);
            var ic = wc.Apply(n);
            b += Vector3.Cross(ic, rc);
            w += Vector3.Dot(n, ic);
        }
        if (ps)
        {
            Joints.AddPointMass(in wp, rp, ref kx, ref ky, ref kz);
            var ip = wp.Apply(n);
            b += Vector3.Cross(ip, rp);
            w += Vector3.Dot(n, ip);
        }
        if (!Joints.Solve(kx, ky, kz, dv, out var kdv) || !Joints.Solve(kx, ky, kz, b, out var kb)) return;
        float schur = w - Vector3.Dot(b, kb);
        if (!(schur > Joints.SchurEpsilon * w)) return;
        float lambda = (Vector3.Dot(b, kdv) - u) / schur;
        var j = -(kdv + kb * lambda);
        var l = n * lambda;
        if (cs)
        {
            wc.Impulse(ref c, j, rc);
            c.AngularVelocity += wc.Apply(l);
        }
        if (ps)
        {
            wp.Impulse(ref p, -j, rp);
            p.AngularVelocity -= wp.Apply(l);
        }
    }

    // ——— Помощники ———

    private ref JointRecord JointsAt(ArticulationHandle articulation, int joint)
    {
        Check(articulation);
        int a = articulation.Index;
        if ((uint)joint >= (uint)_jointCount[a])
            throw new ArgumentOutOfRangeException(nameof(joint), joint, $"в сочленении {articulation} суставов {_jointCount[a]}");
        return ref _joints[a]![joint];
    }

    /// <summary>Привязка по текущей позе: точка на родителе — точка ребёнка в системе родителя, поза покоя — <c>conj(parent) · child</c>.</summary>
    private void JointsRebind(int a)
    {
        var records = _joints[a];
        var members = _artBodies[a]!;
        int n = _jointCount[a];
        for (int j = 0; j < n; j++) JointsBind(ref records![j], members);
    }

    private void JointsBind(ref JointRecord r, int[] members)
    {
        ref readonly var p = ref _bodies[members[r.Desc.Parent]];
        ref readonly var c = ref _bodies[members[r.Desc.Child]];
        r.ParentAnchor = p.ToLocal(c.ToWorld(r.Desc.ChildAnchor));
        r.Rest = Quaternion.Normalize(Quaternion.Conjugate(p.Rotation) * c.Rotation);
    }

    /// <summary>Журнал сочленения — не меньше <paramref name="need"/> записей (рост — только при новых размерах или подшагах).</summary>
    private void JointsEnsureLog(int a, int need)
    {
        var log = _jointHits[a];
        if (need == 0 || (log is not null && log.Length >= need)) return;
        Array.Resize(ref _jointHits[a], Math.Max(need, log is null ? 0 : log.Length * 2));
    }

    private void JointsTraceBegin(int a)
    {
        var members = _artBodies[a]!;
        int n = _artCount[a];
        for (int k = 0; k < n; k++)
            if (_bodies[members[k]].Mode == BodyMode.Simulated) TraceBegin(members[k]);
    }

    private void JointsTraceEnd(int a, float h)
    {
        var members = _artBodies[a]!;
        int n = _artCount[a];
        for (int k = 0; k < n; k++)
            if (_bodies[members[k]].Mode == BodyMode.Simulated) TraceEnd(members[k], PhysicsSource.Joints, h);
    }

    private static string? JointsProblem(ReadOnlySpan<JointDesc> joints, int j, int bodies)
    {
        ref readonly var d = ref joints[j];
        if ((uint)d.Parent >= (uint)bodies || (uint)d.Child >= (uint)bodies) return "родитель и ребёнок — тела сочленения";
        if (d.Parent == d.Child) return "родитель и ребёнок — разные тела";
        for (int k = 0; k < j; k++)
            if (joints[k].Child == d.Child) return "тело — ребёнок не больше чем в одном суставе";
        if (!Finite(d.ChildAnchor)) return "точка на ребёнке — конечная";
        if (!(float.IsFinite(d.BendMin) && float.IsFinite(d.BendMax) && float.IsFinite(d.AxisSlack) && Finite(d.HingeAxis)
            && float.IsFinite(d.SwingXMin) && float.IsFinite(d.SwingXMax) && float.IsFinite(d.SwingZ) && float.IsFinite(d.Twist)))
            return "пределы — конечные";
        if (d.LimitCompliance is { } lc && !(float.IsFinite(lc) && lc >= 0)) return "податливость предела — конечная, не меньше 0";
        switch (d.Kind)
        {
            case JointKind.Hinge:
                if (!(d.BendMin <= d.BendMax)) return "шарнир: BendMin ≤ BendMax";
                if (!(d.AxisSlack >= 0)) return "шарнир: люфт оси — не меньше 0";
                if (d.BendSign is not (1 or -1)) return "шарнир: знак сгиба — +1 или −1";
                if (!(MathF.Abs(d.HingeAxis.LengthSquared() - 1) <= UnitTolerance)) return "шарнир: ось — единичная";
                return null;
            case JointKind.SwingTwist:
                if (!(d.SwingXMin <= 0 && d.SwingXMax >= 0)) return "наклон-скручивание: SwingXMin ≤ 0 ≤ SwingXMax";
                if (!(d.SwingZ > 0)) return "наклон-скручивание: SwingZ — больше 0";
                if (!(d.Twist >= 0)) return "наклон-скручивание: Twist — не меньше 0";
                return null;
            default:
                return "вид — Hinge или SwingTwist";
        }
    }
}
