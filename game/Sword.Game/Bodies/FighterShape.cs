using System.Numerics;
using Sword.Engine.Animation;
using Sword.Game.Arena;
using Sword.Game.Contracts;

namespace Sword.Game.Bodies;

/// <summary>
/// Форма тела бойца при постройке (5б) — радиусы <c>Fighter.Radii</c> и полудлины капсул из <c>Fighter.Build</c> SWORD
/// 2eab13b: полудлина — половина расстояния между концами сегмента в процедурной позе постройки
/// (<c>Fighter.ComputeTargets</c> от корня на высоте земли). Длины считаются в мировых координатах от точки постройки, поэтому
/// последние биты формы зависят от места и курса спауна — от них зависят концы капсул и побитовые удары (этап 6).
///
/// Из живого рэгдолла взято ровно это вычисление — концы сегментов <c>_a/_b</c> в <c>ComputeTargets</c> и опора стоп
/// <c>IdealFoot(leg, 0)</c> — с состоянием бойца сразу после <c>Build</c>: курс постройки, наклоны, поворот торса и
/// опережение верха — 0, цель кисти <c>HandLocal</c> (0,25; 1,3; 0,35), клинок <c>BladeDir</c> — норма (0,1; 0,9; 0,4),
/// двуручный хват, стопы в опоре (фаза <c>Stance</c>) с курсом постройки, скорость корня и план шага — 0. Ориентации целей
/// мышц (<c>TargetRot</c>), мышцы, суставы, сглаживание, шаги и равновесие — не переносятся (решение 2): форма только
/// задаёт капсулы, ставит их аниматор (<see cref="BodyPlacement"/>).
/// </summary>
public static class FighterShape
{
    /// <summary>
    /// Радиусы капсул по номерам сегментов 0…13 (<c>Fighter.Radii</c>; 11 — клинок: с 7ж его тело — <see cref="BladeBody.Radius"/>). Массив, а
    /// не выражение коллекции в свойстве: в Debug оно выделяло новый массив на каждое чтение (216 Б на постройку бойца — возрождения толпы 6ж).
    /// </summary>
    public static ReadOnlySpan<float> Radii => RadiiValues;

    private static readonly float[] RadiiValues = [.15f, .17f, .13f, .065f, .055f, .065f, .055f, .09f, .07f, .09f, .07f, BladeBody.Radius, .045f, .045f];

    /// <summary>
    /// Массы тел по номерам сегментов 0…13, кг (7ж; <c>Fighter.Masses</c> SWORD — отправная точка): таз 12, торс 20, голова 5, плечо 2,5,
    /// предплечье 2, бедро 8, голень 4,5, стопа 1,2; клинок — <see cref="BladeBody.Mass"/> (1,4 для всех мечей, решение 3 этапа 7). Всего
    /// 74,8 кг. Живому бойцу масса ничего не делает (тела кинематические); её берут труп (7з) и толчки (7д, 7к).
    /// </summary>
    public static ReadOnlySpan<float> Masses => MassValues;

    private static readonly float[] MassValues = [12f, 20f, 5f, 2.5f, 2f, 2.5f, 2f, 8f, 4.5f, 8f, 4.5f, BladeBody.Mass, 1.2f, 1.2f];

    // Fighter SWORD: рост таза; Fighter.Locomotion: голеностоп, стопа, развод носков; SwordBody: звенья ноги.
    private const float StandHeight = 0.95f;
    private const float AnkleY = 0.09f, FootLength = 0.2f, ToeOut = 0.12f;
    private const float MaxLead = 0.3f, StrideGrowth = 0.08f, LateralLead = 0.6f, StepReach = 0.55f;
    private const float ThighLength = 0.45f, ShinLength = 0.45f;

    // Состояние живого рэгдолла сразу после Fighter.Build (у кинематического бойца оно дальше не меняется: BeginTick
    // живого рэгдолла у него не идёт): наклоны и их скорости, опережение верха, поворот торса — 0; цель кисти, клинок и
    // двуручный хват — значения Build и поля по умолчанию.
    private const float LeanFwd = 0, LeanSide = 0, UpperLead = 0, TorsoYaw = 0;
    private const bool TwoHanded = true;
    private static readonly Vector3 HandLocal = new(0.25f, 1.3f, 0.35f);
    private static readonly Vector3 BladeDir = Vector3.Normalize(new Vector3(0.1f, 0.9f, 0.4f));

