using System.Numerics;

namespace Sword.Engine.Render;

/// <summary>
/// Сглаживание SMAA 1x (Jimenez и др., 2012) — числа и CPU-эталон <c>common/smaa.hlsl</c>. Края по яркости sRGB-кодированного
/// цвета с местным контрастом → веса: концы линии края, пересекающие края на концах, площадь под линией, переведённой
/// в наклонную (ортогональные узоры, скругление углов; диагонали — серии ступеней 1:1) → смешивание с соседом через край в линейном цвете.
/// Своё: площадь — формулой, а не таблицей AreaTex; концы линии — поштучно, а не SearchTex; диагональ — только у прямой (изгиб
/// остаётся ортогональным узорам), её отрезок — через середины ступеней со сдвигом по ступеням за концами.
/// </summary>
public static class SmaaMath
{
    /// <summary>Порог края по разности яркости (SMAA_THRESHOLD пресета High).</summary>
    public const float Threshold = 0.1f;

    /// <summary>Край слабее сильнейшего рядом больше чем во столько раз — не край.</summary>
    public const float ContrastAdaptation = 2f;

    /// <summary>Поиск конца линии — не дальше стольких пикселей в каждую сторону.</summary>
    public const int MaxSearch = 32;

    /// <summary>Диагональ — серия не короче стольких ступеней 1:1.</summary>
    public const int MinDiagonal = 3;

    /// <summary>Поиск конца диагонали — не дальше стольких ступеней в каждую сторону.</summary>
    public const int MaxSearchDiagonal = 16;

    /// <summary>Скругление углов: доля, на которую угол ослабляет смешивание у своего конца, — 1 − 0.25.</summary>
    public const float CornerRounding = 0.25f;

    /// <summary>Узоры U короче этой длины смягчены корнем площади.</summary>
    public const float SmoothDistance = 32f;

    /// <summary>Яркость по sRGB-кодированному цвету (веса Rec.709, как у SMAA).</summary>
    public static readonly Vector3 Luma = new(0.2126f, 0.7152f, 0.0722f);

    /// <summary>
    /// Доли смешивания пикселя на линии края: X — этому пикселю (сосед через край), Y — пикселю через край. Узор (биты): 1 —
    /// пересекающий край в начале со стороны пикселя, 2 — в конце с этой стороны, 4 — в начале через край, 8 — в конце через край;
    /// <paramref name="left"/>, <paramref name="right"/> — расстояния до концов линии, пиксели.
    /// </summary>
    public static Vector2 Area(int pattern, int left, int right)
    {
        float x = left, d = left + right + 1, h = d * 0.5f;
        const float o1 = 0.5f, o2 = -0.5f;
        return pattern switch
        {
            1 => left <= right ? Segment(0, o2, h, 0, x) : default,
            2 => left >= right ? Segment(h, 0, d, o2, x) : default,
            3 => Smooth(d, Segment(0, o2, h, 0, x)) + Smooth(d, Segment(h, 0, d, o2, x)),
            4 => left <= right ? Segment(0, o1, h, 0, x) : default,
            6 or 7 or 14 => Segment(0, o1, d, o2, x),
            8 => left >= right ? Segment(h, 0, d, o1, x) : default,
            9 or 11 or 13 => Segment(0, o2, d, o1, x),
            12 => Smooth(d, Segment(0, o1, h, 0, x)) + Smooth(d, Segment(h, 0, d, o1, x)),
            _ => default,
        };
    }

