using System.Globalization;
using System.Numerics;
using Sword.Bake.Anim;
using Sword.Engine.Formats;

namespace Sword.Bake.Anim;

/// <summary>
/// Статический меш клинкового оружия (glTF из UE, tools/ue/export_weapons.py) → .swmesh в системе кисти персонажа.
///
/// Оси у мешей разных паков разные, поэтому меш сперва приводится к системе меча SWORD (как у OneHandSword_Mesh
/// пака 1HM): клинок по +Z от навершия (z = 0), кромка по +Y, толщина по X. Ось клинка — главная ось разброса
/// вершин; гарда — самое широкое сечение, рукоять — с её стороны; кромка — главная ось сечения клинка, изогнутый
/// клинок (катана) отклоняется к обуху — кромка смотрит в другую сторону. Середина хвата — посередине рукояти.
/// Дальше крепление по кулаку анимаций (<see cref="SkinBake.GripAttachment"/>) и то же отображение UE → персонаж.
/// </summary>
internal static class WeaponBake
{
    /// <summary>Сечений по длине оружия.</summary>
    private const int Slices = 96;
    /// <summary>Гарда начинается, где радиус сечения превысил эту долю наибольшего.</summary>
    private const float GuardShare = 0.6f;
    /// <summary>Отклонение кончика меньше (см) — клинок прямой, сторона кромки не определяется.</summary>
    private const float MinCurve = 2f;

    /// <summary>Перевод осей glTF экспортёра UE в UE: glTF (x, y-вверх, z) = UE (x, z, y).</summary>
    private static Vector3 GltfToUe(Vector3 g) => new(g.X, g.Z, g.Y);

    /// <param name="gripAlong">Середина хвата, см от навершия; null — посередине рукояти.</param>
    /// <param name="unit">Сантиметров в единице glTF (экспорт UE с export_uniform_scale 1 — сантиметры).</param>
    /// <param name="sections">Секции по материалам glTF (<see cref="Sections"/>); нет — меш без секций.</param>
    /// <param name="outBlood">Зоны крови (<see cref="WeaponBloodBake"/>) рядом с мешем.</param>
    public static void Run(string glb, string animDir, string? refPosePath, string? fingerSrcDir, string outMesh,
        float? gripAlong, float unit, bool sections, WeaponBloodParams blood, string outBlood, TextWriter log)
    {
        var inv = CultureInfo.InvariantCulture;
        var gltf = GltfMesh.Load(glb);
        int n = gltf.Positions.Length;
        if (n == 0) throw new InvalidDataException($"{glb}: пустой меш");
        var p = new Vector3[n];
        for (int v = 0; v < n; v++) p[v] = GltfToUe(gltf.Positions[v]) * unit;

        var frame = Canonical(p, gripAlong, out float length, out float grip, out float curve);
        log.WriteLine(string.Create(inv, $"оружие {Path.GetFileName(glb)}: длина {length:F1} см, хват {grip:F1} см от навершия, изгиб {curve:F1} см"));

        var man = RawAnimIO.ReadManifest(animDir);
        var refPose = refPosePath is null ? null : UeRig.ReadRefPose(refPosePath);
        var source = new ClipSource(man, animDir, FingerSource.Open(fingerSrcDir, refPosePath, man, refPose));
        var space = source.Space;
        float cm = 100 * space.Unit;
        var (t, q) = SkinBake.GripAttachment(source, grip, log);

        var mesh = new SkinnedMeshData
        {
            Vertices = new SkinVertex[n],
            Indices = gltf.Indices.Select(i => (uint)i).ToArray(),
            Sections = sections ? Sections(gltf) : [],
        };
        for (int v = 0; v < n; v++)
        {
            var c = frame.Point(p[v]) / cm;                      // система меча, единицы экспорта
            var nc = frame.Dir(GltfToUe(gltf.Normals[v]));
            mesh.Vertices[v] = new SkinVertex
            {
                Position = space.Point(Vector3.Transform(c, q) + t),
                Normal = Vector3.Normalize(space.Dir(Vector3.Transform(nc, q))),
                Uv = gltf.Uvs.Length > v ? gltf.Uvs[v] : default,
                Weights = Vector3.UnitX,
            };
        }
        ContentFiles.Write(outMesh, mesh.Write);
        log.WriteLine($"оружие → {Path.GetFullPath(outMesh)}: {n} вершин, {mesh.Indices.Length / 3} треугольников");

        var canonical = new Vector3[n];
        for (int v = 0; v < n; v++) canonical[v] = frame.Point(p[v]);
        var zones = WeaponBloodBake.Build(canonical, gltf.VertexMaterial, gltf.MaterialNames, blood,
            c => space.Point(Vector3.Transform(c / cm, q) + t), log);
        ContentFiles.Write(outBlood, zones.Write);
    }

