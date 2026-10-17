using System;
using System.Runtime.CompilerServices;

namespace Sword.Game.Cutting;

/// <summary>
/// Триангуляция крышки отсечением ушей: внешний контур с дырами, соединёнными мостами. Точки не
/// выбрасываются (иначе разрыв на шве). Когда строгих ушей нет — почти вырожденное ухо, затем любое
/// выпуклое ухо без проверки вложенных точек (шум float у почти совпадающих точек), и только потом веер.
/// </summary>
internal sealed class CapTriangulator
{
    private readonly int[] _loopPts, _loopStart, _loopLen;
    private readonly double[] _nx, _ny;

    // Узлы кольцевого списка: координаты (с учётом зеркала), узел контура, соседи.
    private double[] _x = new double[64], _y = new double[64];
    private int[] _id = new int[64], _prev = new int[64], _next = new int[64];
    private int _count;
    private double _eps, _snap;

    /// <summary>Число дыр, соединённых мостом с внешним контуром.</summary>
    public int Bridged;

    /// <summary>Хотя бы одна крышка собрана веером.</summary>
    public bool FellBack;

    public CapTriangulator(int[] loopPts, int[] loopStart, int[] loopLen, double[] nx, double[] ny)
    {
        _loopPts = loopPts;
        _loopStart = loopStart;
        _loopLen = loopLen;
        _nx = nx;
        _ny = ny;
    }

    /// <summary>
    /// Триангулирует контур <paramref name="outer"/> с дырами; mirror = −1 отражает v, чтобы внешний контур
    /// обходился против часовой стрелки. Треугольники (узлы контуров) идут в порядке обхода контура.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Triangulate(int outer, MeshCutter.Buf<int> holes, double mirror, MeshCutter.Buf<int> tris)
    {
        _count = 0;
        SetSnap(outer);
        int ring = Ring(outer, mirror);
        int remaining = _loopLen[outer];
        if (holes.Count > 0)
            remaining += EliminateHoles(ring, holes, mirror, tris);
        SetEps(ring);
        Clip(ring, remaining, tris);
    }

    // Порог слияния соседних почти совпадающих точек: доля размера контура плюс шум float координат.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void SetSnap(int loop)
    {
        int start = _loopStart[loop], len = _loopLen[loop];
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue, abs = 0;
        for (int i = 0; i < len; i++)
        {
            int node = _loopPts[start + i];
            double x = _nx[node], y = _ny[node];
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
            abs = Math.Max(abs, Math.Max(Math.Abs(x), Math.Abs(y)));
        }
        _snap = 1e-5 * Math.Max(maxX - minX, maxY - minY) + 1e-6 * abs;
    }

    // Кольцо контура. Соседние точки ближе порога получают одинаковые координаты: микрозигзаги из шума
    // становятся точно вырожденными и не запирают отсечение ушей. Узлы контура не меняются.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private int Ring(int loop, double mirror)
    {
        int start = _loopStart[loop], len = _loopLen[loop];
        int first = -1, last = -1;
        for (int i = 0; i < len; i++)
        {
            int node = _loopPts[start + i];
            double x = _nx[node], y = _ny[node] * mirror;
            if (last >= 0 && Math.Abs(x - _x[last]) <= _snap && Math.Abs(y - _y[last]) <= _snap)
            {
                x = _x[last];
                y = _y[last];
            }
            last = Insert(node, x, y, last);
            if (first < 0)
                first = last;
        }
        if (last != first && Math.Abs(_x[last] - _x[first]) <= _snap && Math.Abs(_y[last] - _y[first]) <= _snap)
        {
            _x[last] = _x[first];
            _y[last] = _y[first];
        }
        return first;
    }

    private int Insert(int id, double x, double y, int last)
    {
        if (_count == _x.Length)
        {
            int n = _count * 2;
            Array.Resize(ref _x, n);
            Array.Resize(ref _y, n);
            Array.Resize(ref _id, n);
            Array.Resize(ref _prev, n);
            Array.Resize(ref _next, n);
        }
        int p = _count++;
        _x[p] = x;
        _y[p] = y;
        _id[p] = id;
        if (last < 0)
        {
            _prev[p] = p;
            _next[p] = p;
        }
        else
        {
            _next[p] = _next[last];
            _prev[p] = last;
            _prev[_next[last]] = p;
            _next[last] = p;
        }
        return p;
    }

