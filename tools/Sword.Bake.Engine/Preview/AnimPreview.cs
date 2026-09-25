using System.Globalization;
using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Content;
using Sword.Engine.Formats;

namespace Sword.Bake.Engine.Preview;

/// <summary>
/// Предпросмотр источника анимаций (anim.pack) и скелета (anim.skeleton): клипы библиотеки <c>anim/&lt;набор&gt;/*.swclip</c>
/// на скелете <c>skeletons/*.swskeleton</c>. Поза — скиннингом на CPU по мешу источника; своего меша нет — берётся скелетный
/// меш из пака с теми же суставами; нет и его — только кости. Оружие источника (статический меш) — в правой кисти, как в игре.
/// Клип читается из пака при выборе, списку хватает заголовков (<see cref="ClipFile.ReadHeader"/>). Клипов нет (единица
/// скелета) — поза покоя меша и кости по его bind-матрицам. Перенос <c>AnimPreview</c> SWORD 2eab13b (у него позы покоя не было).
/// </summary>
internal sealed class AnimPreview : PreviewAnimation
{
    private static readonly string[] MeshModes = ["Меш", "Меш и кости", "Кости"];
    private const string HandJoint = "hand_r";
    private static readonly uint Skin = PreviewScene.Rgb(190, 178, 160);
    private static readonly uint Metal = PreviewScene.Rgb(205, 210, 220);
    private static readonly uint Bone = PreviewScene.Rgb(90, 230, 120);
    private static readonly uint RootLine = PreviewScene.Rgb(240, 190, 80);

    private readonly IContentSource _content;
    private readonly Skeleton _skeleton;
    private readonly List<string> _clipPaths = [];
    private readonly List<PreviewClip> _clips = [];
    private readonly List<ClipFile> _headers = [];
    private readonly SkinnedMeshData? _mesh, _weapon;
    /// <summary>[сустав меша] → сустав скелета; −1 — нет такого (вершины стоят в позе покоя).</summary>
    private readonly int[] _meshJoint = [];
    private readonly int _hand;
    /// <summary>[сустав] рисуется ли кость к нему.</summary>
    private readonly bool[] _bone;
    private readonly int _mode;

    private int _loaded = -1;
    private ClipLibrary? _clip;
    private string? _clipError;
    private readonly Quaternion[] _rot;
    private readonly Vector3[] _pos;
    private readonly Matrix4x4[] _component;
    private readonly Matrix4x4[] _palette = [];
    private readonly Vector3[] _skinned = [];

    public override IReadOnlyList<PreviewClip> Clips => _clips;

