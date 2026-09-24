using System.Numerics;
using Sword.Engine.Physics;
using Sword.Game.Arena;
using Xunit.Abstractions;

namespace Sword.Game.Tests;

/// <summary>
/// Земля арены для физики, точка 7е (<see cref="ArenaGround"/>): свойства граней своими числами. Пирамида — центр (−16, 0),
/// ярус k (1…5) — квадрат полушириной 7 − k с верхом на 0,12·k м; гладкий рельеф — высота без ступеней. Ровная земля, верх
/// яруса у кромки, подступенок, над ребром и углом, внутренние углы, погружение в ярус, ярус без дна, нормаль рельефа против конечных
/// разностей высоты, касания = грани с глубиной &gt; 0, непрерывность глубины, стена и колонны, 0 Б на вызов.
/// </summary>
public sealed class ArenaGroundTests(ITestOutputHelper output)
{
    private static readonly IGround Ground = ArenaGround.Instance;

    private const float CentreX = -16, Rise = 0.12f;

    private static float Half(int tier) => 7 - tier;

    private static float Top(int tier) => tier * Rise;

    /// <summary>Стороны пирамиды: направление наружу (XZ) и направление вдоль стороны.</summary>
    private static readonly (Vector2 Out, Vector2 Along)[] Sides =
    [
        (new(1, 0), new(0, 1)),
        (new(-1, 0), new(0, 1)),
        (new(0, 1), new(1, 0)),
        (new(0, -1), new(1, 0)),
    ];

    /// <summary>Точка у стороны пирамиды: <paramref name="fromCentre"/> м наружу от центра, <paramref name="along"/> вдоль стороны, высота y.</summary>
    private static Vector3 AtSide((Vector2 Out, Vector2 Along) side, float fromCentre, float along, float y)
    {
        var p = side.Out * fromCentre + side.Along * along;
        return new Vector3(CentreX + p.X, y, p.Y);
    }

    private static GroundTouch[] All(Vector3 center, float radius)
    {
        Span<GroundTouch> span = stackalloc GroundTouch[IGround.MaxTouches];
        int n = Ground.Touches(center, radius, span);
        return span[..n].ToArray();
    }