    /// <summary>
    /// Секции по материалам: подряд идущие треугольники одного материала (материал треугольника — его первой вершины; примитивы glTF
    /// читаются подряд, поэтому треугольники примитива — одна секция). Материал без имени — «материал N»; материалов нет — одна
    /// секция без имени.
    /// </summary>
    /// <exception cref="InvalidDataException">Материал встречается двумя несмежными кусками — секция не одна на материал.</exception>
    private static MeshSection[] Sections(GltfMesh gltf)
    {
        string Name(int material) =>
            material < 0 ? "" : material < gltf.MaterialNames.Length && gltf.MaterialNames[material].Length > 0 ? gltf.MaterialNames[material]
            : string.Create(CultureInfo.InvariantCulture, $"материал {material}");
        var result = new List<MeshSection>();
        int start = 0;
        for (int t = 3; t <= gltf.Indices.Length; t += 3)
        {
            int material = gltf.VertexMaterial[gltf.Indices[start]];
            if (t < gltf.Indices.Length && gltf.VertexMaterial[gltf.Indices[t]] == material) continue;
            string name = Name(material);
            if (result.Any(r => r.Material == name))
                throw new InvalidDataException($"секции: материал «{name}» двумя несмежными кусками треугольников");
            result.Add(new MeshSection(start, t - start, name));
            start = t;
        }
        return [.. result];
    }

    /// <summary>Система меча: начало на навершии, базис x, y (кромка), z (клинок); UE, см.</summary>
    private readonly record struct Frame(Vector3 Origin, Vector3 X, Vector3 Y, Vector3 Z)
    {
        public Vector3 Dir(Vector3 d) => new(Vector3.Dot(d, X), Vector3.Dot(d, Y), Vector3.Dot(d, Z));
        public Vector3 Point(Vector3 p) => Dir(p - Origin);
    }