    private AnimPreview(IContentSource content, Skeleton skeleton, SkinnedMeshData? mesh, SkinnedMeshData? weapon, int mode)
    {
        _content = content;
        _skeleton = skeleton;
        _mesh = mesh;
        _weapon = weapon;
        _mode = mesh is null ? 2 : Math.Clamp(mode, 0, MeshModes.Length - 1);
        int jc = skeleton.JointCount;
        _rot = new Quaternion[jc];
        _pos = new Vector3[jc];
        _component = new Matrix4x4[jc];
        _hand = skeleton.Find(HandJoint);
        // кость — от родителя к суставу; не рисуются дети корня (корень на полу) и служебные ik_*/VB (их линии тянутся через всё тело)
        _bone = new bool[jc];
        var parents = skeleton.JointParents;
        var names = skeleton.JointNames;
        for (int j = 0; j < jc; j++)
        {
            int p = parents[j];
            string name = names[j];
            _bone[j] = p >= 0 && parents[p] >= 0
                       && !name.StartsWith("ik_", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("VB ", StringComparison.Ordinal);
        }
        if (mesh is not null)
        {
            _meshJoint = mesh.JointNames.Select(skeleton.Find).ToArray();
            _palette = new Matrix4x4[mesh.JointCount];
            _skinned = new Vector3[mesh.Vertices.Length];
        }
    }

    public static PreviewScene? Build(IContentSource content, IReadOnlyList<string> paths, int mode)
    {
        var clipPaths = paths.Where(p => p.StartsWith(ClipLibrary.AnimDir + "/", StringComparison.OrdinalIgnoreCase)
                                         && p.EndsWith(ClipLibrary.ClipExt, StringComparison.OrdinalIgnoreCase))
                             .Order(StringComparer.OrdinalIgnoreCase).ToList();
        var headers = new List<ClipFile>(clipPaths.Count);
        foreach (var p in clipPaths)
            using (var s = content.OpenRead(p)) headers.Add(ClipFile.ReadHeader(s, content.Describe(p)));

        // скелет общий для источников и в паке числится за одним из них — свой выход или скелет, названный в клипах
        string? skelPath = paths.FirstOrDefault(p => p.StartsWith(ClipLibrary.SkeletonDir + "/", StringComparison.OrdinalIgnoreCase)
                                                     && p.EndsWith(ClipLibrary.SkeletonExt, StringComparison.OrdinalIgnoreCase));
        if (skelPath is null && headers.Count > 0) skelPath = ClipLibrary.SkeletonFile(headers[0].Skeleton);
        if (skelPath is null || !content.Exists(skelPath)) return null;
        Skeleton skeleton;
        using (var s = content.OpenRead(skelPath)) skeleton = Skeleton.Read(s, content.Describe(skelPath));
        var names = skeleton.JointNames.ToArray().ToHashSet(StringComparer.Ordinal);

        // меш: свой скелетный, иначе любой скелетный из пака на этом скелете; оружие — свой статический
        SkinnedMeshData? mesh = null, weapon = null;
        string? meshPath = null, weaponPath = null;
        foreach (var p in paths.Where(p => p.EndsWith(".swmesh", StringComparison.OrdinalIgnoreCase)))
        {
            var m = MeshPreview.Load(content, p);
            if (m.JointCount == 0) { weapon ??= m; weaponPath ??= p; }
            else if (mesh is null && m.JointNames.All(names.Contains)) { mesh = m; meshPath = p; }
        }
        // своего меша нет — чужой на этом скелете с наибольшим числом вершин: это меш персонажа, а не анатомия
        // на тех же суставах (мозг, органы, кости), которая раньше шла первой по алфавиту
        bool borrowed = false;
        if (mesh is null && MeshPreview.Character(content, null, names) is { } character)
        {
            (mesh, meshPath) = character;
            borrowed = true;
        }

        var anim = new AnimPreview(content, skeleton, mesh, weapon, mode);
        for (int i = 0; i < clipPaths.Count; i++)
        {
            var h = headers[i];
            anim._clipPaths.Add(clipPaths[i]);
            anim._headers.Add(h);
            anim._clips.Add(new PreviewClip(Path.GetFileNameWithoutExtension(clipPaths[i]), (h.Frames - 1) / h.SampleRate, h.SampleRate, h.Loop));
        }

        var scene = new PreviewScene
        {
            Modes = mesh is null ? [] : MeshModes, Animation = anim, Upright = true, CullBackFaces = mesh is not null && anim._mode < 2,
        };
        scene.Info.Add($"скелет {skeleton.Id}: суставов {skeleton.JointCount}; клипов {anim._clips.Count}");
        scene.Info.Add(mesh is null
            ? "скелетного меша на этом скелете в паке нет — показаны кости"
            : $"меш {meshPath}{(borrowed ? " (из пака, не выход ассета)" : "")}: вершин {mesh.Vertices.Length}, треугольников {mesh.Indices.Length / 3}" +
              (weapon is null ? "" : $"; оружие {weaponPath} в {HandJoint}"));
        if (mesh is not null && anim._meshJoint.Contains(-1))
            scene.Info.Add($"суставов меша нет в скелете: {string.Join(", ", mesh.JointNames.Where((_, i) => anim._meshJoint[i] < 0))}");
        scene.Bounds = anim.FrameBounds();
        if (anim._mode >= 1) scene.Legend.Add((Bone, "кости"));
        if (anim._clips.Count > 0) scene.Legend.Add((RootLine, "смещение корня (root motion) от начала клипа"));
        else if (mesh is not null)
        {
            anim.BindPose(scene);
            scene.Info.Add("клипов у ассета нет — поза покоя меша, кости — по его bind-матрицам");
        }
        return scene;
    }

    /// <summary>
    /// Рамка вида: меш в позе покоя или суставы первого кадра первого клипа, с запасом на размах рук, — без следа root motion,
    /// который у бега уходит на метры. Нет ни меша, ни клипа — null.
    /// </summary>
    private (Vector3, Vector3)? FrameBounds()
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        if (_mesh is not null)
            foreach (var v in _mesh.Vertices)
            {
                min = Vector3.Min(min, v.Position);
                max = Vector3.Max(max, v.Position);
            }
        else if (_clips.Count > 0 && LoadClip(0))
        {
            _clip!.Sample(0, 0, _rot, _pos);
            _skeleton.ToModel(_rot, _pos, _component);
            for (int j = 0; j < _component.Length; j++)
                if (_bone[j])
                {
                    min = Vector3.Min(min, _component[j].Translation);
                    max = Vector3.Max(max, _component[j].Translation);
                }
        }
        if (min.X > max.X) return null;
        var pad = new Vector3(0.25f, 0.05f, 0.25f);
        return (min - pad, max + pad);
    }

