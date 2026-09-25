using System.Globalization;
using System.Numerics;
using Sword.Engine.Content;
using Sword.Engine.Formats;
using Sword.Game.Data;

namespace Sword.Bake.Game.Preview;

/// <summary>
/// Предпросмотр оружия (weapon.static): меш, окрашенный по зонам крови (<c>.swblood</c>), контуры зон и главная ось;
/// «клетки» — сетка каждой зоны; «меш» — без зон. Перенос предпросмотра <c>WeaponStaticProcessor</c> SWORD 2eab13b.
/// </summary>
internal static class WeaponPreview
{
    private static readonly string[] Modes = ["Зоны", "Зоны и клетки", "Меш"];
    private static readonly uint NoBlood = PreviewScene.Rgb(120, 120, 128);
    private static readonly uint[] ZoneColours =
    [
        PreviewScene.Rgb(70, 140, 235), PreviewScene.Rgb(235, 120, 50), PreviewScene.Rgb(90, 200, 110),
        PreviewScene.Rgb(210, 90, 200), PreviewScene.Rgb(230, 210, 70), PreviewScene.Rgb(80, 210, 210),
    ];

    public static PreviewScene? Build(IContentSource outputs, IReadOnlyList<string> paths, int mode)
    {
        string? meshPath = paths.FirstOrDefault(p => p.EndsWith(".swmesh", StringComparison.OrdinalIgnoreCase));
        if (meshPath is null || !outputs.Exists(meshPath)) return null;
        SkinnedMeshData mesh;
        using (var s = outputs.OpenRead(meshPath)) mesh = SkinnedMeshData.Read(s, outputs.Describe(meshPath));
        string bloodPath = Path.ChangeExtension(meshPath, WeaponBloodData.Extension).Replace('\\', '/');
        WeaponBloodData? blood = null;
        if (outputs.Exists(bloodPath))
            using (var s = outputs.OpenRead(bloodPath)) blood = WeaponBloodData.Read(s, outputs.Describe(bloodPath));

        var scene = new PreviewScene { Modes = Modes };
        var v = mesh.Vertices;
        var zones = blood?.VertexZones is { } vz && vz.Length == v.Length ? vz : null;
        uint Colour(byte z) => z == WeaponBloodData.NoZone ? NoBlood : ZoneColours[z % ZoneColours.Length];
        for (int i = 0; i + 2 < mesh.Indices.Length; i += 3)
        {
            uint a = mesh.Indices[i], b = mesh.Indices[i + 1], c = mesh.Indices[i + 2];
            uint colour = PreviewScene.Rgb(170, 170, 178);
            if (zones is not null && mode != 2)
            {
                // зона треугольника — большинством вершин (на стыке зон треугольник смешанный)
                byte za = zones[a], zb = zones[b], zc = zones[c];
                colour = Colour(zb == zc ? zb : za);
            }
            scene.Triangles.Add(new PreviewTriangle(v[a].Position, v[b].Position, v[c].Position, colour));
        }

        if (blood is null)
        {
            scene.Info.Add($"нет {bloodPath} — зоны крови не запечены");
            return scene;
        }
        var counts = new int[blood.Zones.Length + 1];
        foreach (byte z in blood.VertexZones) counts[z == WeaponBloodData.NoZone ? blood.Zones.Length : z]++;
        for (int i = 0; i < blood.Zones.Length; i++)
        {
            var z = blood.Zones[i];
            uint colour = ZoneColours[i % ZoneColours.Length];
            scene.Legend.Add((colour, $"{z.Name} — {KindName(z.Kind)}, {counts[i]} вершин"));
            scene.Info.Add(string.Create(CultureInfo.InvariantCulture,
                $"{z.Name}: длина {z.Length * 100:F1} см, {(z.Kind == BloodZoneKind.Plane ? "полуширина" : "радиус")} {z.Half * 100:F1} см, клеток {z.CellsU}×{z.CellsV}"));
            ZoneOutline(scene, z, PreviewScene.Rgb(255, 255, 255, 220), mode == 1);
        }
        scene.Legend.Add((NoBlood, $"без крови — {counts[^1]} вершин"));
        scene.Legend.Add((PreviewScene.Rgb(90, 230, 90), "главная ось: основание → острие"));
        scene.Lines.Add(new PreviewLine(blood.Base, blood.Tip, PreviewScene.Rgb(90, 230, 90)));
        scene.Info.Add($"клеток на оружие {blood.CellCount} из {WeaponBloodData.MaxCells}");
        return scene;
    }

