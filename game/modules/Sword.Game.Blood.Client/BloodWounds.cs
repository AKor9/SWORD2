using System.Numerics;
using Sword.Game.Contracts;

namespace Sword.Game.Blood;

/// <summary>
/// Рана на коже (запись клиента): треугольник меша и барицентрики — в привязке (переживает любую позу и смерть); точка, нормаль и линия раны
/// в привязке (линия — <c>Blade × Dir</c> в плоскости кожи, у укола — вдоль хода клинка); урон, тик, зерно ГСЧ (боец, тик, номер касания),
/// сильная ли (урон ≥ <see cref="BloodTuning.StrongDamage"/>); <see cref="Gap"/> — расстояние якоря на коже до точки попадания в тике удара, м.
/// </summary>
public readonly record struct BloodWound(ulong Fighter, ulong Tick, int Segment, int Triangle, Vector3 Bary, Vector3 BindPoint, Vector3 BindNormal,
    Vector3 BindLine, float Damage, HitKind Kind, bool Strong, bool Probe, bool Corpse, uint Seed, float Gap, int Joint);

/// <summary>
/// Бойцы для крови: строка на ключ бойца (<see cref="FighterKey"/>), кольцо из <see cref="RingSize"/> ран на бойца (вытесняется самая старая).
/// Постройка или снятие бойца и сброс арены чистят его записи (<see cref="Clear"/>). Строки — по первому свободному месту: порядок
/// детерминирован порядком событий.
/// </summary>
public sealed class BloodWounds
{
    public const int RingSize = 16;

    /// <summary>Уточнение якоря в позе тика — в шаре привязки радиусом «расстояние до кожи + это», м.</summary>
    public const float RefineRadius = 0.08f;

    private ulong[] _keys;
    private bool[] _used;
    private BloodWound[] _wounds;
    private int[] _count, _next;

    public BloodWounds(int fighters = 64)
    {
        _keys = new ulong[fighters];
        _used = new bool[fighters];
        _wounds = new BloodWound[fighters * RingSize];
        _count = new int[fighters];
        _next = new int[fighters];
    }

    /// <summary>Мест под бойцов (растёт удвоением).</summary>
    public int Capacity => _keys.Length;

    /// <summary>Ран записано с начала прогона.</summary>
    public long Recorded { get; private set; }

    /// <summary>Попаданий без якоря (нет позы бойца или кожи сегмента).</summary>
    public long Missed { get; private set; }

    public bool Used(int row) => _used[row];

    public ulong Key(int row) => _keys[row];

    /// <summary>Строка бойца; нет — −1.</summary>
    public int Find(ulong fighter)
    {
        for (int r = 0; r < _keys.Length; r++) if (_used[r] && _keys[r] == fighter) return r;
        return -1;
    }

    /// <summary>Строка бойца; нет — заводится.</summary>
    public int Row(ulong fighter)
    {
        int row = Find(fighter);
        if (row >= 0) return row;
        for (int r = 0; r < _keys.Length; r++)
            if (!_used[r])
            {
                (_used[r], _keys[r], _count[r], _next[r]) = (true, fighter, 0, 0);
                return r;
            }
        int old = _keys.Length;
        Array.Resize(ref _keys, old * 2);
        Array.Resize(ref _used, old * 2);
        Array.Resize(ref _wounds, old * 2 * RingSize);
        Array.Resize(ref _count, old * 2);
        Array.Resize(ref _next, old * 2);
        (_used[old], _keys[old]) = (true, fighter);
        return old;
    }

    /// <summary>Раны бойца (порядок кольца: не по времени — время в <see cref="BloodWound.Tick"/>).</summary>
    public ReadOnlySpan<BloodWound> Of(int row) => _used[row] ? _wounds.AsSpan(row * RingSize, _count[row]) : [];

    /// <summary>Рана <paramref name="index"/> бойца строки <paramref name="row"/>.</summary>
    public ref readonly BloodWound At(int row, int index) => ref _wounds[row * RingSize + index];

    /// <summary>Ран сейчас (все бойцы).</summary>
    public int Count
    {
        get
        {
            int n = 0;
            for (int r = 0; r < _keys.Length; r++) if (_used[r]) n += _count[r];
            return n;
        }
    }

    /// <summary>Снять бойца (его раны).</summary>
    public void Clear(ulong fighter)
    {
        int row = Find(fighter);
        if (row >= 0) _used[row] = false;
    }

    public void ClearAll() => Array.Clear(_used);

