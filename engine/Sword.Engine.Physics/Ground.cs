using System.Numerics;

namespace Sword.Engine.Physics;

// Контакт тела с землёй по граням IGround: сначала нормаль всех касаний обоих торцов, затем трение покоя с общим для тела
// бюджетом Кулона.
public sealed partial class PhysicsWorld
{
    /// <summary>Касание торца с землёй в подшаге: торец, нормаль грани, λn; застарелая ли глубина (глубже <c>PopSpeed·h</c> в начале подшага).</summary>
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

    /// <summary>Первый проход мира по слотам тел: Σλn и потраченное трением, кг·м, — бюджет мира после пар продолжает его.</summary>
    private float[] _groundNormalSum = [], _groundFrictionSpent = [];

    /// <summary>Идёт мир после пар: бюджет трения — вместе с первым проходом, запись первого прохода не трогается.</summary>
    private bool _groundPressedPass;

    /// <summary>Тело на физике против земли: нормаль нижнего торца, затем верхнего; затем трение покоя с общим бюджетом.</summary>
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

    /// <summary>Торец против земли: все грани, в которые вошла сфера торца, — по одной; старая глубина — та же грань в прошлой позе.</summary>
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
    /// Грань против торца: глубина — заново в нынешнем положении (у первой — из касания), выталкивание по нормали
    /// (<see cref="ContactsWorldPoint"/>). Старая глубина считается, только если может быть больше <c>PopSpeed·h</c>
    /// (<c>глубина + 2·смещение &gt; PopSpeed·h</c>). Торцы короткой капсулы почти совпадают: λ второго ≈ 0, бюджет трения не удваивается.
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
    /// Трение покоя тела о землю, «всё или ничего», с общим бюджетом <c>μs · Σλn</c> всех касаний: по своей λn касание держало
    /// бы тело на двух торцах только до <c>tg ≈ μs · доля</c>. Удержавшее касание застарелой глубины — затем
    /// <see cref="GroundFrictionPress"/>. Мир после пар — бюджет вместе с первым проходом: сдвиг парами держит вся нагрузка тела.
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
    /// Вдавливание трением покоя застарелой глубины: поправка трения поворачивает тело, и торец уходит по нормали.
    /// <paramref name="press"/>, м — насколько глубже начала подшага за вычетом выхода <c>PopSpeed·h</c>; решается сразу, иначе
    /// стопа под тягой сустава тонула (2 → 26 мм за 4 тика). Только застарелая глубина и только сверх выхода: вышедшее тело не подпрыгивает.
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