    /// <summary>Площадь под отрезком (p1x, p1y) → (p2x, p2y) над пикселем [x, x + 1]: X — отрицательная часть, Y — положительная.</summary>
    private static Vector2 Segment(float p1x, float p1y, float p2x, float p2y, float x)
    {
        float dx = p2x - p1x, dy = p2y - p1y;
        float x1 = x, x2 = x + 1;
        if (!((x1 >= p1x && x1 < p2x) || (x2 > p1x && x2 <= p2x))) return default;
        float y1 = p1y + dy * (x1 - p1x) / dx;
        float y2 = p1y + dy * (x2 - p1x) / dx;
        float a1, a2;
        if ((y1 >= 0) == (y2 >= 0) || MathF.Abs(y1) < 1e-4f || MathF.Abs(y2) < 1e-4f)
        {
            a1 = (y1 + y2) * 0.5f;
            a2 = 0;
        }
        else
        {
            // Отрезок пересекает край внутри пикселя: два треугольника разных знаков.
            float xc = p1x - p1y * dx / dy;
            float f = xc - MathF.Floor(xc);
            a1 = xc > p1x ? y1 * f * 0.5f : 0;
            a2 = xc < p2x ? y2 * (1 - f) * 0.5f : 0;
        }
        return new(MathF.Max(-a1, 0) + MathF.Max(-a2, 0), MathF.Max(a1, 0) + MathF.Max(a2, 0));
    }

    private static Vector2 Smooth(float d, Vector2 a)
    {
        var b = new Vector2(MathF.Sqrt(a.X * 2) * 0.5f, MathF.Sqrt(a.Y * 2) * 0.5f);
        return b + (a - b) * Math.Clamp(d / SmoothDistance, 0f, 1f);
    }

    /// <summary>
    /// CPU-эталон трёх проходов над кадром после тонмапа: <paramref name="rgb"/> — байты RGB по строкам сверху вниз
    /// (sRGB-кодированные), вернёт сглаженный кадр той же раскладки. Веса, как у GPU, — байтами цели RGBA8.
    /// </summary>
    public static byte[] Apply(ReadOnlySpan<byte> rgb, int width, int height)
    {
        if (width <= 0 || height <= 0 || rgb.Length != width * height * 3)
            throw new ArgumentException($"кадр {width}x{height}: нужно {width * height * 3} байт RGB, дано {rgb.Length}");
        var luma = new float[width * height];
        for (int i = 0; i < luma.Length; i++)
            luma[i] = rgb[i * 3] / 255f * Luma.X + rgb[i * 3 + 1] / 255f * Luma.Y + rgb[i * 3 + 2] / 255f * Luma.Z;
        var edges = new byte[width * height * 2];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                Edges(luma, width, height, x, y, edges);
        var weights = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                Weights(edges, width, height, x, y, weights);
        var result = new byte[rgb.Length];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                Blend(rgb, weights, width, height, x, y, result);
        return result;
    }

    private static void Edges(float[] luma, int w, int h, int x, int y, byte[] edges)
    {
        float L(int px, int py) => luma[Math.Clamp(py, 0, h - 1) * w + Math.Clamp(px, 0, w - 1)];
        float l = L(x, y), lLeft = L(x - 1, y), lTop = L(x, y - 1);
        float dLeft = MathF.Abs(l - lLeft), dTop = MathF.Abs(l - lTop);
        float eLeft = dLeft >= Threshold ? 1 : 0, eTop = dTop >= Threshold ? 1 : 0;
        if (eLeft + eTop == 0) return;
        float maxX = MathF.Max(dLeft, MathF.Abs(l - L(x + 1, y))), maxY = MathF.Max(dTop, MathF.Abs(l - L(x, y + 1)));
        maxX = MathF.Max(maxX, MathF.Abs(lLeft - L(x - 2, y)));
        maxY = MathF.Max(maxY, MathF.Abs(lTop - L(x, y - 2)));
        float final = MathF.Max(maxX, maxY);
        int i = (y * w + x) * 2;
        edges[i] = (byte)(final <= ContrastAdaptation * dLeft ? eLeft : 0);
        edges[i + 1] = (byte)(final <= ContrastAdaptation * dTop ? eTop : 0);
    }

    /// <summary>Край пикселя (0 — левый, 1 — верхний); за кадром — нет.</summary>
    private static int Edge(byte[] edges, int w, int h, int x, int y, int c) =>
        x >= 0 && y >= 0 && x < w && y < h ? edges[(y * w + x) * 2 + c] : 0;

