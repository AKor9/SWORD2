using System.Numerics;
using Sword.Engine.Formats;

namespace Sword.Engine.Animation;

/// <summary>
/// Скелет: имена и родители суставов, хеш и поза привязки. Пространство персонажа: +Z вперёд, +X влево, Y вверх, метры.
/// Неизменяем — его делят все симуляции. Родитель может идти после ребёнка (экспорт UE): прямая кинематика — в заранее
/// посчитанном порядке «родитель раньше ребёнка»; цикл в родителях — ошибка данных.
/// </summary>
public sealed class Skeleton
{
    private readonly string[] _jointNames;
    private readonly int[] _jointParents;
    /// <summary>Суставы в порядке «родитель раньше ребёнка» — порядок прямой кинематики.</summary>
    private readonly int[] _order;
    private readonly Quaternion[] _referenceRot;
    private readonly Vector3[] _referencePos;

    public string Id { get; }
    public uint Hash { get; }
    public int JointCount => _jointNames.Length;
    public ReadOnlySpan<string> JointNames => _jointNames;
    public ReadOnlySpan<int> JointParents => _jointParents;

    /// <summary>Поза привязки (локальная); без меша — единичные повороты и нулевые смещения.</summary>
    public ReadOnlySpan<Quaternion> ReferenceRot => _referenceRot;
    public ReadOnlySpan<Vector3> ReferencePos => _referencePos;

    /// <exception cref="InvalidDataException">Родитель вне скелета или цикл в родителях.</exception>
    public Skeleton(string id, ReadOnlySpan<string> jointNames, ReadOnlySpan<int> jointParents)
    {
        if (jointNames.Length != jointParents.Length)
            throw new ArgumentException($"суставов {jointNames.Length}, родителей {jointParents.Length}");
        Id = id;
        _jointNames = jointNames.ToArray();
        _jointParents = jointParents.ToArray();
        _order = BuildOrder(id, _jointParents);
        Hash = SkeletonFile.ComputeHash(_jointNames, _jointParents);
        _referenceRot = new Quaternion[_jointNames.Length];
        _referencePos = new Vector3[_jointNames.Length];
        Array.Fill(_referenceRot, Quaternion.Identity);
    }

    /// <summary>Тот же скелет с другой позой привязки (массивы — уже свои, не копируются).</summary>
    private Skeleton(Skeleton source, Quaternion[] referenceRot, Vector3[] referencePos)
    {
        Id = source.Id;
        _jointNames = source._jointNames;
        _jointParents = source._jointParents;
        Hash = source.Hash;
        _order = source._order;
        _referenceRot = referenceRot;
        _referencePos = referencePos;
    }

    /// <exception cref="InvalidDataException">Файл испорчен или в родителях цикл.</exception>
    public static Skeleton Read(Stream stream, string name)
    {
        var file = SkeletonFile.Read(stream, name);
        return new Skeleton(file.Id, file.JointNames, file.JointParents);
    }

    private static int[] BuildOrder(string id, int[] parents)
    {
        int n = parents.Length;
        var order = new int[n];
        var path = new int[n];
        var state = new byte[n];   // 0 — не посещён, 1 — на пути, 2 — готов
        int next = 0;
        for (int j = 0; j < n; j++)
        {
            // путь вверх до готового сустава или корня, затем — вниз по порядку
            int m = 0;
            for (int k = j; k >= 0 && state[k] != 2; k = parents[k])
            {
                if (state[k] == 1) throw new InvalidDataException($"скелет {id}: цикл в родителях у сустава {k}");
                if (parents[k] < -1 || parents[k] >= n) throw new InvalidDataException($"скелет {id}: родитель сустава {k} вне скелета");
                state[k] = 1;
                path[m++] = k;
            }
            for (int i = m - 1; i >= 0; i--)
            {
                order[next++] = path[i];
                state[path[i]] = 2;
            }
        }
        return order;
    }

    public int Find(string name) => Array.IndexOf(_jointNames, name);

    /// <summary>Маска поддерева для <see cref="PosePool.AddMask"/>: 1 — <paramref name="root"/> и потомки, 0 — остальные. Для настройки, не для тика.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Сустава <paramref name="root"/> нет в скелете.</exception>
    /// <exception cref="ArgumentException">Мест в <paramref name="mask"/> не столько, сколько суставов.</exception>
    public void SubtreeMask(int root, Span<float> mask)
    {
        if ((uint)root >= (uint)JointCount) throw new ArgumentOutOfRangeException(nameof(root), root, $"суставов в скелете {JointCount}");
        if (mask.Length != JointCount) throw new ArgumentException($"мест в маске {mask.Length}, а суставов {JointCount}", nameof(mask));
        for (int j = 0; j < mask.Length; j++)
        {
            int k = j;
            while (k >= 0 && k != root) k = _jointParents[k];   // циклов нет — проверено при постройке
            mask[j] = k == root ? 1 : 0;
        }
    }

    /// <summary>Прямая кинематика: локальные позы → модельные матрицы (вектор слева, сустав = локальная · родитель); каждая матрица — один раз.</summary>
    public void ToModel(ReadOnlySpan<Quaternion> rot, ReadOnlySpan<Vector3> pos, Span<Matrix4x4> model)
    {
        var parents = _jointParents;
        foreach (int j in _order)
        {
            var local = Matrix4x4.CreateFromQuaternion(rot[j]);
            local.Translation = pos[j];
            int p = parents[j];
            if (p >= 0) local *= model[p];
            model[j] = local;
        }
    }

    /// <summary>
    /// Скелет с позой привязки из меша (.swmesh): модельная матрица сустава — обращённая обратная bind-матрица. Скелет меша
    /// совпадает со скелетом клипов; bind-матрица — только поворот и перенос.
    /// </summary>
    /// <exception cref="InvalidDataException">Меш испорчен, его скелет другой или bind-матрица не разлагается.</exception>
    public Skeleton WithMeshBindPose(Stream mesh, string name)
    {
        var data = SkinnedMeshData.Read(mesh, name);
        if (!data.JointNames.AsSpan().SequenceEqual(_jointNames) || !data.JointParents.AsSpan().SequenceEqual(_jointParents))
            throw new InvalidDataException($"{name}: скелет меша не совпадает со скелетом {Id} — перепеките командой skin");
        int n = JointCount;
        var model = new Matrix4x4[n];
        for (int j = 0; j < n; j++)
            if (!Matrix4x4.Invert(data.InverseBind[j], out model[j]))
                throw new InvalidDataException($"{name}: bind-матрица сустава {_jointNames[j]} не обращается");
        var rot = new Quaternion[n];
        var pos = new Vector3[n];
        for (int j = 0; j < n; j++)
        {
            var local = model[j];
            int p = _jointParents[j];
            // model[p] обратима: это обращение обратимой bind-матрицы
            if (p >= 0 && Matrix4x4.Invert(model[p], out var parentInv)) local = model[j] * parentInv;
            if (!Matrix4x4.Decompose(local, out _, out var r, out var t))
                throw new InvalidDataException($"{name}: bind-матрица сустава {_jointNames[j]} не разлагается на поворот и перенос");
            rot[j] = Quaternion.Normalize(r);
            pos[j] = t;
        }
        return new Skeleton(this, rot, pos);
    }
}
