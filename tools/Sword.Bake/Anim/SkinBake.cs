using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Sword.Engine.Animation;
using Sword.Engine.Formats;

namespace Sword.Bake.Anim;

/// <summary>
/// Скелетный персонаж: glTF-меш и клипы полного скелета из UE → .swmesh и клипы библиотеки; крепление оружия по кулаку.
///
/// Одно отображение для всего — вершины, bind-поза, кадры клипов, статические меши оружия — <see cref="UeSpace"/>
/// экспорта. Bind-поза берётся из референсной позы скелета UE (skeleton.json), а не из glTF: тогда соглашение
/// о локальных осях костей у bind-позы и у кадров анимации одно и то же, и скиннинг v·Bind⁻¹·Component
/// не зависит от осей glTF.
/// </summary>
public static class SkinBake
{
    /// <summary>Индекс сустава вершины хранится байтом (<see cref="SkinVertex.Joints"/>).</summary>
    private const int MaxJoints = 256;
    /// <summary>Поворот root motion меньше — шум, не выносится (0,5°).</summary>
    private const float MinTurn = 0.5f * MathF.PI / 180;
    /// <summary>Подбор осей glTF: у сустава не меньше стольких «своих» вершин, суставов не меньше.</summary>
    private const int AxisMinVerts = 8, AxisMinJoints = 3;

    /// <summary>Угол в (−π, π].</summary>
    private static float Wrap(float a) => a - MathF.Tau * MathF.Round(a / MathF.Tau);

    /// <summary>
    /// Скинированный меш персонажа: суставы — кости экспорта <paramref name="animDir"/> (от него нужны только кости и
    /// пространство — manifest.json), bind-поза — поза покоя <paramref name="refPosePath"/>, оси glTF подбираются по ней.
    /// </summary>
    internal static void Mesh(string glbPath, string refPosePath, string animDir, string outMesh, TextWriter log)
    {
        var inv = CultureInfo.InvariantCulture;
        var man = RawAnimIO.ReadManifest(animDir);
        RawAnimIO.RequireFullSkeleton(man);
        int bc = man.Bones.Length;
        if (bc > MaxJoints) throw new InvalidDataException($"суставов {bc}: .swmesh хранит индекс сустава байтом, допустимо до {MaxJoints}");
        var refPose = UeRig.ReadRefPose(refPosePath);
        var space = new UeSpace(man);
        float cm = 100 * space.Unit;   // сантиметров в единице экспорта

        // ── bind-поза: референсная поза UE ──
        var inverseBind = new Matrix4x4[bc];
        for (int j = 0; j < bc; j++)
        {
            if (!refPose.TryGetValue(man.Bones[j], out var bind))
                throw new InvalidDataException($"в {Path.GetFileName(refPosePath)} нет кости {man.Bones[j]}");
            Matrix4x4.Invert(space.Transform(bind.T, bind.Q), out inverseBind[j]);
        }

        // ── меш: суставы по имени, оси glTF подбираются по совпадению с bind-позой ──
        var gltf = GltfMesh.Load(glbPath);
        var remap = new int[gltf.JointNames.Length];
        for (int k = 0; k < remap.Length; k++)
        {
            remap[k] = Array.IndexOf(man.Bones, gltf.JointNames[k]);
            if (remap[k] < 0) throw new InvalidDataException($"сустав меша {gltf.JointNames[k]} отсутствует в анимациях");
        }
        var axes = MatchAxes(gltf, refPose);
        log.WriteLine(string.Create(inv, $"оси glTF → UE: {axes.Name}, среднее отклонение вершин от своих костей {axes.Error * cm:F1} см по {axes.Joints} суставам"));
        if (axes.Error * cm > 15) log.WriteLine("  ВНИМАНИЕ: отклонение велико — проверьте масштаб экспорта (ожидаются сантиметры)");

        var mesh = new SkinnedMeshData
        {
            JointNames = man.Bones, JointParents = man.Parents, InverseBind = inverseBind,
            Vertices = new SkinVertex[gltf.Positions.Length],
            Indices = gltf.Indices.Select(i => (uint)i).ToArray(),
        };
        for (int v = 0; v < gltf.Positions.Length; v++)
        {
            uint joints = 0;
            for (int k = 0; k < 4; k++)
            {
                int gj = gltf.Joints[v * 4 + k];
                if ((uint)gj >= (uint)remap.Length) throw new InvalidDataException($"вершина {v}: сустав {gj} вне скина ({remap.Length} суставов)");
                joints |= (uint)remap[gj] << (8 * k);
            }
            mesh.Vertices[v] = new SkinVertex
            {
                Position = space.Point(axes.Map(gltf.Positions[v])),
                Normal = Vector3.Normalize(space.Dir(axes.Map(gltf.Normals[v]))),
                Uv = gltf.Uvs[v],
                Joints = joints,
                Weights = new Vector3(gltf.Weights[v * 4], gltf.Weights[v * 4 + 1], gltf.Weights[v * 4 + 2]),
            };
        }
        ContentFiles.Write(outMesh, mesh.Write);
        log.WriteLine($"меш → {Path.GetFullPath(outMesh)}: {mesh.Vertices.Length} вершин, {mesh.Indices.Length / 3} треугольников, {bc} суставов");
    }

