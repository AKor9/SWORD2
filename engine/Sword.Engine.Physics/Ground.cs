using System.Numerics;

namespace Sword.Engine.Physics;

// Контакт тела с землёй по граням IGround: сначала нормаль всех касаний обоих торцов, затем трение покоя с общим для тела
// бюджетом Кулона.
public sealed partial class PhysicsWorld
{
    /// <summary>
    /// Касание сферы тела с землёй в подшаге: центр сферы в системе тела и её радиус (торец — точка оси и радиус капсулы, щуп —
    /// <see cref="SetGroundProbes"/>), нормаль грани, λn; застарелая ли глубина (глубже <c>PopSpeed·h</c> в начале подшага).
    /// </summary>
    private struct GroundContactEntry
    {
        public Vector3 Local;
        public float Radius, LambdaN;
        public bool Old;
        public Vector3 Normal;
    }

    /// <summary>Щупов земли у тела не больше (<see cref="SetGroundProbes"/>).</summary>
    public const int MaxGroundProbes = 6;

    /// <summary>Щупы земли по слотам тел: <see cref="MaxGroundProbes"/> мест на тело (центр в системе тела, радиус) и их число.</summary>
    private Vector4[] _groundProbes = [];
    private int[] _groundProbeCount = [];

    public void SetGroundProbes(BodyHandle body, ReadOnlySpan<Vector4> spheres)
    {
        Check(body);
        if (spheres.Length > MaxGroundProbes) throw new ArgumentException($"щупов {spheres.Length}, мест {MaxGroundProbes}", nameof(spheres));
        foreach (var p in spheres)
            if (!float.IsFinite(p.X + p.Y + p.Z + p.W) || !(p.W > 0)) throw new ArgumentException($"щуп {p}: конечный, радиус больше 0", nameof(spheres));
        SleepWakeBody(body.Index, WakeReason.Command);
        spheres.CopyTo(_groundProbes.AsSpan(body.Index * MaxGroundProbes, MaxGroundProbes));
        _groundProbeCount[body.Index] = spheres.Length;
    }

    public ReadOnlySpan<Vector4> GroundProbes(BodyHandle body)
    {
        Check(body);
        return _groundProbes.AsSpan(body.Index * MaxGroundProbes, _groundProbeCount[body.Index]);
    }

    /// <summary>Слоты тел выросли: место под щупы (только рост).</summary>
    private void GroundProbesGrow(int bodies)
    {
        if (_groundProbeCount.Length >= bodies) return;
        Array.Resize(ref _groundProbes, bodies * MaxGroundProbes);
        Array.Resize(ref _groundProbeCount, bodies);
    }

    /// <summary>Тело в слоте выдано заново: щупов нет.</summary>
    private void GroundProbesReset(int body) => _groundProbeCount[body] = 0;

    /// <summary>Вдавливание трением покоя сверх выхода, м, меньше которого не решается.</summary>
    private const float GroundFrictionPressSlack = 1e-5f;

    /// <summary>Касания тела с землёй в подшаге (оба торца × до <see cref="IGround.MaxTouches"/> граней) — для трения после нормали.</summary>
    private readonly GroundContactEntry[] _groundContacts = new GroundContactEntry[(2 + MaxGroundProbes) * IGround.MaxTouches];
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

    /// <summary>
    /// Нормали тела против земли (без трения): нижний конец оси, затем верхний, затем щупы по порядку; касания — в
    /// <see cref="_groundContacts"/>.
    /// </summary>
    private void GroundNormals(int body, float h)
    {
        _groundContactCount = 0;
        float half = _bodies[body].HalfLength, radius = _bodies[body].Radius;
        GroundEnd(body, new Vector3(0, -half, 0), radius, h);
        if (half > 0) GroundEnd(body, new Vector3(0, half, 0), radius, h);
        for (int k = 0, n = _groundProbeCount[body]; k < n; k++)
        {
            var p = _groundProbes[body * MaxGroundProbes + k];
            GroundEnd(body, new Vector3(p.X, p.Y, p.Z), p.W, h);
        }
    }

    /// <summary>Сфера тела против земли: все грани, в которые она вошла, — по одной; старая глубина — та же грань в прошлой позе.</summary>
    private void GroundEnd(int body, Vector3 local, float radius, float h)
    {
        ref var s = ref _bodies[body];
        var end = s.ToWorld(local);
        int count = _ground!.Touches(end, radius, _groundTouches);
        if (count <= 0) return;
        count = Math.Min(count, _groundTouches.Length);
        var prevEnd = s.PrevPosition + Vector3.Transform(local, s.PrevRotation);
        // Первая грань — торец, глубина и нормаль касания (тело с тех пор не двигалось: тот же расчёт), остальные — заново
        GroundFace(body, _groundTouches[0].Face, local, radius, prevEnd, h, in _groundTouches[0], end);
        for (int i = 1; i < count; i++) GroundFace(body, _groundTouches[i].Face, local, radius, prevEnd, h, in Unsolved, default);
    }

    /// <summary>Касание без глубины — «посчитать грань заново в нынешнем положении торца».</summary>
    private static readonly GroundTouch Unsolved = new(-1, default, 0);

    /// <summary>
    /// Грань против торца: глубина — заново в нынешнем положении (у первой — из касания), выталкивание по нормали
    /// (<see cref="ContactsWorldPoint"/>). Старая глубина считается, только если может быть больше <c>PopSpeed·h</c>
    /// (<c>глубина + 2·смещение &gt; PopSpeed·h</c>). Торцы короткой капсулы почти совпадают: λ второго ≈ 0, бюджет трения не удваивается.
    /// </summary>
    private void GroundFace(int body, int face, Vector3 local, float radius, Vector3 prevEnd, float h, in GroundTouch touch, Vector3 touchEnd)
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
            end = s.ToWorld(local);
            pen = ground.Depth(end, radius, face, out n);
        }
        if (!(pen > 0)) return;
        float pen0 = pen + 2 * Vector3.Distance(end, prevEnd) <= _contactsPopSpeed * h ? 0 : ground.Depth(prevEnd, radius, face, out _);
        s.Grounded = true;
        float lambdaN = ContactsWorldPoint(body, PhysicsSource.Ground, end - n * radius, n, pen, pen0, _contactsFriction, h);
        if (!(lambdaN > 0) || _groundContactCount == _groundContacts.Length) return;
        ref var c = ref _groundContacts[_groundContactCount++];
        c.Local = local;
        c.Radius = radius;
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
            var end = s.ToWorld(c.Local);
            var p = end - c.Normal * c.Radius;   // материальная точка — после выталкивания
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
        var end = s.ToWorld(c.Local);
        Xpbd.Contact(ref s, end - c.Normal * c.Radius, c.Normal, -press, 0, h);
    }

    /// <summary>Точка не сдвинулась вдоль земли за подшаг (меньше <see cref="Xpbd.FrictionEpsilon"/>) — трению держать нечего (трасса).</summary>
    private static bool GroundStill(in BodyState s, Vector3 p, Vector3 n)
    {
        var d = p - s.PrevPoint(p);
        return (d - n * Vector3.Dot(d, n)).Length() < Xpbd.FrictionEpsilon;
    }
}