    private static void Weights(byte[] edges, int w, int h, int x, int y, byte[] weights)
    {
        int i = (y * w + x) * 4;
        bool vertical = Edge(edges, w, h, x, y, 0) > 0;
        if (Edge(edges, w, h, x, y, 1) > 0)
        {
            var d = Diagonal(edges, w, h, x, y);
            var a = d.Kind != DiagKind.None ? d.Area : Horizontal(edges, w, h, x, y);
            (weights[i], weights[i + 1]) = (Quantize(a.X), Quantize(a.Y));
            // Диагональ «/»: левый край пикселя — её ступень, его доля уже в весах верхнего.
            if (d.Kind == DiagKind.Rising) vertical = false;
        }
        // Левый край пикселя — ступень диагонали «\» левого соседа: его доля уже в её весах.
        if (vertical && Edge(edges, w, h, x - 1, y, 1) > 0 && Diagonal(edges, w, h, x - 1, y).Kind == DiagKind.Falling) vertical = false;
        if (vertical)
        {
            var a = Vertical(edges, w, h, x, y);
            (weights[i + 2], weights[i + 3]) = (Quantize(a.X), Quantize(a.Y));
        }
    }

    private enum DiagKind { None, Rising, Falling }

    private readonly record struct Diag(DiagKind Kind, Vector2 Area);

    /// <summary>Ячейка «/» — пиксель с левым и верхним краем; «\» — с верхним краем, у правого соседа — левый.</summary>
    private static bool Cell(byte[] e, int w, int h, int x, int y, DiagKind kind) => kind == DiagKind.Rising
        ? Edge(e, w, h, x, y, 0) > 0 && Edge(e, w, h, x, y, 1) > 0
        : Edge(e, w, h, x, y, 1) > 0 && Edge(e, w, h, x + 1, y, 0) > 0;

    /// <summary>
    /// Диагональ через пиксель с верхним краем: серия ячеек одного вида подряд (шаг (1, −1) у «/», (1, 1) у «\») не короче
    /// <see cref="MinDiagonal"/>, у которой концы говорят о прямой: наклон (ступени на концах в разные стороны) или известен один
    /// конец. Изгиб (ступени в одну сторону) и серия без концов — не диагональ: там лучше ортогональные узоры. Вернёт доли пары
    /// «пиксель — верхний сосед»: отрезок сдвинут от середин ступеней на t, отсекает у угла пикселя треугольник (½ + t)²/2.
    /// </summary>
    private static Diag Diagonal(byte[] e, int w, int h, int x, int y)
    {
        Diag best = default;
        int bestN = 0;
        for (var kind = DiagKind.Rising; kind <= DiagKind.Falling; kind++)
        {
            if (!Cell(e, w, h, x, y, kind)) continue;
            int sy = kind == DiagKind.Rising ? -1 : 1;   // шаг по y на шаг вправо
            int back = 0, fwd = 0;
            while (back < MaxSearchDiagonal && Cell(e, w, h, x - back - 1, y - sy * (back + 1), kind)) back++;
            while (fwd < MaxSearchDiagonal && Cell(e, w, h, x + fwd + 1, y + sy * (fwd + 1), kind)) fwd++;
            int n = back + fwd + 1;
            if (n < MinDiagonal || n <= bestN) continue;
            int x0 = x - back, y0 = y - sy * back, x1 = x + fwd, y1 = y + sy * fwd;
            // Ступень за концом: круче (дальше вертикально) или положе (дальше горизонтально) — сдвиг отрезка ±½ на этом конце.
            float ts, te;
            if (kind == DiagKind.Rising)
            {
                ts = End(Edge(e, w, h, x0, y0 + 1, 0), Edge(e, w, h, x0 - 1, y0 + 1, 1));
                te = -End(Edge(e, w, h, x1 + 1, y1 - 1, 0), Edge(e, w, h, x1 + 1, y1, 1));
            }
            else
            {
                ts = -End(Edge(e, w, h, x0, y0 - 1, 0), Edge(e, w, h, x0 - 1, y0, 1));
                te = End(Edge(e, w, h, x1 + 1, y1 + 1, 0), Edge(e, w, h, x1 + 1, y1 + 1, 1));
            }
            if (back == MaxSearchDiagonal) ts = 0;   // конец не найден
            if (fwd == MaxSearchDiagonal) te = 0;
            if (ts == te) continue;
            float t = DiagonalOffset(ts, te, back + 0.5f, n);
            best = new Diag(kind, new Vector2((0.5f + t) * (0.5f + t) * 0.5f, (0.5f - t) * (0.5f - t) * 0.5f));
            bestN = n;
        }
        return best;
    }