    /// <summary>
    /// Полудлины капсул бойца, построенного в <paramref name="position"/> (y не важен — корень на земле) курсом
    /// <paramref name="facing"/>, по номерам сегментов (<paramref name="halfLength"/> — не меньше <see cref="BodySegs.Count"/>;
    /// клинку — 0: форма его тела — по мечу в руке, <see cref="BladeBody"/>).
    /// </summary>
    public static void HalfLengths(Vector3 position, float facing, Span<float> halfLength)
    {
        if (halfLength.Length < BodySegs.Count) throw new ArgumentException($"нужно {BodySegs.Count} мест", nameof(halfLength));
        Span<Vector3> a = stackalloc Vector3[BodySegs.Count], b = stackalloc Vector3[BodySegs.Count];
        Targets(position, facing, a, b);
        for (int s = 0; s < BodySegs.Count; s++)
            halfLength[s] = s == Weapon ? 0 : Vector3.Distance(a[s], b[s]) * 0.5f;
    }

    private const int Weapon = 11;

    private static Vector3 Mirror(Vector3 v) => new(-v.X, v.Y, v.Z);

    /// <summary>
    /// Концы сегментов процедурной позы постройки (<c>Fighter.Build</c>: корень, земля под ним, опоры стоп —
    /// <c>IdealFoot(leg, 0)</c>, затем <c>ComputeTargets(корень на высоте земли)</c>) — выражения SWORD в том же порядке.
    /// </summary>
    private static void Targets(Vector3 pos, float facing, Span<Vector3> _a, Span<Vector3> _b)
    {
        var root = new Vector3(pos.X, 0, pos.Z);
        float groundY = ArenaTerrain.Height(pos.X, pos.Z);
        Span<Vector3> foot = stackalloc Vector3[2];
        for (int leg = 0; leg < 2; leg++) foot[leg] = IdealFoot(root, facing, leg);

        // ComputeTargets(new Vector3(pos.X, _groundY, pos.Z))
        var origin = new Vector3(pos.X, groundY, pos.Z);
        var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, facing);
        var ty = Quaternion.CreateFromAxisAngle(Vector3.UnitY, TorsoYaw);
        Vector3 T(Vector3 v) => Vector3.Transform(v, ty);

        float pelvisY = StandHeight;
        float pelvisHeight = pelvisY - 0.01f;

        _a[BodySegs.Pelvis] = new(0, pelvisY - 0.06f, 0); _b[BodySegs.Pelvis] = new(0, pelvisY + 0.1f, 0);
        var chest = new Vector3(LeanSide * 0.55f, pelvisY + 0.55f, LeanFwd * 0.55f);
        _a[BodySegs.Torso] = new(0, pelvisY + 0.16f, 0); _b[BodySegs.Torso] = chest;
        _a[BodySegs.Head] = chest + T(new(0, 0.2f, 0.02f)); _b[BodySegs.Head] = chest + T(new(0, 0.3f, 0.03f));

        var shR = chest + T(new(0.24f, -0.04f, 0));
        var shL = chest + T(new(-0.24f, -0.04f, 0));
        var (eR, hR) = AnimMath.Ik2Bone(shR, HandLocal, shR + T(new(0.35f, -0.6f, -0.25f)), 0.3f, 0.28f);
        _a[BodySegs.UArmR] = shR; _b[BodySegs.UArmR] = eR;
        _a[BodySegs.FArmR] = eR; _b[BodySegs.FArmR] = hR;

        var offTarget = TwoHanded ? hR - BladeDir * 0.13f : shL + T(new(-0.12f, -0.45f, 0.12f));
        var (eL, hL) = AnimMath.Ik2Bone(shL, offTarget, shL + T(new(-0.35f, -0.6f, -0.25f)), 0.3f, 0.28f);
        _a[BodySegs.UArmL] = shL; _b[BodySegs.UArmL] = eL;
        _a[BodySegs.FArmL] = eL; _b[BodySegs.FArmL] = hL;