    /// <summary>
    /// Поза покоя без клипа: меш как есть, суставы — обратные bind-матрицы меша (кость рисуется, если в меше есть и сустав,
    /// и его родитель).
    /// </summary>
    private void BindPose(PreviewScene scene)
    {
        var mesh = _mesh!;
        var at = new Vector3?[_skeleton.JointCount];
        for (int j = 0; j < mesh.JointCount; j++)
            if (_meshJoint[j] >= 0 && Matrix4x4.Invert(mesh.InverseBind[j], out var bind)) at[_meshJoint[j]] = bind.Translation;
        if (_mode < 2)
        {
            var v = mesh.Vertices;
            var idx = mesh.Indices;
            for (int i = 0; i + 2 < idx.Length; i += 3)
                scene.Triangles.Add(new PreviewTriangle(v[idx[i]].Position, v[idx[i + 1]].Position, v[idx[i + 2]].Position, Skin));
        }
        if (_mode >= 1)
        {
            var parents = _skeleton.JointParents;
            for (int j = 0; j < at.Length; j++)
                if (_bone[j] && at[j] is { } a && at[parents[j]] is { } p) scene.Lines.Add(new PreviewLine(p, a, Bone));
        }
    }

    private bool LoadClip(int clip)
    {
        if (_loaded == clip) return _clip is not null;
        _loaded = clip;
        _clip = null;
        _clipError = null;
        var h = _headers[clip];
        if (h.Skeleton != _skeleton.Id || h.SkeletonHash != _skeleton.Hash || h.Joints != _skeleton.JointCount)
        {
            _clipError = $"клип на скелете {h.Skeleton} (суставов {h.Joints}) не сходится со скелетом {_skeleton.Id} — переимпортируйте источник";
            return false;
        }
        try
        {
            ClipFile c;
            string path = _clipPaths[clip];
            using (var s = _content.OpenRead(path)) c = ClipFile.Read(s, _content.Describe(path));
            _clip = new ClipLibrary(_skeleton, c.SampleRate, [new ClipLibrary.Clip(_clips[clip].Name, 0, c.Frames, c.Loop)],
                c.LocalRot, c.LocalPos, c.RootMotion, c.RootYaw);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException)
        {
            _clipError = "клип не прочитался: " + e.Message;
        }
        return _clip is not null;
    }

