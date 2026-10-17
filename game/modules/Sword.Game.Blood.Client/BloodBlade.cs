using System.Numerics;
using Sword.Game.Contracts;

namespace Sword.Game.Blood;

/// <summary>
/// Кровь на клинке: слот на оружие бойца (в руке или выпавшее — ключ бойца и номер оружия), на нём — объём, мл, и время рождения, с, в
/// <see cref="Cells"/> клетках вдоль лезвия (зона «blade» <c>.swblood</c>: от гарды к острию) на каждой из двух сторон, и две капли на концах
/// (у гарды, на кончике). Попадание кладёт кровь около места касания (∝ урону, размазано вдоль лезвия); шаг 60 Гц по положению лезвия на
/// трёх шагах: сила на плёнку (g − a точки, a — вторая разность) вдоль оси гонит избыток над смачивающим слоем к концу (вязкость, порог,
/// тонкая течёт медленнее — скорость ∝ толщине избытка²;
/// засохшая не течёт — подвижность = свежесть <see cref="BloodLook"/>), на конце копится капля и отрывается (<see cref="Drops"/>); сила от
/// плоскости лезвия при взмахе стряхивает часть подвижной крови каплями. Без выделений после старта.
/// </summary>
public sealed class BloodBlade
{
    /// <summary>Клеток вдоль лезвия на сторону.</summary>
    public const int Cells = 48;

    private const int PerSlot = 2 * Cells;
    private static readonly Vector3 G = new(0, -BloodFlight.Gravity, 0);

    private readonly BloodTuning _t;
    private readonly ulong[] _key;
    private readonly int[] _weapon;
    private readonly bool[] _used;
    private readonly BladeBloodZone[] _zone;
    private readonly float[] _ml, _birth, _bead, _beadBirth, _move;
    private readonly Matrix4x4[] _w1, _w0;
    private readonly int[] _history;
    private readonly long[] _version;
    private readonly uint[] _drip;
    private BloodDrop[] _drops = new BloodDrop[64];
    private int _dropCount;

    public BloodBlade(int slots, BloodTuning? tuning = null)
    {
        _t = tuning ?? new BloodTuning();
        _key = new ulong[slots];
        _weapon = new int[slots];
        _used = new bool[slots];
        _zone = new BladeBloodZone[slots];
        _ml = new float[slots * PerSlot];
        _birth = new float[slots * PerSlot];
        _bead = new float[slots * 2];
        _beadBirth = new float[slots * 2];
        _move = new float[Cells];
        _w1 = new Matrix4x4[slots];
        _w0 = new Matrix4x4[slots];
        _history = new int[slots];
        _version = new long[slots];
        _drip = new uint[slots];
    }

    public int SlotCount => _used.Length;

    /// <summary>Занятых слотов; попаданий без свободного слота.</summary>
    public int UsedSlots { get; private set; }
    public long SlotMisses { get; private set; }

    /// <summary>Положено на клинки, мл; оторвалось каплями (с концов и стряхнуто), мл и капель; стряхнуто, мл.</summary>
    public double Deposited { get; private set; }
    public double DroppedMl { get; private set; }
    public long Dropped { get; private set; }
    public double ShakenMl { get; private set; }

    /// <summary>Капли шага (<see cref="Step"/>): точка, скорость, объём; <see cref="TakeDrops"/> — забрать.</summary>
    public ReadOnlySpan<BloodDrop> Drops => _drops.AsSpan(0, _dropCount);
    public void TakeDrops() => _dropCount = 0;

    public bool Used(int slot) => _used[slot];
    public ulong Key(int slot) => _key[slot];
    public int Weapon(int slot) => _weapon[slot];
    public ref readonly BladeBloodZone Zone(int slot) => ref _zone[slot];

    /// <summary>Счётчик изменений слота: вид на GPU пересчитывается, когда он сменился.</summary>
    public long Version(int slot) => _version[slot];

