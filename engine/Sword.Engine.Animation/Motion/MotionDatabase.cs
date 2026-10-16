using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Sword.Engine.Animation.Motion;

/// <summary>
/// База Motion Matching: кадры выбранных клипов подряд с локальными номерами, граф переходов, нормализованные признаки,
/// блоки поиска, контакты стоп. Строится раз на процесс и неизменяема — её делят все симуляции и потоки. Кадр выхода
/// нециклического клипа (<see cref="Entry"/> ≥ 0) «становится» кадром входа продолжения. Порядок клипов — по номеру в
/// библиотеке; <see cref="Fingerprint"/> — одна база, один отпечаток.
/// </summary>
public sealed partial class MotionDatabase
{
    /// <summary>Строк в блоке поиска.</summary>
    public const int BlockSize = 16;

    private readonly MotionClip[] _clips;
    private readonly int[] _clipOf;
    private readonly int[] _next;
    private readonly Pose2[] _interval;
    private readonly int[] _entry;
    private readonly bool[] _searchable;
    private readonly int[] _rows;
    private readonly float[] _features;
    private readonly float[] _mean;
    private readonly float[] _std;
    private readonly float[] _boundsMin;
    private readonly float[] _boundsMax;
    private readonly Vector3[] _rootVelocity;
    private readonly byte[] _contacts;
    private readonly int[] _movingJoints;
    private readonly int _featureCount;

    /// <summary>База из готовых массивов (копируются) — для тестов без постройки; строки, блоки и отпечаток — как у <see cref="Build"/>.</summary>
    /// <param name="clips">Клипы базы: подряд с кадра 0, по строгому возрастанию номера в библиотеке, целиком (число кадров и цикл — как в библиотеке).</param>
    /// <param name="next">[кадр] следующий кадр графа; −1 — нет.</param>
    /// <param name="interval">[кадр] перенос корня за интервал кадр → next, в системе кадра.</param>
    /// <param name="entry">[кадр] кадр входа, которым «становится» кадр выхода; −1 — не выход.</param>
    /// <param name="rootVelocity">[кадр] скорость корня клипа в системе корня кадра, м/с (Y = 0).</param>
    /// <param name="contacts">[кадр] бит i — сустав <c>schema.ContactJoints[i]</c> на земле.</param>
    /// <exception cref="ArgumentException">Массивы не сходятся по длинам, номера вне базы, числа не конечны.</exception>
    public MotionDatabase(ClipLibrary library, MotionSchema schema, ReadOnlySpan<MotionClip> clips, ReadOnlySpan<int> next,
        ReadOnlySpan<Pose2> interval, ReadOnlySpan<int> entry, ReadOnlySpan<bool> searchable, ReadOnlySpan<float> features,
        ReadOnlySpan<float> mean, ReadOnlySpan<float> std, ReadOnlySpan<Vector3> rootVelocity, ReadOnlySpan<byte> contacts,
        ReadOnlySpan<int> movingJoints)
        : this(library, schema, clips.ToArray(), next.ToArray(), interval.ToArray(), entry.ToArray(), searchable.ToArray(),
            features.ToArray(), mean.ToArray(), std.ToArray(), rootVelocity.ToArray(), contacts.ToArray(), movingJoints.ToArray(),
            Stopwatch.GetTimestamp())
    {
    }

    /// <summary>Общая часть постройки и конструктора из массивов: массивы уже свои (не копируются).</summary>
    private MotionDatabase(ClipLibrary library, MotionSchema schema, MotionClip[] clips, int[] next, Pose2[] interval, int[] entry,
        bool[] searchable, float[] features, float[] mean, float[] std, Vector3[] rootVelocity, byte[] contacts, int[] movingJoints,
        long started)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(schema);
        Library = library;
        Schema = schema;
        _featureCount = schema.FeatureCount;
        _clips = clips;
        _next = next;
        _interval = interval;
        _entry = entry;
        _searchable = searchable;
        _features = features;
        _mean = mean;
        _std = std;
        _rootVelocity = rootVelocity;
        _contacts = contacts;
        _movingJoints = movingJoints;

