using System.Numerics;
using System.Runtime.CompilerServices;

namespace Sword.Engine.Animation.Motion;

/// <summary>
/// Кинематика кадров базы (точка 8б) — только на постройку: на каждый кадр выбранных клипов (локальные номера, клипы — по
/// возрастанию номера в библиотеке, как у <see cref="MotionDatabase"/>) система корня, скорость и угловая скорость корня,
/// положения и скорости суставов <see cref="MotionSchema.Joints"/> в системе корня кадра, контакты. Корень кадра —
/// <see cref="ClipLibrary.MotionMatrix"/> (<see cref="Pose2"/>); соседи у краёв цикла — через шов на соседний оборот (последний
/// кадр цикла = первый, оборот — <c>Relative(первый, последний)</c>), у нециклического — упор в край. Положения — прямой
/// кинематикой <see cref="Skeleton.ToModel"/>, скорости — центральной разностью мировых точек, выраженной в системе корня кадра.
/// Внутренняя кухня постройки: другие точки её не зовут, 8б может уточнять её сигнатуры.
///
/// Подробнее:
/// - корень кадра — <c>Pose2(RootMotion, RootYaw)</c> библиотеки (та же система, что <see cref="ClipLibrary.MotionMatrix"/>, без
///   обхода через atan2: курс непрерывный), приведённый к системе первого кадра клипа (у пака первый кадр — тождество, и приведение
///   его не меняет);
/// - поза клипа стоит на месте лицом вперёд, поэтому модельное пространство прямой кинематики — это система корня кадра;
/// - разность — в системе кадра: соседи переводятся в неё переносом <c>Relative(кадр, сосед)</c> (без мировых координат — точность
///   не падает вдали от начала клипа); у края нециклического клипа разность односторонняя (делится на своё время, а не на 2 кадра);
///   у цикла первый и последний кадр — один кадр: соседи — предпоследний кадр (прошлый оборот) и второй (следующий оборот), скорости
///   на шве совпадают;
/// - контакты — <see cref="ContactDetector"/> по высоте сустава (модельная Y) и горизонтальной скорости, по клипу;
/// - <see cref="OffsetDrift"/> — наибольший уход локального смещения каждого сустава скелета от первого кадра своего клипа (по нему
///   база выбирает суставы с движущимся смещением) — в том же проходе по кадрам, что прямая кинематика.
///
/// Клипы независимы: параллельный расчёт (<c>parallel</c>) пишет каждый клип в свои места теми же операциями — итог побитово тот
/// же, что у последовательного (тест <c>ClipKinematicsTests.Parallel_IsBitwiseSequential</c>). Тела циклов по кадрам — в своих
/// методах с <c>AggressiveOptimization</c>: постройка идёт раз на процесс, PGO ей не нужен, а на tier-0 она в разы медленнее.
/// </summary>
public sealed class ClipKinematics
{
    private readonly ClipLibrary _library;
    private readonly MotionClip[] _clips;
    private readonly int _jointCount;
    private readonly int[] _joints;
    private readonly int[] _contactSlots;
    private readonly ContactParams _contactParams;
    private readonly Pose2[] _root;
    private readonly Vector3[] _rootVelocity;
    private readonly float[] _yawRate;
    private readonly Vector3[] _positions;
    private readonly Vector3[] _velocities;
    private readonly byte[] _contacts;
    private readonly float[] _drift;
    private float[] _offsetDrift = [];

    private ClipKinematics(ClipLibrary library, MotionClip[] clips, MotionSchema schema, in ContactParams contacts)
    {
        _library = library;
        _clips = clips;
        _joints = schema.Joints.ToArray();
        _jointCount = _joints.Length;
        _contactSlots = new int[schema.ContactJoints.Length];
        for (int k = 0; k < _contactSlots.Length; k++) _contactSlots[k] = schema.JointSlot(schema.ContactJoints[k]);
        _contactParams = contacts;
        int frames = clips[^1].End;
        _root = new Pose2[frames];
        _rootVelocity = new Vector3[frames];
        _yawRate = new float[frames];
        _positions = new Vector3[frames * _jointCount];
        _velocities = new Vector3[frames * _jointCount];
        _contacts = new byte[frames];
        _drift = new float[clips.Length * library.JointCount];
    }