    /// <summary>Слот бойца; нет — −1.</summary>
    public int SlotOf(ulong key)
    {
        for (int s = 0; s < _used.Length; s++)
            if (_used[s] && _key[s] == key) return s;
        return -1;
    }

    /// <summary>Объём клетки, мл, и время рождения: сторона 0 — по нормали лезвия.</summary>
    public float Volume(int slot, int side, int cell) => _ml[slot * PerSlot + side * Cells + cell];
    public float Birth(int slot, int side, int cell) => _birth[slot * PerSlot + side * Cells + cell];

    /// <summary>Капля на конце: 0 — у гарды, 1 — на кончике, мл.</summary>
    public float Bead(int slot, int end) => _bead[2 * slot + end];

    /// <summary>Крови на клинке слота (клетки и капли на концах), мл.</summary>
    public double OnBlade(int slot)
    {
        double sum = _bead[2 * slot] + _bead[2 * slot + 1];
        for (int i = 0; i < PerSlot; i++) sum += _ml[slot * PerSlot + i];
        return sum;
    }

    /// <summary>Середина крови слота по массе клеток — доля длины лезвия (0 — у гарды); крови нет — 0.</summary>
    public double Centre(int slot)
    {
        double m = 0, mu = 0;
        for (int i = 0; i < PerSlot; i++)
        {
            float v = _ml[slot * PerSlot + i];
            m += v;
            mu += v * ((i % Cells) + 0.5) / Cells;
        }
        return m > 0 ? mu / m : 0;
    }

    /// <summary>Крови на всех клинках, мл.</summary>
    public double OnBlades()
    {
        double sum = 0;
        for (int s = 0; s < _used.Length; s++) if (_used[s]) sum += OnBlade(s);
        return sum;
    }

    /// <summary>Толщина плёнки клетки, мм, — объём на площадь клетки одной стороны.</summary>
    public float Thickness(int slot, int side, int cell) => Volume(slot, side, cell) / MlPerMm(_zone[slot]);

    /// <summary>Объём клетки одной стороны на миллиметр толщины, мл/мм.</summary>
    public static float MlPerMm(in BladeBloodZone zone) => zone.Length / Cells * 2 * zone.Half * 1e3f;

    /// <summary>
    /// Кровь попадания: клинок бойца <paramref name="key"/> (оружие <paramref name="weapon"/>, лезвие <paramref name="zone"/>), место —
    /// доля длины лезвия <paramref name="along"/> (вне 0…1 — рукоять, гарда: не кладётся), объём, мл, — по клеткам около места (±
    /// <see cref="BloodTuning.BladeSpreadCells"/>, треугольный вес) на обе стороны (доля 0.35…0.65 по хешу <paramref name="seed"/>).
    /// Другое оружие у слота — слот начинается заново. Вернёт, положено ли.
    /// </summary>
    public bool Deposit(ulong key, int weapon, in BladeBloodZone zone, float along, float ml, float now, uint seed)
    {
        if (!(along >= 0 && along <= 1) || !(ml > 0) || !(zone.Length > 0) || !(zone.Half > 0)) return false;
        int slot = SlotOf(key);
        if (slot >= 0 && _weapon[slot] != weapon)
        {
            Release(slot);
            slot = -1;
        }
        if (slot < 0 && (slot = Take(key, weapon, zone)) < 0)
        {
            SlotMisses++;
            return false;
        }
        float spread = MathF.Max(_t.BladeSpreadCells, 0.5f);
        float centre = along * Cells - 0.5f;
        int lo = Math.Max(0, (int)MathF.Floor(centre - spread)), hi = Math.Min(Cells - 1, (int)MathF.Ceiling(centre + spread));
        float sum = 0;
        for (int i = lo; i <= hi; i++) sum += Weight(i, centre, spread);
        if (!(sum > 0)) return false;
        float share = 0.35f + 0.3f * BloodHash.Unit(seed, 0);
        for (int side = 0; side < 2; side++)
        {
            float part = ml * (side == 0 ? share : 1 - share) / sum;
            for (int i = lo; i <= hi; i++) Add(slot, side, i, part * Weight(i, centre, spread), now);
        }
        Deposited += ml;
        _version[slot]++;
        return true;
    }