    /// <summary>Знаковые перестановки осей — кандидаты перевода glTF экспортёра в UE.</summary>
    private static IEnumerable<(string Name, Func<Vector3, Vector3> Map)> AxisCandidates()
    {
        int[][] perms = [[0, 1, 2], [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0]];
        foreach (var p in perms)
            for (int s = 0; s < 8; s++)
            {
                var perm = p;
                float sx = (s & 1) != 0 ? -1 : 1, sy = (s & 2) != 0 ? -1 : 1, sz = (s & 4) != 0 ? -1 : 1;
                string name = $"ue=({(sx < 0 ? "-" : "")}{"xyz"[perm[0]]},{(sy < 0 ? "-" : "")}{"xyz"[perm[1]]},{(sz < 0 ? "-" : "")}{"xyz"[perm[2]]})";
                yield return (name, v =>
                {
                    Span<float> a = [v.X, v.Y, v.Z];
                    return new Vector3(sx * a[perm[0]], sy * a[perm[1]], sz * a[perm[2]]);
                });
            }
    }

    /// <summary>
    /// Какая знаковая перестановка осей переводит glTF в UE: центр вершин, привязанных к суставу (вес больше 0,6),
    /// сравнивается с позой покоя сустава. Берутся все суставы, которые есть и в меше, и в позе покоя, —
    /// имена костей конкретного скелета не нужны.
    /// </summary>
    private static (string Name, Func<Vector3, Vector3> Map, double Error, int Joints) MatchAxes(
        GltfMesh gltf, Dictionary<string, (Vector3 T, Quaternion Q)> refPose)
    {
        int jc = gltf.JointNames.Length;
        if (jc == 0) throw new InvalidDataException("в меше нет скина — оси glTF подбираются по суставам, нужен скинированный меш");
        var sum = new Vector3[jc];
        var count = new int[jc];
        for (int v = 0; v < gltf.Positions.Length; v++)
            if (gltf.Weights[v * 4] > 0.6f && gltf.Joints[v * 4] < jc)
            {
                int k = gltf.Joints[v * 4];
                sum[k] += gltf.Positions[v];
                count[k]++;
            }
        var probes = new List<(Vector3 Centre, Vector3 Target)>();
        for (int k = 0; k < jc; k++)
            if (count[k] >= AxisMinVerts && refPose.TryGetValue(gltf.JointNames[k], out var r))
                probes.Add((sum[k] / count[k], r.T));
        if (probes.Count < AxisMinJoints)
            throw new InvalidDataException($"оси glTF не подобрать: суставов меша с вершинами, которые есть в позе покоя, {probes.Count}, " +
                $"нужно не меньше {AxisMinJoints} — меш и skeleton.json должны быть от одного скелета");

        var candidates = AxisCandidates().ToArray();
        var best = candidates[0];
        double bestErr = double.MaxValue;
        foreach (var cand in candidates)
        {
            double err = 0;
            foreach (var (centre, target) in probes) err += Vector3.Distance(cand.Map(centre), target);
            err /= probes.Count;
            if (err < bestErr) { bestErr = err; best = cand; }
        }
        return (best.Name, best.Map, bestErr, probes.Count);
    }