    /// <summary>Ступень за концом серии: круче — ½, положе — −½, нет или обе — 0.</summary>
    private static float End(int steep, int flat) => steep > 0 && flat == 0 ? 0.5f : flat > 0 && steep == 0 ? -0.5f : 0f;

    /// <summary>
    /// Сдвиг отрезка от середин ступеней у ячейки на <paramref name="u"/> (0…n) серии длины <paramref name="n"/>: известны оба
    /// конца — прямая от <paramref name="ts"/> до <paramref name="te"/>; один — от него до 0 к середине серии, дальше 0.
    /// </summary>
    private static float DiagonalOffset(float ts, float te, float u, int n)
    {
        float half = n * 0.5f;
        if (ts != 0 && te != 0) return ts + (te - ts) * u / n;
        if (ts != 0) return u < half ? ts * (1 - u / half) : 0;
        return u > half ? te * (u - half) / half : 0;
    }

    private static Vector2 Horizontal(byte[] e, int w, int h, int x, int y)
    {
        int left = x;
        for (int i = 0; i < MaxSearch; i++)
        {
            if (Edge(e, w, h, left, y, 0) + Edge(e, w, h, left, y - 1, 0) > 0 || Edge(e, w, h, left - 1, y, 1) == 0) break;
            left--;
        }
        int right = x;
        for (int i = 0; i < MaxSearch; i++)
        {
            if (Edge(e, w, h, right + 1, y, 0) + Edge(e, w, h, right + 1, y - 1, 0) > 0 || Edge(e, w, h, right + 1, y, 1) == 0) break;
            right++;
        }
        int pattern = Edge(e, w, h, left, y, 0) | Edge(e, w, h, right + 1, y, 0) << 1 | Edge(e, w, h, left, y - 1, 0) << 2
            | Edge(e, w, h, right + 1, y - 1, 0) << 3;
        int dl = x - left, dr = right - x;
        return Area(pattern, dl, dr) * Corners(dl, dr, Edge(e, w, h, left, y + 1, 0), Edge(e, w, h, right + 1, y + 1, 0),
            Edge(e, w, h, left, y - 2, 0), Edge(e, w, h, right + 1, y - 2, 0));
    }

    private static Vector2 Vertical(byte[] e, int w, int h, int x, int y)
    {
        int top = y;
        for (int i = 0; i < MaxSearch; i++)
        {
            if (Edge(e, w, h, x, top, 1) + Edge(e, w, h, x - 1, top, 1) > 0 || Edge(e, w, h, x, top - 1, 0) == 0) break;
            top--;
        }
        int bottom = y;
        for (int i = 0; i < MaxSearch; i++)
        {
            if (Edge(e, w, h, x, bottom + 1, 1) + Edge(e, w, h, x - 1, bottom + 1, 1) > 0 || Edge(e, w, h, x, bottom + 1, 0) == 0) break;
            bottom++;
        }
        int pattern = Edge(e, w, h, x, top, 1) | Edge(e, w, h, x, bottom + 1, 1) << 1 | Edge(e, w, h, x - 1, top, 1) << 2
            | Edge(e, w, h, x - 1, bottom + 1, 1) << 3;
        int dt = y - top, db = bottom - y;
        return Area(pattern, dt, db) * Corners(dt, db, Edge(e, w, h, x + 1, top, 1), Edge(e, w, h, x + 1, bottom + 1, 1),
            Edge(e, w, h, x - 2, top, 1), Edge(e, w, h, x - 2, bottom + 1, 1));
    }

