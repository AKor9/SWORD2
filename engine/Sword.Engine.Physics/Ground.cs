using System.Numerics;

namespace Sword.Engine.Physics;

// 7г: контакт тела с землёй по граням IGround (идея — GroundEnd SWORD 2eab13b, research-arena-tick §1.3, §6): сначала нормаль
// всех касаний обоих торцов, затем трение покоя с общим для тела бюджетом Кулона.
public sealed partial class PhysicsWorld
{
    /// <summary>Касание торца с землёй в подшаге: торец (по оси от центра, м), нормаль грани, нормальная λ.</summary>
    private struct GroundContactEntry
    {
        public float Along, LambdaN;
        public Vector3 Normal;
    }

    /// <summary>Касания тела с землёй в подшаге (оба торца × до <see cref="IGround.MaxTouches"/> граней) — для трения после нормали.</summary>
    private readonly GroundContactEntry[] _groundContacts = new GroundContactEntry[2 * IGround.MaxTouches];
    private int _groundContactCount;

    /// <summary>
    /// Тело на физике против земли: нормаль торцов капсулы по очереди — нижний конец оси, затем верхний (у шара торец один);
    /// затем трение покоя всех касаний с общим бюджетом тела.
    /// </summary>
    private void GroundBody(int body, float h)
    {
        _groundContactCount = 0;
        float half = _bodies[body].HalfLength;
        GroundEnd(body, -half, h);
        if (half > 0) GroundEnd(body, half, h);
        if (_contactsFriction > 0 && _groundContactCount > 0) GroundFriction(body, h);
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
        int count = _ground!.Touches(s.ToWorld(local), s.Radius, _groundTouches);
        if (count <= 0) return;
        count = Math.Min(count, _groundTouches.Length);
        var prevEnd = s.PrevPosition + Vector3.Transform(local, s.PrevRotation);
        for (int i = 0; i < count; i++) GroundFace(body, _groundTouches[i].Face, along, prevEnd, h);
    }

    /// <summary>
    /// Грань <paramref name="face"/> против торца: глубина — заново в нынешнем положении торца (прошлые грани и торец уже сдвинули
    /// тело); касание ставит <see cref="BodyState.Grounded"/> (трение о землю профиля — в обновлении скоростей); выталкивание по
    /// нормали грани (<see cref="ContactsWorldPoint"/>: выход старой глубины — не быстрее <see cref="PhysicsSettings.PopSpeed"/>).
    /// Торцы короткой капсулы почти совпадают: второй видит глубину, уже закрытую первым, его λ ≈ 0 — нормаль (а с ней и бюджет
    /// трения) не удваивается.
    /// </summary>
    private void GroundFace(int body, int face, float along, Vector3 prevEnd, float h)
    {
        ref var s = ref _bodies[body];
        var ground = _ground!;
        var end = s.ToWorld(new Vector3(0, along, 0));
        float pen = ground.Depth(end, s.Radius, face, out var n);
        if (!(pen > 0)) return;
        float pen0 = ground.Depth(prevEnd, s.Radius, face, out _);
        s.Grounded = true;
        float lambdaN = ContactsWorldPoint(body, PhysicsSource.Ground, end - n * s.Radius, n, pen, pen0, _contactsFriction, h);
        if (!(lambdaN > 0) || _groundContactCount == _groundContacts.Length) return;
        ref var c = ref _groundContacts[_groundContactCount++];
        c.Along = along;
        c.Normal = n;
        c.LambdaN = lambdaN;
    }

    /// <summary>
    /// Трение покоя тела о землю в подшаге («всё или ничего», <see cref="Xpbd.Friction(ref BodyState, Vector3, Vector3, float, float, float, float)"/>)
    /// по касаниям, в точке после выталкивания. Бюджет — общий на тело: <c>μs · Σλn</c> всех его касаний в этом подшаге; касание
    /// держит, если его λt не больше остатка, и тратит его. У SWORD каждое касание держало по своей λn: первое касание гасит сдвиг
    /// всего тела, а его λn — лишь доля веса, поэтому тело на двух торцах держалось только до <c>tg ≈ μs · доля</c>, а не до μs.
    /// Трассе — каждое касание: держало — удержало сдвиг или сдвига не было; бюджет кончился — λt = 0 (<c>Xpbd.Friction</c> с
    /// нулевым бюджетом ничего не делает).
    /// </summary>
    private void GroundFriction(int body, float h)
    {
        ref var s = ref _bodies[body];
        float total = 0;
        for (int i = 0; i < _groundContactCount; i++) total += _groundContacts[i].LambdaN;
        float budget = _contactsFriction * total;
        for (int i = 0; i < _groundContactCount; i++)
        {
            ref var c = ref _groundContacts[i];
            var p = s.ToWorld(new Vector3(0, c.Along, 0)) - c.Normal * s.Radius;   // материальная точка — после выталкивания
            bool still = _trace is not null && GroundStill(in s, p, c.Normal);
            float lambdaT = Xpbd.Friction(ref s, p, c.Normal, budget, 1, 0, h);
            budget -= lambdaT;
            if (_trace is not null) TraceFriction(body, c.LambdaN, lambdaT, lambdaT > 0 || still);
        }
    }

    /// <summary>Точка не сдвинулась вдоль земли за подшаг (меньше <see cref="Xpbd.FrictionEpsilon"/>) — трению держать нечего (трасса).</summary>
    private static bool GroundStill(in BodyState s, Vector3 p, Vector3 n)
    {
        var d = p - s.PrevPoint(p);
        return (d - n * Vector3.Dot(d, n)).Length() < Xpbd.FrictionEpsilon;
    }
}
