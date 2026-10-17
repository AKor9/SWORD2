namespace Sword.Game.Blood;

/// <summary>Группа форм штампа пола: брызги отвесной капли, мелкие капли, брызги косой капли (хвост в +U), лужи (мягкие карты толщины).</summary>
public enum BloodShapeGroup { Omni, Spots, Directional, Puddles }

/// <summary>
/// Формы штампов пола — слои текстур пака одним массивом (<see cref="Paths"/> по порядку групп): форма k ≥ 1 штампа Fluid2D — слой k − 1.
/// На CPU — где группа в массиве, среднее канала r слоя (нормировка массы штампа) и соотношение сторон исходника (ширина / высота).
/// Выбор слоя — из хеша (зерно штампа).
/// </summary>
public sealed class BloodStampShapes
{
    public static readonly string[] Paths =
        ["textures/blood/omni.swtex", "textures/blood/spots.swtex", "textures/blood/directional.swtex", "textures/blood/puddles.swtex"];

    private readonly (int First, int Count)[] _groups;
    private readonly float[] _means, _aspects;
    private readonly bool[] _usable;

    /// <param name="slices">Слоёв каждой текстуры <see cref="Paths"/>.</param>
    /// <param name="means">Среднее канала r каждого слоя массива.</param>
    /// <param name="aspects">Соотношение сторон исходника каждого слоя (нет — 1).</param>
    public BloodStampShapes(IReadOnlyList<int> slices, float[] means, IReadOnlyList<float> aspects)
    {
        if (slices.Count != Paths.Length) throw new ArgumentException($"групп форм {Paths.Length}, слоёв дано {slices.Count}", nameof(slices));
        _groups = new (int, int)[slices.Count];
        int first = 0;
        for (int g = 0; g < slices.Count; g++)
        {
            _groups[g] = (first, slices[g]);
            first += slices[g];
        }
        if (means.Length != first) throw new ArgumentException($"средних {means.Length}, слоёв {first}", nameof(means));
        // Почти пустой слой даёт пик штампа без предела — такой слой берёт среднее 1 и не выбирается.
        _usable = [.. means.Select(m => m > MinMean)];
        _means = [.. means.Select(m => m > MinMean ? m : 1f)];
        _aspects = [.. Enumerable.Range(0, first).Select(i => i < aspects.Count && aspects[i] > 0 ? aspects[i] : 1f)];
    }

    /// <summary>Слой с меньшим средним не выбирается.</summary>
    public const float MinMean = 0.01f;

    /// <summary>Средние слоёв для <see cref="Sword.Engine.Fluid2D.Fluid2DStamps"/> (форма k — элемент k − 1).</summary>
    public float[] Means => _means;

    public int Count(BloodShapeGroup group) => _groups[(int)group].Count;

    /// <summary>Форма штампа (k ≥ 1) группы по хешу; слоя нет — 0 (гаусс).</summary>
    public int Pick(BloodShapeGroup group, uint seed)
    {
        var (first, count) = _groups[(int)group];
        if (count == 0) return 0;
        int start = (int)(BloodHash.Mix(seed) % (uint)count);
        for (int i = 0; i < count; i++)
        {
            int k = first + (start + i) % count;
            if (_usable[k]) return k + 1;
        }
        return 0;
    }

    /// <summary>Среднее слоя формы k ≥ 1.</summary>
    public float Mean(int shape) => _means[shape - 1];

    /// <summary>Соотношение сторон исходника формы k ≥ 1.</summary>
    public float Aspect(int shape) => _aspects[shape - 1];
}