    // Дыры по возрастанию левой точки соединяются мостом с кольцом; неудачная дыра триангулируется отдельно.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private int EliminateHoles(int outer, MeshCutter.Buf<int> holes, double mirror, MeshCutter.Buf<int> tris)
    {
        int n = holes.Count;
        var left = new int[n];
        var order = new int[n];
        for (int h = 0; h < n; h++)
        {
            int p = Ring(holes.Items[h], mirror), best = p;
            for (int q = _next[p]; q != p; q = _next[q])
                if (_x[q] < _x[best] || (_x[q] == _x[best] && _y[q] < _y[best]))
                    best = q;
            left[h] = best;
            order[h] = h;
        }
        // Сортировка вставками: дыр мало, порядок детерминирован номером дыры при равенстве.
        for (int i = 1; i < n; i++)
        {
            int k = order[i], j = i - 1;
            while (j >= 0 && Before(left[k], left[order[j]]))
            {
                order[j + 1] = order[j];
                j--;
            }
            order[j + 1] = k;
        }

        int added = 0;
        int failedFrom = -1;
        Span<bool> failed = n <= 64 ? stackalloc bool[n] : new bool[n];
        for (int i = 0; i < n; i++)
        {
            int h = order[i];
            int bridge = FindHoleBridge(left[h], outer);
            if (bridge < 0)
            {
                failed[h] = true;
                failedFrom = h;
                continue;
            }
            SplitPolygon(bridge, left[h]);
            added += _loopLen[holes.Items[h]] + 2;
            Bridged++;
        }

        // Дыра без моста: отдельная крышка в собственном обходе, чтобы край оставался замкнутым.
        if (failedFrom >= 0)
        {
            for (int h = 0; h < n; h++)
            {
                if (!failed[h])
                    continue;
                int ring = Ring(holes.Items[h], -mirror);
                SetEps(ring);
                Clip(ring, _loopLen[holes.Items[h]], tris);
            }
        }
        return added;
    }

