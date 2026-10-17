using System.Numerics;
using Sword.Game.Contracts;

namespace Sword.Game.Blood;

/// <summary>Капля брызг попала в тело бойца: ключ бойца, сегмент, точка на капсуле (мир), скорость касания, м/с, объём, мл.</summary>
public readonly record struct BloodStick(ulong Fighter, int Segment, Vector3 Point, Vector3 Velocity, float Volume);

/// <summary>
/// Касание клинка тика (для крови на оружии): тик, кто бил, жертва, точка попадания (мир), она же в системе тела клинка (ось Y — от хвата
/// −полудлина к кончику +полудлина), доля длины от хвата (0) к кончику (1), урон. Клинка в позах нет — <see cref="HasBlade"/> false.
/// </summary>
public readonly record struct BloodBladeContact(ulong Tick, ulong Attacker, ulong Victim, Vector3 Point, Vector3 BladeLocal, float Along, float Damage,
    bool HasBlade);

/// <summary>
/// Брызги из раны в тике удара: веер капель из точки раны на коже позы тика. Объём — урон × <see cref="BloodTuning.SprayPerDamage"/>
/// (капли набираются, пока не наберётся; число ∝ урону), диаметр — лог-нормально 1…6 мм; направление — по ходу клинка с разбросом в
/// плоскости хода (Dir, Blade) и поперёк неё, плюс наружу по нормали кожи; у укола — струя назад по ходу клинка; доля капель — прицельно
/// в торс, голову и руки атакующего (замкнутая формула полёта с разбросом). Скорость — доля скорости кончика. ГСЧ — хеш (зерно раны, номер
/// капли). Буфер растёт удвоением.
/// </summary>
public sealed class BloodSpray
{
    private BloodDrop[] _drops = new BloodDrop[256];
    private int _count;

    /// <summary>Капель и объём, мл, с начала прогона.</summary>
    public long Emitted { get; private set; }
    public double EmittedMl { get; private set; }

    /// <summary>Капель последнего выброса.</summary>
    public ReadOnlySpan<BloodDrop> Drops => _drops.AsSpan(0, _count);

    /// <summary>Сегменты атакующего, в которые целит обратный конус (торс, голова, руки — веса по порядку).</summary>
    private static readonly int[] BackTargets = [BodySegs.Torso, BodySegs.Torso, BodySegs.Head, BodySegs.FArmR, BodySegs.UArmR, BodySegs.FArmL, BodySegs.UArmL];

    /// <summary>
    /// Брызги попадания <paramref name="hit"/> из раны <paramref name="w"/> в позе тика жертвы <paramref name="joints"/>; тела атакующего тика
    /// <paramref name="attacker"/> (пусто — без обратного конуса). Удар по трупу — треть объёма.
    /// </summary>
    public ReadOnlySpan<BloodDrop> Emit(in BloodHit hit, in BloodWound w, ReadOnlySpan<Matrix4x4> joints, ReadOnlySpan<SegmentBody> attacker,
        SkinSurface s, BloodTuning t)
    {
        _count = 0;
        float target = MathF.Max(hit.Damage, 0) * t.SprayPerDamage * (hit.Corpse ? 1f / 3 : 1);
        if (!(target > 0) || joints.IsEmpty) return Drops;
        s.SkinTriangle(w.Triangle, joints, out var w0, out var w1, out var w2);
        var c = Vector3.Cross(w1 - w0, w2 - w0);
        float l = c.Length();
        var n = l > 1e-12f ? c * (s.FaceSign(w.Triangle) / l) : Vector3.UnitY;
        var origin = w.Bary.X * w0 + w.Bary.Y * w1 + w.Bary.Z * w2 + n * 0.005f;
        var d = Safe(hit.Dir, -n);
        var b = hit.Blade - d * Vector3.Dot(hit.Blade, d);
        b = Safe(b, Safe(Vector3.Cross(n, d), Vector3.UnitY));
        var m = Vector3.Cross(b, d);   // нормаль плоскости хода
        bool aim = attacker.Length > BodySegs.Torso && Vector3.Distance(attacker[BodySegs.Torso].Position, origin) < t.SprayBackRange;
        uint seed = BloodHash.Combine(w.Seed, 0x5A00u);
        int max = Math.Max(1, t.SprayMaxDrops);
        float sum = 0;
        for (int k = 0; k < max && sum < target; k++)
        {
            float volume = DropMl(seed, k, t);
            if (sum + volume > target || k == max - 1) volume = target - sum;
            sum += volume;
            var v = Velocity(seed, k, hit, t, d, b, m, n, origin, aim ? attacker : default);
            if (_count == _drops.Length) Array.Resize(ref _drops, 2 * _drops.Length);
            _drops[_count++] = new BloodDrop(hit.Fighter, hit.Tick, origin, v, volume);
        }
        Emitted += _count;
        EmittedMl += sum;
        return Drops;
    }