    private static void AssertNear(Vector3 expected, Vector3 actual, float tolerance, string what) =>
        Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"{what}: ждали {expected}, получили {actual}");

    [Fact]
    public void Faces_FitMaxTouches_UnknownFaceThrows()
    {
        Assert.Equal(6, ArenaGround.FaceCount);
        Assert.True(ArenaGround.FaceCount <= IGround.MaxTouches);
        Assert.Throws<ArgumentOutOfRangeException>(() => Ground.Depth(Vector3.Zero, 0.1f, -1, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => Ground.Depth(Vector3.Zero, 0.1f, ArenaGround.FaceCount, out _));
    }

    /// <summary>Ровная земля (вне участков): одна грань — гладкий рельеф, нормаль вверх, глубина = радиус − высота центра.</summary>
    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(5f, -3f)]
    [InlineData(-30f, 20f)]
    [InlineData(40f, -35f)]
    [InlineData(-16f, 12f)]   // рядом с пирамидой, но дальше радиуса сферы от подступенка
    public void FlatGround_NormalUp_DepthIsRadiusMinusHeight(float x, float z)
    {
        const float r = 0.1f;
        Assert.Equal(-1, ArenaTerrain.ZoneAt(x, z));
        foreach (float y in new[] { 0.09f, 0.05f, 0f, -0.07f })
        {
            var touches = All(new Vector3(x, y, z), r);
            var t = Assert.Single(touches);
            Assert.Equal(ArenaGround.SmoothFace, t.Face);
            Assert.Equal(Vector3.UnitY, t.Normal);
            Assert.Equal(r - y, t.Depth);
        }
        Assert.Empty(All(new Vector3(x, r + 0.001f, z), r));
    }

    /// <summary>
    /// Верх яруса у самой кромки: глубина — ровно вертикальное погружение (ложной глубины от наклонной нормали кромки нет),
    /// нормаль строго вверх, грань — только этот ярус (ни рельеф, ни нижние ярусы). Над верхом на миллиметр — касаний нет.
    /// </summary>
    [Fact]
    public void TierTop_AtEdge_NoFalseDepth_NormalUp()
    {
        const float r = 0.08f, sink = 0.004f;
        for (int tier = 1; tier <= 5; tier++)
            foreach (var side in Sides)
                foreach (float fromEdge in new[] { 0.0005f, 0.01f, 0.05f })
                    foreach (float along in new[] { 0f, 0.5f * Half(tier), -(Half(tier) - fromEdge) })   // середина, ближе к углу, у самого угла
                    {
                        var c = AtSide(side, Half(tier) - fromEdge, along, Top(tier) + r - sink);
                        var t = Assert.Single(All(c, r));
                        Assert.Equal(tier, t.Face);
                        Assert.Equal(0f, t.Normal.X);
                        Assert.Equal(1f, t.Normal.Y);
                        Assert.Equal(0f, t.Normal.Z);
                        Assert.Equal(sink, t.Depth, 1e-5f);
                        Assert.Empty(All(c + new Vector3(0, sink + 0.001f, 0), r));
                    }
    }

    /// <summary>
    /// Проступи ярусов физики совпадают с высотой, на которой стоят живые (<see cref="ArenaTerrain.Height"/>): сфера,
    /// опущенная на высоту рельефа в случайной точке проступи, касается ровно своего яруса на заданную глубину, нормаль вверх.
    /// </summary>
    [Fact]
    public void TierTreads_MatchHeightOfTheLiving()
    {
        var rng = new Random(71);
        const float r = 0.1f, sink = 0.003f;
        for (int i = 0; i < 2000; i++)
        {
            int tier = 1 + rng.Next(5);
            float outer = Half(tier) - r - 0.01f, inner = tier < 5 ? Half(tier + 1) + r + 0.01f : 0;
            float u, v;
            do
            {
                u = (float)(rng.NextDouble() * 2 - 1) * outer;
                v = (float)(rng.NextDouble() * 2 - 1) * outer;
            }
            while (MathF.Max(MathF.Abs(u), MathF.Abs(v)) < inner);
            float x = CentreX + u, z = v, height = ArenaTerrain.Height(x, z);
            Assert.Equal(Top(tier), height);
            var t = Assert.Single(All(new Vector3(x, height + r - sink, z), r));
            Assert.Equal(tier, t.Face);
            Assert.Equal(Vector3.UnitY, t.Normal);
            Assert.Equal(sink, t.Depth, 1e-5f);
        }
    }

    /// <summary>Подступенок: сфера сбоку на середине высоты подступенка — одна грань (ярус), нормаль горизонтальна наружу.</summary>
    [Fact]
    public void Riser_NormalHorizontal()
    {
        const float r = 0.05f, depth = 0.01f;
        for (int tier = 1; tier <= 5; tier++)
            foreach (var side in Sides)
            {
                var c = AtSide(side, Half(tier) + r - depth, 0.4f * Half(tier), Top(tier) - Rise / 2);
                var t = Assert.Single(All(c, r));
                Assert.Equal(tier, t.Face);
                Assert.Equal(new Vector3(side.Out.X, 0, side.Out.Y), t.Normal);
                Assert.Equal(depth, t.Depth, 1e-5f);
            }
    }

    /// <summary>
    /// За кромкой (центр сферы над ребром снаружи): нормаль — от ребра к центру, глубина — радиус минус расстояние до ребра;
    /// у угла яруса — от вершины. Ближайшая точка поверхности (центр − нормаль · расстояние) лежит на ребре.
    /// </summary>
    [Fact]
    public void BeyondEdge_NormalFromEdge_CornerFromVertex()
    {
        const float r = 0.1f;
        for (int tier = 1; tier <= 5; tier++)
        {
            foreach (var side in Sides)
            {
                var c = AtSide(side, Half(tier) + 0.03f, 0.2f, Top(tier) + 0.04f);
                var t = Assert.Single(All(c, r));
                Assert.Equal(tier, t.Face);
                AssertNear(new Vector3(0.6f * side.Out.X, 0.8f, 0.6f * side.Out.Y), t.Normal, 1e-4f, $"ярус {tier}, над ребром");
                Assert.Equal(r - 0.05f, t.Depth, 1e-5f);
                var closest = c - t.Normal * (r - t.Depth);
                AssertNear(AtSide(side, Half(tier), 0.2f, Top(tier)), closest, 1e-4f, "ближайшая точка — на ребре");
            }

            // угол: наружу по X и Z на 3 см, над верхом на 4 см
            var corner = new Vector3(CentreX + Half(tier) + 0.03f, Top(tier) + 0.04f, -(Half(tier) + 0.03f));
            var ct = Assert.Single(All(corner, r));
            Assert.Equal(tier, ct.Face);
            AssertNear(Vector3.Normalize(new Vector3(0.03f, 0.04f, -0.03f)), ct.Normal, 1e-4f, $"ярус {tier}, угол");
            Assert.Equal(r - MathF.Sqrt(0.0034f), ct.Depth, 1e-5f);
        }
    }

    /// <summary>
    /// Внутренний угол — две грани, у каждой своя глубина: у подножия пирамиды — рельеф (вверх) и подступенок первого яруса
    /// (вбок), на проступи яруса k у подступенка яруса k + 1 — верх яруса k и подступенок k + 1. Каждая глубина — та же,
    /// что <see cref="IGround.Depth"/> по номеру грани.
    /// </summary>
    [Fact]
    public void InnerCorner_TwoFaces_EachOwnDepth()
    {
        const float r = 0.1f, intoTread = 0.01f, intoRiser = 0.02f;
        for (int tier = 0; tier <= 4; tier++)   // 0 — подножие: «проступь» — рельеф
            foreach (var side in Sides)
            {
                float treadTop = tier == 0 ? 0 : Top(tier);
                var c = AtSide(side, Half(tier + 1) + r - intoRiser, 0.3f, treadTop + r - intoTread);
                var touches = All(c, r);
                Assert.Equal(2, touches.Length);
                Assert.Equal(tier, touches[0].Face);
                Assert.Equal(tier + 1, touches[1].Face);
                AssertNear(Vector3.UnitY, touches[0].Normal, 1e-6f, "проступь");
                AssertNear(new Vector3(side.Out.X, 0, side.Out.Y), touches[1].Normal, 1e-6f, "подступенок");
                Assert.Equal(intoTread, touches[0].Depth, 1e-5f);
                Assert.Equal(intoRiser, touches[1].Depth, 1e-5f);
                foreach (var t in touches)
                {
                    Assert.Equal(t.Depth, Ground.Depth(c, r, t.Face, out var n));
                    Assert.Equal(t.Normal, n);
                }
            }
    }

    /// <summary>
    /// Погружение в верх яруса выталкивает вверх, а не вбок: центр сферы под проступью (глубже её радиуса), дальше от
    /// кромки, чем от верха, — все касания с нормалью вверх, глубина своего яруса — радиус + погружение. Равенство
    /// расстояний до кромки и до верха — тоже вверх.
    /// </summary>
    [Fact]
    public void SunkIntoTierTop_PushedUpNotSideways()
    {
        var rng = new Random(72);
        const float r = 0.1f;
        for (int i = 0; i < 3000; i++)
        {
            int tier = 1 + rng.Next(5);
            var side = Sides[rng.Next(4)];
            float sink = 0.002f + (float)rng.NextDouble() * 0.11f;
            float maxFromEdge = tier < 5 ? 1 - r - 0.01f : Half(tier);
            float fromEdge = sink + 0.001f + (float)rng.NextDouble() * (maxFromEdge - sink - 0.001f);
            float along = (float)(rng.NextDouble() * 2 - 1) * (Half(tier) - fromEdge);
            var c = AtSide(side, Half(tier) - fromEdge, along, Top(tier) - sink);
            var touches = All(c, r);
            Assert.NotEmpty(touches);
            foreach (var t in touches) AssertNear(Vector3.UnitY, t.Normal, 1e-6f, $"ярус {tier}, погружение {sink}, от кромки {fromEdge}");
            var own = Assert.Single(touches, t => t.Face == tier);
            Assert.Equal(r + sink, own.Depth, 1e-5f);
        }

        // Равенство: ярус 5 (полуширина 2), 0,25 м от кромки и 0,25 м под верхом — числа точны во float.
        var tie = new Vector3(CentreX + 2 - 0.25f, Top(5) - 0.25f, 0);
        Assert.Equal(r + 0.25f, Ground.Depth(tie, r, 5, out var tieNormal));
        Assert.Equal(Vector3.UnitY, tieNormal);
    }

    /// <summary>
    /// Ярус — призма без дна: сколь угодно глубоко под проступью (до 2 м — ниже дна ящика SWORD на −1 м) грань яруса выталкивает
    /// к ближайшему выходу — вверх или вбок, никогда вниз; глубина — радиус плюс расстояние до этого выхода.
    /// </summary>
    [Fact]
    public void TierPrism_Bottomless_NeverPushesDown()
    {
        var rng = new Random(77);
        const float r = 0.1f;
        for (int i = 0; i < 3000; i++)
        {
            int tier = 1 + rng.Next(5);
            float u = (float)(rng.NextDouble() * 2 - 1) * Half(tier), v = (float)(rng.NextDouble() * 2 - 1) * Half(tier);
            float below = 0.01f + (float)rng.NextDouble() * 2;
            var c = new Vector3(CentreX + u, Top(tier) - below, v);
            float toSide = Half(tier) - MathF.Max(MathF.Abs(u), MathF.Abs(v));
            float depth = Ground.Depth(c, r, tier, out var n);
            Assert.True(n.Y >= 0, $"ярус {tier}, {below} м под верхом, {toSide} м от кромки: нормаль {n} — вниз");
            Assert.Equal(r + MathF.Min(below, toSide), depth, 1e-5f);
        }
    }

    /// <summary>
    /// Нормаль гладкого рельефа на холмах, склоне и ухабах (и в переходах к ровному) согласована с высотой: совпадает с нормалью
    /// по конечным разностям <see cref="ArenaTerrain.SmoothHeightAt(float, float)"/>; глубина центра на поверхности — ровно
    /// радиус; сфера, лежащая на поверхности по нормали, касается (глубина ≈ 0), а не проваливается и не висит.
    /// </summary>
    [Fact]
    public void SmoothNormal_MatchesHeightFiniteDifferences()
    {
        var rng = new Random(73);
        (Vector2 Centre, float Radius)[] zones = [(new(16, 0), 9.5f), (new(0, 18), 9.5f), (new(0, -18), 8.5f)];
        const float r = 0.1f, e = 0.01f;
        float worstNormal = 0, worstRest = 0;
        for (int i = 0; i < 3000; i++)
        {
            var (centre, radius) = zones[i % zones.Length];
            float a = (float)(rng.NextDouble() * MathF.Tau), d = radius * MathF.Sqrt((float)rng.NextDouble());
            float x = centre.X + MathF.Cos(a) * d, z = centre.Y + MathF.Sin(a) * d;
            float xp = x + e, xm = x - e, zp = z + e, zm = z - e;
            float gx = (ArenaTerrain.SmoothHeightAt(xp, z) - ArenaTerrain.SmoothHeightAt(xm, z)) / (xp - xm);
            float gz = (ArenaTerrain.SmoothHeightAt(x, zp) - ArenaTerrain.SmoothHeightAt(x, zm)) / (zp - zm);
            var expected = Vector3.Normalize(new Vector3(-gx, 1, -gz));
            float h = ArenaTerrain.SmoothHeightAt(x, z);

            Assert.Equal(r, Ground.Depth(new Vector3(x, h, z), r, ArenaGround.SmoothFace, out var n));
            worstNormal = MathF.Max(worstNormal, Vector3.Distance(expected, n));

            var resting = new Vector3(x, h, z) + n * r;
            worstRest = MathF.Max(worstRest, MathF.Abs(Ground.Depth(resting, r, ArenaGround.SmoothFace, out _)));
        }
        output.WriteLine($"худшее расхождение нормали с конечными разностями {worstNormal:G3}, глубина лежащей сферы {worstRest:G3} м");
        Assert.True(worstNormal < 1e-3f, $"нормаль разошлась с конечными разностями на {worstNormal}");
        Assert.True(worstRest < 5e-4f, $"сфера на поверхности по нормали: глубина {worstRest}");
    }

    /// <summary>
    /// Высота с градиентом — та же функция: бит в бит <see cref="ArenaTerrain.SmoothHeightAt(float, float)"/> по всей арене,
    /// вне пирамиды — бит в бит <see cref="ArenaTerrain.Height"/> (земля трупов = земля живых); вне участков градиент 0.
    /// </summary>
    [Fact]
    public void SmoothHeightWithGradient_IsTheSameHeight()
    {
        for (float z = -40; z <= 40; z += 0.37f)
            for (float x = -40; x <= 40; x += 0.37f)
            {
                float h = ArenaTerrain.SmoothHeightAt(x, z, out var g);
                Assert.Equal(BitConverter.SingleToInt32Bits(ArenaTerrain.SmoothHeightAt(x, z)), BitConverter.SingleToInt32Bits(h));
                if (MathF.Max(MathF.Abs(x - CentreX), MathF.Abs(z)) > 6.5f) Assert.Equal(ArenaTerrain.Height(x, z), h);
                if (ArenaTerrain.ZoneAt(x, z) == -1) Assert.Equal(Vector2.Zero, g);
            }
    }

    /// <summary>
    /// Касания — ровно грани с глубиной &gt; 0 (отсев ярусов ничего не теряет), по возрастанию номера, глубина и нормаль — те
    /// же, что у <see cref="IGround.Depth"/> по номеру грани; короткий буфер — первые касания по порядку.
    /// </summary>
    [Fact]
    public void Touches_AreExactlyFacesWithPositiveDepth()
    {
        var rng = new Random(74);
        Span<GroundTouch> span = stackalloc GroundTouch[IGround.MaxTouches];
        Span<GroundTouch> expected = stackalloc GroundTouch[ArenaGround.FaceCount];
        int multi = 0;
        for (int i = 0; i < 20000; i++)
        {
            float R() => (float)(rng.NextDouble() * 2 - 1);
            var c = i % 4 == 0
                ? new Vector3(R() * 40, 0.4f + R() * 2, R() * 40)                       // вся арена
                : new Vector3(CentreX + R() * 7.5f, 0.3f + R() * 0.6f, R() * 7.5f);     // пирамида
            float r = 0.03f + (float)rng.NextDouble() * 0.17f;
            int n = 0;
            for (int face = 0; face < ArenaGround.FaceCount; face++)
            {
                float d = Ground.Depth(c, r, face, out var normal);
                if (d > 0) expected[n++] = new GroundTouch(face, normal, d);
            }
            int count = Ground.Touches(c, r, span);
            Assert.Equal(n, count);
            for (int k = 0; k < n; k++) Assert.Equal(expected[k], span[k]);
            if (n > 1) multi++;
            if (n > 0)
            {
                Assert.Equal(1, Ground.Touches(c, r, span[..1]));
                Assert.Equal(expected[0], span[0]);
                Assert.Equal(0, Ground.Touches(c, r, Span<GroundTouch>.Empty));
            }
        }
        output.WriteLine($"точек с несколькими гранями: {multi} из 20000");
        Assert.True(multi > 100, $"случаев с несколькими гранями — {multi}: выборка не проверяет углы");
    }

    /// <summary>
    /// Глубина каждой грани непрерывна: у ярусов — точное расстояние, поэтому сдвиг центра на δ меняет глубину не больше чем
    /// на |δ|; у рельефа вблизи поверхности — не больше чем на 3|δ|. Случайные блуждания мелкими шагами через рёбра, углы и
    /// внутренность ярусов.
    /// </summary>
    [Fact]
    public void FaceDepth_IsContinuous()
    {
        var rng = new Random(75);
        const float r = 0.1f;
        for (int walk = 0; walk < 60; walk++)
        {
            int face = walk % ArenaGround.FaceCount;
            float R() => (float)(rng.NextDouble() * 2 - 1);
            Vector3 c = face == 0
                ? new Vector3(new[] { 16f, 0f, 0f }[walk % 3] + R() * 8, 0, new[] { 0f, 18f, -18f }[walk % 3] + R() * 8)
                : new Vector3(CentreX + R() * (Half(face) + 0.2f), Top(face) + R() * 0.2f, R() * (Half(face) + 0.2f));
            if (face == 0) c.Y = ArenaTerrain.SmoothHeightAt(c.X, c.Z) + R() * 0.2f;
            float prev = Ground.Depth(c, r, face, out _);
            for (int step = 0; step < 2000; step++)
            {
                var next = c + new Vector3(R(), R(), R()) * 0.002f;
                float moved = Vector3.Distance(c, next);   // настоящий сдвиг (после округления координат)
                c = next;
                float depth = Ground.Depth(c, r, face, out _);
                float bound = (face == 0 ? 3 : 1) * moved + 2e-6f;
                Assert.True(MathF.Abs(depth - prev) <= bound, $"грань {face}: скачок глубины {depth - prev} при сдвиге {moved} в {c}");
                prev = depth;
            }
        }
    }

    /// <summary>
    /// Нечисловой (NaN) центр — ни одного касания. Бесконечный центр — не вход земли (положения тел физика держит конечными):
    /// у y = −∞ глубина рельефа бесконечна.
    /// </summary>
    [Fact]
    public void NaNCenter_NoTouches()
    {
        Span<GroundTouch> span = stackalloc GroundTouch[IGround.MaxTouches];
        Assert.Equal(0, Ground.Touches(new Vector3(float.NaN, 0, 0), 0.1f, span));
        Assert.Equal(0, Ground.Touches(new Vector3(CentreX, float.NaN, 0), 0.1f, span));
        Assert.Equal(0, Ground.Touches(new Vector3(CentreX, 0.3f, float.NaN), 0.1f, span));
    }

    /// <summary>Стена и колонны физики — геометрия арены: радиус арены, 8 колонн (центр xz, радиус), все внутри стены.</summary>
    [Fact]
    public void WallAndPillars_AreArenaLayout()
    {
        Assert.Equal(ArenaLayout.ArenaRadius, ArenaGround.WallRadius);
        var pillars = ArenaGround.Pillars;
        Assert.Equal(ArenaLayout.PillarCount, pillars.Length);
        for (int i = 0; i < pillars.Length; i++)
        {
            var p = ArenaLayout.Pillars[i];
            Assert.Equal(new Pillar(p.X, p.Y, p.Z), pillars[i]);
            Assert.True(new Vector2(pillars[i].X, pillars[i].Z).Length() + pillars[i].Radius < ArenaGround.WallRadius);
        }
    }

    /// <summary>0 Б на вызов: касания и глубины по всем граням через интерфейс, в точках пирамиды, рельефа и ровной земли.</summary>
    [Fact]
    public void Calls_AllocateNothing()
    {
        var rng = new Random(76);
        var points = new Vector3[512];
        for (int i = 0; i < points.Length; i++)
        {
            float R() => (float)(rng.NextDouble() * 2 - 1);
            points[i] = (i % 3) switch
            {
                0 => new Vector3(CentreX + R() * 7, 0.3f + R() * 0.4f, R() * 7),
                1 => new Vector3(16 + R() * 9, 0.3f + R() * 0.3f, R() * 9),
                _ => new Vector3(R() * 40, R() * 0.2f, R() * 40),
            };
        }
        float sink = 0;
        void Round()
        {
            Span<GroundTouch> span = stackalloc GroundTouch[IGround.MaxTouches];
            foreach (var p in points)
            {
                int n = Ground.Touches(p, 0.1f, span);
                for (int k = 0; k < n; k++) sink += span[k].Depth;
                for (int face = 0; face < ArenaGround.FaceCount; face++) sink += Ground.Depth(p, 0.1f, face, out var normal) + normal.Y;
            }
        }
        for (int i = 0; i < 50; i++) Round();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++) Round();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"200 кругов по 512 точек выделили {allocated} Б");
        Assert.True(float.IsFinite(sink));
    }
}