    private static Frame Canonical(Vector3[] p, float? gripAlong, out float length, out float grip, out float curve)
    {
        var centre = Vector3.Zero;
        foreach (var v in p) centre += v;
        centre /= p.Length;
        var axis = MajorAxis(p, centre, null);

        // сечения по оси: центр и наибольший радиус
        float tMin = float.MaxValue, tMax = float.MinValue;
        foreach (var v in p)
        {
            float s = Vector3.Dot(v - centre, axis);
            tMin = MathF.Min(tMin, s);
            tMax = MathF.Max(tMax, s);
        }
        float len = length = tMax - tMin;
        if (len < 1e-3f) throw new InvalidDataException("меш вырожден: нет длины");
        var sum = new Vector3[Slices];
        var count = new int[Slices];
        int Slice(Vector3 v) => Math.Clamp((int)((Vector3.Dot(v - centre, axis) - tMin) / len * Slices), 0, Slices - 1);
        foreach (var v in p)
        {
            int k = Slice(v);
            sum[k] += v;
            count[k]++;
        }
        var radius = new float[Slices];
        foreach (var v in p)
        {
            int k = Slice(v);
            var d = v - sum[k] / count[k];
            d -= axis * Vector3.Dot(d, axis);
            radius[k] = MathF.Max(radius[k], d.Length());
        }
        var sliceOf = p.Select(Slice).ToArray();   // до разворота оси: Slice читает axis
        int guard = Array.IndexOf(radius, radius.Max());
        bool handleAtMin = guard < Slices / 2;
        if (!handleAtMin) axis = -axis;
        int Ordered(int i) => handleAtMin ? i : Slices - 1 - i;       // от навершия
        int guardFromPommel = Ordered(guard);

        // рукоять: сечения до начала гарды
        int handleEnd = 0;
        while (handleEnd < guardFromPommel && radius[Ordered(handleEnd)] < GuardShare * radius[guard]) handleEnd++;
        if (handleEnd == 0) handleEnd = Math.Max(1, guardFromPommel);
        var handleCentre = Vector3.Zero;
        int handleCount = 0;
        for (int i = 0; i < handleEnd; i++)
            if (count[Ordered(i)] > 0)
            {
                handleCentre += sum[Ordered(i)] / count[Ordered(i)];
                handleCount++;
            }
        handleCentre = handleCount > 0 ? handleCentre / handleCount : centre;
        // навершие: крайняя точка оси со стороны рукояти
        float pommel = handleAtMin ? tMin : -tMax;
        var origin = handleCentre + axis * (pommel - Vector3.Dot(handleCentre - centre, axis));
        grip = gripAlong ?? handleEnd * 0.5f * len / Slices;

        // кромка — главная ось сечения клинка (после гарды)
        var blade = p.Where((_, i) => Ordered(sliceOf[i]) > guardFromPommel + 1).ToArray();
        if (blade.Length < 3) blade = p;
        var bladeCentre = Vector3.Zero;
        foreach (var v in blade) bladeCentre += v;
        bladeCentre /= blade.Length;
        var y = MajorAxis(blade, bladeCentre, axis);

        // изгиб: центр последней четверти клинка относительно оси рукояти
        var tipCentre = Vector3.Zero;
        int tipCount = 0;
        for (int i = Slices * 3 / 4; i < Slices; i++)
            if (count[Ordered(i)] > 0)
            {
                tipCentre += sum[Ordered(i)] / count[Ordered(i)];
                tipCount++;
            }
        curve = tipCount > 0 ? Vector3.Dot(tipCentre / tipCount - handleCentre, y) : 0;
        if (curve > MinCurve) y = -y;                             // клинок гнётся к обуху
        curve = MathF.Abs(curve);
        var x = Vector3.Cross(y, axis);
        return new Frame(origin, x, y, axis);
    }

    /// <summary>Главная ось разброса точек степенным методом; <paramref name="normal"/> — в плоскости, перпендикулярной ему.</summary>
    private static Vector3 MajorAxis(Vector3[] p, Vector3 centre, Vector3? normal)
    {
        double xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
        foreach (var v in p)
        {
            var d = v - centre;
            if (normal is { } nn) d -= nn * Vector3.Dot(d, nn);
            xx += d.X * d.X; xy += d.X * d.Y; xz += d.X * d.Z;
            yy += d.Y * d.Y; yz += d.Y * d.Z; zz += d.Z * d.Z;
        }
        // начальный вектор — по наибольшей диагонали, чтобы не попасть в ортогональный собственный
        var a = xx >= yy && xx >= zz ? Vector3.UnitX : yy >= zz ? Vector3.UnitY : Vector3.UnitZ;
        if (normal is { } n0) a = Vector3.Normalize(a - n0 * Vector3.Dot(a, n0) + (Vector3.One - n0) * 1e-3f);
        for (int i = 0; i < 64; i++)
        {
            var b = new Vector3(
                (float)(xx * a.X + xy * a.Y + xz * a.Z),
                (float)(xy * a.X + yy * a.Y + yz * a.Z),
                (float)(xz * a.X + yz * a.Y + zz * a.Z));
            if (normal is { } n1) b -= n1 * Vector3.Dot(b, n1);
            if (b.LengthSquared() < 1e-12f) break;
            a = Vector3.Normalize(b);
        }
        return a;
    }
}