    /// <summary>Кинематика клипов <paramref name="clips"/> библиотеки (номера — по строгому возрастанию) для суставов и контактов схемы.</summary>
    /// <param name="parallel">Клипы — параллельно (итог побитово тот же, что последовательно).</param>
    /// <exception cref="ArgumentException">Номера клипов вне библиотеки или не по возрастанию; суставы схемы вне скелета; пороги контактов противоречивы.</exception>
    public static ClipKinematics Compute(ClipLibrary library, ReadOnlySpan<int> clips, MotionSchema schema, in ContactParams contacts,
        bool parallel = true)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(schema);
        ContactDetector.Validate(contacts);
        if (clips.IsEmpty) throw new ArgumentException("нет клипов", nameof(clips));
        foreach (int j in schema.Joints)
            if (j >= library.JointCount)
                throw new ArgumentException($"сустав {j} схемы вне скелета {library.Skeleton.Id} ({library.JointCount} суставов)", nameof(schema));
        var layout = new MotionClip[clips.Length];
        for (int c = 0, start = 0; c < clips.Length; start += layout[c].Count, c++)
        {
            if ((uint)clips[c] >= (uint)library.ClipCount || c > 0 && clips[c] <= clips[c - 1])
                throw new ArgumentException($"клип {clips[c]}: вне библиотеки ({library.ClipCount}) или не по строгому возрастанию", nameof(clips));
            var l = library.Clips[clips[c]];
            layout[c] = new MotionClip(clips[c], start, l.Count, l.Loop);
        }

