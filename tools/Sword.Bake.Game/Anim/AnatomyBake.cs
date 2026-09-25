using System.Globalization;
using System.Numerics;
using Sword.Bake.Engine.Anim;
using Sword.Engine.Formats;

namespace Sword.Bake.Game.Anim;

/// <summary>
/// Анатомия (скелет, органы) из пака на скелете UE4 Mannequin → .swmesh на риге персонажа SWORD.
///
/// Меш приходит скинированным на СВОЙ скелет (у паков анатомии это UE4 Mannequin), а игре нужен меш на
/// риге персонажа: та же палитра, те же плоскости разрезов. Ретаргет меша UE из Python не делает
/// (<c>duplicate_and_retarget</c> берёт только анимации), поэтому перенос делает пекарня: у каждой кости
/// источника берётся её bind-поза из glTF, у одноимённой кости рига — bind-поза из позы покоя UE, и вершины
/// переносятся обычным линейным скиннингом v' = Σ w·v·Bind⁻¹ᵢ·Bindᵣ. Кости источника и рига совпадают по
/// именам (UE4 и UE5 Mannequin — одна семья), разница поз покоя и пропорций и есть то, что снимает перенос.
/// Кроме позвоночника: позвонков у UE4 три, у UE5 пять, и он переносится по доле цепочки (<see cref="SpineChain"/>).
/// </summary>
internal static class AnatomyBake
{
    /// <summary>Перевод осей glTF экспортёра UE в UE: glTF (x, y-вверх, z) = UE (x, z, y).</summary>
    private static Vector3 GltfToUe(Vector3 g) => new(g.X, g.Z, g.Y);

    /// <summary>
    /// Посадка статики (мозг пака — отдельный меш в своём масштабе) на кость рига: габариты меша
    /// вписываются так, что самая длинная сторона — <see cref="Size"/> м, центр — в <see cref="Center"/>
    /// (система персонажа в позе покоя, м). Оси — как у пака (UE Mannequin), поворота нет.
    /// </summary>
    public sealed record Attach(string Bone, Vector3 Center, float Size);

