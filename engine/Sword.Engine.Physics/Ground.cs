using System.Numerics;

namespace Sword.Engine.Physics;

// Контакт тела с землёй по граням IGround (идея — GroundEnd SWORD 2eab13b, research-arena-tick §1.3, §6): сначала нормаль
// всех касаний обоих торцов, затем трение покоя с общим для тела бюджетом Кулона.
public sealed partial class PhysicsWorld
{
    /// <summary>
    /// Касание торца с землёй в подшаге: торец (по оси от центра, м), нормаль грани, нормальная λ; застарелая ли глубина (в начале
    /// подшага глубже <c>PopSpeed·h</c> — нормаль вывела торец ровно на <c>PopSpeed·h</c>).
    /// </summary>
    private struct GroundContactEntry
    {
        public float Along, LambdaN;
        public bool Old;
        public Vector3 Normal;
    }

    /// <summary>Вдавливание трением покоя сверх выхода, м, меньше которого не решается.</summary>
    private const float GroundFrictionPressSlack = 1e-5f;

    /// <summary>Касания тела с землёй в подшаге (оба торца × до <see cref="IGround.MaxTouches"/> граней) — для трения после нормали.</summary>
    private readonly GroundContactEntry[] _groundContacts = new GroundContactEntry[2 * IGround.MaxTouches];
    private int _groundContactCount;

    /// <summary>
    /// Первый проход мира подшага по слотам тел: Σλn касаний земли и потраченное трением покоя, кг·м, — бюджет трения мира после пар
    /// (<see cref="ContactsPressedWorld"/>) продолжает бюджет первого прохода. Пишется в начале <see cref="GroundBody"/> первого прохода.
    /// </summary>
    private float[] _groundNormalSum = [], _groundFrictionSpent = [];

    /// <summary>Идёт мир после пар: бюджет трения — вместе с первым проходом, запись первого прохода не трогается.</summary>
    private bool _groundPressedPass;

    /// <summary>
    /// Тело на физике против земли: нормаль торцов капсулы по очереди — нижний конец оси, затем верхний (у шара торец один);
    /// затем трение покоя всех касаний с общим бюджетом тела.
    /// </summary>
    private void GroundBody(int body, float h)
    {
        if (!_groundPressedPass) _groundNormalSum[body] = _groundFrictionSpent[body] = 0;
        GroundNormals(body, h);
        if (_contactsFriction > 0 && _groundContactCount > 0) GroundFriction(body, h);
    }

    /// <summary>Нормали торцов тела против земли (без трения): нижний конец оси, затем верхний; касания — в <see cref="_groundContacts"/>.</summary>
    private void GroundNormals(int body, float h)
    {
        _groundContactCount = 0;
        float half = _bodies[body].HalfLength;
        GroundEnd(body, -half, h);
        if (half > 0) GroundEnd(body, half, h);
    }

    /// <summary>
    /// Торец капсулы (<paramref name="along"/> — по оси от центра, м) против земли: все грани, в которые вошла сфера торца
    /// (<see cref="IGround.Touches"/>, по возрастанию номера), решаются по одной. Старая глубина — та же грань в прошлой позе
    /// торца (начало подшага).
    /// </summary>
    private void GroundEnd(int body, float along, float h)
    {
        ref var s = ref _bodies[body];
        var local = new Vector3(0, along, 0);
        var end = s.ToWorld(local);
        int count = _ground!.Touches(end, s.Radius, _groundTouches);
        if (count <= 0) return;
        count = Math.Min(count, _groundTouches.Length);
        var prevEnd = s.PrevPosition + Vector3.Transform(local, s.PrevRotation);
        // Первая грань — торец, глубина и нормаль касания (тело с тех пор не двигалось: тот же расчёт), остальные — заново
        GroundFace(body, _groundTouches[0].Face, along, prevEnd, h, in _groundTouches[0], end);
        for (int i = 1; i < count; i++) GroundFace(body, _groundTouches[i].Face, along, prevEnd, h, in Unsolved, default);
    }

    /// <summary>Касание без глубины — «посчитать грань заново в нынешнем положении торца».</summary>
    private static readonly GroundTouch Unsolved = new(-1, default, 0);