        var kin = new ClipKinematics(library, layout, schema, contacts);
        int maxCount = 0;
        foreach (var clip in layout) maxCount = Math.Max(maxCount, clip.Count);
        if (parallel)
            Parallel.For(0, layout.Length, () => new Workspace(library.JointCount, maxCount), (c, _, w) =>
            {
                kin.ComputeClip(c, w);
                return w;
            }, _ => { });
        else
        {
            var w = new Workspace(library.JointCount, maxCount);
            for (int c = 0; c < layout.Length; c++) kin.ComputeClip(c, w);
        }
        kin._offsetDrift = kin.ReduceDrift();
        return kin;
    }

    /// <summary>Клипы — в раскладке базы: номер в библиотеке, первый кадр (локальный), число кадров, цикл.</summary>
    public ReadOnlySpan<MotionClip> Clips => _clips;

    /// <summary>Кадров (всех выбранных клипов подряд).</summary>
    public int FrameCount => _root.Length;

    /// <summary>Суставов измерено — <see cref="MotionSchema.Joints"/> (в их порядке).</summary>
    public int JointCount => _jointCount;

    /// <summary>[кадр] система корня кадра в системе первого кадра своего клипа.</summary>
    public ReadOnlySpan<Pose2> Root => _root;

    /// <summary>[кадр] скорость корня в системе корня кадра, м/с (Y = 0).</summary>
    public ReadOnlySpan<Vector3> RootVelocity => _rootVelocity;

    /// <summary>[кадр] угловая скорость курса корня, рад/с (+ — от +Z к +X).</summary>
    public ReadOnlySpan<float> YawRate => _yawRate;

    /// <summary>[кадр·сустав] положение сустава в системе корня кадра, м.</summary>
    public ReadOnlySpan<Vector3> Positions => _positions;

    /// <summary>[кадр·сустав] скорость сустава (мировая), выраженная в системе корня кадра, м/с.</summary>
    public ReadOnlySpan<Vector3> Velocities => _velocities;

    /// <summary>[кадр] контакты: бит i — сустав <see cref="MotionSchema.ContactJoints"/>[i] на земле.</summary>
    public ReadOnlySpan<byte> Contacts => _contacts;

    /// <summary>
    /// [сустав скелета] наибольшее расстояние, м, на которое локальное смещение сустава отходит от первого кадра своего клипа (по всем
    /// выбранным клипам) — по нему база находит суставы с движущимся смещением (<see cref="MotionDatabase.MovingJoints"/>).
    /// </summary>
    public ReadOnlySpan<float> OffsetDrift => _offsetDrift;

    // массивы — постройке базы: база забирает их себе (кинематика после постройки не живёт и наружу отдаёт только чтение)
    internal MotionClip[] ClipArray => _clips;
    internal Vector3[] RootVelocityArray => _rootVelocity;
    internal byte[] ContactArray => _contacts;

    /// <summary>
    /// Система корня кадра <paramref name="frame"/> клипа <paramref name="clip"/> (номер среди выбранных) в системе его первого
    /// кадра; кадр вне клипа — у цикла по оборотам (−1 — предпоследний кадр прошлого оборота), у нециклического — упор в край.
    /// </summary>
    public Pose2 RootAt(int clip, int frame)
    {
        var c = _clips[clip];
        int n = c.Count;
        if (!c.Loop || n < 2) return _root[c.Start + Math.Clamp(frame, 0, n - 1)];
        int period = n - 1;
        int turns = (int)Math.Floor((double)frame / period);
        int r = frame - turns * period;
        var pose = _root[c.Start + r];
        if (turns == 0) return pose;
        // оборот — последний кадр в системе первого; следующий оборот — Compose(оборот, кадр), прошлый — Compose(оборот⁻¹, кадр)
        var turn = Pose2.Relative(_root[c.Start], _root[c.Start + period]);
        var step = turns > 0 ? turn : turn.Inverse();
        for (int t = Math.Abs(turns); t > 0; t--) pose = Pose2.Compose(step, pose);
        return pose;
    }

    /// <summary>Буферы одного потока: модельные матрицы кадра, высоты и скорости сустава контакта по клипу.</summary>
    private sealed class Workspace(int skeletonJoints, int maxFrames)
    {
        public readonly Matrix4x4[] Model = new Matrix4x4[skeletonJoints];
        public readonly float[] Height = new float[maxFrames];
        public readonly float[] Speed = new float[maxFrames];
        public readonly bool[] Contact = new bool[maxFrames];
    }

    private void ComputeClip(int c, Workspace w)
    {
        var clip = _clips[c];
        var lib = _library;
        int libStart = lib.Clips[clip.LibraryClip].Start;
        var toFirst = new Pose2(lib.RootMotion[libStart], lib.RootYaw[libStart]).Inverse();
        for (int i = 0; i < clip.Count; i++)
            _root[clip.Start + i] = Pose2.Compose(toFirst, new Pose2(lib.RootMotion[libStart + i], lib.RootYaw[libStart + i]));
        var drift = _drift.AsSpan(c * lib.JointCount, lib.JointCount);
        for (int i = 0; i < clip.Count; i++) FrameModel(clip, libStart, i, w.Model, drift);
        for (int i = 0; i < clip.Count; i++) FrameVelocity(clip, i);
        for (int k = 0; k < _contactSlots.Length; k++) ClipContacts(clip, k, w);
    }

    /// <summary>Прямая кинематика кадра: положения суставов схемы в системе корня кадра; уход локальных смещений (квадрат).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void FrameModel(MotionClip clip, int libStart, int i, Matrix4x4[] model, Span<float> drift)
    {
        var lib = _library;
        int jc = lib.JointCount, at = (libStart + i) * jc, first = libStart * jc;
        var rot = lib.LocalRot.Slice(at, jc);
        var pos = lib.LocalPos.Slice(at, jc);
        lib.Skeleton.ToModel(rot, pos, model);
        var into = _positions.AsSpan((clip.Start + i) * _jointCount, _jointCount);
        for (int s = 0; s < into.Length; s++) into[s] = model[_joints[s]].Translation;
        var pos0 = lib.LocalPos.Slice(first, jc);
        for (int j = 0; j < jc; j++) drift[j] = MathF.Max(drift[j], Vector3.DistanceSquared(pos[j], pos0[j]));
    }

    /// <summary>Скорости корня и суставов кадра — разностью соседей, переведённых в систему кадра.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void FrameVelocity(MotionClip clip, int i)
    {
        int n = clip.Count, s0 = clip.Start;
        int prev, next;
        Pose2 rp, rn;
        if (clip.Loop && n >= 2)
        {
            if (i == 0 || i == n - 1)
            {
                // шов: первый и последний кадр — один; соседи — предпоследний кадр прошлого оборота и второй кадр следующего
                prev = n - 2;
                next = 1;
                rp = Pose2.Relative(_root[s0 + n - 1], _root[s0 + prev]);
                rn = Pose2.Relative(_root[s0], _root[s0 + next]);
            }
            else
            {
                prev = i - 1;
                next = i + 1;
                rp = Pose2.Relative(_root[s0 + i], _root[s0 + prev]);
                rn = Pose2.Relative(_root[s0 + i], _root[s0 + next]);
            }
        }
        else
        {
            prev = Math.Max(i - 1, 0);
            next = Math.Min(i + 1, n - 1);
            rp = Pose2.Relative(_root[s0 + i], _root[s0 + prev]);
            rn = Pose2.Relative(_root[s0 + i], _root[s0 + next]);
        }
        int f = s0 + i;
        int frames = clip.Loop && n >= 2 ? 2 : next - prev;
        if (frames == 0) return;   // клип из одного кадра — стоит
        float span = frames / _library.SampleRate;
        _rootVelocity[f] = (rn.Position - rp.Position) / span;
        _yawRate[f] = Pose2.Wrap(rn.Yaw - rp.Yaw) / span;
        var pp = _positions.AsSpan((s0 + prev) * _jointCount, _jointCount);
        var pn = _positions.AsSpan((s0 + next) * _jointCount, _jointCount);
        var into = _velocities.AsSpan(f * _jointCount, _jointCount);
        for (int s = 0; s < into.Length; s++) into[s] = (rn.ToParent(pn[s]) - rp.ToParent(pp[s])) / span;
    }

    /// <summary>Контакт сустава <paramref name="contact"/> по кадрам клипа — в бит кадра.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void ClipContacts(MotionClip clip, int contact, Workspace w)
    {
        int n = clip.Count, slot = _contactSlots[contact];
        var height = w.Height.AsSpan(0, n);
        var speed = w.Speed.AsSpan(0, n);
        var on = w.Contact.AsSpan(0, n);
        for (int i = 0; i < n; i++)
        {
            int at = (clip.Start + i) * _jointCount + slot;
            height[i] = _positions[at].Y;
            var v = _velocities[at];
            speed[i] = MathF.Sqrt(v.X * v.X + v.Z * v.Z);
        }
        ContactDetector.Detect(height, speed, _contactParams, on);
        byte bit = (byte)(1 << contact);
        for (int i = 0; i < n; i++)
            if (on[i]) _contacts[clip.Start + i] |= bit;
    }

    /// <summary>Уход смещений по всем клипам: наибольший квадрат по клипам → расстояние (максимум не зависит от порядка).</summary>
    private float[] ReduceDrift()
    {
        int jc = _library.JointCount;
        var drift = new float[jc];
        for (int c = 0; c < _clips.Length; c++)
        {
            var row = _drift.AsSpan(c * jc, jc);
            for (int j = 0; j < jc; j++) drift[j] = MathF.Max(drift[j], row[j]);
        }
        for (int j = 0; j < jc; j++) drift[j] = MathF.Sqrt(drift[j]);
        return drift;
    }
}
