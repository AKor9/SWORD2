using Sword.Bake.Tests.PluginA;

namespace Sword.Bake.Tests.PluginB;

public sealed class WeaponParams
{
    [AssetRef(SkeletonProcessor.ProcessorId)]
    public string Skeleton { get; set; } = "";
    public string Grip { get; set; } = "hand_r";
}

/// <summary>Оружие на скелете: наследует основу плагина A и читает параметры его единицы → <c>&lt;id&gt;.weapon</c>.</summary>
public sealed class WeaponProcessor : OnSkeletonProcessor<WeaponParams>
{
    public override string Id => "testb.weapon";
    public override string Title => "Оружие на скелете (тестовый плагин B)";

    /// <summary>Тип параметров скелета, каким его видит плагин B, — тест сверяет его с типом из загруженного A.</summary>
    public static Type SkeletonParamsType => typeof(SkeletonParams);

    protected override IReadOnlyList<DeclaredOutput> Outputs(AssetManifest asset, WeaponParams p) => [DeclaredOutput.File(asset.Id + ".weapon")];

    protected override void Process(BakeContext ctx, WeaponParams p) =>
        File.WriteAllText(ctx.Output(ctx.Asset.Id + ".weapon"), Skeleton(ctx, p.Skeleton) + "|grip:" + p.Grip);
}
