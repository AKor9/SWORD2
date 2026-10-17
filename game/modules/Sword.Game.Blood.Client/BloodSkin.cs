using System.Numerics;

namespace Sword.Game.Blood;

/// <summary>Изменённый элемент буфера крови слота для GPU: слот, элемент (<c>V + вершина меша</c>), два слова (толщина, мм; рождение, с).</summary>
public readonly record struct BloodSkinEntry(int Slot, int Element, uint X, uint Y);

/// <summary>
/// Кровь на коже по вершинам (WR П8) — CPU-состояние: слот на бойца (пул заранее, <see cref="SlotCount"/>), на сваренную вершину — толщина,
/// мм (объём / площадь вершины), и время рождения, с (возраст = сейчас − рождение; приток омолаживает взвешенно по толщине, без притока
/// возраст растёт). Копии вершины на шве — одна сваренная вершина: GPU получает одинаковые значения во все копии (<see cref="Gather"/>).
/// Штампы — следы потёков (барицентрики трём вершинам) и пятно у раны (по связной коже в радиусе в привязке с проверкой нормали: не
/// перескакивает на соседний палец и на другую сторону складки).
/// </summary>
public sealed class BloodSkin
{
    /// <summary>Пятно не идёт на вершины, нормаль которых с нормалью раны образует угол больше, чем этот косинус.</summary>
    public const float SpotNormalCos = 0f;

    private readonly SkinSurface _surface;
    private readonly int _welds, _vertices;
    private readonly float[] _area;          // площадь вершины в привязке, м² (треть площадей её треугольников)
    private readonly Vector3[] _position, _normal;
    private readonly int[] _adjStart, _adj;  // соседи сваренной вершины по рёбрам
    private readonly ulong[] _key;
    private readonly bool[] _used, _clear;
    private readonly float[] _thick, _birth; // слот · вершины
    private readonly bool[] _dirty;
    private readonly int[] _dirtyList, _dirtyCount;
    private readonly int[] _mark, _queue;
    private readonly float[] _weight;
    private int _generation;
    private BloodSkinEntry[] _entries = new BloodSkinEntry[4096];

    public BloodSkin(SkinSurface surface, int slots)
    {
        _surface = surface;
        _welds = surface.WeldCount;
        _vertices = surface.VertexCount;
        SlotCount = slots;
        _area = new float[_welds];
        _position = new Vector3[_welds];
        _normal = new Vector3[_welds];
        var pairs = new long[6 * surface.TriangleCount];
        int pairCount = 0;
        for (int t = 0; t < surface.TriangleCount; t++)
        {
            var a = surface.BindPosition(surface.Vertex(t, 0));
            var n = Vector3.Cross(surface.BindPosition(surface.Vertex(t, 1)) - a, surface.BindPosition(surface.Vertex(t, 2)) - a);
            float area = n.Length() * 0.5f;
            for (int k = 0; k < 3; k++)
            {
                int w = surface.WeldVertex(t, k), u = surface.WeldVertex(t, (k + 1) % 3);
                _area[w] += area / 3;
                _normal[w] += surface.FaceNormalBind(t) * area;
                _position[w] = surface.BindPosition(surface.Vertex(t, k));
                if (w == u) continue;
                pairs[pairCount++] = (long)w << 32 | (uint)u;
                pairs[pairCount++] = (long)u << 32 | (uint)w;
            }
        }
        for (int w = 0; w < _welds; w++)
        {
            if (_normal[w].LengthSquared() > 1e-20f) _normal[w] = Vector3.Normalize(_normal[w]);
            if (!(_area[w] > 1e-10f)) _area[w] = 1e-10f;
        }
        Array.Sort(pairs, 0, pairCount);
        int unique = 0;
        for (int i = 0; i < pairCount; i++)
            if (unique == 0 || pairs[i] != pairs[unique - 1]) pairs[unique++] = pairs[i];
        _adjStart = new int[_welds + 1];
        _adj = new int[unique];
        for (int i = 0; i < unique; i++) _adjStart[(int)(pairs[i] >> 32) + 1]++;
        for (int w = 0; w < _welds; w++) _adjStart[w + 1] += _adjStart[w];
        for (int i = 0; i < unique; i++) _adj[i] = (int)(uint)pairs[i];

        _key = new ulong[slots];
        _used = new bool[slots];
        _clear = new bool[slots];
        _thick = new float[slots * _welds];
        _birth = new float[slots * _welds];
        _dirty = new bool[slots * _welds];
        _dirtyList = new int[slots * _welds];
        _dirtyCount = new int[slots];
        _mark = new int[_welds];
        _queue = new int[_welds];
        _weight = new float[_welds];
    }

