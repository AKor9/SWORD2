using System.Numerics;
using Sword.Engine.Formats;

namespace Sword.Engine.Animation;

/// <summary>Файл клипа для <see cref="ClipLibrary.Load"/>: путь в контенте, как открыть и как назвать в сообщениях.</summary>
public readonly record struct ClipSource(string Path, Func<Stream> Open, string Describe);

/// <summary>
/// Библиотека клипов одного скелета: все клипы одной таблицей, на кадр — локальные повороты и смещения всех суставов и
/// корневое движение. ID клипа — «источник/имя» (папка и файл в <c>anim/</c>). Только для чтения: её делят все позы и системы.
/// </summary>
public sealed class ClipLibrary
{
    /// <summary>Каталог клипов в контенте и расширения.</summary>
    public const string AnimDir = "animations", ClipExt = ".swclip", SkeletonDir = "skeletons", SkeletonExt = ".swskeleton";

    public static string SkeletonFile(string id) => $"{SkeletonDir}/{id}{SkeletonExt}";

    /// <summary>Клип: ID «источник/имя», первый кадр в таблице, число кадров, цикл.</summary>
    public readonly record struct Clip(string Name, int Start, int Count, bool Loop)
    {
        public int End => Start + Count;
        public string Source => Name.IndexOf('/') is var i and >= 0 ? Name[..i] : "";
        public string ShortName => Name[(Name.IndexOf('/') + 1)..];
    }

    private readonly Clip[] _clips;
    private readonly Quaternion[] _localRot;
    private readonly Vector3[] _localPos;
    private readonly Vector3[] _rootMotion;
    private readonly float[] _rootYaw;

    public Skeleton Skeleton { get; }
    public float SampleRate { get; }
    public ReadOnlySpan<Clip> Clips => _clips;
    public int ClipCount => _clips.Length;
    /// <summary>[кадр·суставы].</summary>
    public ReadOnlySpan<Quaternion> LocalRot => _localRot;
    public ReadOnlySpan<Vector3> LocalPos => _localPos;
    /// <summary>
    /// [кадр] смещение персонажа от первого кадра клипа (Y = 0): движение, запечённое в таз, вынесено сюда, поза клипа стоит
    /// на месте. У циклов — равномерный дрейф.
    /// </summary>
    public ReadOnlySpan<Vector3> RootMotion => _rootMotion;
    /// <summary>[кадр] поворот персонажа вокруг вертикали от первого кадра, рад (+ — от +Z к +X).</summary>
    public ReadOnlySpan<float> RootYaw => _rootYaw;

    public int JointCount => Skeleton.JointCount;
    public int FrameCount => _clips.Length == 0 ? 0 : _clips[^1].End;

    /// <summary>Библиотека из готовых таблиц; данные копируются — вызывающий не может менять их потом.</summary>
    public ClipLibrary(Skeleton skeleton, float sampleRate, ReadOnlySpan<Clip> clips, ReadOnlySpan<Quaternion> localRot,
        ReadOnlySpan<Vector3> localPos, ReadOnlySpan<Vector3> rootMotion, ReadOnlySpan<float> rootYaw)
        : this(skeleton, sampleRate, clips.ToArray(), localRot.ToArray(), localPos.ToArray(), rootMotion.ToArray(), rootYaw.ToArray())
    {
    }

    /// <summary>Массивы — уже свои (<see cref="Load"/>), не копируются.</summary>
    private ClipLibrary(Skeleton skeleton, float sampleRate, Clip[] clips, Quaternion[] localRot, Vector3[] localPos,
        Vector3[] rootMotion, float[] rootYaw)
    {
        int frames = clips.Length == 0 ? 0 : clips[^1].End;
        if (localRot.Length != frames * skeleton.JointCount || localPos.Length != localRot.Length
            || rootMotion.Length != frames || rootYaw.Length != frames)
            throw new ArgumentException($"кадров {frames}, суставов {skeleton.JointCount}: поз {localRot.Length}/{localPos.Length}, " +
                $"корня {rootMotion.Length}/{rootYaw.Length}");
        for (int i = 0, start = 0; i < clips.Length; start = clips[i].End, i++)
            if (clips[i].Start != start || clips[i].Count < 1) throw new ArgumentException($"клип {clips[i].Name}: кадры не подряд");
        Skeleton = skeleton;
        SampleRate = sampleRate;
        _clips = clips;
        _localRot = localRot;
        _localPos = localPos;
        _rootMotion = rootMotion;
        _rootYaw = rootYaw;
    }