    public override void Pose(int clip, float time, PreviewScene scene, List<string> status)
    {
        scene.Triangles.Clear();
        scene.Lines.Clear();
        if ((uint)clip >= (uint)_clips.Count) return;
        if (!LoadClip(clip))
        {
            status.Add(_clipError ?? "клип не загружен");
            return;
        }
        var data = _clip!;
        data.Sample(0, time, _rot, _pos);
        _skeleton.ToModel(_rot, _pos, _component);

        if (_mesh is not null && _mode < 2) PoseMesh(scene);
        if (_mode >= 1)
        {
            var parents = _skeleton.JointParents;
            for (int j = 0; j < _component.Length; j++)
                if (_bone[j])
                    scene.Lines.Add(new PreviewLine(_component[parents[j]].Translation, _component[j].Translation, Bone));
        }

        // root motion: поза клипа стоит на месте, смещение персонажа — отдельной дорожкой; след на полу
        var c = data.Clips[0];
        float duration = data.Duration(0);
        var prev = Vector3.Zero;
        const int steps = 24;
        for (int k = 1; k <= steps && duration > 0; k++)
        {
            var at = data.RootMotionAt(0, duration * k / steps);
            scene.Lines.Add(new PreviewLine(prev, at, RootLine));
            prev = at;
        }
        var now = data.RootMotionAt(0, time);
        float yaw = data.RootYawAt(0, time) * 180 / MathF.PI;
        int frame = Math.Clamp((int)MathF.Round(time * data.SampleRate), 0, c.Count - 1);
        status.Add(string.Create(CultureInfo.InvariantCulture,
            $"{_clipPaths[clip]}: кадр {frame + 1}/{c.Count}, {data.SampleRate:0.#} Гц, {(c.Loop ? "цикл" : "не цикл")}; " +
            $"смещение корня ({now.X:0.00}, {now.Z:0.00}) м, поворот {yaw:0.#}°, скорость {data.RootSpeed(0):0.00} м/с"));
    }

    /// <summary>Скиннинг меша на CPU по позе <see cref="_component"/>, оружие — в правой кисти.</summary>
    private void PoseMesh(PreviewScene scene)
    {
        var mesh = _mesh!;
        var inv = mesh.InverseBind;
        for (int j = 0; j < _palette.Length; j++)
            _palette[j] = _meshJoint[j] >= 0 ? inv[j] * _component[_meshJoint[j]] : Matrix4x4.Identity;
        var verts = mesh.Vertices;
        for (int v = 0; v < verts.Length; v++) _skinned[v] = SkinVertex(verts[v]);
        var idx = mesh.Indices;
        for (int i = 0; i + 2 < idx.Length; i += 3)
            scene.Triangles.Add(new PreviewTriangle(_skinned[idx[i]], _skinned[idx[i + 1]], _skinned[idx[i + 2]], Skin));
        if (_weapon is not null && _hand >= 0)
        {
            var hand = _component[_hand];
            var wv = _weapon.Vertices;
            var wi = _weapon.Indices;
            for (int i = 0; i + 2 < wi.Length; i += 3)
                scene.Triangles.Add(new PreviewTriangle(Vector3.Transform(wv[wi[i]].Position, hand),
                    Vector3.Transform(wv[wi[i + 1]].Position, hand), Vector3.Transform(wv[wi[i + 2]].Position, hand), Metal));
        }
    }

    private Vector3 SkinVertex(in Sword.Engine.Formats.SkinVertex vx)
    {
        float w3 = 1 - vx.Weights.X - vx.Weights.Y - vx.Weights.Z;
        return vx.Weights.X * Vector3.Transform(vx.Position, _palette[vx.Joints & 0xFF])
               + vx.Weights.Y * Vector3.Transform(vx.Position, _palette[vx.Joints >> 8 & 0xFF])
               + vx.Weights.Z * Vector3.Transform(vx.Position, _palette[vx.Joints >> 16 & 0xFF])
               + w3 * Vector3.Transform(vx.Position, _palette[vx.Joints >> 24 & 0xFF]);
    }
}