    private static float Weight(int i, float centre, float spread) => MathF.Max(0, 1 - MathF.Abs(i - centre) / (spread + 0.5f));

    private void Add(int slot, int side, int cell, float ml, float birth)
    {
        int k = slot * PerSlot + side * Cells + cell;
        float total = _ml[k] + ml;
        if (!(total > 0)) return;
        _birth[k] = (_ml[k] * _birth[k] + ml * birth) / total;
        _ml[k] = total;
    }

    private int Take(ulong key, int weapon, in BladeBloodZone zone)
    {
        for (int s = 0; s < _used.Length; s++)
        {
            if (_used[s]) continue;
            _used[s] = true;
            _key[s] = key;
            _weapon[s] = weapon;
            _zone[s] = zone;
            _history[s] = 0;
            _drip[s] = 0;
            UsedSlots++;
            return s;
        }
        return -1;
    }

    /// <summary>Снять слот: кровь пропадает.</summary>
    public void Release(int slot)
    {
        if (!_used[slot]) return;
        _used[slot] = false;
        UsedSlots--;
        Array.Clear(_ml, slot * PerSlot, PerSlot);
        Array.Clear(_birth, slot * PerSlot, PerSlot);
        _bead[2 * slot] = _bead[2 * slot + 1] = 0;
        _version[slot]++;
    }

    /// <summary>Снять слот бойца (постройка заново, снятие трупа).</summary>
    public void Release(ulong key)
    {
        int slot = SlotOf(key);
        if (slot >= 0) Release(slot);
    }

    public void ReleaseAll()
    {
        for (int s = 0; s < _used.Length; s++) Release(s);
        _dropCount = 0;
    }

    /// <summary>
    /// Шаг слота: <paramref name="meshToWorld"/> — пространство меша оружия → мир на этом шаге (тела клинка тика), <paramref name="dt"/> —
    /// шаг, с, <paramref name="now"/> — часы крови, с, <paramref name="look"/> — свежесть (подвижность). Течение, капли на концах,
    /// стряхивание; капли — в <see cref="Drops"/> с тиком <paramref name="tick"/>.
    /// </summary>
    public void Step(int slot, in Matrix4x4 meshToWorld, ulong tick, float dt, float now, in BloodLookParams look)
    {
        if (!_used[slot] || !(dt > 0)) return;
        ref readonly var zone = ref _zone[slot];
        var startMesh = zone.Origin;
        var endMesh = zone.Origin + zone.AxisU * zone.Length;
        var s2 = Vector3.Transform(startMesh, meshToWorld);
        var e2 = Vector3.Transform(endMesh, meshToWorld);
        int history = _history[slot];
        Vector3 aS = default, aE = default, vS = default, vE = default;
        if (history >= 1)
        {
            vS = (s2 - Vector3.Transform(startMesh, _w1[slot])) / dt;
            vE = (e2 - Vector3.Transform(endMesh, _w1[slot])) / dt;
        }
        if (history >= 2)
        {
            aS = Clamp((s2 - 2 * Vector3.Transform(startMesh, _w1[slot]) + Vector3.Transform(startMesh, _w0[slot])) / (dt * dt));
            aE = Clamp((e2 - 2 * Vector3.Transform(endMesh, _w1[slot]) + Vector3.Transform(endMesh, _w0[slot])) / (dt * dt));
        }
        _w0[slot] = _w1[slot];
        _w1[slot] = meshToWorld;
        _history[slot] = Math.Min(history + 1, 2);
        var axisRaw = e2 - s2;
        float length = axisRaw.Length();
        if (!(length > 1e-5f)) return;
        var axis = axisRaw / length;
        var normal = Vector3.TransformNormal(zone.Normal, meshToWorld);
        normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitY;
        var frame = new Frame(s2, axis, normal, G - aS, G - aE, vS, vE, length);
        bool changed = false;
        for (int side = 0; side < 2; side++) changed |= Flow(slot, side, frame, dt, now, look, tick);
        changed |= Beads(slot, frame, now, tick);
        if (changed) _version[slot]++;
    }

