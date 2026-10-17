using System.Numerics;

namespace Sword.Engine.Animation;

/// <summary>Нога в пуле (<see cref="PosePool.AddLeg"/>): бедро, голень, голеностоп и их поддеревья для <see cref="PosePool.LegIk"/>.</summary>
public readonly record struct PoseLeg(int Index)
{
    public override string ToString() => $"нога {Index}";
}

public sealed partial class PosePool
{
    /// <summary>Задание IK ноги: цель голеностопа в модели, поворот стопы к её повороту в позе (модель), мягкая зона у предела.</summary>
    private struct LegJob
    {
        public int Leg;
        public Vector3 Ankle;
        public Quaternion Foot;
        public float Soft;

        /// <summary>Задание — колено над стопой (<see cref="KneeAlign"/>): носок, предел, ширина мягкого входа, вес.</summary>
        public bool Align;
        public int Toe;
        public float Limit, Weight;
    }

    /// <summary>Нога: суставы цепочки и поддеревья бедра, голени и голеностопа (в каждом — сам сустав и потомки).</summary>
    private sealed record LegSpec(int Thigh, int Calf, int Foot, int[] ThighTree, int[] CalfTree, int[] FootTree);

    private readonly List<LegSpec> _legs = [];
    private LegJob[] _legJobs = [];
    private int _legCount;

    /// <summary>
    /// Нога для <see cref="LegIk"/>: <paramref name="calf"/> — ребёнок <paramref name="thigh"/>, <paramref name="foot"/> — ребёнок
    /// <paramref name="calf"/>; регистрируется при настройке, номер — порядок.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Сустава нет в скелете.</exception>
    /// <exception cref="ArgumentException">Суставы — не цепочка «родитель — ребёнок».</exception>
    public PoseLeg AddLeg(int thigh, int calf, int foot)
    {
        foreach (int j in (ReadOnlySpan<int>)[thigh, calf, foot])
            if ((uint)j >= (uint)_jc) throw new ArgumentOutOfRangeException(nameof(thigh), j, $"суставов в скелете {_jc}");
        var parents = Skeleton.JointParents;
        if (parents[calf] != thigh || parents[foot] != calf)
            throw new ArgumentException($"{Skeleton.JointNames[thigh]} → {Skeleton.JointNames[calf]} → {Skeleton.JointNames[foot]} — не цепочка ноги");
        _legs.Add(new LegSpec(thigh, calf, foot, Subtree(thigh), Subtree(calf), Subtree(foot)));
        return new PoseLeg(_legs.Count - 1);
    }

    public int LegCount => _legs.Count;

    /// <summary>Сустав и все его потомки (порядок не важен: поворот поддерева — каждой модельной матрице отдельно).</summary>
    private int[] Subtree(int root)
    {
        var mask = new float[_jc];
        Skeleton.SubtreeMask(root, mask);
        var list = new List<int>();
        for (int j = 0; j < _jc; j++)
            if (mask[j] > 0) list.Add(j);
        return [.. list];
    }

    /// <summary>
    /// Задание: двухзвенный IK ноги по модельным матрицам позы — голеностоп к <paramref name="ankle"/> (модель), колено в плоскости
    /// колена позы, стопа — её поворот в позе, затем <paramref name="foot"/> (поворот в модели вокруг голеностопа). Последняя доля
    /// <paramref name="soft"/> хода ноги прижимается к пределу гладко (колено не щёлкает); дальше предела нога не тянется. Меняет
    /// только модельные матрицы (локальная поза — анимация, правка не копится): ставится после <see cref="ToModel"/> той же позы;
    /// следующий <see cref="ToModel"/> правку снимает.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Ноги нет в пуле, цель или поворот не конечны, доля не в [0, 0,5].</exception>
    public void LegIk(PoseHandle dst, PoseLeg leg, Vector3 ankle, Quaternion foot, float soft = 0.05f)
    {
        Check(dst);
        if ((uint)leg.Index >= (uint)_legs.Count) throw new ArgumentOutOfRangeException(nameof(leg), leg, $"ног в пуле {_legs.Count}");
        if (!float.IsFinite(ankle.X) || !float.IsFinite(ankle.Y) || !float.IsFinite(ankle.Z))
            throw new ArgumentOutOfRangeException(nameof(ankle), ankle, "цель голеностопа — конечная");
        if (!float.IsFinite(foot.X) || !float.IsFinite(foot.Y) || !float.IsFinite(foot.Z) || !float.IsFinite(foot.W) || foot.LengthSquared() < 1e-6f)
            throw new ArgumentOutOfRangeException(nameof(foot), foot, "поворот стопы — конечный ненулевой");
        if (!(soft is >= 0 and <= 0.5f)) throw new ArgumentOutOfRangeException(nameof(soft), soft, "мягкая доля — в [0, 0,5]");
        if (_legCount == _legJobs.Length) Array.Resize(ref _legJobs, Math.Max(4, _legJobs.Length * 2));
        _legJobs[_legCount] = new LegJob { Leg = leg.Index, Ankle = ankle, Foot = Quaternion.Normalize(foot), Soft = soft };
        Push(new Job { Op = Op.LegIk, Dst = dst, Clip = _legCount });
        _legCount++;
    }