    /// <summary>Объём капли, мл: диаметр лог-нормально (медиана, σ), в пределах; V = π/6·d³ (мм³ = мкл).</summary>
    public static float DropMl(uint seed, int k, BloodTuning t)
    {
        float g = Gauss(seed, (uint)(8 * k));
        float mm = Math.Clamp(t.SprayMedianMm * MathF.Exp(t.SpraySigma * g), t.SprayMinMm, t.SprayMaxMm);
        return MathF.PI / 6 * mm * mm * mm * 1e-3f;
    }

    private static Vector3 Velocity(uint seed, int k, in BloodHit hit, BloodTuning t, Vector3 d, Vector3 b, Vector3 m, Vector3 n, Vector3 origin,
        ReadOnlySpan<SegmentBody> attacker)
    {
        uint i = (uint)(8 * k);
        float u = Math.Clamp((t.SpraySpeedMin + (t.SpraySpeedMax - t.SpraySpeedMin) * BloodHash.Unit(seed, i + 2)) * hit.Speed,
            t.SprayMinSpeed, t.SprayMaxSpeed);
        if (!attacker.IsEmpty && BloodHash.Unit(seed, i + 3) < t.SprayBackShare && Aim(seed, i, attacker, origin, u, t, out var aimed)) return aimed;
        float a = 2 * BloodHash.Unit(seed, i + 4) - 1, e = 2 * BloodHash.Unit(seed, i + 5) - 1;
        float out_ = t.SprayOutward * (0.5f + BloodHash.Unit(seed, i + 6));
        Vector3 dir;
        if (hit.Kind == HitKind.Thrust)
        {
            // Укол: струя назад из раны по ходу клинка, в узком конусе.
            dir = -d + t.SprayThrustCone * (a * b + e * m) + out_ * n;
        }
        else
        {
            float theta = t.SprayFan * a;
            dir = MathF.Cos(theta) * d + MathF.Sin(theta) * b + t.SprayCross * e * m + out_ * n;
        }
        return Safe(dir, n) * u;
    }

    /// <summary>
    /// Прицел в тело атакующего: точка сегмента с разбросом, время полёта — расстояние / скорость; начальная скорость — из замкнутой формулы
    /// x(T) = цель. Слишком быстрая (больше 1.5 предела) — нет.
    /// </summary>
    private static bool Aim(uint seed, uint i, ReadOnlySpan<SegmentBody> attacker, Vector3 origin, float u, BloodTuning t, out Vector3 v)
    {
        v = default;
        int pick = BackTargets[Math.Min((int)(BloodHash.Unit(seed, i + 7) * BackTargets.Length), BackTargets.Length - 1)];
        if (pick >= attacker.Length) return false;
        uint j = BloodHash.Combine(seed, i + 1);
        var jitter = new Vector3(BloodHash.Unit(j, 0) - 0.5f, BloodHash.Unit(j, 1) - 0.5f, BloodHash.Unit(j, 2) - 0.5f) * (2 * t.SprayBackJitter);
        var to = attacker[pick].Position + jitter - origin;
        float dist = to.Length();
        if (!(dist > 0.05f)) return false;
        float time = dist / MathF.Max(u, 0.1f);
        float k = MathF.Max(t.DropDrag, BloodFlight.MinDrag);
        var vInf = new Vector3(0, -BloodFlight.Gravity / k, 0);
        v = vInf + (to - vInf * time) * (k / (1 - MathF.Exp(-k * time)));
        return v.Length() <= 1.5f * t.SprayMaxSpeed;
    }

