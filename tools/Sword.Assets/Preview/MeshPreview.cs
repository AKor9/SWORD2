using System.Globalization;
using System.Numerics;
using Sword.Engine.Content;
using Sword.Engine.Formats;

namespace Sword.Assets.Preview;

/// <summary>
/// Предпросмотр скинированного меша в bind-позе (меш персонажа, анатомия): треугольники по секциям с легендой,
/// сплошной вид и — у анатомии — «под кожей»: меш поверх полупрозрачной кожи персонажа на том же скелете.
/// Перенос <c>MeshPreview</c> SWORD 2eab13b.
/// </summary>
public static class MeshPreview
{
    private static readonly string[] Modes = ["Секции", "Сплошной"];
    private static readonly string[] ModesWithSkin = ["Секции", "Сплошной", "Под кожей"];
    private static readonly uint Plain = PreviewScene.Rgb(200, 190, 175);
    private static readonly uint SkinColour = PreviewScene.Rgb(190, 178, 160, 14);

    /// <param name="withSkin">Предлагать режим «под кожей» (анатомия).</param>
    public static PreviewScene? Build(IContentSource content, IReadOnlyList<string> paths, int mode, bool withSkin)
    {
        string? path = paths.FirstOrDefault(p => p.EndsWith(".swmesh", StringComparison.OrdinalIgnoreCase));
        if (path is null || !content.Exists(path)) return null;
        var mesh = Load(content, path);
        var inv = CultureInfo.InvariantCulture;
        var modes = withSkin ? ModesWithSkin : Modes;
        mode = Math.Clamp(mode, 0, modes.Length - 1);
        var scene = new PreviewScene { Modes = modes, Upright = true };

        // секции: своя секция на каждый диапазон индексов; нет секций — одна на весь меш
        var sections = mesh.Sections.Length > 0 ? mesh.Sections : [new MeshSection(0, mesh.Indices.Length, "")];
        var v = mesh.Vertices;
        for (int s = 0; s < sections.Length; s++)
        {
            var sec = sections[s];
            uint colour = mode == 1 ? Plain : SectionColour(s);
            int end = Math.Min(sec.Start + sec.Count, mesh.Indices.Length);
            for (int i = sec.Start; i + 2 < end; i += 3)
                scene.Triangles.Add(new PreviewTriangle(v[mesh.Indices[i]].Position, v[mesh.Indices[i + 1]].Position, v[mesh.Indices[i + 2]].Position, colour));
            if (mode != 1)
                scene.Legend.Add((colour, string.Create(inv, $"{(sec.Material.Length > 0 ? sec.Material : "без имени")} — {(end - sec.Start) / 3} треугольников")));
        }
        var (min, max) = Bounds(v);
        scene.Bounds = (min, max);

        if (mode == 2 && Skin(content, path, mesh) is { } skin)
        {
            // кожа — последней и полупрозрачной: анатомия видна сквозь неё, а рамка вида — по персонажу целиком
            var sv = skin.Mesh.Vertices;
            for (int i = 0; i + 2 < skin.Mesh.Indices.Length; i += 3)
                scene.Triangles.Add(new PreviewTriangle(sv[skin.Mesh.Indices[i]].Position, sv[skin.Mesh.Indices[i + 1]].Position, sv[skin.Mesh.Indices[i + 2]].Position, SkinColour));
            scene.Bounds = Bounds(sv);
            scene.Legend.Add((SkinColour | 0xFF000000u, $"кожа — {skin.Path}"));
        }
        else if (mode == 2) scene.Info.Add("меша персонажа на этом скелете в паке нет — кожу не показать");

        var size = max - min;
        scene.Info.Add(string.Create(inv,
            $"{path}: вершин {v.Length}, треугольников {mesh.Indices.Length / 3}, секций {mesh.Sections.Length}, суставов {mesh.JointCount}"));
        scene.Info.Add(string.Create(inv, $"габариты {size.X:F3} × {size.Y:F3} × {size.Z:F3} м (x влево, y вверх, z вперёд), поза покоя"));
        return scene;
    }

    internal static SkinnedMeshData Load(IContentSource content, string path)
    {
        using var s = content.OpenRead(path);
        return SkinnedMeshData.Read(s, content.Describe(path));
    }

    /// <summary>
    /// Меш персонажа на суставах <paramref name="joints"/>: скинированный меш пака (кроме <paramref name="self"/>), все суставы
    /// которого есть среди них, с наибольшим числом вершин — меш персонажа, а не анатомия на тех же суставах. Нет — null.
    /// </summary>
    internal static (SkinnedMeshData Mesh, string Path)? Character(IContentSource content, string? self, IReadOnlySet<string> joints)
    {
        (SkinnedMeshData Mesh, string Path)? best = null;
        foreach (var p in content.List("", ".swmesh"))
        {
            if (self is not null && p.Equals(self, StringComparison.OrdinalIgnoreCase)) continue;
            var m = Load(content, p);
            if (m.JointCount == 0 || !m.JointNames.All(joints.Contains)) continue;
            if (best is null || m.Vertices.Length > best.Value.Mesh.Vertices.Length) best = (m, p);
        }
        return best;
    }

    /// <summary>Кожа: скинированный меш пака на тех же суставах с наибольшим числом вершин — меш персонажа, не анатомия.</summary>
    private static (SkinnedMeshData Mesh, string Path)? Skin(IContentSource content, string self, SkinnedMeshData mesh) =>
        mesh.JointCount == 0 ? null : Character(content, self, mesh.JointNames.ToHashSet(StringComparer.Ordinal));

    internal static (Vector3 Min, Vector3 Max) Bounds(SkinVertex[] v)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var x in v)
        {
            min = Vector3.Min(min, x.Position);
            max = Vector3.Max(max, x.Position);
        }
        return v.Length == 0 ? (Vector3.Zero, Vector3.Zero) : (min, max);
    }

    /// <summary>Цвет секции: оттенки по золотому углу — соседние секции не сливаются.</summary>
    private static uint SectionColour(int i)
    {
        float h = (i * 0.618034f + 0.08f) % 1f, s = 0.55f, l = 0.6f;
        float c = (1 - MathF.Abs(2 * l - 1)) * s, x = c * (1 - MathF.Abs(h * 6 % 2 - 1)), m = l - c / 2;
        var (r, g, b) = (int)(h * 6) switch
        {
            0 => (c, x, 0f),
            1 => (x, c, 0f),
            2 => (0f, c, x),
            3 => (0f, x, c),
            4 => (x, 0f, c),
            _ => (c, 0f, x),
        };
        return PreviewScene.Rgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
