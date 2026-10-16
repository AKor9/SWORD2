using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Sword.Game.Data;

namespace Sword.Bake.Anim;

/// <summary>Параметры зон крови оружия (поле <c>blood</c> рецепта оружия).</summary>
internal sealed class WeaponBloodParams
{
    /// <summary>Форма оружия — по ней пекарня предлагает зоны. Сейчас «blade» (меч, катана); топор, молот, посох, щит — позже.</summary>
    public string Shape { get; set; } = "blade";
    /// <summary>Конец рукояти, см от навершия; пусто — по сечениям (начало гарды).</summary>
    public float? HandleEnd { get; set; }
    /// <summary>Начало лезвия, см от навершия; пусто — по сечениям (конец гарды).</summary>
    public float? BladeStart { get; set; }
    /// <summary>
    /// Слоты материалов → зона: шаблон имени слота (* и ?, без учёта регистра) → имя зоны («blade», «handle») или «none» (крови нет).
    /// Вершина слота, совпавшего с шаблоном, берёт эту зону вместо геометрической. У слота без имени шаблоны не срабатывают.
    /// </summary>
    public Dictionary<string, string>? Materials { get; set; }
    /// <summary>Клеток лезвия вдоль и поперёк.</summary>
    public int BladeCellsU { get; set; } = 32;
    public int BladeCellsV { get; set; } = 6;
    /// <summary>Клеток рукояти вдоль и вокруг.</summary>
    public int HandleCellsU { get; set; } = 8;
    public int HandleCellsV { get; set; } = 8;
}

/// <summary>
/// Зоны крови оружия (<see cref="WeaponBloodData"/>): разметка по геометрии в системе меча (клинок по +Z от навершия, кромка +Y, см),
/// уточнение по слотам материалов, перевод в пространство меша. Меч: рукоять — цилиндр до гарды, гарда — без крови (с неё капает),
/// лезвие — плоскость от гарды до острия.
/// </summary>
internal static class WeaponBloodBake
{
    private const int Slices = 96;
    private const float GuardShare = 0.6f;