    /// <param name="unit">Сантиметров в единице glTF (экспорт UE с export_uniform_scale 1 — сантиметры).</param>
    /// <param name="skinGlb">Меш персонажа: под его кожу прячется анатомия; null — не прятать.</param>
    /// <param name="margin">Насколько глубоко под кожей должна лежать анатомия, м.</param>
    /// <param name="materials">Имена материалов по номеру материала glTF (экспортёр UE имён не пишет); null — из glTF.</param>
    /// <param name="keep">Какие материалы оставить (по имени); null — все. Из одного меша пака выходят скелет и органы.</param>
    /// <param name="attach">Статика на кость; null — меш скинирован на скелет пака.</param>
    public static void Run(string glb, string animDir, string refPosePath, string? skinGlb, string outMesh,
        float unit, float margin, TextWriter log, string[]? materials = null, string[]? keep = null, Attach? attach = null,
        int maxTriangles = 0)
    {
        var inv = CultureInfo.InvariantCulture;
        var gltf = GltfMesh.Load(glb);
        int n = gltf.Positions.Length;
        if (n == 0) throw new InvalidDataException($"{glb}: пустой меш");
        if (gltf.JointNames.Length == 0 && attach is null)
            throw new InvalidDataException($"{glb}: меш без скина — анатомия сажается на кости рига (или задайте attach)");

        var man = RawAnimIO.ReadManifest(animDir);
        var refPose = UeRig.ReadRefPose(refPosePath);
        var space = new UeSpace(man);
        int bc = man.Bones.Length;

        // ── bind-поза рига персонажа (та же, что у меша Manny) ──
        var bindTgt = new Matrix4x4[bc];
        var invBindTgt = new Matrix4x4[bc];
        for (int j = 0; j < bc; j++)
        {
            if (!refPose.TryGetValue(man.Bones[j], out var b))
                throw new InvalidDataException($"в {Path.GetFileName(refPosePath)} нет кости {man.Bones[j]}");
            bindTgt[j] = space.Transform(b.T, b.Q);
            Matrix4x4.Invert(bindTgt[j], out invBindTgt[j]);
        }

        var mesh = new SkinnedMeshData
        {
            JointNames = man.Bones, JointParents = man.Parents, InverseBind = invBindTgt,
            Vertices = new SkinVertex[n],
            Indices = gltf.Indices.Select(i => (uint)i).ToArray(),
        };
        string source;
        if (attach is null) source = Transfer(gltf, space, unit, man, bindTgt, mesh.Vertices, log);
        else source = Place(gltf, space, unit, man, attach, mesh.Vertices);

        Sections(mesh, gltf, materials, keep, log);
        if (maxTriangles > 0 && mesh.Indices.Length / 3 > maxTriangles)
        {
            // UE не строит LOD статики из commandlet — прореживает пекарня (мозг пака — 123 тыс. треугольников)
            int before = mesh.Indices.Length / 3, openBefore = MeshEdges.Open(mesh);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int after = MeshDecimate.Run(mesh, maxTriangles);
            log.WriteLine(string.Create(inv,
                $"прорежено {before} → {after} треугольников за {clock.Elapsed.TotalSeconds:F1} с; незамкнутых рёбер {openBefore} → {MeshEdges.Open(mesh)}"));
        }
        if (skinGlb is not null) Hide(mesh.Vertices, skinGlb, space, unit, margin, log);

        var (min, max) = Bounds(mesh.Vertices);
        log.WriteLine(string.Create(inv,
            $"анатомия {Path.GetFileName(glb)}: {mesh.Vertices.Length} вершин, {mesh.Indices.Length / 3} треугольников, секций {mesh.Sections.Length}; {source}"));
        log.WriteLine(string.Create(inv, $"габариты в системе персонажа: {min.X:F2}…{max.X:F2} × {min.Y:F2}…{max.Y:F2} × {min.Z:F2}…{max.Z:F2} м"));
        foreach (var s in mesh.Sections)
        {
            var (smin, smax) = Bounds(mesh.Vertices, mesh.Indices.AsSpan(s.Start, s.Count));
            log.WriteLine(string.Create(inv,
                $"  {s.Material}: {s.Count / 3} тр., {smin.X:F3}…{smax.X:F3} × {smin.Y:F3}…{smax.Y:F3} × {smin.Z:F3}…{smax.Z:F3} м"));
        }
        ContentFiles.Write(outMesh, mesh.Write);
        log.WriteLine($"меш → {Path.GetFullPath(outMesh)}");
    }