    /// <summary>Слотов в пуле (бойцов с кровью на коже одновременно).</summary>
    public int SlotCount { get; }

    /// <summary>Вершин меша (элементов вида в буфере слота; сырые — следом).</summary>
    public int VertexCount => _vertices;

    /// <summary>Бойцу не хватило слота — с начала прогона.</summary>
    public long SlotMisses { get; private set; }

    /// <summary>Объём, положенный на кожу, мл — с начала прогона.</summary>
    public double Deposited { get; private set; }

    public bool Used(int slot) => _used[slot];

    public ulong Key(int slot) => _key[slot];

    /// <summary>Слот бойца; нет — −1.</summary>
    public int SlotOf(ulong fighter)
    {
        for (int s = 0; s < SlotCount; s++) if (_used[s] && _key[s] == fighter) return s;
        return -1;
    }

    /// <summary>Занятых слотов.</summary>
    public int UsedSlots
    {
        get
        {
            int n = 0;
            for (int s = 0; s < SlotCount; s++) if (_used[s]) n++;
            return n;
        }
    }

    /// <summary>Слот бойца; нет — первый свободный (пул кончился — −1, крови на коже бойца нет).</summary>
    public int Acquire(ulong fighter)
    {
        int slot = SlotOf(fighter);
        if (slot >= 0) return slot;
        for (int s = 0; s < SlotCount; s++)
            if (!_used[s])
            {
                (_used[s], _key[s]) = (true, fighter);
                return s;
            }
        SlotMisses++;
        return -1;
    }

    /// <summary>Снять кровь бойца (постройка заново, снятие): слот свободен, GPU обнулит его в ближайшем кадре.</summary>
    public void Release(ulong fighter)
    {
        int slot = SlotOf(fighter);
        if (slot >= 0) ReleaseSlot(slot);
    }

    public void ReleaseAll()
    {
        for (int s = 0; s < SlotCount; s++) if (_used[s]) ReleaseSlot(s);
    }

    private void ReleaseSlot(int slot)
    {
        _used[slot] = false;
        _clear[slot] = true;
        Array.Clear(_thick, slot * _welds, _welds);
        Array.Clear(_birth, slot * _welds, _welds);
        int n = _dirtyCount[slot];
        for (int i = 0; i < n; i++) _dirty[slot * _welds + _dirtyList[slot * _welds + i]] = false;
        _dirtyCount[slot] = 0;
    }

    /// <summary>Толщина крови сваренной вершины, мм (слота нет — 0).</summary>
    public float Thickness(ulong fighter, int weld)
    {
        int slot = SlotOf(fighter);
        return slot < 0 ? 0 : _thick[slot * _welds + weld];
    }

    /// <summary>Возраст крови сваренной вершины в момент <paramref name="now"/>, с (крови нет — 0).</summary>
    public float Age(ulong fighter, int weld, float now)
    {
        int slot = SlotOf(fighter);
        return slot < 0 || !(_thick[slot * _welds + weld] > 0) ? 0 : now - _birth[slot * _welds + weld];
    }

    /// <summary>Площадь сваренной вершины в привязке, м².</summary>
    public float Area(int weld) => _area[weld];

    /// <summary>Объём <paramref name="volume"/>, мл, в сваренную вершину слота: толщина растёт, возраст — взвешенно по толщине.</summary>
    public void Deposit(int slot, int weld, float volume, float now)
    {
        if (!(volume > 0)) return;
        int i = slot * _welds + weld;
        float add = volume * 1e-3f / _area[weld];
        float old = _thick[i];
        _thick[i] = old + add;
        _birth[i] = old > 0 ? (old * _birth[i] + add * now) / (old + add) : now;
        Deposited += volume;
        if (_dirty[i]) return;
        _dirty[i] = true;
        _dirtyList[slot * _welds + _dirtyCount[slot]++] = weld;
    }