    /// <summary>
    /// Все клипы скелета одной таблицей в порядке <paramref name="files"/>; ID — из пути <c>anim/&lt;источник&gt;/&lt;имя&gt;.swclip</c>.
    /// Клипы другого скелета молча не берутся; изменившегося скелета, другой частоты и повреждённые — в <paramref name="skipped"/>.
    /// Клипов нет — null.
    /// </summary>
    public static ClipLibrary? Load(Skeleton skeleton, IEnumerable<ClipSource> files, ICollection<string>? skipped = null)
    {
        var list = new List<Clip>();
        var loaded = new List<ClipFile>();
        float rate = 0;
        foreach (var file in files)
        {
            var parts = file.Path.Split('/');
            if (parts.Length != 3 || !parts[0].Equals(AnimDir, StringComparison.OrdinalIgnoreCase) || !parts[2].EndsWith(ClipExt, StringComparison.OrdinalIgnoreCase)) continue;
            string source = parts[1];
            string id = source + "/" + parts[2][..^ClipExt.Length];
            ClipFile c;
            try
            {
                using var s = file.Open();
                c = ClipFile.Read(s, file.Describe);
            }
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
                skipped?.Add($"{id}: пропущен — {e.Message}");
                continue;
            }
            if (c.Skeleton != skeleton.Id) continue;   // клип другого скелета — не для этого персонажа
            if (c.SkeletonHash != skeleton.Hash || c.Joints != skeleton.JointCount)
            {
                skipped?.Add($"{id}: скелет {c.Skeleton} изменился после импорта — переимпортируйте источник {source}");
                continue;
            }
            if (rate == 0) rate = c.SampleRate;
            else if (MathF.Abs(c.SampleRate - rate) > 1e-3f)
            {
                skipped?.Add($"{id}: частота {c.SampleRate} Гц ≠ {rate} Гц — пропущен");
                continue;
            }
            int start = list.Count == 0 ? 0 : list[^1].End;
            list.Add(new Clip(id, start, c.Frames, c.Loop));
            loaded.Add(c);
        }
        if (list.Count == 0) return null;