    /// <param name="canonical">Вершины в системе меча, см.</param>
    /// <param name="toMesh">Точка системы меча (см) → пространство меша.</param>
    public static WeaponBloodData Build(Vector3[] canonical, int[] vertexMaterial, string[] materialNames, WeaponBloodParams p,
        Func<Vector3, Vector3> toMesh, TextWriter log)
    {
        if (!string.Equals(p.Shape, "blade", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"blood.shape «{p.Shape}»: пока поддерживается только «blade»");
        var inv = CultureInfo.InvariantCulture;
        int n = canonical.Length;

        // сечения по длине: наибольший радиус от оси клинка
        float length = 0;
        foreach (var c in canonical) length = MathF.Max(length, c.Z);
        if (length <= 1e-3f) throw new InvalidDataException("зоны крови: меш без длины");
        // радиус — от центра сечения, а не от оси рукояти: у изогнутого клинка (катана) ось уходит от лезвия
        var radius = new float[Slices];
        var centre = new Vector2[Slices];
        var count = new int[Slices];
        int SliceOf(float z) => Math.Clamp((int)(z / length * Slices), 0, Slices - 1);
        foreach (var c in canonical)
        {
            int k = SliceOf(c.Z);
            centre[k] += new Vector2(c.X, c.Y);
            count[k]++;
        }
        for (int k = 0; k < Slices; k++) if (count[k] > 0) centre[k] /= count[k];
        foreach (var c in canonical)
        {
            int k = SliceOf(c.Z);
            radius[k] = MathF.Max(radius[k], (new Vector2(c.X, c.Y) - centre[k]).Length());
        }
        int guard = Array.IndexOf(radius, radius.Max());
        float share = GuardShare * radius[guard];
        int handleEnd = guard;
        while (handleEnd > 0 && radius[handleEnd - 1] >= share) handleEnd--;
        int bladeStart = guard;
        while (bladeStart < Slices - 1 && radius[bladeStart + 1] >= share) bladeStart++;
        float slice = length / Slices;
        float handleZ = p.HandleEnd ?? handleEnd * slice;
        float bladeZ = p.BladeStart ?? (bladeStart + 1) * slice;
        if (!(handleZ > 0 && bladeZ > handleZ && bladeZ < length))
            throw new InvalidDataException(string.Create(inv, $"зоны крови: рукоять до {handleZ:F1} см, лезвие с {bladeZ:F1} см при длине {length:F1} см"));

        // размеры частей
        float handleR = 0, bladeY0 = float.MaxValue, bladeY1 = float.MinValue, bladeX = 0;
        var handleC = Vector2.Zero;
        int handleN = 0, bladeN = 0;
        foreach (var c in canonical)
        {
            if (c.Z < handleZ) { handleC += new Vector2(c.X, c.Y); handleN++; }
            else if (c.Z > bladeZ)
            {
                bladeY0 = MathF.Min(bladeY0, c.Y);
                bladeY1 = MathF.Max(bladeY1, c.Y);
                bladeX += c.X;
                bladeN++;
            }
        }
        if (handleN == 0 || bladeN == 0) throw new InvalidDataException("зоны крови: нет вершин рукояти или лезвия");
        handleC /= handleN;
        bladeX /= bladeN;
        foreach (var c in canonical)
            if (c.Z < handleZ) handleR = MathF.Max(handleR, (new Vector2(c.X, c.Y) - handleC).Length());

        const int Handle = 0, Blade = 1;
        var zonesCm = new[]
        {
            new WeaponBloodZone
            {
                Name = "handle", Kind = BloodZoneKind.Cylinder, Origin = new Vector3(handleC, 0), AxisU = Vector3.UnitZ, AxisV = Vector3.UnitY,
                Length = handleZ, Half = MathF.Max(handleR, 0.1f), CellsU = p.HandleCellsU, CellsV = p.HandleCellsV,
            },
            new WeaponBloodZone
            {
                Name = "blade", Kind = BloodZoneKind.Plane, Origin = new Vector3(bladeX, (bladeY0 + bladeY1) * 0.5f, bladeZ),
                AxisU = Vector3.UnitZ, AxisV = Vector3.UnitY, Length = length - bladeZ, Half = MathF.Max((bladeY1 - bladeY0) * 0.5f, 0.1f),
                CellsU = p.BladeCellsU, CellsV = p.BladeCellsV,
            },
        };

        // зона вершины: геометрия, поверх — слоты материалов
        var vertexZones = new byte[n];
        for (int v = 0; v < n; v++)
            vertexZones[v] = canonical[v].Z < handleZ ? (byte)Handle : canonical[v].Z > bladeZ ? (byte)Blade : WeaponBloodData.NoZone;
        int bySlot = ApplyMaterials(vertexZones, vertexMaterial, materialNames, p.Materials, zonesCm);

        var data = new WeaponBloodData
        {
            Base = toMesh(new Vector3(bladeX, (bladeY0 + bladeY1) * 0.5f, bladeZ)),
            Tip = toMesh(new Vector3(bladeX, (bladeY0 + bladeY1) * 0.5f, length)),
            Zones = zonesCm.Select(z => ToMesh(z, toMesh)).ToArray(),
            VertexZones = vertexZones,
        };
        data.Validate("зоны крови");
        int[] counts = new int[zonesCm.Length + 1];
        foreach (byte b in vertexZones) counts[b == WeaponBloodData.NoZone ? zonesCm.Length : b]++;
        log.WriteLine(string.Create(inv,
            $"зоны крови: рукоять 0–{handleZ:F1} см r {handleR:F1} ({counts[Handle]} вершин), гарда без крови ({counts[^1]}), " +
            $"лезвие {bladeZ:F1}–{length:F1} см ширина {bladeY1 - bladeY0:F1} ({counts[Blade]}); клеток {data.CellCount}; по слотам материалов {bySlot} вершин"));
        return data;
    }

    /// <summary>Переопределяет зоны вершин по шаблонам имён слотов; возвращает, сколько вершин переопределено.</summary>
    private static int ApplyMaterials(byte[] vertexZones, int[] vertexMaterial, string[] names, Dictionary<string, string>? rules,
        WeaponBloodZone[] zones)
    {
        if (rules is null || rules.Count == 0 || vertexMaterial.Length != vertexZones.Length) return 0;
        var slotZone = new int[names.Length];   // −2 — правила нет
        for (int m = 0; m < names.Length; m++)
        {
            slotZone[m] = -2;
            if (names[m].Length == 0) continue;
            foreach (var (pattern, zone) in rules)
            {
                if (!Regex.IsMatch(names[m], "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase))
                    continue;
                if (string.Equals(zone, "none", StringComparison.OrdinalIgnoreCase)) slotZone[m] = WeaponBloodData.NoZone;
                else
                {
                    slotZone[m] = Array.FindIndex(zones, z => string.Equals(z.Name, zone, StringComparison.OrdinalIgnoreCase));
                    if (slotZone[m] < 0) throw new InvalidDataException($"blood.materials: «{pattern}» → неизвестная зона «{zone}»");
                }
                break;
            }
        }
        int changed = 0;
        for (int v = 0; v < vertexZones.Length; v++)
        {
            int m = vertexMaterial[v];
            if (m < 0 || m >= slotZone.Length || slotZone[m] == -2) continue;
            vertexZones[v] = (byte)slotZone[m];
            changed++;
        }
        return changed;
    }

    /// <summary>Зона системы меча (см) → пространство меша: преобразование аффинное, длины берут его масштаб.</summary>
    private static WeaponBloodZone ToMesh(WeaponBloodZone z, Func<Vector3, Vector3> toMesh)
    {
        var o = toMesh(z.Origin);
        var u = toMesh(z.Origin + z.AxisU) - o;
        var v = toMesh(z.Origin + z.AxisV) - o;
        float scale = u.Length();
        return z with
        {
            Origin = o, AxisU = Vector3.Normalize(u), AxisV = Vector3.Normalize(v), Length = z.Length * scale, Half = z.Half * scale,
        };
    }
}