    /// <summary>Скругление углов: пересекающий край идёт дальше конца линии — настоящий угол, у ближнего конца смешивать меньше.</summary>
    private static Vector2 Corners(int dStart, int dEnd, int nearStart, int nearEnd, int farStart, int farEnd)
    {
        float ls = dEnd >= dStart ? 1 : 0, le = dStart >= dEnd ? 1 : 0;
        float rs = (1 - CornerRounding) * ls / (ls + le), re = (1 - CornerRounding) * le / (ls + le);
        return new(Math.Clamp(1 - (rs * nearStart + re * nearEnd), 0f, 1f), Math.Clamp(1 - (rs * farStart + re * farEnd), 0f, 1f));
    }

    private static byte Quantize(float v) => (byte)MathF.Round(Math.Clamp(v, 0f, 1f) * 255);

    private static void Blend(ReadOnlySpan<byte> rgb, byte[] weights, int w, int h, int x, int y, byte[] result)
    {
        int p = y * w + x;
        float right = x + 1 < w ? weights[(p + 1) * 4 + 3] / 255f : 0, bottom = y + 1 < h ? weights[(p + w) * 4 + 1] / 255f : 0;
        float left = weights[p * 4 + 2] / 255f, top = weights[p * 4] / 255f;
        if (right + bottom + left + top == 0)
        {
            rgb.Slice(p * 3, 3).CopyTo(result.AsSpan(p * 3, 3));
            return;
        }
        bool horizontal = MathF.Max(right, left) > MathF.Max(bottom, top);
        float w1 = horizontal ? right : bottom, w2 = horizontal ? left : top;
        int n1 = horizontal ? Math.Min(x + 1, w - 1) + y * w : x + Math.Min(y + 1, h - 1) * w;
        int n2 = horizontal ? Math.Max(x - 1, 0) + y * w : x + Math.Max(y - 1, 0) * w;
        for (int c = 0; c < 3; c++)
        {
            float lc = FilmicTonemap.DecodeSrgb(rgb[p * 3 + c] / 255f), a = FilmicTonemap.DecodeSrgb(rgb[n1 * 3 + c] / 255f),
                b = FilmicTonemap.DecodeSrgb(rgb[n2 * 3 + c] / 255f);
            float r = (w1 * (lc + (a - lc) * w1) + w2 * (lc + (b - lc) * w2)) / (w1 + w2);
            result[p * 3 + c] = (byte)MathF.Round(FilmicTonemap.EncodeSrgb(r) * 255);
        }
    }

    internal static void Contribute(ShaderContract c)
    {
        c.Section("сглаживание SMAA (SmaaMath)");
        c.Add("SMAA_THRESHOLD", Threshold, "порог края по разности яркости");
        c.Add("SMAA_CONTRAST_ADAPTATION", ContrastAdaptation, "край слабее сильнейшего рядом во столько раз - не край");
        c.Add("SMAA_MAX_SEARCH", MaxSearch, "поиск конца линии, пикселей в каждую сторону");
        c.Add("SMAA_MIN_DIAGONAL", MinDiagonal, "диагональ - серия не короче стольких ступеней");
        c.Add("SMAA_MAX_SEARCH_DIAGONAL", MaxSearchDiagonal, "поиск конца диагонали, ступеней в каждую сторону");
        c.Add("SMAA_CORNER_ROUNDING", CornerRounding, "скругление углов");
        c.Add("SMAA_SMOOTH_DISTANCE", SmoothDistance, "узоры U короче - смягчены");
        c.Add("SMAA_LUMA_R", Luma.X);
        c.Add("SMAA_LUMA_G", Luma.Y);
        c.Add("SMAA_LUMA_B", Luma.Z);
    }
}