        int frames = list[^1].End, jc = skeleton.JointCount;
        var rot = new Quaternion[frames * jc];
        var pos = new Vector3[frames * jc];
        var motion = new Vector3[frames];
        var yaw = new float[frames];
        for (int i = 0; i < loaded.Count; i++)
        {
            int s = list[i].Start;
            loaded[i].LocalRot.CopyTo(rot, s * jc);
            loaded[i].LocalPos.CopyTo(pos, s * jc);
            loaded[i].RootMotion.CopyTo(motion, s);
            loaded[i].RootYaw.CopyTo(yaw, s);
        }
        return new ClipLibrary(skeleton, rate, list.ToArray(), rot, pos, motion, yaw);
    }

    /// <summary>Система персонажа в кадре клипа относительно первого кадра: поворот yaw, затем смещение (вектор слева: v·M).</summary>
    public static Matrix4x4 MotionMatrix(Vector3 displacement, float yaw)
    {
        var m = Matrix4x4.CreateRotationY(yaw);
        m.Translation = displacement;
        return m;
    }

    /// <summary>Клип по ID «источник/имя»; без источника — по имени, если оно одно. −1 — нет или неоднозначно.</summary>
    public int FindClip(string name)
    {
        for (int i = 0; i < _clips.Length; i++)
            if (_clips[i].Name == name) return i;
        if (name.Contains('/')) return -1;
        int found = -1;
        for (int i = 0; i < _clips.Length; i++)
            if (_clips[i].ShortName == name)
            {
                if (found >= 0) return -1;
                found = i;
            }
        return found;
    }

    public float Duration(int clip) => (_clips[clip].Count - 1) / SampleRate;

    /// <summary>Поворот корневого движения в момент <paramref name="time"/>, рад (с упором в концы клипа).</summary>
    public float RootYawAt(int clip, float time)
    {
        var c = _clips[clip];
        float f = Math.Clamp(time * SampleRate, 0, c.Count - 1);
        int i0 = Math.Min((int)f, c.Count - 1), i1 = Math.Min(i0 + 1, c.Count - 1);
        return float.Lerp(_rootYaw[c.Start + i0], _rootYaw[c.Start + i1], f - i0);
    }

    /// <summary>Поворот корневого движения за весь клип, рад.</summary>
    public float RootTurn(int clip) => _rootYaw[_clips[clip].End - 1] - _rootYaw[_clips[clip].Start];

    /// <summary>Скорость корневого движения клипа, м/с (для циклов — скорость хода, под неё подгоняется темп).</summary>
    public float RootSpeed(int clip)
    {
        var c = _clips[clip];
        float d = Duration(clip);
        return d > 1e-4f ? Vector3.Distance(_rootMotion[c.End - 1], _rootMotion[c.Start]) / d : 0;
    }

    /// <summary>Смещение корневого движения в момент <paramref name="time"/> (с упором в концы клипа).</summary>
    public Vector3 RootMotionAt(int clip, float time)
    {
        var c = _clips[clip];
        float f = Math.Clamp(time * SampleRate, 0, c.Count - 1);
        int i0 = Math.Min((int)f, c.Count - 1), i1 = Math.Min(i0 + 1, c.Count - 1);
        return Vector3.Lerp(_rootMotion[c.Start + i0], _rootMotion[c.Start + i1], f - i0);
    }

    /// <summary>Локальная поза клипа в момент <paramref name="time"/>, с (цикл — по кругу, иначе упор в конец): слерп поворотов и лерп смещений.</summary>
    public void Sample(int clip, float time, Span<Quaternion> rot, Span<Vector3> pos)
    {
        var c = _clips[clip];
        float f = time * SampleRate;
        float last = c.Count - 1;
        if (c.Loop && last > 0) f = ((f % last) + last) % last;
        else f = Math.Clamp(f, 0, last);
        int i0 = Math.Min((int)f, c.Count - 1);
        int i1 = Math.Min(i0 + 1, c.Count - 1);
        float a = f - i0;
        int jc = JointCount, o0 = (c.Start + i0) * jc, o1 = (c.Start + i1) * jc;
        var r0 = _localRot.AsSpan(o0, jc);
        var r1 = _localRot.AsSpan(o1, jc);
        var p0 = _localPos.AsSpan(o0, jc);
        var p1 = _localPos.AsSpan(o1, jc);
        rot = rot[..jc];
        pos = pos[..jc];
        for (int j = 0; j < rot.Length; j++)
        {
            rot[j] = Quaternion.Slerp(r0[j], r1[j], a);
            pos[j] = Vector3.Lerp(p0[j], p1[j], a);
        }
    }

    /// <summary>Смешать позу <paramref name="bRot"/> в <paramref name="aRot"/> с весом w; mask (если задан) — вес по суставу.</summary>
    public static void Blend(Span<Quaternion> aRot, Span<Vector3> aPos, ReadOnlySpan<Quaternion> bRot, ReadOnlySpan<Vector3> bPos,
        float w, ReadOnlySpan<float> mask = default)
    {
        for (int j = 0; j < aRot.Length; j++)
        {
            float k = mask.IsEmpty ? w : w * mask[j];
            if (k <= 0) continue;
            aRot[j] = Quaternion.Slerp(aRot[j], bRot[j], k);
            aPos[j] = Vector3.Lerp(aPos[j], bPos[j], k);
        }
    }
}