    /// <summary>Нормальное число из хеша (Бокс — Мюллер).</summary>
    private static float Gauss(uint seed, uint i)
    {
        float u1 = MathF.Max(BloodHash.Unit(seed, i), 1e-7f), u2 = BloodHash.Unit(seed, i + 1);
        return MathF.Sqrt(-2 * MathF.Log(u1)) * MathF.Cos(2 * MathF.PI * u2);
    }

    private static Vector3 Safe(Vector3 v, Vector3 fallback) => v.LengthSquared() > 1e-10f ? Vector3.Normalize(v) : fallback;

    /// <summary>
    /// Отрезок <paramref name="a"/>–<paramref name="b"/> и капсула (ось — отрезок <paramref name="p"/>–<paramref name="q"/>, радиус
    /// <paramref name="r"/>): первое касание снаружи — доля отрезка 0…1; нет или начало внутри — −1. Расстояние до оси вдоль отрезка
    /// выпукло: касание — делением пополам до точки наибольшего сближения.
    /// </summary>
    public static float SegmentCapsule(Vector3 a, Vector3 b, Vector3 p, Vector3 q, float r)
    {
        ClosestSegments(a, b, p, q, out float s, out _, out float d2);
        if (d2 > r * r) return -1;
        if (PointSegment2(a, p, q) <= r * r) return -1;   // начало уже внутри: капля вылетела из этого тела (рана под капсулой)
        float lo = 0, hi = s;
        for (int i = 0; i < 24; i++)
        {
            float mid = 0.5f * (lo + hi);
            if (PointSegment2(Vector3.Lerp(a, b, mid), p, q) <= r * r) hi = mid;
            else lo = mid;
        }
        return hi;
    }

    /// <summary>Квадрат расстояния от точки до отрезка.</summary>
    public static float PointSegment2(Vector3 x, Vector3 p, Vector3 q)
    {
        var e = q - p;
        float ee = Vector3.Dot(e, e);
        float h = ee > 1e-12f ? Math.Clamp(Vector3.Dot(x - p, e) / ee, 0, 1) : 0;
        return Vector3.DistanceSquared(x, p + e * h);
    }

    /// <summary>Ближайшие точки двух отрезков (Эриксон, 5.1.9): доли s на первом, u на втором, квадрат расстояния.</summary>
    public static void ClosestSegments(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2, out float s, out float u, out float d2)
    {
        Vector3 d1 = q1 - p1, d2v = q2 - p2, r = p1 - p2;
        float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2v, d2v), f = Vector3.Dot(d2v, r);
        if (a <= 1e-12f && e <= 1e-12f)
        {
            (s, u) = (0, 0);
        }
        else if (a <= 1e-12f)
        {
            s = 0;
            u = Math.Clamp(f / e, 0, 1);
        }
        else
        {
            float c = Vector3.Dot(d1, r);
            if (e <= 1e-12f)
            {
                u = 0;
                s = Math.Clamp(-c / a, 0, 1);
            }
            else
            {
                float bb = Vector3.Dot(d1, d2v), denom = a * e - bb * bb;
                s = denom > 1e-12f ? Math.Clamp((bb * f - c * e) / denom, 0, 1) : 0;
                u = (bb * s + f) / e;
                if (u < 0)
                {
                    u = 0;
                    s = Math.Clamp(-c / a, 0, 1);
                }
                else if (u > 1)
                {
                    u = 1;
                    s = Math.Clamp((bb - c) / a, 0, 1);
                }
            }
        }
        d2 = Vector3.DistanceSquared(p1 + d1 * s, p2 + d2v * u);
    }
}