    /// <summary>
    /// Грань <paramref name="face"/> против торца: глубина — заново в нынешнем положении торца (прошлые грани и торец уже сдвинули
    /// тело), у первой грани — из касания <paramref name="touch"/> (тот же расчёт); касание ставит <see cref="BodyState.Grounded"/>
    /// (трение о землю профиля — в обновлении скоростей); выталкивание по нормали грани (<see cref="ContactsWorldPoint"/>: выход старой
    /// глубины — не быстрее <see cref="PhysicsSettings.PopSpeed"/>). Старая глубина (прошлая поза торца) нужна, только если она может быть
    /// больше <c>PopSpeed·h</c>: глубина грани меняется не быстрее смещения центра сферы (<see cref="IGround"/>), и при
    /// <c>глубина + 2·смещение ≤ PopSpeed·h</c> решается вся глубина — второй расчёт грани не нужен (результат тот же). Торцы
    /// короткой капсулы почти совпадают: второй видит глубину, уже закрытую первым, его λ ≈ 0 — нормаль (а с ней и бюджет трения) не
    /// удваивается.
    /// </summary>
    private void GroundFace(int body, int face, float along, Vector3 prevEnd, float h, in GroundTouch touch, Vector3 touchEnd)
    {
        ref var s = ref _bodies[body];
        var ground = _ground!;
        Vector3 end, n;
        float pen;
        if (touch.Face == face)
        {
            end = touchEnd;
            pen = touch.Depth;
            n = touch.Normal;
        }
        else
        {
            end = s.ToWorld(new Vector3(0, along, 0));
            pen = ground.Depth(end, s.Radius, face, out n);
        }
        if (!(pen > 0)) return;
        float pen0 = pen + 2 * Vector3.Distance(end, prevEnd) <= _contactsPopSpeed * h ? 0 : ground.Depth(prevEnd, s.Radius, face, out _);
        s.Grounded = true;
        float lambdaN = ContactsWorldPoint(body, PhysicsSource.Ground, end - n * s.Radius, n, pen, pen0, _contactsFriction, h);
        if (!(lambdaN > 0) || _groundContactCount == _groundContacts.Length) return;
        ref var c = ref _groundContacts[_groundContactCount++];
        c.Along = along;
        c.Normal = n;
        c.LambdaN = lambdaN;
        c.Old = pen0 > _contactsPopSpeed * h;
    }

    /// <summary>
    /// Трение покоя тела о землю в подшаге («всё или ничего», <see cref="Xpbd.Friction(ref BodyState, Vector3, Vector3, float, float, float, float, out Vector3)"/>)
    /// по касаниям, в точке после выталкивания. Бюджет — общий на тело: <c>μs · Σλn</c> всех его касаний в этом подшаге; касание
    /// держит, если его λt не больше остатка, и тратит его. У SWORD каждое касание держало по своей λn: первое касание гасит сдвиг
    /// всего тела, а его λn — лишь доля веса, поэтому тело на двух торцах держалось только до <c>tg ≈ μs · доля</c>, а не до μs.
    /// Удержавшее касание застарелой глубины — затем вдавливание трением (<see cref="GroundFrictionPress"/>). Трассе — каждое касание:
    /// держало — удержало сдвиг или сдвига не было; бюджет кончился — λt = 0 (трение с нулевым бюджетом ничего не делает).
    /// <para>
    /// Мир после пар — бюджет вместе с первым проходом: <c>μs · (Σλn первого + Σλn этого) − потраченное первым</c>. Трение держит сдвиг
    /// по земле, сделанный парами (от позы до пар), всей нагрузкой тела — как держало, пока мир решался после пар (толчок живого не сильнее
    /// трения труп не двигает: s3, s6); по одной λn второго прохода (только вдавливание парами) толчок сдвигал труп почти без трения.
    /// </para>
    /// </summary>
    private void GroundFriction(int body, float h)
    {
        ref var s = ref _bodies[body];
        float total = 0;
        for (int i = 0; i < _groundContactCount; i++) total += _groundContacts[i].LambdaN;
        float budget;
        if (_groundPressedPass)
            budget = _contactsFriction * (total + _groundNormalSum[body]) - _groundFrictionSpent[body];   // вместе с первым проходом
        else
        {
            _groundNormalSum[body] = total;
            budget = _contactsFriction * total;
        }
        float start = budget;
        GroundFrictionContacts(body, ref s, ref budget, h);
        if (!_groundPressedPass) _groundFrictionSpent[body] = start - budget;
    }