    /// <summary>
    /// Средние положения фаланг 01…03 пальцев хвата (<see cref="UeRig.GripFingers"/>: [палец, фаланга]) правой кисти в системе
    /// hand_r (единицы экспорта) по клипам с мечом в правой руке; null — нет костей пальцев.
    /// </summary>
    private static (Vector3[,] Joints, int Frames)? GripFrame(ClipSource source)
    {
        var man = source.Manifest;
        int B(string name) => Array.IndexOf(man.Bones, name);
        int hand = B(UeRig.Hand("r"));
        var fingers = UeRig.GripFingers;
        var bones = new int[fingers.Length, 3];
        for (int f = 0; f < fingers.Length; f++)
            for (int k = 0; k < 3; k++)
                if ((bones[f, k] = B(UeRig.Phalanx(fingers[f], k + 1, "r"))) < 0) return null;
        if (hand < 0) return null;
        var sum = new Vector3[fingers.Length, 3];
        int n = 0;
        foreach (var info in man.Clips.Where(c => !UeRig.IsMirrored(c.Name)))
        {
            var raw = source.Load(info);
            for (int fr = 0; fr < info.Frames; fr += 3)
            {
                var inv = Quaternion.Conjugate(raw.R(fr, hand));
                var h = raw.P(fr, hand);
                for (int f = 0; f < fingers.Length; f++)
                    for (int k = 0; k < 3; k++)
                        sum[f, k] += Vector3.Transform(raw.P(fr, bones[f, k]) - h, inv);
                n++;
            }
        }
        if (n == 0) return null;
        for (int f = 0; f < fingers.Length; f++)
            for (int k = 0; k < 3; k++)
                sum[f, k] /= n;
        return (sum, n);
    }

    /// <summary>Центр окружности через три точки; точки на одной прямой — null.</summary>
    private static Vector3? Circumcentre(Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a;
        var ac = c - a;
        var n = Vector3.Cross(ab, ac);
        float nn = n.LengthSquared();
        if (nn < 1e-12f * ab.LengthSquared() * ac.LengthSquared()) return null;
        return a + (Vector3.Cross(n, ab) * ac.LengthSquared() + Vector3.Cross(ac, n) * ab.LengthSquared()) / (2 * nn);
    }

