using System.Numerics;

namespace Sword.Engine.Render;

/// <summary>
/// Матрица карты теней солнца: ортогональная проекция шара (центр, радиус) вдоль направления света, глубина — обратный Z
/// (ближе к солнцу — больше, как у сцены). Центр сдвинут к сетке текселей в осях света: при движении шара тень не дрожит.
/// </summary>
public static class SunShadowMath
{
    /// <summary>Запас глубины к солнцу, м: высокие заслоняющие вне шара (колонны, рельеф) тоже попадают в карту.</summary>
    public const float CasterMargin = 40f;

    /// <summary>
    /// Матрица мир → карта (вектор-строка, как <see cref="Matrix4x4"/>; в HLSL — <c>mul(m, float4(p, 1))</c>): xy — −1…1 на шаре,
    /// z — 1 у ближнего к солнцу края, 0 у дальнего. <paramref name="texel"/> — сторона текселя, м; <paramref name="depthRange"/> —
    /// глубина карты, м (единица z).
    /// </summary>
    public static Matrix4x4 ViewProj(Vector3 lightDir, Vector3 center, float radius, int size, out float texel, out float depthRange)
    {
        if (!(radius > 0) || size <= 0) throw new ArgumentOutOfRangeException(nameof(radius), $"шар радиуса {radius}, карта {size}");
        var d = Vector3.Normalize(lightDir);
        var up = MathF.Abs(d.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitZ;
        // Оси света без сдвига (только поворот): точка мира в них не зависит от центра — сетка текселей неподвижна в мире.
        var view = Matrix4x4.CreateLookAt(Vector3.Zero, d, up);
        texel = 2 * radius / size;
        depthRange = 2 * radius + CasterMargin;
        var c = Vector3.Transform(center, view);
        float sx = MathF.Round(c.X / texel) * texel, sy = MathF.Round(c.Y / texel) * texel;
        // Глубина: z вида = −(вдоль света); ближняя к солнцу плоскость — на radius + CasterMargin перед центром, там 1, дальняя — 0.
        var proj = new Matrix4x4(
            1 / radius, 0, 0, 0,
            0, 1 / radius, 0, 0,
            0, 0, 1 / depthRange, 0,
            -sx / radius, -sy / radius, 1 - (c.Z + radius + CasterMargin) / depthRange, 1);
        return view * proj;
    }
}

/// <summary>
/// Тени от солнца (<see cref="Renderer.Shadow"/>): два каскада карт глубины — ближний (вокруг точки взгляда, мелкий тексель) и
/// дальний (вся видимая земля). Кадр: <see cref="Prepare"/> — матрицы и цели (до констант кадра: их читают и заслоняющие, и
/// принимающие), <see cref="AddPass"/> — проходы <c>scene.shadow.near</c> и <c>scene.shadow.far</c> в фазе Opaque (до сцены),
/// заслоняющих рисует вызывающий: индекс каскада — корневая константа <c>SHADOW_CASCADE</c>. Принимающий проход читает
/// <see cref="Map"/> обоих каскадов, шейдер — <c>SunShadow</c> из <c>common/shadow.hlsli</c> (ближний, вне него — дальний).
/// </summary>
public sealed class SunShadow
{
    /// <summary>SRV «карты нет» для шейдера (<c>SHADOW_NONE</c>): тень — 1.</summary>
    public const uint None = uint.MaxValue;

    /// <summary>Каскадов: 0 — ближний, 1 — дальний.</summary>
    public const int Cascades = 2;

    /// <summary>Корневая константа проходов заслоняющих: индекс каскада (слоты сцены и скина — ниже).</summary>
    public const int SlotCascade = 21;

    /// <summary>Сдвиг точки по нормали, тексели: у скользящего света — полностью, у отвесного — половина (против «угрей»).</summary>
    public const float NormalOffset = 1.5f;

    /// <summary>Сдвиг глубины к солнцу, м.</summary>
    public const float DepthBias = 0.03f;

    private static readonly string[] PassNames = ["scene.shadow.near", "scene.shadow.far"];
    private static readonly string[] MapNames = ["shadow.near", "shadow.far"];

    private int _size = 2048;
    private float _softness = 1.5f;
    private readonly RenderTexture[] _maps = new RenderTexture[Cascades];
    private readonly uint[] _srv = [None, None];
    private readonly Matrix4x4[] _viewProj = new Matrix4x4[Cascades];
    private readonly float[] _texel = new float[Cascades], _bias = new float[Cascades];
    private readonly Action<RenderPassContext>[] _record = new Action<RenderPassContext>[Cascades];
    private Action<RenderPassContext>? _casters;