    /// <summary>Лезвие шага в мире: начало, ось, нормаль, сила на плёнку и скорость на концах, длина.</summary>
    private readonly record struct Frame(Vector3 Start, Vector3 Axis, Vector3 Normal, Vector3 ForceStart, Vector3 ForceEnd, Vector3 VelocityStart,
        Vector3 VelocityEnd, float Length)
    {
        public Vector3 Force(float u) => Vector3.Lerp(ForceStart, ForceEnd, u);
        public Vector3 Velocity(float u) => Vector3.Lerp(VelocityStart, VelocityEnd, u);
        public Vector3 Point(float u) => Start + Axis * (u * Length);
    }

    private Vector3 Clamp(Vector3 a)
    {
        float l = a.Length(), max = _t.MaxAccel;
        return l > max ? a * (max / l) : a;
    }

    /// <summary>Течение по одной стороне и стряхивание с неё; true — что-то сдвинулось.</summary>
    private bool Flow(int slot, int side, in Frame f, float dt, float now, in BloodLookParams look, ulong tick)
    {
        ref readonly var zone = ref _zone[slot];
        int at = slot * PerSlot + side * Cells;
        float perMm = MlPerMm(zone), hold = _t.BladeHoldMm * perMm, cell = zone.Length / Cells;
        var outward = side == 0 ? f.Normal : -f.Normal;
        float k = MathF.Max(_t.BladeViscosity, 1e-3f), stick = _t.StickAccel;
        float shake0 = MathF.Max(_t.BladeShakeAccel, 1e-3f);
        bool moved = false;
        Array.Clear(_move);
        int group = Math.Max(1, _t.BladeShakeGroup);
        float shaken = 0, shakenU = 0;
        for (int i = 0; i < Cells; i++)
        {
            float take = Cell(at, i, f, outward, hold, perMm, k, stick, shake0, cell, dt, now, look);
            if (take > 0)
            {
                shaken += take;
                shakenU += take * (i + 0.5f) / Cells;
                moved = true;
            }
            if (shaken > 0 && ((i + 1) % group == 0 || i == Cells - 1))
                moved |= Shake(slot, ref shaken, ref shakenU, at, i, group, f, outward, tick);
        }
        for (int i = 0; i < Cells; i++) moved |= Move(slot, side, at, i);
        return moved;
    }

    /// <summary>Клетка за шаг: стряхнутое (вернёт, мл; из клетки уже вычтено) и сдвиг к соседу по ходу силы (в <see cref="_move"/>).</summary>
    private float Cell(int at, int i, in Frame f, Vector3 outward, float hold, float perMm, float k, float stick, float shake0, float cell,
        float dt, float now, in BloodLookParams look)
    {
        float ml = _ml[at + i];
        if (!(ml > hold)) return 0;
        float u = (i + 0.5f) / Cells;
        float wet = Mobility(now - _birth[at + i], ml / perMm, look);
        if (!(wet > 0)) return 0;
        float mobile = (ml - hold) * wet;
        var force = f.Force(u);
        float along = Vector3.Dot(force, f.Axis);
        float off = Vector3.Dot(force, outward);
        float take = 0;
        if (off > shake0)
        {
            take = mobile * MathF.Min(1, (off - shake0) / shake0) * _t.BladeShakeShare;
            _ml[at + i] -= take;
            mobile -= take;
        }
        if (MathF.Abs(along) >= stick)
        {
            float run = MathF.Min(1, (ml - hold) / perMm / MathF.Max(_t.BladeRunMm, 1e-4f));
            float frac = MathF.Min(1, MathF.Abs(along) / k * run * run * dt / cell);
            _move[i] = along > 0 ? mobile * frac : -mobile * frac;
        }
        return take;
    }

