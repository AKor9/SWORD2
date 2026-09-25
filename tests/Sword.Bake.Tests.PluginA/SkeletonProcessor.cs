namespace Sword.Bake.Tests.PluginA;

/// <summary>Параметры единицы «скелет»: их читает обработчик плагина B через <c>ctx.Ref&lt;SkeletonParams&gt;</c>.</summary>
public sealed class SkeletonParams
{
    [Source(SourceKind.File)]
    public string Joints { get; set; } = "";
}

/// <summary>Скелет: текст исходника → <c>&lt;id&gt;.skel</c>.</summary>
public sealed class SkeletonProcessor : AssetProcessor<SkeletonParams>
{
    public const string ProcessorId = "testa.skeleton";

    public override string Id => ProcessorId;
    public override string Title => "Скелет (тестовый плагин A)";

    protected override IReadOnlyList<DeclaredOutput> Outputs(AssetManifest asset, SkeletonParams p) => [DeclaredOutput.File(asset.Id + ".skel")];

    protected override void Process(BakeContext ctx, SkeletonParams p) =>
        File.WriteAllText(ctx.Output(ctx.Asset.Id + ".skel"), OnSkeletonProcessor<SkeletonParams>.Describe(File.ReadAllText(ctx.InputFile(p.Joints))));
}

/// <summary>
/// Основа обработчиков «на скелете» — её наследуют обработчики других плагинов (как обработчики Bake.Game — код Bake.Engine):
/// тип плагина B выводится из типа плагина A, поэтому A должен быть загружен до B.
/// </summary>
public abstract class OnSkeletonProcessor<TParams> : AssetProcessor<TParams> where TParams : class, new()
{
    public static string Describe(string joints) => "skeleton:" + joints.Trim();

    /// <summary>Скелет единицы <paramref name="skeletonId"/>: параметры — по ссылке, исходник — вход этого ассета.</summary>
    protected static string Skeleton(BakeContext ctx, string skeletonId) =>
        Describe(File.ReadAllText(ctx.InputFile(ctx.Ref<SkeletonParams>(skeletonId).Joints)));
}
