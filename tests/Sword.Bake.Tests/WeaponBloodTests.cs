using System.Numerics;
using Sword.Bake.Anim;
using Sword.Game.Data;

namespace Sword.Bake.Tests;

/// <summary>Зоны крови оружия: разметка меча по сечениям, слоты материалов и формат .swblood.</summary>
public sealed class WeaponBloodTests
{
    /// <summary>
    /// Меч в системе меча (см): рукоять r 1.5 до 20, гарда r 7 до 24, лезвие ±2.5 × ±0.3 до 100.
    /// <paramref name="bend"/> — изгиб клинка к концу (катана), см.
    /// </summary>
    private static Vector3[] Sword(float bend = 0)
    {
        var p = new List<Vector3>();
        for (float z = 0; z <= 20; z += 1)
            for (int a = 0; a < 8; a++)
                p.Add(new Vector3(MathF.Cos(a * MathF.PI / 4) * 1.5f, MathF.Sin(a * MathF.PI / 4) * 1.5f, z));
        for (float z = 20.5f; z <= 24; z += 0.5f)
            for (int a = 0; a < 16; a++)
                p.Add(new Vector3(MathF.Cos(a * MathF.PI / 8) * 7, MathF.Sin(a * MathF.PI / 8) * 7, z));
        for (float z = 24.5f; z <= 100; z += 0.5f)
        {
            float off = bend * MathF.Pow((z - 24) / 76, 2);
            foreach (var (x, y) in new[] { (0.3f, 2.5f), (-0.3f, 2.5f), (0.3f, -2.5f), (-0.3f, -2.5f) })
                p.Add(new Vector3(x, y + off, z));
        }
        return p.ToArray();
    }

    private static WeaponBloodData Build(Vector3[] p, WeaponBloodParams? prm = null, int[]? materials = null, string[]? names = null) =>
        WeaponBloodBake.Build(p, materials ?? new int[p.Length], names ?? [""], prm ?? new WeaponBloodParams(), c => c * 0.01f, TextWriter.Null);

    [Theory]
    [InlineData(0f)]
    [InlineData(8f)]
    public void Blade_split_into_handle_guard_blade(float bend)
    {
        var p = Sword(bend);
        var data = Build(p);
        Assert.Equal(["handle", "blade"], data.Zones.Select(z => z.Name));
        Assert.Equal(BloodZoneKind.Cylinder, data.Zones[0].Kind);
        Assert.Equal(BloodZoneKind.Plane, data.Zones[1].Kind);
        // пространство меша здесь — метры: рукоять ~0.20, лезвие от ~0.24 до 1.00
        Assert.InRange(data.Zones[0].Length, 0.19f, 0.215f);
        Assert.InRange(data.Zones[1].Origin.Z, 0.235f, 0.26f);
        Assert.InRange(data.Zones[1].Origin.Z + data.Zones[1].Length, 0.99f, 1.001f);
        for (int v = 0; v < p.Length; v++)
        {
            // граница — с точностью до сечения (~1 см): вершины у самой границы не проверяются
            if (MathF.Abs(p[v].Z - 20) < 1.1f || MathF.Abs(p[v].Z - 24) < 1.1f) continue;
            byte expected = p[v].Z <= 20 ? (byte)0 : p[v].Z <= 24 ? WeaponBloodData.NoZone : (byte)1;
            Assert.Equal(expected, data.VertexZones[v]);
        }
    }

    [Fact]
    public void Material_slots_override_geometry()
    {
        var p = Sword();
        // рукоять — слот «Leather_Wrap», остальное — «Steel»
        var mats = p.Select(v => v.Z <= 20 ? 1 : 0).ToArray();
        var prm = new WeaponBloodParams { Materials = new() { ["*leather*"] = "none", ["Steel"] = "blade" } };
        var data = Build(p, prm, mats, ["Steel", "Leather_Wrap"]);
        for (int v = 0; v < p.Length; v++)
            Assert.Equal(p[v].Z <= 20 ? WeaponBloodData.NoZone : (byte)1, data.VertexZones[v]);
    }

    [Fact]
    public void Format_round_trip()
    {
        var data = Build(Sword());
        using var ms = new MemoryStream();
        data.Write(ms);
        ms.Position = 0;
        var back = WeaponBloodData.Read(ms, "тест");
        Assert.Equal(data.Zones, back.Zones);
        Assert.Equal(data.VertexZones, back.VertexZones);
        Assert.Equal(data.Base, back.Base);
        Assert.Equal(data.Tip, back.Tip);
    }
}