    /// <summary>
    /// Рана по попаданию <paramref name="hit"/> в позе его тика <paramref name="joints"/>: точка — в привязку через опорный сустав сегмента
    /// (<see cref="SkinSurface.ReferenceJoint"/>), у пробы (точка — центр капсулы) — луч по ходу удара до кожи; затем ближайший треугольник
    /// сегмента, уточнённый по скинированной коже позы тика. Вернёт номер раны в кольце бойца; якоря нет — −1.
    /// </summary>
    public int Record(in BloodHit hit, ReadOnlySpan<Matrix4x4> joints, SkinSurface surface, BloodTuning tuning, out int row)
    {
        row = -1;
        int segment = hit.Segment;
        int joint = surface.ReferenceJoint(segment, hit.Point, joints);
        if (joint < 0 || joint >= joints.Length || surface.SegmentTriangles(segment).IsEmpty)
        {
            Missed++;
            return -1;
        }
        var toBind = surface.WorldToBind(joint, joints);
        var p = Vector3.Transform(hit.Point, toBind);
        var dir = SafeNormal(Vector3.TransformNormal(hit.Dir, toBind));
        int triangle = -1;
        Vector3 bary = default;
        if (hit.Probe && dir != Vector3.Zero)
            triangle = surface.Raycast(segment, p - dir * 0.5f, dir, out bary, out _);
        if (triangle < 0)
        {
            triangle = NearestSkin(surface, segment, p, hit.Point, joints, out bary);
            if (triangle < 0)
            {
                Missed++;
                return -1;
            }
        }
        var point = surface.BindPoint(triangle, bary);
        var normal = surface.BindNormal(triangle, bary);
        var blade = SafeNormal(Vector3.TransformNormal(hit.Blade, toBind));
        var line = hit.Kind == HitKind.Thrust ? dir : Vector3.Cross(blade, dir);
        if (line.LengthSquared() < 0.09f) line = dir;   // ход вдоль клинка: плоскость реза вырождена — линия по ходу
        line -= normal * Vector3.Dot(normal, line);
        if (line.LengthSquared() < 0.01f) line = Vector3.UnitY - normal * normal.Y;   // ход в кожу (проба): вдоль оси тела
        if (line.LengthSquared() < 1e-6f) line = Vector3.Cross(normal, Vector3.UnitX);
        line = Vector3.Normalize(line);
        float gap = Vector3.Distance(surface.SkinPoint(triangle, bary, joints), hit.Point);

        row = Row(hit.Fighter);
        int index = _next[row];
        _wounds[row * RingSize + index] = new BloodWound(hit.Fighter, hit.Tick, segment, triangle, bary, point, normal, line, hit.Damage, hit.Kind,
            hit.Damage >= tuning.StrongDamage, hit.Probe, hit.Corpse, BloodHash.Seed(hit.Fighter, hit.Tick, hit.ContactIndex), gap, joint);
        _next[row] = (index + 1) % RingSize;
        _count[row] = Math.Min(_count[row] + 1, RingSize);
        Recorded++;
        return index;
    }

    /// <summary>
    /// Кожа сегмента у точки мира <paramref name="world"/> в позе <paramref name="joints"/>: через опорный сустав в привязку, ближайший
    /// треугольник сегмента; <paramref name="refine"/> — уточнённый по скинированной коже позы в шаре «расстояние + 8 см» (якорь раны;
    /// ≈ 0.3 мс). Без уточнения (капля брызг на чужом теле: ≈ 0.1 мс) ошибка перевода через один сустав — до сантиметров. Нет — −1.
    /// </summary>
    public static int Anchor(SkinSurface surface, int segment, Vector3 world, ReadOnlySpan<Matrix4x4> joints, out Vector3 bary, bool refine = true)
    {
        bary = default;
        int joint = surface.ReferenceJoint(segment, world, joints);
        if (joint < 0 || joint >= joints.Length) return -1;
        var bind = Vector3.Transform(world, surface.WorldToBind(joint, joints));
        return refine ? NearestSkin(surface, segment, bind, world, joints, out bary) : surface.Nearest(segment, bind, out bary, out _);
    }

    private static int NearestSkin(SkinSurface surface, int segment, Vector3 bind, Vector3 world, ReadOnlySpan<Matrix4x4> joints, out Vector3 bary)
    {
        int triangle = surface.Nearest(segment, bind, out bary, out float bindDistance);
        if (triangle < 0) return -1;
        // Перевод через один сустав ошибается на сантиметры (WR §3.2: торс p90 до 8 см) — уточнение по коже в позе тика.
        int refined = surface.NearestSkinned(segment, bind, bindDistance + RefineRadius, world, joints, out var refinedBary, out _);
        if (refined >= 0) (triangle, bary) = (refined, refinedBary);
        return triangle;
    }

    private static Vector3 SafeNormal(Vector3 v) => v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : Vector3.Zero;
}

/// <summary>ГСЧ крови — хеш целых (без <see cref="Random"/>): одно и то же зерно и номер дают одно и то же число при любом темпе кадров.</summary>
public static class BloodHash
{
    public static uint Mix(uint h)
    {
        h ^= h >> 16;
        h *= 0x7FEB352Du;
        h ^= h >> 15;
        h *= 0x846CA68Bu;
        h ^= h >> 16;
        return h;
    }

    public static uint Combine(uint a, uint b) => Mix(a ^ (b + 0x9E3779B9u + (a << 6) + (a >> 2)));

    /// <summary>Зерно раны: боец, тик, номер касания.</summary>
    public static uint Seed(ulong fighter, ulong tick, uint contact) =>
        Combine(Combine(Combine((uint)fighter, (uint)(fighter >> 32)), Combine((uint)tick, (uint)(tick >> 32))), contact);

    /// <summary>Число 0…1 из зерна и номера.</summary>
    public static float Unit(uint seed, uint index) => (Combine(seed, index) >> 8) * (1f / (1 << 24));
}