    /// <summary>Сдвиг клетки за шаг: к соседу по ходу силы, с конца — в каплю на конце.</summary>
    private bool Move(int slot, int side, int at, int i)
    {
        float m = _move[i];
        if (m == 0) return false;
        float amount = MathF.Abs(m);
        float birth = _birth[at + i];
        _ml[at + i] = MathF.Max(0, _ml[at + i] - amount);
        int to = m > 0 ? i + 1 : i - 1;
        if (to < 0 || to >= Cells)
        {
            int end = 2 * slot + (to < 0 ? 0 : 1);
            float total = _bead[end] + amount;
            _beadBirth[end] = (_bead[end] * _beadBirth[end] + amount * birth) / total;
            _bead[end] = total;
        }
        else Add(slot, side, to, amount, birth);
        return true;
    }

    /// <summary>Стряхнутое с группы клеток — каплей из её середины по массе; меньше порога — обратно в клетку.</summary>
    private bool Shake(int slot, ref float shaken, ref float shakenU, int at, int last, int group, in Frame f, Vector3 outward, ulong tick)
    {
        float ml = shaken, u = shakenU / shaken;
        shaken = shakenU = 0;
        if (ml < _t.BladeShakeMinMl)
        {
            int back = Math.Clamp((int)(u * Cells), Math.Max(0, last - group + 1), last);
            _ml[at + back] += ml;
            return false;
        }
        uint seed = BloodHash.Combine(BloodHash.Seed(_key[slot], tick, 0xB1AD0000u), _drip[slot]++);
        var force = f.Force(u);
        var dir = force - Vector3.Dot(force, f.Axis) * f.Axis;
        dir = dir.LengthSquared() > 1e-8f ? Vector3.Normalize(dir) : outward;
        var v = f.Velocity(u) + dir * (0.3f + 0.7f * BloodHash.Unit(seed, 1));
        Emit(slot, tick, f.Point(u) + outward * 0.004f, v, ml);
        ShakenMl += ml;
        return true;
    }

    /// <summary>Капли на концах: сила к концу — копится, от конца — стекает обратно в крайние клетки; с порога (при рывке — с меньшего) отрывается.</summary>
    private bool Beads(int slot, in Frame f, float now, ulong tick)
    {
        bool changed = false;
        for (int end = 0; end < 2; end++)
        {
            int b = 2 * slot + end;
            float ml = _bead[b];
            if (!(ml > 0)) continue;
            float u = end;
            var force = f.Force(u);
            float along = Vector3.Dot(force, f.Axis);
            bool outwardPull = end == 1 ? along > _t.StickAccel : along < -_t.StickAccel;
            bool inwardPull = end == 1 ? along < -_t.StickAccel : along > _t.StickAccel;
            if (inwardPull)
            {
                int cell = end == 1 ? Cells - 1 : 0;
                Add(slot, 0, cell, 0.5f * ml, _beadBirth[b]);
                Add(slot, 1, cell, 0.5f * ml, _beadBirth[b]);
                _bead[b] = 0;
                changed = true;
                continue;
            }
            bool jerk = force.Length() > _t.JerkAccel;
            if (!(ml >= _t.BladeDetachMl || (jerk && ml >= _t.BladeJerkMl))) continue;
            if (!outwardPull && !jerk) continue;
            var dir = end == 1 ? f.Axis : -f.Axis;
            Emit(slot, tick, f.Point(u) + dir * 0.005f, f.Velocity(u), ml);
            _bead[b] = 0;
            changed = true;
        }
        return changed;
    }

    private void Emit(int slot, ulong tick, Vector3 p, Vector3 v, float ml)
    {
        if (_dropCount == _drops.Length) Array.Resize(ref _drops, 2 * _drops.Length);
        _drops[_dropCount++] = new BloodDrop(_key[slot], tick, p, v, ml);
        DroppedMl += ml;
        Dropped++;
    }

    /// <summary>Подвижность плёнки: свежесть (блеск) функции свежести — засохшая не течёт.</summary>
    public static float Mobility(float age, float thicknessMm, in BloodLookParams look) => BloodLook.Evaluate(age, thicknessMm, look).Wet;
}
