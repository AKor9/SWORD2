using Sword.Bake.Engine.Anim;
using Sword.Bake.Engine.Processors;

namespace Sword.Bake.Engine.Tests;

/// <summary>Маршрутизация клипов экспорта по наборам библиотеки (anim.pack, поле sets) и ID скелета единицы anim.skeleton.</summary>
public sealed class RouteTests
{
    private static RawManifest Manifest(params (string Name, string Path)[] clips) =>
        new() { Clips = clips.Select(c => new RawClipInfo { Name = c.Name, Path = c.Path }).ToList() };

    private static readonly RawManifest Ext = Manifest(
        ("Anim_DK_A", "/Game/SWORD/AnimationsCombatExt/DynamicKatanaAnims/Anim_DK_A"),
        ("Anim_DK2_A", "/Game/SWORD/AnimationsCombatExt/DynamicKatanaAnimsV2/Anim_DK2_A"),
        ("Anim_Katana_A", "/Game/SWORD/AnimationsCombatExt/KatanaAnims/Anim_Katana_A"),
        ("Anim_SAS_A", "/Game/SWORD/AnimationsCombatExt/DynamicSwordAndShieldV2/Anim_SAS_A"),
        ("Anim_Tb_A", "/Game/SWORD/AnimationsCombatExt/TwinSwordAnims/Anim_Tb_A"));

    [Fact]
    public void ClipsGoToSetsByUeFolderMasks()
    {
        var setOf = AnimImport.Route(Ext, new Dictionary<string, List<string>>
        {
            ["katana"] = ["DynamicKatanaAnims*", "KatanaAnims"],
            ["sword_shield"] = ["DynamicSwordAndShield*"],
            ["twin_swords"] = ["twinswordanims"],   // без учёта регистра
        });
        Assert.Equal("katana", setOf["Anim_DK_A"]);
        Assert.Equal("katana", setOf["Anim_DK2_A"]);
        Assert.Equal("katana", setOf["Anim_Katana_A"]);
        Assert.Equal("sword_shield", setOf["Anim_SAS_A"]);
        Assert.Equal("twin_swords", setOf["Anim_Tb_A"]);
    }

    [Fact]
    public void EmptyMaskListTakesAllClips()
    {
        var setOf = AnimImport.Route(Ext, new Dictionary<string, List<string>> { ["everything"] = [] });
        Assert.All(setOf.Values, s => Assert.Equal("everything", s));
        Assert.Equal(5, setOf.Count);
    }

    [Fact]
    public void ClipOutsideAllSetsOrInSeveralIsAnError()
    {
        var lost = Assert.Throws<ArgumentException>(() => AnimImport.Route(Ext, new Dictionary<string, List<string>> { ["katana"] = ["*Katana*"] }));
        Assert.Contains("не попал ни в один набор", lost.Message);
        var twice = Assert.Throws<ArgumentException>(() => AnimImport.Route(Ext, new Dictionary<string, List<string>>
        {
            ["all"] = [],
            ["katana"] = ["KatanaAnims"],
        }));
        Assert.Contains("в несколько наборов", twice.Message);
        Assert.Throws<ArgumentException>(() => AnimImport.Route(Ext, new Dictionary<string, List<string>>()));
        Assert.Throws<ArgumentException>(() => AnimImport.Route(Ext, new Dictionary<string, List<string>> { ["Bad Name"] = [] }));
    }

    [Fact]
    public void SkeletonIdIsLastSegmentOfUnitInSkeletonsFolder()
    {
        Assert.Equal("ue5_manny", SkeletonProcessor.SkeletonId("skeletons/ue5_manny"));
        Assert.Throws<ArgumentException>(() => SkeletonProcessor.SkeletonId("characters/manny/rig"));
        Assert.Throws<ArgumentException>(() => SkeletonProcessor.SkeletonId("skeletons/a/b"));
    }
}
