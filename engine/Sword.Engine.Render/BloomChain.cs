namespace Sword.Engine.Render;

/// <summary>
/// Проходы bloom (<see cref="BloomKernel"/>) в фазе Post перед тонмапом: порог и уменьшение, цепочка уменьшений, затем
/// гауссианы x/y от широкой ступени к узкой. Итог — <c>bloom.y2</c> (½ экрана, RGBA16F). Кадр без выделений.
/// </summary>
internal sealed class BloomChain
{
    private const string Shader = "common/post.hlsl";
    private static readonly GraphicsPipelineDesc Setup = new(Shader, "VsFullscreen", "PsBloomSetup");
    private static readonly GraphicsPipelineDesc Downsample = new(Shader, "VsFullscreen", "PsBloomDownsample");
    private static readonly GraphicsPipelineDesc Blur = new(Shader, "VsFullscreen", "PsBloomBlur");

    /// <summary>Корневые константы проходов bloom: вход (SRV), прибавка (SRV или BLOOM_NONE), порог, экспозиция, направление, радиус, N, усиление.</summary>
    public const int SlotSource = 0, SlotAdditive = 1, SlotThreshold = 2, SlotExposure = 3, SlotVertical = 4, SlotRadius = 5, SlotTaps = 6,
        SlotGain = 7;

    private const int N = BloomKernel.Stages;

    // Имена по уровню (0 — ½ … 5 — 1/64): постоянные строки — без выделений в кадре.
    private static readonly string[] LevelNames = ["bloom.d2", "bloom.d4", "bloom.d8", "bloom.d16", "bloom.d32", "bloom.d64"];
    private static readonly string[] LevelPasses =
        ["post.bloom.setup", "post.bloom.d4", "post.bloom.d8", "post.bloom.d16", "post.bloom.d32", "post.bloom.d64"];
    private static readonly string[] XNames = ["bloom.x2", "bloom.x4", "bloom.x8", "bloom.x16", "bloom.x32", "bloom.x64"];
    private static readonly string[] XPasses = ["post.bloom.x2", "post.bloom.x4", "post.bloom.x8", "post.bloom.x16", "post.bloom.x32", "post.bloom.x64"];
    private static readonly string[] YNames = ["bloom.y2", "bloom.y4", "bloom.y8", "bloom.y16", "bloom.y32", "bloom.y64"];
    private static readonly string[] YPasses = ["post.bloom.y2", "post.bloom.y4", "post.bloom.y8", "post.bloom.y16", "post.bloom.y32", "post.bloom.y64"];

    private readonly RenderTexture[] _levels = new RenderTexture[N];
    private readonly int[] _levelSrv = new int[N], _xSrv = new int[N], _ySrv = new int[N], _taps = new int[N];
    private readonly float[] _radius = new float[N], _gain = new float[N];
    private readonly Action<RenderPassContext>[] _recordLevel = new Action<RenderPassContext>[N];
    private readonly Action<RenderPassContext>[] _recordX = new Action<RenderPassContext>[N];
    private readonly Action<RenderPassContext>[] _recordY = new Action<RenderPassContext>[N];
    private int _sceneSrv;
    private float _threshold, _exposure;

    public BloomChain()
    {
        for (int i = 0; i < N; i++)
        {
            int s = i;
            _recordLevel[s] = ctx => RecordLevel(ctx, s);
            _recordX[s] = ctx => RecordBlur(ctx, s, vertical: false);
            _recordY[s] = ctx => RecordBlur(ctx, s, vertical: true);
        }
    }