    private bool Before(int a, int b) => _x[a] < _x[b] || (_x[a] == _x[b] && _y[a] < _y[b]);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void SetEps(int ring)
    {
        double minX = _x[ring], maxX = minX, minY = _y[ring], maxY = minY;
        for (int p = _next[ring]; p != ring; p = _next[p])
        {
            minX = Math.Min(minX, _x[p]);
            maxX = Math.Max(maxX, _x[p]);
            minY = Math.Min(minY, _y[p]);
            maxY = Math.Max(maxY, _y[p]);
        }
        double ext = Math.Max(maxX - minX, maxY - minY);
        _eps = 1e-6 * ext * ext;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Clip(int ear, int remaining, MeshCutter.Buf<int> tris)
    {
        int stop = ear, pass = 0;
        while (remaining > 2)
        {
            int a = _prev[ear], c = _next[ear];
            if (IsEar(ear, pass))
            {
                tris.Add(_id[a]);
                tris.Add(_id[ear]);
                tris.Add(_id[c]);
                _next[a] = c;
                _prev[c] = a;
                remaining--;
                ear = _next[c];
                stop = ear;
                pass = 0;
                continue;
            }
            ear = c;
            if (ear == stop)
            {
                if (pass < 2)
                {
                    pass++;
                    continue;
                }
                Fan(ear, remaining, tris);
                FellBack = true;
                return;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Fan(int v0, int remaining, MeshCutter.Buf<int> tris)
    {
        int v1 = _next[v0];
        for (int i = 0; i < remaining - 2; i++)
        {
            int v2 = _next[v1];
            tris.Add(_id[v0]);
            tris.Add(_id[v1]);
            tris.Add(_id[v2]);
            v1 = v2;
        }
    }

    // Проход 0: выпуклое ухо без вогнутых точек внутри; 1: почти нулевой площади; 2: любое выпуклое.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private bool IsEar(int b, int pass)
    {
        int a = _prev[b], c = _next[b];
        double cr = Cross(a, b, c);
        if (pass == 1)
            return Math.Abs(cr) <= _eps;
        if (cr <= 0)
            return false;
        if (pass == 2)
            return true;

        double ax = _x[a], ay = _y[a], bx = _x[b], by = _y[b], cx = _x[c], cy = _y[c];
        double minX = Math.Min(ax, Math.Min(bx, cx)), maxX = Math.Max(ax, Math.Max(bx, cx));
        double minY = Math.Min(ay, Math.Min(by, cy)), maxY = Math.Max(ay, Math.Max(by, cy));
        for (int p = _next[c]; p != a; p = _next[p])
        {
            double px = _x[p], py = _y[p];
            if (px < minX || px > maxX || py < minY || py > maxY)
                continue;
            // Совпадающие точки (мосты, швы) не мешают уху.
            if ((px == ax && py == ay) || (px == bx && py == by) || (px == cx && py == cy))
                continue;
            if (Cross(a, b, p) >= 0 && Cross(b, c, p) >= 0 && Cross(c, a, p) >= 0 && Cross(_prev[p], p, _next[p]) <= 0)
                return false;
        }
        return true;
    }

    private double Cross(int a, int b, int c) =>
        (_x[b] - _x[a]) * (_y[c] - _y[a]) - (_y[b] - _y[a]) * (_x[c] - _x[a]);

    // Знак в соглашении поиска моста: Area &lt; 0 — поворот против часовой стрелки.
    private double Area(int p, int q, int r) =>
        (_y[q] - _y[p]) * (_x[r] - _x[q]) - (_x[q] - _x[p]) * (_y[r] - _y[q]);

    // Видимая точка внешнего кольца для левой точки дыры (луч влево, затем точка с наименьшим углом).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private int FindHoleBridge(int hole, int outer)
    {
        double hx = _x[hole], hy = _y[hole], qx = double.NegativeInfinity;
        int p = outer, m = -1;
        if (Same(hole, p))
            return p;
        do
        {
            int pn = _next[p];
            if (Same(hole, pn))
                return pn;
            if (hy <= _y[p] && hy >= _y[pn] && _y[pn] != _y[p])
            {
                double x = _x[p] + (hy - _y[p]) * (_x[pn] - _x[p]) / (_y[pn] - _y[p]);
                if (x <= hx && x > qx)
                {
                    qx = x;
                    m = _x[p] < _x[pn] ? p : pn;
                    if (x == hx)
                        return m;
                }
            }
            p = pn;
        }
        while (p != outer);
        if (m < 0)
            return -1;

        int stop = m;
        double mx = _x[m], my = _y[m], tanMin = double.PositiveInfinity;
        p = m;
        do
        {
            double px = _x[p], py = _y[p];
            if (hx >= px && px >= mx && hx != px &&
                PointInTriangle(hy < my ? hx : qx, hy, mx, my, hy < my ? qx : hx, hy, px, py))
            {
                double tan = Math.Abs(hy - py) / (hx - px);
                if (LocallyInside(p, hole) &&
                    (tan < tanMin || (tan == tanMin && (px > _x[m] || (px == _x[m] && SectorContainsSector(m, p))))))
                {
                    m = p;
                    tanMin = tan;
                }
            }
            p = _next[p];
        }
        while (p != stop);
        return m;
    }

    private bool Same(int a, int b) => _x[a] == _x[b] && _y[a] == _y[b];

    private bool SectorContainsSector(int m, int p) => Area(_prev[m], m, _prev[p]) < 0 && Area(_next[p], m, _next[m]) < 0;

    private bool LocallyInside(int a, int b) =>
        Area(_prev[a], a, _next[a]) < 0
            ? Area(a, b, _next[a]) >= 0 && Area(a, _prev[a], b) >= 0
            : Area(a, b, _prev[a]) < 0 || Area(a, _next[a], b) < 0;

    private static bool PointInTriangle(double ax, double ay, double bx, double by, double cx, double cy, double px, double py) =>
        (cx - px) * (ay - py) >= (ax - px) * (cy - py) &&
        (ax - px) * (by - py) >= (bx - px) * (ay - py) &&
        (bx - px) * (cy - py) >= (cx - px) * (by - py);

    // Мост a–b: кольцо дыры вставляется после a, a и b дублируются.
    private void SplitPolygon(int a, int b)
    {
        int an = _next[a], bp = _prev[b];
        int a2 = Insert(_id[a], _x[a], _y[a], -1);
        int b2 = Insert(_id[b], _x[b], _y[b], -1);
        _next[a] = b;
        _prev[b] = a;
        _next[a2] = an;
        _prev[an] = a2;
        _next[b2] = a2;
        _prev[a2] = b2;
        _next[bp] = b2;
        _prev[b2] = bp;
    }
}