    /// <summary>
    /// Крепление меча к кисти по пальцам анимации (значение крепления <see cref="UeRig.GripAttach"/>), UE (единицы экспорта).
    /// Согнутый палец охватывает рукоять: его фаланги 01…03 лежат на окружности вокруг неё, поэтому ось рукояти — прямая
    /// через центры этих окружностей (от мизинца к указательному), середина хвата — их средний центр. Центр суставов кулака
    /// для этого не годится: у рукояти со стороны ладони суставов нет, и он сдвинут к кончикам пальцев — рукоять шла сквозь
    /// пальцы; линия костяшек — не ось: в кулаке рукоять идёт наискосок ладони. Система меча UE: клинок +Z, кромка по +Y,
    /// толщина по X — кромка направляется от запястья к костяшкам (по X клинок лёг бы в кулаке плашмя, повёрнутым на 90°).
    /// Сокет WeaponSocket пака для этого меша не годится: ставит клинок перпендикулярно ладони.
    /// </summary>
    /// <param name="gripAlong">Середина хвата, см от навершия по клинку.</param>
    public static (Vector3 T, Quaternion Q) GripAttachment(ClipSource source, float gripAlong, TextWriter log)
    {
        var (joints, n) = GripFrame(source)
            ?? throw new InvalidDataException($"для крепления {UeRig.GripAttach} нужны кости hand_r и фаланги index…pinky 01…03 правой кисти");
        float cm = 100 * source.Space.Unit;                       // сантиметров в единице экспорта
        int count = joints.GetLength(0);
        var centres = new Vector3[count];
        var centre = Vector3.Zero;
        float radius = 0;
        for (int f = 0; f < count; f++)
        {
            centres[f] = Circumcentre(joints[f, 0], joints[f, 1], joints[f, 2])
                ?? throw new InvalidDataException($"для крепления {UeRig.GripAttach}: палец {UeRig.GripFingers[f]} в клипах не согнут — рукоять не охватывает");
            centre += centres[f];
            radius += Vector3.Distance(centres[f], joints[f, 1]);
        }
        centre /= count;
        radius /= count;
        // ось — главная ось разброса центров, от мизинца (последний) к указательному (первый)
        var z = Vector3.Normalize(centres[0] - centres[^1]);
        for (int it = 0; it < 32; it++)
        {
            var s = Vector3.Zero;
            foreach (var c in centres) s += (c - centre) * Vector3.Dot(c - centre, z);
            z = Vector3.Normalize(s);
        }
        if (Vector3.Dot(z, centres[0] - centres[^1]) < 0) z = -z;
        int middle = Array.IndexOf(UeRig.GripFingers, "middle");
        var m = joints[middle, 0];
        var y = Vector3.Normalize(m - z * Vector3.Dot(m, z));     // кромка (+Y меча): к костяшкам
        var x = Vector3.Cross(y, z);                              // толщина клинка
        var q = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new Matrix4x4(
            x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1)));
        var knuckles = Vector3.Normalize(joints[0, 0] - joints[count - 1, 0]);
        log.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"крепление по пальцам ({n} кадров): середина хвата в {centre.Length() * cm:F1} см от кисти, охват пальцев {radius * cm:F1} см, " +
            $"ось к линии костяшек {MathF.Acos(Math.Clamp(Vector3.Dot(z, knuckles), -1, 1)) * 180 / MathF.PI:F1}°"));
        return (centre - z * (gripAlong / cm), q);
    }

    /// <summary>
    /// Клипы raw-экспорта полного скелета: компонентные позы UE → локальные персонажа, root motion — в дорожки.
    /// Результат — таблица <see cref="ClipLibrary"/> на скелете экспорта; имена клипов — имена экспорта (без набора).
    /// </summary>
    internal static ClipLibrary BuildClips(ClipSource source, TextWriter log)
    {
        var man = source.Manifest;
        var space = source.Space;
        var inv = CultureInfo.InvariantCulture;
        if (man.Clips.Count == 0) throw new InvalidDataException("в экспорте нет клипов");
        int bc = man.Bones.Length;
        // скелет экспорта: ID не нужен — клипы в пак пишет импорт со своим ID (AnimImport)
        var skeleton = new Skeleton("", man.Bones, man.Parents);

        // ── клипы: компонентные UE → локальные персонажа ──
        float sampleRate = man.Clips[0].Fps;
        var list = new List<ClipLibrary.Clip>();
        var rots = new List<Quaternion>();
        var poss = new List<Vector3>();
        var motion = new List<Vector3>();
        var yaws = new List<float>();
        var starts = new List<Matrix4x4>();   // система персонажа на первом кадре каждого клипа (в экспорте)
        var comp = new Matrix4x4[bc];
        var compInv = new Matrix4x4[bc];
        var isParent = new bool[bc];
        foreach (int p in man.Parents) if (p >= 0) isParent[p] = true;
        int rootJoint = Array.IndexOf(man.Bones, "root") is var rj and >= 0 ? rj : Array.IndexOf(man.Parents, -1);
        int turnClip = -1, rootDriven = 0;
        float maxTurn = 0;
        int pelvis = Array.IndexOf(man.Bones, "pelvis");
        if (pelvis < 0) throw new InvalidDataException("в скелете нет pelvis");
        double maxShift = 0;
        string maxShiftClip = "";
        foreach (var info in man.Clips)
        {
            if (MathF.Abs(info.Fps - sampleRate) > 1e-3f) throw new InvalidDataException($"{info.Name}: fps {info.Fps} ≠ {sampleRate}");
            var raw = source.Load(info);
            list.Add(new ClipLibrary.Clip(info.Name, rots.Count / bc, info.Frames, info.Loop));

            // Root motion. Пак мечника запёк движение в таз (кость root стоит), GASP — в кость root.
            // Источник — root, если она движется или поворачивается, иначе таз. Горизонтальное смещение
            // и поворот вокруг вертикали выносятся в дорожки, корневые суставы выражаются в системе,
            // которая едет и поворачивается с персонажем, — поза стоит на месте лицом вперёд.
            // Цикл: только равномерный дрейф смещения и поворота (раскачка остаётся, стык непрерывен).
            int n = info.Frames;
            var heading = new float[n];
            float maxYaw = 0, rootTravel = 0;
            if (rootJoint >= 0)
            {
                var r0 = space.Point(raw.P(0, rootJoint)) with { Y = 0 };
                for (int f = 0; f < n; f++)
                {
                    var fwd = Vector3.TransformNormal(Vector3.UnitZ, space.Transform(raw.P(f, rootJoint), raw.R(f, rootJoint)));
                    float prev = f > 0 ? heading[f - 1] : 0;
                    float h = new Vector2(fwd.X, fwd.Z).LengthSquared() > 1e-6f ? MathF.Atan2(fwd.X, fwd.Z) : prev;
                    heading[f] = f > 0 ? prev + Wrap(h - prev) : h;   // без скачков через ±π
                    maxYaw = MathF.Max(maxYaw, MathF.Abs(heading[f] - heading[0]));
                    rootTravel = MathF.Max(rootTravel, Vector3.Distance(space.Point(raw.P(f, rootJoint)) with { Y = 0 }, r0));
                }
            }
            bool turning = maxYaw > MinTurn;
            int src = rootJoint >= 0 && (turning || rootTravel > 0.01f) ? rootJoint : pelvis;
            if (src != pelvis) rootDriven++;
            var p0 = space.Point(raw.P(0, src)) with { Y = 0 };
            var pn = space.Point(raw.P(n - 1, src)) with { Y = 0 };
            float yawN = turning ? heading[n - 1] - heading[0] : 0;
            // Система персонажа на первом кадре — там, где стоит источник движения, лицом по курсу root. Поза и
            // смещения выражаются от неё: клип, начатый не в нуле (Stop/Pivot GASP), иначе стоял в метрах от бойца,
            // а его root motion шёл в осях экспорта, а не персонажа.
            float h0 = rootJoint >= 0 ? heading[0] : 0;
            var start = ClipLibrary.MotionMatrix(p0, h0);
            var toStart = Matrix4x4.CreateRotationY(-h0);
            starts.Add(start);
            // Равномерный дрейф — только у цикла с движением в тазу: раскачка таза остаётся в позе. Кость root
            // (GASP) — авторская траектория персонажа: берётся покадрово и у цикла, иначе круговой стрейф
            // (Circle_Strafe: root возвращается в начало) стоял бы на месте, а тело ходило по кругу в позе.
            bool uniform = info.Loop && src == pelvis;
            for (int f = 0; f < n; f++)
            {
                float k = n > 1 ? f / (float)(n - 1) : 0;
                var shift = uniform ? (pn - p0) * k : (space.Point(raw.P(f, src)) with { Y = 0 }) - p0;
                motion.Add(Vector3.TransformNormal(shift, toStart));
                yaws.Add(!turning ? 0 : uniform ? yawN * k : heading[f] - heading[0]);
            }
            if (Vector3.Distance(pn, p0) > maxShift) { maxShift = Vector3.Distance(pn, p0); maxShiftClip = info.Name; }
            if (!info.Loop && MathF.Abs(yawN) > maxTurn) { maxTurn = MathF.Abs(yawN); turnClip = list.Count - 1; }

            int baseFrame = motion.Count - n;
            for (int f = 0; f < n; f++)
            {
                for (int j = 0; j < bc; j++)
                {
                    comp[j] = space.Transform(raw.P(f, j), raw.R(f, j));
                    if (isParent[j] && !Matrix4x4.Invert(comp[j], out compInv[j]))
                        throw new InvalidDataException($"{info.Name} кадр {f}: вырожденный трансформ кости {man.Bones[j]}");
                }
                Matrix4x4.Invert(ClipLibrary.MotionMatrix(motion[baseFrame + f], yaws[baseFrame + f]) * start, out var motionInv);
                for (int j = 0; j < bc; j++)
                {
                    int p = man.Parents[j];
                    // корневые — в системе root motion кадра, остальные — относительно родителя
                    var local = comp[j] * (p >= 0 ? compInv[p] : motionInv);
                    Matrix4x4.Decompose(local, out _, out var r, out var t);
                    rots.Add(Quaternion.Normalize(r));
                    poss.Add(t);
                }
            }
        }
        var clips = new ClipLibrary(skeleton, sampleRate, CollectionsMarshal.AsSpan(list), CollectionsMarshal.AsSpan(rots),
            CollectionsMarshal.AsSpan(poss), CollectionsMarshal.AsSpan(motion), CollectionsMarshal.AsSpan(yaws));
        log.WriteLine(string.Create(inv,
            $"root motion: из корневой кости {(rootJoint >= 0 ? man.Bones[rootJoint] : "—")} {rootDriven} клипов, из таза {man.Clips.Count - rootDriven}; наибольший поворот {maxTurn * 180 / MathF.PI:F0}°{(turnClip >= 0 ? $" ({list[turnClip].Name})" : "")}"));
        log.WriteLine(string.Create(inv, $"root motion: наибольший сдвиг за клип {maxShift:F2} м ({maxShiftClip})"));
        if (source.Stats is { } stats)
        {
            int k = Math.Max(1, stats.Clips);
            log.WriteLine(string.Create(inv, $"пальцы перенесены с исходных клипов: {stats.Clips} из {man.Clips.Count}, " +
                $"отклонение фаланг от пака {stats.DevBefore / k:F1}° → {stats.DevAfter / k:F1}°, " +
                $"сгиб кисть→пясть→фаланга {stats.MetaBefore / k:F0}° → {stats.MetaAfter / k:F0}° (у Manny в покое 10–30°)"));
        }

        // проверка FK против экспорта; источник кэширует кадры, поэтому с переносом пальцев сверяются те же кадры, что запеклись
        Span<Quaternion> qr = new Quaternion[bc];
        Span<Vector3> qp = new Vector3[bc];
        Span<Matrix4x4> fk = new Matrix4x4[bc];
        double maxErr = 0;
        // кадр 0 первого клипа: смещение root motion нулевое — FK в системе первого кадра совпадает с экспортом
        var first = source.Load(man.Clips[0]);
        clips.Sample(0, 0, qr, qp);
        skeleton.ToModel(qr, qp, fk);
        for (int j = 0; j < bc; j++)
            maxErr = Math.Max(maxErr, Vector3.Distance(Vector3.Transform(fk[j].Translation, starts[0]), space.Point(first.P(0, j))));
        // клип с наибольшим поворотом: последний кадр, FK · root motion · система первого кадра против экспорта
        if (turnClip >= 0)
        {
            var tc = clips.Clips[turnClip];
            var traw = source.Load(man.Clips[turnClip]);
            int last = tc.Count - 1;
            clips.Sample(turnClip, last / clips.SampleRate, qr, qp);
            skeleton.ToModel(qr, qp, fk);
            var mm = ClipLibrary.MotionMatrix(clips.RootMotion[tc.End - 1], clips.RootYaw[tc.End - 1]) * starts[turnClip];
            for (int j = 0; j < bc; j++)
                maxErr = Math.Max(maxErr, Vector3.Distance(Vector3.Transform(fk[j].Translation, mm), space.Point(traw.P(last, j))));
        }
        log.WriteLine(string.Create(inv, $"проверка FK: макс. расхождение суставов {maxErr * 100:F3} см"));
        return clips;
    }
}