    /// <summary>Штамп потёка: объём — трём вершинам треугольника по барицентрикам.</summary>
    public void Stamp(ulong fighter, int triangle, Vector3 bary, float volume, float now)
    {
        if (!(volume > 0)) return;
        int slot = Acquire(fighter);
        if (slot < 0) return;
        Deposit(slot, _surface.WeldVertex(triangle, 0), volume * bary.X, now);
        Deposit(slot, _surface.WeldVertex(triangle, 1), volume * bary.Y, now);
        Deposit(slot, _surface.WeldVertex(triangle, 2), volume * bary.Z, now);
    }

    /// <summary>
    /// Пятно у раны: объём <paramref name="volume"/>, мл, на связную кожу от треугольника раны в шаре радиуса <paramref name="radius"/> вокруг
    /// <paramref name="center"/> (привязка) — только по вершинам, нормаль которых смотрит в ту же сторону, что <paramref name="normal"/>;
    /// толщина спадает к краю как (1 − d²/R²)². Вернёт число вершин пятна.
    /// </summary>
    public int Spot(ulong fighter, int triangle, Vector3 center, Vector3 normal, float radius, float volume, float now)
    {
        if (!(volume > 0) || !(radius > 0)) return 0;
        int slot = Acquire(fighter);
        if (slot < 0) return 0;
        int generation = ++_generation;
        int head = 0, tail = 0;
        for (int k = 0; k < 3; k++)
        {
            int w = _surface.WeldVertex(triangle, k);
            if (_mark[w] == generation) continue;
            _mark[w] = generation;
            _queue[tail++] = w;
        }
        float r2 = radius * radius, sum = 0;
        while (head < tail) sum += Visit(_queue[head++], center, normal, r2, generation, ref tail);
        if (!(sum > 0)) return 0;
        for (int i = 0; i < tail; i++)
        {
            int w = _queue[i];
            if (_weight[w] > 0) Deposit(slot, w, volume * _weight[w] * _area[w] / sum, now);
        }
        return tail;
    }

    /// <summary>Вершина пятна: её вес (·площадь) и соседи в шаре с той же стороной — в очередь.</summary>
    private float Visit(int w, Vector3 center, Vector3 normal, float r2, int generation, ref int tail)
    {
        float d2 = Vector3.DistanceSquared(_position[w], center);
        float f = MathF.Max(0, 1 - d2 / r2);
        _weight[w] = f * f;
        for (int a = _adjStart[w]; a < _adjStart[w + 1]; a++)
        {
            int u = _adj[a];
            if (_mark[u] == generation) continue;
            _mark[u] = generation;
            if (Vector3.DistanceSquared(_position[u], center) >= r2 || Vector3.Dot(_normal[u], normal) <= SpotNormalCos) continue;
            _queue[tail++] = u;
        }
        return _weight[w] * _area[w];
    }

    /// <summary>Слот ждёт обнуления на GPU (снят); <paramref name="take"/> — отметка снимается.</summary>
    public bool PendingClear(int slot, bool take)
    {
        bool c = _clear[slot];
        if (take) _clear[slot] = false;
        return c;
    }

    /// <summary>Изменённых сваренных вершин слота с прошлого <see cref="Gather"/>.</summary>
    public int DirtyCount(int slot) => _dirtyCount[slot];

    /// <summary>
    /// Изменения с прошлого раза для GPU — по слотам подряд: каждая изменённая сваренная вершина — во все свои копии (элемент
    /// <c>V + вершина меша</c>). Отметки снимаются.
    /// </summary>
    public ReadOnlySpan<BloodSkinEntry> Gather()
    {
        int count = 0;
        for (int s = 0; s < SlotCount; s++)
        {
            int n = _dirtyCount[s];
            for (int i = 0; i < n; i++) count = GatherWeld(s, _dirtyList[s * _welds + i], count);
            _dirtyCount[s] = 0;
        }
        return _entries.AsSpan(0, count);
    }

    private int GatherWeld(int slot, int weld, int count)
    {
        int i = slot * _welds + weld;
        _dirty[i] = false;
        uint x = BitConverter.SingleToUInt32Bits(_thick[i]), y = BitConverter.SingleToUInt32Bits(_birth[i]);
        foreach (int v in _surface.WeldCopies(weld))
        {
            if (count == _entries.Length) Array.Resize(ref _entries, _entries.Length * 2);
            _entries[count++] = new BloodSkinEntry(slot, _vertices + v, x, y);
        }
        return count;
    }
}