    /// <summary>
    /// Задание: колено над стопой — нога поворачивается вокруг линии бедро — голеностоп так, чтобы направление колена (поперёк этой
    /// линии) расходилось с направлением стопы (голеностоп → <paramref name="toe"/>, поперёк той же линии) не больше
    /// <paramref name="limit"/>, рад; сверх предела — с мягким входом шириной <paramref name="soft"/>, рад, и долей
    /// <paramref name="weight"/>. Бедро, голеностоп и стопа (её поворот в модели) остаются, движется колено. Как <see cref="LegIk"/>,
    /// правит только модельные матрицы — после <see cref="ToModel"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Ноги нет, носок вне скелета, предел, мягкость или вес вне пределов.</exception>
    public void KneeAlign(PoseHandle dst, PoseLeg leg, int toe, float limit, float soft, float weight)
    {
        Check(dst);
        if ((uint)leg.Index >= (uint)_legs.Count) throw new ArgumentOutOfRangeException(nameof(leg), leg, $"ног в пуле {_legs.Count}");
        if ((uint)toe >= (uint)_jc) throw new ArgumentOutOfRangeException(nameof(toe), toe, $"суставов в скелете {_jc}");
        if (!(limit is >= 0 and <= MathF.PI)) throw new ArgumentOutOfRangeException(nameof(limit), limit, "предел — в [0, π]");
        if (!(soft is >= 0 and <= MathF.PI)) throw new ArgumentOutOfRangeException(nameof(soft), soft, "мягкий вход — в [0, π]");
        if (!(weight is >= 0 and <= 1)) throw new ArgumentOutOfRangeException(nameof(weight), weight, "вес — в [0, 1]");
        if (weight == 0) return;
        if (_legCount == _legJobs.Length) Array.Resize(ref _legJobs, Math.Max(4, _legJobs.Length * 2));
        _legJobs[_legCount] = new LegJob { Leg = leg.Index, Align = true, Toe = toe, Limit = limit, Soft = soft, Weight = weight };
        Push(new Job { Op = Op.LegIk, Dst = dst, Clip = _legCount });
        _legCount++;
    }

    /// <summary>
    /// Поворот колена к стопе (<see cref="KneeAlign"/>) при расхождении <paramref name="angle"/>, рад: в пределе — 0, сверх него — избыток с
    /// мягким входом (квадратичный на первых <paramref name="soft"/>, дальше линейный — без излома скорости колена).
    /// </summary>
    public static float KneeExcess(float angle, float limit, float soft)
    {
        float e = MathF.Abs(angle) - limit;
        if (e <= 0) return 0;
        float x = soft > 0 && e < soft ? e * e / (2 * soft) : e - soft / 2;
        return MathF.CopySign(x, angle);
    }

    /// <summary>Колено над стопой (<see cref="KneeAlign"/>) по модельным матрицам слота <paramref name="d"/>.</summary>
    private void ApplyKneeAlign(in LegJob job, int d)
    {
        var leg = _legs[job.Leg];
        var model = _model.AsSpan(d, _jc);
        var hip = model[leg.Thigh].Translation;
        var ankle = model[leg.Foot].Translation;
        var line = ankle - hip;
        float length = line.Length();
        if (length < 1e-4f) return;
        var axis = line / length;
        var bend = model[leg.Calf].Translation - hip;
        bend -= axis * Vector3.Dot(bend, axis);
        var toe = model[job.Toe].Translation - ankle;
        toe -= axis * Vector3.Dot(toe, axis);
        // Почти прямая нога или стопа вдоль ноги: направление не определено — без правки (вес гаснет плавно).
        float defined = Math.Clamp((bend.Length() - 0.02f) / 0.04f, 0, 1) * Math.Clamp((toe.Length() - 0.02f) / 0.04f, 0, 1);
        if (defined <= 0) return;
        float angle = MathF.Atan2(Vector3.Dot(Vector3.Cross(bend, toe), axis), Vector3.Dot(bend, toe));
        float turn = KneeExcess(angle, job.Limit, job.Soft) * job.Weight * defined;
        if (turn == 0) return;
        var q = Quaternion.CreateFromAxisAngle(axis, turn);
        Rotate(model, leg.ThighTree, hip, q);
        Rotate(model, leg.FootTree, ankle, Quaternion.Conjugate(q));
    }