    /// <summary>Скинированный меш пака: перенос из bind-позы его скелета в bind-позу рига.</summary>
    private static string Transfer(GltfMesh gltf, UeSpace space, float unit, RawManifest man, Matrix4x4[] bindTgt,
        SkinVertex[] verts, TextWriter log)
    {
        int n = gltf.Positions.Length;
        var srcInvs = new Matrix4x4[gltf.JointNames.Length];
        var srcPos = new Vector3[gltf.JointNames.Length];
        for (int k = 0; k < srcInvs.Length; k++)
        {
            srcInvs[k] = ToSword(gltf.InverseBind[k], space, unit, gltf.JointNames[k]);
            Matrix4x4.Invert(srcInvs[k], out var srcBind);
            srcPos[k] = srcBind.Translation;
        }
        var spine = SpineChain.Map(gltf.JointNames, srcPos, man.Bones, bindTgt.Select(b => b.Translation).ToArray());
        if (spine.Count > 0)
            log.WriteLine("позвоночник по доле цепочки " + SpineChain.Start + " → " + SpineChain.End + ": " +
                string.Join(", ", spine.Select(kv => string.Create(CultureInfo.InvariantCulture,
                    $"{gltf.JointNames[kv.Key]} → {man.Bones[kv.Value.Joint]} (сдвиг {Vector3.Distance(srcPos[kv.Key], kv.Value.Point) * 100:F1} см)"))));

        // ── кости источника: по имени в риг (позвоночник — по доле цепочки), подгонка = из своей bind-позы в bind-позу рига ──
        var fit = new Matrix4x4[gltf.JointNames.Length];
        var remap = new int[gltf.JointNames.Length];
        var nearest = new List<string>();
        for (int k = 0; k < remap.Length; k++)
        {
            var srcInv = srcInvs[k];
            if (spine.TryGetValue(k, out var link))
            {
                // кость ставится в точку той же доли цепочки рига, с поворотом кости рига, в чьё звено точка попала
                var target = bindTgt[link.Joint];
                target.Translation = link.Point;
                remap[k] = link.Joint;
                fit[k] = srcInv * target;
                continue;
            }
            remap[k] = Array.IndexOf(man.Bones, gltf.JointNames[k]);
            if (remap[k] < 0)
            {
                // кости нет в риге (служебная у источника) — ближайшая по bind-позе, чтобы не терять вершины
                Matrix4x4.Invert(srcInv, out var srcBind);
                remap[k] = Nearest(srcBind.Translation, bindTgt);
                nearest.Add($"{gltf.JointNames[k]}→{man.Bones[remap[k]]}");
            }
            fit[k] = srcInv * bindTgt[remap[k]];
        }
        if (nearest.Count > 0)
            log.WriteLine($"костей источника нет в риге: {nearest.Count} ({string.Join(", ", nearest.Take(8))}{(nearest.Count > 8 ? ", …" : "")}) — взяты ближайшие");

        // ── вершины: свой скиннинг из bind-позы источника в bind-позу рига ──
        float moved = 0;
        for (int v = 0; v < n; v++)
        {
            var src = space.Point(GltfToUe(gltf.Positions[v]) * unit);
            var srcNormal = space.Dir(GltfToUe(gltf.Normals[v]));
            Vector3 pos = default, normal = default;
            uint joints = 0;
            for (int k = 0; k < 4; k++)
            {
                int gj = gltf.Joints[v * 4 + k];
                if ((uint)gj >= (uint)remap.Length) throw new InvalidDataException($"вершина {v}: сустав {gj} вне скина ({remap.Length} суставов)");
                float w = gltf.Weights[v * 4 + k];
                joints |= (uint)remap[gj] << (8 * k);
                if (w <= 0) continue;
                pos += w * Vector3.Transform(src, fit[gj]);
                normal += w * Vector3.TransformNormal(srcNormal, fit[gj]);
            }
            moved += Vector3.Distance(pos, src);
            verts[v] = new SkinVertex
            {
                Position = pos,
                Normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitY,
                Uv = gltf.Uvs.Length > v ? gltf.Uvs[v] : default,
                Joints = joints,
                Weights = new Vector3(gltf.Weights[v * 4], gltf.Weights[v * 4 + 1], gltf.Weights[v * 4 + 2]),
            };
        }
        return string.Create(CultureInfo.InvariantCulture,
            $"суставов источника {remap.Length}, перенос в позу рига сдвинул вершины в среднем на {moved / n * 100:F1} см");
    }