    private void GroundFrictionContacts(int body, ref BodyState s, ref float budget, float h)
    {
        for (int i = 0; i < _groundContactCount; i++)
        {
            ref var c = ref _groundContacts[i];
            var end = s.ToWorld(new Vector3(0, c.Along, 0));
            var p = end - c.Normal * s.Radius;   // материальная точка — после выталкивания
            var arm = end - s.Position;
            bool still = _trace is not null && GroundStill(in s, p, c.Normal);
            float lambdaT = Xpbd.Friction(ref s, p, c.Normal, budget, 1, 0, h, out var turn);
            budget -= lambdaT;
            if (lambdaT > 0 && c.Old) GroundFrictionPress(body, in c, -Vector3.Dot(Vector3.Cross(turn, arm), c.Normal) - _contactsPopSpeed * h, h);
            if (_trace is not null) TraceFriction(body, c.LambdaN, lambdaT, lambdaT > 0 || still);
        }
    }

    /// <summary>
    /// Вдавливание трением покоя застарелой глубины: касательная поправка трения поворачивает тело, и торец уходит и по нормали
    /// (обратная масса точки связывает касательное с нормальным через поворот). <paramref name="press"/>, м, — насколько трение сделало торец
    /// глубже, чем он был в начале подшага: сдвиг торца поворотом поправки (× плечо, первый порядок) за вычетом выхода нормали за подшаг
    /// (<c>PopSpeed·h</c>). Это решается сразу — трение не топит тело глубже начала подшага; иначе вдавленное снова застарелое, выходит не
    /// быстрее <see cref="PhysicsSettings.PopSpeed"/>, а трение вдавливает опять. Замер (толпа 32 трупов): стопа, которую сустав тянет
    /// вбок, тонула с 2 до 26 мм за 4 тика — нормаль выводила её на 2 мм за подшаг, трение возвращало 3; с правкой — не глубже вдавленного
    /// трением за подшаг. Только застарелая глубина и только сверх выхода: у лежащего тела (глубина — доли миллиметра, решается целиком)
    /// поведение прежнее, выход застарелой глубины не ускоряется (вышедшее тело не подпрыгивает — <c>SunkBody_…</c>).
    /// <para>
    /// Опыты (откачены; 205 мест трупа на ступенях пирамиды пака, окно покоя 2…7 с): трение о землю — касательный сдвиг целиком (решение
    /// 2 × 2 в касательной плоскости) и вдавливание, решаемое у всех касаний, — подъём энергии над началом окна больше 0,05 Дж у 4 мест из 205
    /// (вдоль сдвига без правки — 30), но труп через кромку ступени сползал с неё (нормаль вдавливания после трения — наискось у ребра: трение
    /// её уже не держит) и вышедшее тело подпрыгивало; касательное целиком без вдавливания — 45; с запретом нормального сдвига трением (блок
    /// 3 × 3) — склон держал сверх μ.
    /// </para>
    /// </summary>
    private void GroundFrictionPress(int body, in GroundContactEntry c, float press, float h)
    {
        if (!(press > GroundFrictionPressSlack)) return;
        ref var s = ref _bodies[body];
        var end = s.ToWorld(new Vector3(0, c.Along, 0));
        Xpbd.Contact(ref s, end - c.Normal * s.Radius, c.Normal, -press, 0, h);
    }

    /// <summary>Точка не сдвинулась вдоль земли за подшаг (меньше <see cref="Xpbd.FrictionEpsilon"/>) — трению держать нечего (трасса).</summary>
    private static bool GroundStill(in BodyState s, Vector3 p, Vector3 n)
    {
        var d = p - s.PrevPoint(p);
        return (d - n * Vector3.Dot(d, n)).Length() < Xpbd.FrictionEpsilon;
    }
}