    public SunShadow()
    {
        for (int i = 0; i < Cascades; i++)
        {
            int cascade = i;
            _record[i] = ctx => Record(ctx, cascade);
        }
    }

    /// <summary>Тени в кадре (со следующего кадра); по умолчанию выключены — игра включает.</summary>
    public bool Enabled { get; set; }

    /// <summary>Сторона карты каскада, тексели (256…8192).</summary>
    public int Size
    {
        get => _size;
        set => _size = value is >= 256 and <= 8192 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "карта теней 256…8192");
    }

    /// <summary>Мягкость края: шаг выборок PCF 3×3, тексели (0.5…4).</summary>
    public float Softness
    {
        get => _softness;
        set => _softness = value is >= 0.5f and <= 4f ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "мягкость 0.5…4");
    }

    /// <summary>Карты в этом кадре (<see cref="Prepare"/> при <see cref="Enabled"/>).</summary>
    public bool Active { get; private set; }

    /// <summary>Карта каскада этого кадра — читать в принимающем проходе.</summary>
    public RenderTexture Map(int cascade) => _maps[cascade];

    /// <summary>SRV карты каскада для констант кадра или <see cref="None"/>.</summary>
    public uint Srv(int cascade) => _srv[cascade];

    /// <summary>Матрица мир → карта каскада (<see cref="SunShadowMath.ViewProj"/>).</summary>
    public Matrix4x4 ViewProj(int cascade) => _viewProj[cascade];

    /// <summary>Сторона текселя карты каскада, м.</summary>
    public float Texel(int cascade) => _texel[cascade];

    /// <summary><see cref="DepthBias"/> в единицах глубины карты каскада.</summary>
    public float Bias(int cascade) => _bias[cascade];

    internal void BeginFrame()
    {
        Active = false;
        _srv[0] = _srv[1] = None;
    }

    /// <summary>
    /// Матрицы каскадов вдоль <paramref name="lightDir"/>: ближний — шар (<paramref name="nearCenter"/>, <paramref name="nearRadius"/>),
    /// дальний — (<paramref name="farCenter"/>, <paramref name="farRadius"/>), м; и цели кадра. Выключены — false.
    /// </summary>
    public bool Prepare(RenderFrame frame, Vector3 lightDir, Vector3 nearCenter, float nearRadius, Vector3 farCenter, float farRadius)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!Enabled) return false;
        Cascade(frame, 0, lightDir, nearCenter, nearRadius);
        Cascade(frame, 1, lightDir, farCenter, farRadius);
        Active = true;
        return true;
    }

    private void Cascade(RenderFrame frame, int i, Vector3 lightDir, Vector3 center, float radius)
    {
        _viewProj[i] = SunShadowMath.ViewProj(lightDir, center, radius, _size, out _texel[i], out float range);
        _bias[i] = DepthBias / range;
        _maps[i] = frame.Graph.CreateTexture(MapNames[i], new TextureDesc(TextureFormat.Depth32Float, TextureUsage.DepthTarget, _size, _size, ClearDepth: 0));
        _srv[i] = (uint)frame.Graph.SrvOf(_maps[i]);
    }

    /// <summary>Проходы заслоняющих (фаза Opaque, только глубина, очистка 0) — по каскаду; <paramref name="casters"/> рисует их.</summary>
    public void AddPass(RenderFrame frame, Action<RenderPassContext> casters)
    {
        if (!Active) throw new InvalidOperationException("тени: AddPass без Prepare в этом кадре");
        _casters = casters;
        for (int i = 0; i < Cascades; i++)
            frame.Graph.AddPass(PassNames[i], FramePhase.Opaque).Depth(_maps[i], DepthAccess.Write, clear: 0f).Execute(_record[i]);
    }

    private void Record(RenderPassContext ctx, int cascade)
    {
        // Слоты обнуляются только в начале прохода: индекс каскада ставится до заслоняющих, их слоты — другие.
        ctx.SetConstant(SlotCascade, cascade);
        _casters!(ctx);
    }

    internal static void Contribute(ShaderContract c)
    {
        c.Section("тени от солнца (SunShadow)");
        c.Add("SHADOW_NONE", None, "карты нет - тень 1");
        c.Add("SHADOW_NORMAL_OFFSET", NormalOffset, "сдвиг по нормали, тексели");
        c.AddSlot("SHADOW_CASCADE", SlotCascade, "каскад прохода заслоняющих: 0 - ближний, 1 - дальний");
    }
}