    /// <summary>Статика на кость рига (<see cref="Attach"/>): вписывается габаритами, вес целиком на кости.</summary>
    private static string Place(GltfMesh gltf, UeSpace space, float unit, RawManifest man, Attach attach, SkinVertex[] verts)
    {
        int bone = Array.IndexOf(man.Bones, attach.Bone);
        if (bone < 0) throw new InvalidDataException($"attach: в риге нет кости {attach.Bone}");
        int n = gltf.Positions.Length;
        var pos = new Vector3[n];
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int v = 0; v < n; v++)
        {
            pos[v] = space.Point(GltfToUe(gltf.Positions[v]) * unit);
            min = Vector3.Min(min, pos[v]);
            max = Vector3.Max(max, pos[v]);
        }
        var ext = max - min;
        float scale = attach.Size / MathF.Max(ext.X, MathF.Max(ext.Y, ext.Z));
        var centre = (min + max) * 0.5f;
        for (int v = 0; v < n; v++)
        {
            var normal = space.Dir(GltfToUe(gltf.Normals[v]));
            verts[v] = new SkinVertex
            {
                Position = (pos[v] - centre) * scale + attach.Center,
                Normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitY,
                Uv = gltf.Uvs.Length > v ? gltf.Uvs[v] : default,
                Joints = (uint)bone * 0x01010101u,
                Weights = new Vector3(1, 0, 0),
            };
        }
        return string.Create(CultureInfo.InvariantCulture,
            $"статика на кости {attach.Bone}, масштаб {scale:F4} (исходные габариты {ext.X * 100:F1}×{ext.Y * 100:F1}×{ext.Z * 100:F1} см)");
    }

    /// <summary>
    /// Секции по материалам: треугольники сортируются по материалу, лишние материалы (<paramref name="keep"/>)
    /// выбрасываются, неиспользуемые вершины уходят. Материал треугольника — материал его первой вершины
    /// (у экспортёра UE примитив glTF — одна секция).
    /// </summary>
    private static void Sections(SkinnedMeshData mesh, GltfMesh gltf, string[]? materials, string[]? keep, TextWriter log)
    {
        string Name(int m)
        {
            if (m < 0) return "default";
            if (materials is not null)
                return m < materials.Length ? materials[m] : throw new InvalidDataException($"materials: нет имени для материала {m} (их {materials.Length})");
            return m < gltf.MaterialNames.Length && gltf.MaterialNames[m].Length > 0 ? gltf.MaterialNames[m] : $"mat{m}";
        }
        var idx = mesh.Indices;
        var byName = new SortedDictionary<int, List<int>>();   // материал → треугольники, по номеру материала
        int dropped = 0;
        for (int t = 0; t < idx.Length / 3; t++)
        {
            int m = gltf.VertexMaterial.Length > idx[t * 3] ? gltf.VertexMaterial[idx[t * 3]] : -1;
            if (keep is not null && !keep.Contains(Name(m)))
            {
                dropped++;
                continue;
            }
            if (!byName.TryGetValue(m, out var list)) byName[m] = list = [];
            list.Add(t);
        }
        if (keep is not null)
            foreach (var k in keep)
                if (!byName.Keys.Any(m => Name(m) == k)) throw new InvalidDataException($"keep: материала {k} в меше нет");

        // одинаковые имена (два материала glTF на одну секцию) сливаются в одну секцию
        var groups = byName.GroupBy(kv => Name(kv.Key)).ToList();
        var remap = new int[mesh.Vertices.Length];
        Array.Fill(remap, -1);
        var verts = new List<SkinVertex>(mesh.Vertices.Length);
        var indices = new List<uint>(idx.Length);
        var sections = new List<MeshSection>();
        foreach (var g in groups)
        {
            int start = indices.Count;
            foreach (int t in g.SelectMany(kv => kv.Value))
                for (int c = 0; c < 3; c++)
                {
                    uint v = idx[t * 3 + c];
                    if (remap[v] < 0)
                    {
                        remap[v] = verts.Count;
                        verts.Add(mesh.Vertices[v]);
                    }
                    indices.Add((uint)remap[v]);
                }
            sections.Add(new MeshSection(start, indices.Count - start, g.Key));
        }
        mesh.Vertices = [.. verts];
        mesh.Indices = [.. indices];
        mesh.Sections = [.. sections];
        if (dropped > 0) log.WriteLine($"отброшено треугольников чужих материалов: {dropped}");
    }

    /// <summary>
    /// Прячет анатомию под кожу персонажа: вершина, оказавшаяся снаружи или ближе <paramref name="margin"/>
    /// к коже, уходит по нормали кожи внутрь. Сторона определяется по ближайшей вершине кожи и её нормали —
    /// у плотного меша (Manny: шаг вершин около сантиметра) этого достаточно.
    ///
    /// Нужно потому, что анатомия нарисована на другом манекене: после переноса в позу рига лопатка, таз
    /// и колени вылезают наружу на сантиметр-другой.
    /// </summary>
    private static void Hide(SkinVertex[] verts, string skinGlb, UeSpace space, float unit, float margin, TextWriter log)
    {
        var skin = GltfMesh.Load(skinGlb);
        int sn = skin.Positions.Length;
        if (sn == 0) throw new InvalidDataException($"{skinGlb}: пустой меш кожи");
        var pos = new Vector3[sn];
        var nor = new Vector3[sn];
        for (int i = 0; i < sn; i++)
        {
            pos[i] = space.Point(GltfToUe(skin.Positions[i]) * unit);
            nor[i] = space.Dir(GltfToUe(skin.Normals[i]));
        }

        // сетка по вершинам кожи: ячейка крупнее шага вершин, чтобы ближайшая нашлась в соседних
        const float cell = 0.05f;
        var grid = new Dictionary<(int, int, int), List<int>>();
        (int, int, int) Cell(Vector3 p) => ((int)MathF.Floor(p.X / cell), (int)MathF.Floor(p.Y / cell), (int)MathF.Floor(p.Z / cell));
        for (int i = 0; i < sn; i++)
        {
            if (!grid.TryGetValue(Cell(pos[i]), out var list)) grid[Cell(pos[i])] = list = [];
            list.Add(i);
        }

        int moved = 0;
        float worst = 0;
        for (int v = 0; v < verts.Length; v++)
        {
            var p = verts[v].Position;
            var (cx, cy, cz) = Cell(p);
            int near = -1;
            float best = float.MaxValue;
            for (int dz = -1; dz <= 1; dz++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        if (grid.TryGetValue((cx + dx, cy + dy, cz + dz), out var list))
                            foreach (int i in list)
                            {
                                float d = Vector3.DistanceSquared(p, pos[i]);
                                if (d < best) { best = d; near = i; }
                            }
            if (near < 0) continue;   // далеко от кожи — значит глубоко внутри
            float depth = Vector3.Dot(p - pos[near], nor[near]);   // > 0 — снаружи кожи
            if (depth <= -margin) continue;
            verts[v].Position = p - nor[near] * (depth + margin);
            worst = MathF.Max(worst, depth);
            moved++;
        }
        log.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"под кожу убрано вершин: {moved} из {verts.Length}, наибольший выход наружу {worst * 100:F1} см (запас {margin * 100:F1} см)"));
    }

    /// <summary>Жёсткая матрица glTF (оси glTF, единицы экспорта) → система персонажа (метры).</summary>
    private static Matrix4x4 ToSword(Matrix4x4 m, UeSpace space, float unit, string bone)
    {
        if (!Matrix4x4.Decompose(m, out var scale, out var q, out var t))
            throw new InvalidDataException($"bind-поза кости {bone} не раскладывается");
        if (MathF.Abs(scale.X - 1) > 0.01f || MathF.Abs(scale.Y - 1) > 0.01f || MathF.Abs(scale.Z - 1) > 0.01f)
            throw new InvalidDataException($"bind-поза кости {bone} с масштабом {scale} — перенос рассчитан на жёсткие кости");
        // оси glTF → UE меняют рукость (y и z переставлены): поворот вокруг того же направления идёт в другую сторону
        var axis = GltfToUe(new Vector3(q.X, q.Y, q.Z));
        return space.Transform(GltfToUe(t) * unit, new Quaternion(-axis.X, -axis.Y, -axis.Z, q.W));
    }

    private static int Nearest(Vector3 p, Matrix4x4[] bind)
    {
        int best = 0;
        float dist = float.MaxValue;
        for (int j = 0; j < bind.Length; j++)
        {
            float d = Vector3.DistanceSquared(p, bind[j].Translation);
            if (d < dist) { dist = d; best = j; }
        }
        return best;
    }

    private static (Vector3 Min, Vector3 Max) Bounds(SkinVertex[] verts, ReadOnlySpan<uint> indices)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (uint i in indices)
        {
            min = Vector3.Min(min, verts[i].Position);
            max = Vector3.Max(max, verts[i].Position);
        }
        return (min, max);
    }

    private static (Vector3 Min, Vector3 Max) Bounds(SkinVertex[] verts)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var v in verts)
        {
            min = Vector3.Min(min, v.Position);
            max = Vector3.Max(max, v.Position);
        }
        return (min, max);
    }
}