    /// <summary>
    /// Длина ноги к цели <paramref name="distance"/> при ходе <paramref name="reach"/>: до начала мягкой зоны — она же, дальше — гладко
    /// (без излома) к <paramref name="reach"/>, не достигая его. Зона начинается с <c>reach·(1 − soft)</c>, но не раньше длины ноги
    /// в позе <paramref name="current"/>: цель на месте голеностопа позы — поза как есть.
    /// </summary>
    public static float SoftReach(float distance, float reach, float soft, float current = 0)
    {
        float start = MathF.Max(reach * (1 - soft), MathF.Min(current, reach)), zone = reach - start;
        if (distance <= start || zone <= 0) return MathF.Min(distance, reach);
        return start + zone * (1 - MathF.Exp(-(distance - start) / zone));
    }

    /// <summary>IK ноги (<see cref="LegIk"/>) по модельным матрицам слота <paramref name="d"/>.</summary>
    private void ApplyLegIk(in LegJob job, int d)
    {
        var leg = _legs[job.Leg];
        var model = _model.AsSpan(d, _jc);
        var hip = model[leg.Thigh].Translation;
        var knee = model[leg.Calf].Translation;
        var ankle = model[leg.Foot].Translation;
        float a = Vector3.Distance(hip, knee), b = Vector3.Distance(knee, ankle);
        var toTarget = job.Ankle - hip;
        float distance = toTarget.Length();
        if (a < 1e-6f || b < 1e-6f || distance < 1e-6f) return;
        var dir = toTarget / distance;
        float length = Math.Clamp(SoftReach(distance, a + b, job.Soft, Vector3.Distance(hip, ankle)), MathF.Abs(a - b) + 1e-4f, a + b);
        // Плоскость колена — из позы: составляющая колена поперёк линии бедро — цель; прямая нога — вперёд модели.
        var bend = knee - hip;
        var perp = bend - dir * Vector3.Dot(bend, dir);
        if (perp.LengthSquared() < 1e-10f) perp = Vector3.UnitZ - dir * dir.Z;
        if (perp.LengthSquared() < 1e-10f) perp = Vector3.UnitX - dir * dir.X;
        perp = Vector3.Normalize(perp);
        float cos = Math.Clamp((a * a + length * length - b * b) / (2 * a * length), -1, 1);
        var newKnee = hip + dir * (a * cos) + perp * (a * MathF.Sqrt(1 - cos * cos));
        var newAnkle = hip + dir * length;
        var thigh = FromTo(knee - hip, newKnee - hip);
        Rotate(model, leg.ThighTree, hip, thigh);
        var calf = FromTo(model[leg.Foot].Translation - newKnee, newAnkle - newKnee);
        Rotate(model, leg.CalfTree, newKnee, calf);
        // Стопа: снять повороты бедра и голени, добавить свой — поворот стопы позы, затем заданный.
        var foot = Quaternion.Normalize(Quaternion.Concatenate(Quaternion.Conjugate(Quaternion.Concatenate(thigh, calf)), job.Foot));
        Rotate(model, leg.FootTree, model[leg.Foot].Translation, foot);
    }

    /// <summary>Модельные матрицы суставов <paramref name="joints"/> — поворотом <paramref name="q"/> вокруг точки <paramref name="pivot"/> (тело цикла — свой метод).</summary>
    private static void Rotate(Span<Matrix4x4> model, int[] joints, Vector3 pivot, Quaternion q)
    {
        var m = Matrix4x4.CreateTranslation(-pivot) * Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(pivot);
        foreach (int j in joints) model[j] *= m;
    }

    /// <summary>Кратчайший поворот направления <paramref name="from"/> в <paramref name="to"/> (<c>Vector3.Transform</c>); нулевой вектор — тождество.</summary>
    public static Quaternion FromTo(Vector3 from, Vector3 to)
    {
        float lf = from.Length(), lt = to.Length();
        if (lf < 1e-9f || lt < 1e-9f) return Quaternion.Identity;
        from /= lf;
        to /= lt;
        float dot = Vector3.Dot(from, to);
        if (dot > 1 - 1e-7f) return Quaternion.Identity;
        if (dot < -1 + 1e-6f)
        {
            var axis = Vector3.Cross(from, MathF.Abs(from.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }
        var c = Vector3.Cross(from, to);
        return Quaternion.Normalize(new Quaternion(c, 1 + dot));
    }
}