/// <summary>
/// Проходы SMAA (<see cref="SmaaMath"/>) в фазе Post после тонмапа: тонмап пишет <c>post.ldr</c> (RGBA8, как задний буфер),
/// <c>post.smaa.edges</c> → <c>smaa.edges</c>, <c>post.smaa.weights</c> → <c>smaa.weights</c>, <c>post.smaa.blend</c> — в задний
/// буфер (с резкостью — в <c>post.smaa</c>). Кадр без выделений.
/// </summary>
internal sealed class SmaaPasses
{
    private const string Shader = "common/smaa.hlsl";
    private static readonly GraphicsPipelineDesc EdgesPipeline = new(Shader, "VsFullscreen", "PsSmaaEdges");
    private static readonly GraphicsPipelineDesc WeightsPipeline = new(Shader, "VsFullscreen", "PsSmaaWeights");
    private static readonly GraphicsPipelineDesc BlendPipeline = new(Shader, "VsFullscreen", "PsSmaaBlend");

    /// <summary>Корневые константы проходов SMAA: SRV кадра после тонмапа, краёв, весов.</summary>
    public const int SlotColor = 0, SlotEdges = 1, SlotWeights = 2;

    private readonly Action<RenderPassContext> _recordEdges, _recordWeights, _recordBlend;
    private int _color, _edges, _weights;

    public SmaaPasses()
    {
        _recordEdges = RecordEdges;
        _recordWeights = RecordWeights;
        _recordBlend = RecordBlend;
    }

    public static void Contribute(ShaderContract c)
    {
        c.AddSlot("SMAA_COLOR", SlotColor, "SRV кадра после тонмапа");
        c.AddSlot("SMAA_EDGES", SlotEdges, "SRV краёв");
        c.AddSlot("SMAA_WEIGHTS", SlotWeights, "SRV весов");
        SmaaMath.Contribute(c);
    }

    /// <summary>Проходы над <paramref name="color"/> (LDR после тонмапа, <see cref="Desc"/>) — в фазу Post; итог — в <paramref name="target"/>.</summary>
    public void Add(RenderGraph graph, RenderTexture color, RenderTexture target)
    {
        var edges = graph.CreateTexture("smaa.edges", Desc(graph));
        var weights = graph.CreateTexture("smaa.weights", Desc(graph));
        _color = graph.SrvOf(color);
        _edges = graph.SrvOf(edges);
        _weights = graph.SrvOf(weights);
        graph.AddPass("post.smaa.edges", FramePhase.Post).Read(color).Color(edges).Execute(_recordEdges);
        graph.AddPass("post.smaa.weights", FramePhase.Post).Read(edges).Color(weights).Execute(_recordWeights);
        graph.AddPass("post.smaa.blend", FramePhase.Post).Read(color).Read(weights).Color(target).Execute(_recordBlend);
    }

    /// <summary>Цель LDR размера кадра (RGBA8, как задний буфер): вход и выход ступеней после тонмапа.</summary>
    internal static TextureDesc Desc(RenderGraph graph) => new(TextureFormat.Rgba8Unorm, TextureUsage.ColorTarget, graph.Width, graph.Height);

    private void RecordEdges(RenderPassContext ctx)
    {
        ctx.SetPipeline(EdgesPipeline);
        ctx.SetConstant(SlotColor, _color);
        ctx.Draw(3);
    }

    private void RecordWeights(RenderPassContext ctx)
    {
        ctx.SetPipeline(WeightsPipeline);
        ctx.SetConstant(SlotEdges, _edges);
        ctx.Draw(3);
    }

    private void RecordBlend(RenderPassContext ctx)
    {
        ctx.SetPipeline(BlendPipeline);
        ctx.SetConstant(SlotColor, _color);
        ctx.SetConstant(SlotWeights, _weights);
        ctx.Draw(3);
    }
}