        CheckClips();
        int frames = FrameCount;
        CheckLength(next.Length, frames, nameof(next));
        CheckLength(interval.Length, frames, nameof(interval));
        CheckLength(entry.Length, frames, nameof(entry));
        CheckLength(searchable.Length, frames, nameof(searchable));
        CheckLength(rootVelocity.Length, frames, nameof(rootVelocity));
        CheckLength(contacts.Length, frames, nameof(contacts));
        CheckLength(features.Length, frames * _featureCount, nameof(features));
        CheckLength(mean.Length, _featureCount, nameof(mean));
        CheckLength(std.Length, _featureCount, nameof(std));
        CheckFrames();
        CheckNumbers();

        _clipOf = new int[frames];
        for (int c = 0; c < _clips.Length; c++) _clipOf.AsSpan(_clips[c].Start, _clips[c].Count).Fill(c);
        int rows = 0;
        foreach (bool s in _searchable) if (s) rows++;
        _rows = new int[rows];
        for (int f = 0, r = 0; f < frames; f++) if (_searchable[f]) _rows[r++] = f;
        int blocks = (rows + BlockSize - 1) / BlockSize;
        _boundsMin = new float[blocks * _featureCount];
        _boundsMax = new float[blocks * _featureCount];
        for (int b = 0; b < blocks; b++) BlockBounds(b);