    public static void Contribute(ShaderContract c)
    {
        c.AddSlot("BLOOM_SOURCE", SlotSource, "SRV входа");
        c.AddSlot("BLOOM_ADDITIVE", SlotAdditive, "SRV суммы более широких ступеней или BLOOM_NONE");
        c.AddSlot("BLOOM_THRESHOLD", SlotThreshold, "порог, float");
        c.AddSlot("BLOOM_EXPOSURE", SlotExposure, "экспозиция, float");
        c.AddSlot("BLOOM_VERTICAL", SlotVertical, "0 - по x, 1 - по y");
        c.AddSlot("BLOOM_RADIUS", SlotRadius, "радиус ядра, тексели уровня, float");
        c.AddSlot("BLOOM_TAPS", SlotTaps, "целый радиус N: тексели -N..N");
        c.AddSlot("BLOOM_GAIN", SlotGain, "усиление ступени, float");
    }

    /// <summary>Проходы bloom над цветом сцены — в фазу Post; вернёт итог (½ экрана) для тонмапа этого кадра.</summary>
    public RenderTexture Add(RenderGraph graph, RenderTexture scene, int sceneSrv, float exposure, in BloomSettings bloom)
    {
        _sceneSrv = sceneSrv;
        _exposure = exposure;
        _threshold = bloom.Threshold;
        var input = scene;
        for (int l = 0; l < N; l++)
        {
            var level = graph.CreateTexture(LevelNames[l], Desc(graph, l));
            _levels[l] = level;
            _levelSrv[l] = graph.SrvOf(level);
            graph.AddPass(LevelPasses[l], FramePhase.Post).Read(input).Color(level).Execute(_recordLevel[l]);
            input = level;
        }
        RenderTexture sum = default;
        for (int s = N - 1; s >= 0; s--)
        {
            var desc = Desc(graph, s);
            _radius[s] = BloomKernel.Radius(desc.Width, s);
            _taps[s] = BloomKernel.IntegerRadius(_radius[s]);
            _gain[s] = BloomKernel.Gain(s, bloom.Intensity);
            var x = graph.CreateTexture(XNames[s], desc);
            var y = graph.CreateTexture(YNames[s], desc);
            _xSrv[s] = graph.SrvOf(x);
            _ySrv[s] = graph.SrvOf(y);
            graph.AddPass(XPasses[s], FramePhase.Post).Read(_levels[s]).Color(x).Execute(_recordX[s]);
            var pass = graph.AddPass(YPasses[s], FramePhase.Post).Read(x);
            if (s < N - 1) pass.Read(sum);
            pass.Color(y).Execute(_recordY[s]);
            sum = y;
        }
        return sum;
    }

    private static TextureDesc Desc(RenderGraph graph, int level) => new(TextureFormat.Rgba16Float, TextureUsage.ColorTarget,
        BloomKernel.LevelSize(graph.Width, level), BloomKernel.LevelSize(graph.Height, level));

    private void RecordLevel(RenderPassContext ctx, int level)
    {
        if (level == 0)
        {
            ctx.SetPipeline(Setup);
            ctx.SetConstant(SlotSource, _sceneSrv);
            ctx.SetConstant(SlotThreshold, _threshold);
            ctx.SetConstant(SlotExposure, _exposure);
        }
        else
        {
            ctx.SetPipeline(Downsample);
            ctx.SetConstant(SlotSource, _levelSrv[level - 1]);
        }
        ctx.Draw(3);
    }

    private void RecordBlur(RenderPassContext ctx, int stage, bool vertical)
    {
        ctx.SetPipeline(Blur);
        ctx.SetConstant(SlotSource, vertical ? _xSrv[stage] : _levelSrv[stage]);
        // По x — белым и без прибавки; по y — усиление ступени и сумма более широких (у самой широкой её нет).
        ctx.SetConstant(SlotAdditive, vertical && stage < N - 1 ? _ySrv[stage + 1] : -1);
        ctx.SetConstant(SlotVertical, vertical ? 1 : 0);
        ctx.SetConstant(SlotRadius, _radius[stage]);
        ctx.SetConstant(SlotTaps, _taps[stage]);
        ctx.SetConstant(SlotGain, vertical ? _gain[stage] : 1f);
        ctx.Draw(3);
    }
}
