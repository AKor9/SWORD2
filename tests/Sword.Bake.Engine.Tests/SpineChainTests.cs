using System.Numerics;
using Sword.Bake.Engine.Anim;

namespace Sword.Bake.Engine.Tests;

/// <summary>Позвоночник UE4 (три позвонка) на риге UE5 (пять): по доле цепочки, а не по имени.</summary>
public sealed class SpineChainTests
{
    // высоты позвонков, м: UE4 Mannequin пака HumanOrgans и UE5 Manny
    private static readonly string[] Ue4 = ["pelvis", "spine_01", "spine_02", "spine_03", "neck_01", "clavicle_l"];
    private static readonly Vector3[] Ue4Pos = [Y(0.968f), Y(1.076f), Y(1.268f), Y(1.400f), Y(1.564f), new(0.04f, 1.52f, 0)];
    private static readonly string[] Ue5 = ["pelvis", "spine_01", "spine_02", "spine_03", "spine_04", "spine_05", "spine_04_latissimus_l", "neck_01"];
    private static readonly Vector3[] Ue5Pos = [Y(0.959f), Y(0.996f), Y(1.062f), Y(1.134f), Y(1.219f), Y(1.411f), new(0.1f, 1.305f, 0), Y(1.528f)];

    private static Vector3 Y(float y) => new(0, y, 0);

    [Fact]
    public void Vertebrae_keep_their_place_along_the_spine()
    {
        var map = SpineChain.Map(Ue4, Ue4Pos, Ue5, Ue5Pos);
        Assert.Equal([1, 2, 3], map.Keys.Order());   // только позвонки: таз, шея и ключица — по имени
        // spine_03 UE4 — верх груди: садится в звено spine_04 → spine_05, а не на низкий spine_03 Manny
        Assert.Equal(Array.IndexOf(Ue5, "spine_04"), map[3].Joint);
        Assert.InRange(map[3].Point.Y, 1.34f, 1.40f);
        Assert.Equal(Array.IndexOf(Ue5, "spine_04"), map[2].Joint);
        Assert.InRange(map[2].Point.Y, 1.23f, 1.28f);
        // spine_01 UE4 — у spine_02 Manny (та же доля цепочки)
        Assert.Equal(Array.IndexOf(Ue5, "spine_02"), map[1].Joint);
        Assert.InRange(map[1].Point.Y, 1.05f, 1.08f);
        // точки — на цепочке рига и по порядку
        Assert.True(map[1].Point.Y < map[2].Point.Y && map[2].Point.Y < map[3].Point.Y);
    }

    [Fact]
    public void Same_skeleton_maps_onto_itself()
    {
        var map = SpineChain.Map(Ue5, Ue5Pos, Ue5, Ue5Pos);
        foreach (var (src, (joint, point)) in map)
        {
            Assert.Equal(src, joint);
            Assert.Equal(Ue5Pos[src].Y, point.Y, 1e-4f);
        }
    }

    [Fact]
    public void Without_a_chain_nothing_is_mapped() =>
        Assert.Empty(SpineChain.Map(["root", "spine_01"], [Y(0), Y(1)], Ue5, Ue5Pos));
}