        Fingerprint = ComputeFingerprint();
        int edges = 0, early = 0;
        for (int f = 0; f < frames; f++)
            if (_entry[f] >= 0)
            {
                edges++;
                if (f != _clips[_clipOf[f]].End - 1) early++;
            }
        Summary = new MotionDatabaseSummary(_clips.Length, frames, rows, edges, early, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    /// <summary>
    /// Постройка из клипов при загрузке, не в тике: кинематика кадров, признаки, граф, траектории, нормализация. Порядок
    /// <paramref name="clips"/> не важен. Итог не зависит от потоков, сборки и уровня JIT; без AVX2 признаки отличаются в
    /// последних битах, но граф тот же при доле ничьей 10⁻⁴. Циклы по кадрам — с <c>AggressiveOptimization</c>: постройка
    /// раз на процесс, на tier-0 — в разы медленнее.
    /// </summary>
    /// <exception cref="ArgumentException">Повторы или номера вне библиотеки, суставы вне скелета, параметры вне допустимого, политика вернула клип вне базы.</exception>
    /// <exception cref="InvalidDataException">Данные клипов не дают базы — с понятной причиной.</exception>
    public static MotionDatabase Build(ClipLibrary library, ReadOnlySpan<int> clips, MotionSchema schema, IMotionGraphPolicy policy,
        in MotionBuildParams parameters)
    {
        long started = Stopwatch.GetTimestamp();
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(policy);
        CheckParameters(parameters);
        int[] sorted = clips.ToArray();
        Array.Sort(sorted);
        if (sorted.Length == 0) throw new ArgumentException("нет клипов для базы", nameof(clips));
        for (int i = 0; i < sorted.Length; i++)
        {
            if ((uint)sorted[i] >= (uint)library.ClipCount)
                throw new ArgumentException($"клип {sorted[i]} вне библиотеки ({library.ClipCount} клипов)", nameof(clips));
            if (i > 0 && sorted[i] == sorted[i - 1])
                throw new ArgumentException($"клип {sorted[i]} ({library.Clips[sorted[i]].Name}) повторяется", nameof(clips));
        }

        var kin = ClipKinematics.Compute(library, sorted, schema, parameters.Contacts);
        var b = new Builder(library, schema, policy, parameters, kin);
        b.FillPose();
        b.NormalizePose();
        b.MeasureClips();
        b.LinkClips();
        b.LinkEdges();
        b.FillTrajectories();
        var (mean, std) = b.Normalize();
        return new MotionDatabase(library, schema, kin.ClipArray, b.Next, b.Interval, b.Entry, b.Searchable, b.Raw, mean, std,
            kin.RootVelocityArray, kin.ContactArray, b.MovingJoints(), started);
    }

    private static void CheckParameters(in MotionBuildParams p)
    {
        if (!float.IsFinite(p.ExitWindow) || p.ExitWindow < 0)
            throw new ArgumentException($"окно раннего выхода {p.ExitWindow} с, а нужно конечное и не меньше 0", "parameters");
        if (!float.IsFinite(p.VelocityWindow) || p.VelocityWindow < 0)
            throw new ArgumentException($"окно скоростей {p.VelocityWindow} с, а нужно конечное и не меньше 0", "parameters");
        if (!float.IsFinite(p.MovingThreshold) || p.MovingThreshold < 0)
            throw new ArgumentException($"порог движущегося смещения {p.MovingThreshold} м, а нужен конечный и не меньше 0", "parameters");
        if (!float.IsFinite(p.SeamTie) || p.SeamTie is < 0 or >= 1)
            throw new ArgumentException($"доля ничьей шва {p.SeamTie}, а нужна в [0, 1)", "parameters");
        ContactDetector.Validate(p.Contacts);
    }

    /// <summary>Состояние постройки: вход, кинематика и массивы базы по мере заполнения; <see cref="Raw"/> после <see cref="Normalize"/> — нормализованные.</summary>
    private sealed partial class Builder
    {
        public readonly ClipLibrary Library;
        public readonly MotionSchema Schema;
        public readonly IMotionGraphPolicy Policy;
        public readonly MotionBuildParams Params;
        public readonly ClipKinematics Kin;
        public readonly MotionClip[] Clips;
        public readonly int Frames, Fc;
        public readonly float Rate;
        public readonly float[] Raw;
        public readonly int[] Next, Entry;
        public readonly Pose2[] Interval;
        public readonly bool[] Searchable;

        /// <summary>[группа] места суставов группы в <see cref="MotionSchema.Joints"/> (кинематика).</summary>
        private readonly int[][] _groupSlots;

        public Builder(ClipLibrary library, MotionSchema schema, IMotionGraphPolicy policy, in MotionBuildParams parameters, ClipKinematics kin)
        {
            Library = library;
            Schema = schema;
            Policy = policy;
            Params = parameters;
            Kin = kin;
            Clips = kin.ClipArray;
            Frames = kin.FrameCount;
            Fc = schema.FeatureCount;
            Rate = library.SampleRate;
            Raw = new float[Frames * Fc];
            Next = new int[Frames];
            Entry = new int[Frames];
            Interval = new Pose2[Frames];
            Searchable = new bool[Frames];
            _groupSlots = new int[schema.GroupCount][];
            for (int g = 0; g < schema.GroupCount; g++)
            {
                var joints = schema.GroupJoints(g);
                _groupSlots[g] = new int[joints.Length];
                for (int k = 0; k < joints.Length; k++) _groupSlots[g][k] = schema.JointSlot(joints[k]);
            }
        }

        public string ClipName(int clip) => Library.Clips[Clips[clip].LibraryClip].Name;

        /// <summary>Признаки корня и суставов всех кадров — из кинематики.</summary>
        public void FillPose()
        {
            for (int f = 0; f < Frames; f++) FillPoseFrame(f);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void FillPoseFrame(int f)
        {
            var row = Raw.AsSpan(f * Fc, Fc);
            int jc = Kin.JointCount;
            var positions = Kin.Positions.Slice(f * jc, jc);
            var velocities = Kin.Velocities.Slice(f * jc, jc);
            var groups = Schema.Groups;
            for (int g = 0; g < groups.Length; g++)
            {
                var group = groups[g];
                var into = row.Slice(group.Offset, group.Size);
                var slots = _groupSlots[g];
                switch (group.Kind)
                {
                    case FeatureKind.RootVelocity:
                        into[0] = Kin.RootVelocity[f].X;
                        into[1] = Kin.RootVelocity[f].Z;
                        break;
                    case FeatureKind.RootYawRate:
                        into[0] = Kin.YawRate[f];
                        break;
                    case FeatureKind.JointPosition:
                        for (int k = 0; k < slots.Length; k++) Put(into, k, positions[slots[k]]);
                        break;
                    case FeatureKind.JointVelocity:
                        for (int k = 0; k < slots.Length; k++) Put(into, k, velocities[slots[k]]);
                        break;
                    case FeatureKind.JointPairFacing:
                        for (int k = 0; k < slots.Length; k += 2)
                        {
                            var d = positions[slots[k]] - positions[slots[k + 1]];
                            // «вперёд» — (левый − правый), повёрнутое так, что +X переходит в +Z: (x, z) → (−z, x)
                            float x = -d.Z, z = d.X, length = MathF.Sqrt(x * x + z * z);
                            if (length > 1e-6f)
                            {
                                into[k] = x / length;
                                into[k + 1] = z / length;
                            }
                            else
                            {
                                into[k] = 0;
                                into[k + 1] = 1;
                            }
                        }
                        break;
                }
            }
        }

        private static void Put(Span<float> into, int joint, Vector3 v)
        {
            into[3 * joint] = v.X;
            into[3 * joint + 1] = v.Y;
            into[3 * joint + 2] = v.Z;
        }

        /// <summary>Суставы скелета, чьё локальное смещение уходит от первого кадра своего клипа дальше порога, — по возрастанию.</summary>
        public int[] MovingJoints()
        {
            var drift = Kin.OffsetDrift;
            var moving = new List<int>();
            for (int j = 0; j < drift.Length; j++)
                if (drift[j] > Params.MovingThreshold) moving.Add(j);
            return [.. moving];
        }
    }

    public ClipLibrary Library { get; }
    public MotionSchema Schema { get; }

    /// <summary>Частота кадров базы, Гц.</summary>
    public float SampleRate => Library.SampleRate;

    public int FeatureCount => _featureCount;

    public int FrameCount => _clips.Length == 0 ? 0 : _clips[^1].End;

    public int ClipCount => _clips.Length;

    /// <summary>Клипы базы по возрастанию номера в библиотеке; кадры клипа — <c>Start…End − 1</c>.</summary>
    public ReadOnlySpan<MotionClip> Clips => _clips;

    public ReadOnlySpan<int> ClipOf => _clipOf;

    /// <summary>[кадр] следующий кадр графа; −1 — нет.</summary>
    public ReadOnlySpan<int> Next => _next;

    /// <summary>[кадр] перенос корня за интервал кадр → <see cref="Next"/>, в системе кадра; у кадра без следующего — тождество.</summary>
    public ReadOnlySpan<Pose2> Interval => _interval;

    /// <summary>[кадр] кадр входа, которым «становится» этот кадр выхода; −1 — не выход.</summary>
    public ReadOnlySpan<int> Entry => _entry;

    public ReadOnlySpan<bool> Searchable => _searchable;

    /// <summary>Искомые кадры по возрастанию — строки поиска; блок b — строки <c>b·BlockSize…</c> (<see cref="BlockRows"/>).</summary>
    public ReadOnlySpan<int> Rows => _rows;

    public int BlockCount => (_rows.Length + BlockSize - 1) / BlockSize;

    /// <summary>[кадр·признак] нормализованные признаки (без весов).</summary>
    public ReadOnlySpan<float> Features => _features;

    /// <summary>[признак] среднее сырых признаков по искомым кадрам.</summary>
    public ReadOnlySpan<float> Mean => _mean;

    /// <summary>[признак] разброс — общий на группу, не меньше её пола.</summary>
    public ReadOnlySpan<float> Std => _std;

    /// <summary>[блок·признак] наименьший нормализованный признак строк блока.</summary>
    public ReadOnlySpan<float> BoundsMin => _boundsMin;

    /// <summary>[блок·признак] наибольший нормализованный признак строк блока.</summary>
    public ReadOnlySpan<float> BoundsMax => _boundsMax;

    /// <summary>[кадр] скорость корня клипа в системе корня кадра, м/с (Y = 0) — темп проигрывания под скорость корня.</summary>
    public ReadOnlySpan<Vector3> RootVelocity => _rootVelocity;

    /// <summary>[кадр] контакты с землёй: бит i — сустав <see cref="MotionSchema.ContactJoints"/>[i].</summary>
    public ReadOnlySpan<byte> Contacts => _contacts;

    /// <summary>Суставы, чьё локальное смещение движется (таз, корень): их смещение тоже инерциализируется; по возрастанию.</summary>
    public ReadOnlySpan<int> MovingJoints => _movingJoints;

    /// <summary>64 бита от схемы, клипов, графа, признаков и разметки (время постройки не входит).</summary>
    public ulong Fingerprint { get; }

    public MotionDatabaseSummary Summary { get; }

    public ReadOnlySpan<float> FeatureRow(int frame) => _features.AsSpan(frame * _featureCount, _featureCount);

    /// <summary>Сырые признаки кадра (<c>нормализованный·Std + Mean</c>) — в <paramref name="into"/> (не меньше <see cref="FeatureCount"/>).</summary>
    public void RawFeatures(int frame, Span<float> into)
    {
        var row = FeatureRow(frame);
        for (int i = 0; i < row.Length; i++) into[i] = row[i] * _std[i] + _mean[i];
    }

    /// <summary>Искомые кадры блока <paramref name="block"/> (до <see cref="BlockSize"/> строк подряд).</summary>
    public ReadOnlySpan<int> BlockRows(int block)
    {
        int start = block * BlockSize;
        return _rows.AsSpan(start, Math.Min(BlockSize, _rows.Length - start));
    }

    /// <summary>Кадр в библиотеке клипов (для сэмпла позы) — кадра базы <paramref name="frame"/>.</summary>
    public int LibraryFrame(int frame)
    {
        var clip = _clips[_clipOf[frame]];
        return Library.Clips[clip.LibraryClip].Start + frame - clip.Start;
    }

    public int FrameInClip(int frame) => frame - _clips[_clipOf[frame]].Start;

    /// <summary>Клип базы по номеру клипа библиотеки; нет в базе — −1.</summary>
    public int FindClip(int libraryClip)
    {
        int lo = 0, hi = _clips.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            int v = _clips[mid].LibraryClip;
            if (v == libraryClip) return mid;
            if (v < libraryClip) lo = mid + 1;
            else hi = mid - 1;
        }
        return -1;
    }

    /// <summary>Сустав контакта <paramref name="contact"/> (номер в <see cref="MotionSchema.ContactJoints"/>) на земле в кадре.</summary>
    public bool Contact(int frame, int contact) => (_contacts[frame] >> contact & 1) != 0;

    private static void CheckLength(int actual, int expected, string name)
    {
        if (actual != expected) throw new ArgumentException($"длина {actual}, а нужна {expected}", name);
    }

    private void CheckClips()
    {
        if (_clips.Length == 0) throw new ArgumentException("в базе нет клипов", "clips");
        int jc = Library.JointCount;
        foreach (int j in Schema.Joints)
            if (j >= jc) throw new ArgumentException($"сустав {j} схемы вне скелета {Library.Skeleton.Id} ({jc} суставов)", "schema");
        var lib = Library.Clips;
        for (int c = 0, start = 0, prev = -1; c < _clips.Length; start = _clips[c].End, prev = _clips[c].LibraryClip, c++)
        {
            var clip = _clips[c];
            if (clip.Start != start || clip.Count < 1) throw new ArgumentException($"клип базы {c}: кадры не подряд с 0", "clips");
            if ((uint)clip.LibraryClip >= (uint)lib.Length || clip.LibraryClip <= prev)
                throw new ArgumentException($"клип базы {c}: номер в библиотеке {clip.LibraryClip} вне её или не по возрастанию", "clips");
            var l = lib[clip.LibraryClip];
            if (clip.Count != l.Count || clip.Loop != l.Loop)
                throw new ArgumentException($"клип базы {c} ({l.Name}): кадров {clip.Count}, цикл {clip.Loop}, а в библиотеке {l.Count}, {l.Loop}", "clips");
        }
    }

    private void CheckFrames()
    {
        int frames = FrameCount;
        for (int f = 0; f < frames; f++)
        {
            if (_next[f] < -1 || _next[f] >= frames) throw new ArgumentException($"кадр {f}: следующий {_next[f]} вне базы", "next");
            if (_entry[f] < -1 || _entry[f] >= frames) throw new ArgumentException($"кадр {f}: вход {_entry[f]} вне базы", "entry");
            if (_searchable[f] && _next[f] < 0) throw new ArgumentException($"кадр {f} ищется, а следующего нет", "searchable");
            if (!_interval[f].IsFinite) throw new ArgumentException($"кадр {f}: перенос корня {_interval[f]}", "interval");
            if (!float.IsFinite(_rootVelocity[f].X) || !float.IsFinite(_rootVelocity[f].Y) || !float.IsFinite(_rootVelocity[f].Z))
                throw new ArgumentException($"кадр {f}: скорость корня {_rootVelocity[f]}", "rootVelocity");
            if (_contacts[f] >> Schema.ContactJoints.Length != 0)
                throw new ArgumentException($"кадр {f}: бит контакта сверх {Schema.ContactJoints.Length} суставов", "contacts");
        }
        for (int i = 0; i < _movingJoints.Length; i++)
            if ((uint)_movingJoints[i] >= (uint)Library.JointCount || i > 0 && _movingJoints[i] <= _movingJoints[i - 1])
                throw new ArgumentException($"сустав {_movingJoints[i]}: вне скелета или не по возрастанию", "movingJoints");
    }

    private void CheckNumbers()
    {
        for (int i = 0; i < _featureCount; i++)
            if (!float.IsFinite(_mean[i]) || !float.IsFinite(_std[i]) || _std[i] <= 0)
                throw new ArgumentException($"признак {i}: среднее {_mean[i]}, разброс {_std[i]}", "std");
        for (int i = 0; i < _features.Length; i++)
            if (!float.IsFinite(_features[i]))
                throw new ArgumentException($"кадр {i / _featureCount}, признак {i % _featureCount}: {_features[i]}", "features");
    }

    private void BlockBounds(int block)
    {
        var min = _boundsMin.AsSpan(block * _featureCount, _featureCount);
        var max = _boundsMax.AsSpan(block * _featureCount, _featureCount);
        min.Fill(float.PositiveInfinity);
        max.Fill(float.NegativeInfinity);
        foreach (int f in BlockRows(block))
        {
            var row = FeatureRow(f);
            for (int i = 0; i < row.Length; i++)
            {
                min[i] = MathF.Min(min[i], row[i]);
                max[i] = MathF.Max(max[i], row[i]);
            }
        }
    }

    private ulong ComputeFingerprint()
    {
        var h = new Fingerprinter(0x4D4D_4442);   // «MMDB»
        h.Add(_featureCount);
        foreach (var g in Schema.Groups)
        {
            h.Add((int)g.Kind);
            h.Add(g.Size);
        }
        for (int g = 0; g < Schema.GroupCount; g++)
            foreach (int j in Schema.GroupJoints(g)) h.Add(j);
        foreach (float t in Schema.FutureTimes) h.Add(t);
        foreach (float t in Schema.PastTimes) h.Add(t);
        foreach (int j in Schema.ContactJoints) h.Add(j);
        foreach (var clip in _clips)
        {
            h.Add(Library.Clips[clip.LibraryClip].Name);
            h.Add(clip.Count);
            h.Add(clip.Loop ? 1 : 0);
        }
        for (int f = 0; f < _next.Length; f++)
        {
            h.Add(_next[f]);
            h.Add(_entry[f]);
            h.Add(_searchable[f] ? 1 : 0);
            h.Add(_interval[f].X);
            h.Add(_interval[f].Z);
            h.Add(_interval[f].Yaw);
            h.Add(_rootVelocity[f].X);
            h.Add(_rootVelocity[f].Y);
            h.Add(_rootVelocity[f].Z);
            h.Add(_contacts[f]);
        }
        foreach (float v in _features) h.Add(v);
        foreach (float v in _mean) h.Add(v);
        foreach (float v in _std) h.Add(v);
        foreach (int j in _movingJoints) h.Add(j);
        return h.Value;
    }

    /// <summary>
    /// Хеш отпечатка: раунд и итоговое перемешивание — как у xxHash64; числа — значениями (float: −0 → +0), строки — длиной и
    /// кодами символов. Память не выделяет.
    /// </summary>
    private struct Fingerprinter(ulong seed)
    {
        private const ulong P1 = 11400714785074694791UL, P2 = 14029467366897019727UL, P3 = 1609587929392839161UL,
            P4 = 9650029242287828579UL, P5 = 2870177450012600261UL;

        private ulong _acc = seed + P5;
        private ulong _count;

        public void Add(ulong value)
        {
            _acc ^= BitOperations.RotateLeft(value * P2, 31) * P1;
            _acc = BitOperations.RotateLeft(_acc, 27) * P1 + P4;
            _count++;
        }

        public void Add(int value) => Add(unchecked((ulong)(long)value));

        public void Add(float value) => Add((ulong)(uint)BitConverter.SingleToInt32Bits(value == 0 ? 0f : value));

        public void Add(string value)
        {
            Add(value.Length);
            foreach (char c in value) Add((ulong)c);
        }

        public readonly ulong Value
        {
            get
            {
                ulong h = _acc ^ (_count * P5);
                h ^= h >> 33;
                h *= P2;
                h ^= h >> 29;
                h *= P3;
                h ^= h >> 32;
                return h;
            }
        }
    }
}