    private static string KindName(BloodZoneKind k) => k switch
    {
        BloodZoneKind.Plane => "плоскость",
        BloodZoneKind.Cylinder => "цилиндр",
        _ => "сфера",
    };

    /// <summary>Контур зоны; <paramref name="cells"/> — ещё и линии клеток.</summary>
    private static void ZoneOutline(PreviewScene scene, WeaponBloodZone z, uint colour, bool cells)
    {
        uint faint = (colour & 0x00FFFFFF) | (90u << 24);
        var w = Vector3.Normalize(Vector3.Cross(z.AxisU, z.AxisV));
        switch (z.Kind)
        {
            case BloodZoneKind.Plane:
                PlaneOutline(scene, z, colour, faint, cells);
                break;
            case BloodZoneKind.Cylinder:
                CylinderOutline(scene, z, w, colour, faint, cells);
                break;
            default:
            {
                const int Seg = 24;
                foreach (var (x, y) in new[] { (z.AxisU, z.AxisV), (z.AxisV, w), (w, z.AxisU) })
                    for (int k = 0; k < Seg; k++)
                    {
                        float a0 = k * MathF.Tau / Seg, a1 = (k + 1) * MathF.Tau / Seg;
                        scene.Lines.Add(new PreviewLine(z.Origin + (x * MathF.Cos(a0) + y * MathF.Sin(a0)) * z.Half,
                            z.Origin + (x * MathF.Cos(a1) + y * MathF.Sin(a1)) * z.Half, colour));
                    }
                break;
            }
        }
    }

    private static void PlaneOutline(PreviewScene scene, WeaponBloodZone z, uint colour, uint faint, bool cells)
    {
        Vector3 P(float u, float v) => z.Origin + z.AxisU * (u * z.Length) + z.AxisV * ((v * 2 - 1) * z.Half);
        int nu = cells ? z.CellsU : 1, nv = cells ? z.CellsV : 1;
        for (int k = 0; k <= nu; k++)
        {
            float u = k / (float)nu;
            scene.Lines.Add(new PreviewLine(P(u, 0), P(u, 1), k == 0 || k == nu ? colour : faint));
        }
        for (int k = 0; k <= nv; k++)
        {
            float vv = k / (float)nv;
            scene.Lines.Add(new PreviewLine(P(0, vv), P(1, vv), k == 0 || k == nv ? colour : faint));
        }
    }

    private static void CylinderOutline(PreviewScene scene, WeaponBloodZone z, Vector3 w, uint colour, uint faint, bool cells)
    {
        Vector3 P(float u, float a) => z.Origin + z.AxisU * (u * z.Length) + (z.AxisV * MathF.Cos(a) + w * MathF.Sin(a)) * z.Half;
        int around = Math.Max(z.CellsV, 8), rings = cells ? z.CellsU : 1;
        for (int r = 0; r <= rings; r++)
            for (int k = 0; k < around * 2; k++)
            {
                float a0 = k * MathF.PI / around, a1 = (k + 1) * MathF.PI / around;
                scene.Lines.Add(new PreviewLine(P(r / (float)rings, a0), P(r / (float)rings, a1), r == 0 || r == rings ? colour : faint));
            }
        int lines = cells ? z.CellsV : 4;
        for (int k = 0; k < lines; k++)
        {
            float a = k * MathF.Tau / lines;
            scene.Lines.Add(new PreviewLine(P(0, a), P(1, a), cells ? faint : colour));
        }
    }
}