        // Поза описана с +X вправо от бойца; в мире правая сторона при взгляде вдоль +Z — −X (отражение X).
        var yawUpper = Quaternion.CreateFromAxisAngle(Vector3.UnitY, facing + UpperLead);
        for (int i = 0; i < BodySegs.Count; i++)
        {
            // ноги — ниже, прямо в мире; клинок — этап 6
            if (i is >= BodySegs.ThighL and <= BodySegs.ShinR or BodySegs.FootL or BodySegs.FootR or Weapon) continue;
            var q = i == BodySegs.Pelvis ? yaw : yawUpper;
            _a[i] = origin + Vector3.Transform(Mirror(_a[i]), q);
            _b[i] = origin + Vector3.Transform(Mirror(_b[i]), q);
        }

        // Ноги — IK от тазобедренного сустава к опоре стопы (мир), колено вперёд; ступня — от голеностопа вперёд по склону.
        var fwdW = new Vector3(MathF.Sin(facing), 0, MathF.Cos(facing));
        for (int leg = 0; leg < 2; leg++)
        {
            int th = leg == 0 ? BodySegs.ThighL : BodySegs.ThighR;
            var hip = origin + Vector3.Transform(Mirror(new Vector3(leg == 0 ? -0.1f : 0.1f, pelvisHeight - 0.05f, 0)), yaw);
            var (knee, ankle) = AnimMath.Ik2Bone(hip, foot[leg], hip + fwdW * 0.6f - Vector3.UnitY * 0.3f, ThighLength, ShinLength);
            _a[th] = hip; _b[th] = knee;
            _a[th + 1] = knee; _b[th + 1] = ankle;

            // Стопа в опоре (Stance: swing = false), курс стопы — курс постройки.
            int f = leg == 0 ? BodySegs.FootL : BodySegs.FootR;
            const bool swing = false;
            var n = ArenaTerrain.Normal(ankle.X, ankle.Z);
            float fy = facing + (leg == 0 ? ToeOut : -ToeOut);
            var ff = new Vector3(MathF.Sin(fy), 0, MathF.Cos(fy));
            var along = Vector3.Normalize(ff - n * Vector3.Dot(ff, n));
            float heelLift = MathF.Max(0, ankle.Y - ArenaTerrain.Height(ankle.X, ankle.Z) - AnkleY);
            _a[f] = ankle;
            _b[f] = ankle + along * FootLength + n * (swing ? 0.05f : Radii[f] - AnkleY - heelLift);
        }
    }

    /// <summary>
    /// Опора стопы под телом с упреждением 0 (<c>Fighter.IdealFoot(leg, 0)</c> сразу после <c>Build</c>): план шага и
    /// скорость корня — 0, сдвиг захвата — 0, поэтому вынос нулевой; точка — корень плюс полшага вбок по курсу, на высоте
    /// земли плюс голеностоп.
    /// </summary>
    private static Vector3 IdealFoot(Vector3 root, float facing, int leg)
    {
        var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, facing);
        var offset = Vector3.Zero * 0f;   // PlanVel · lead: план шага 0, упреждение 0
        float ol = offset.Length();
        float maxLead = MaxLead + StrideGrowth * Vector3.Zero.Length();   // RootVel = 0
        if (ol > maxLead) offset *= maxLead / ol;
        var fwd = new Vector3(MathF.Sin(facing), 0, MathF.Cos(facing));
        float fwdPart = Vector3.Dot(offset, fwd);
        var sidePart = offset - fwd * fwdPart;
        float sideLen = sidePart.Length(), sideMax = maxLead * LateralLead;
        if (sideLen > sideMax) offset = fwd * fwdPart + sidePart * (sideMax / sideLen);
        offset += Vector3.Zero;   // _captureShift = 0
        float reach = offset.Length();
        if (reach > StepReach) offset *= StepReach / reach;
        var p = root + Vector3.Transform(Mirror(new Vector3(leg == 0 ? -0.12f : 0.12f, 0, 0)), yaw) + offset;
        return new Vector3(p.X, ArenaTerrain.Height(p.X, p.Z) + AnkleY, p.Z);
    }
}
