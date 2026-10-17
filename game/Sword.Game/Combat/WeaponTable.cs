using System.Numerics;
using Sword.Engine.Content;
using Sword.Game.Contracts;
using Sword.Game.Data;

namespace Sword.Game.Combat;

/// <summary>
/// Таблица оружия: все мечи пака в порядке списка контента (по алфавиту без учёта регистра), номер меча — номер в таблице;
/// клинок и тело клинка (<see cref="Bodies"/>) каждого — по его мешу, один раз при загрузке. Меч по умолчанию —
/// <see cref="DefaultName"/> (по его клинку размечены окна ударов). Неизменяема, общая для симуляций процесса; без оружия игра не стартует.
/// </summary>
public sealed class WeaponTable
{
    /// <summary>Меч по умолчанию (<c>DefaultWeaponName</c>): им размечены удары, он же в руке игрока без конфига.</summary>
    public const string DefaultName = "sword_1h";

    private readonly WeaponInfo[] _weapons;
    private readonly BladeBody[] _bodies;
    private readonly Vector4[][] _probes;

    public ReadOnlySpan<WeaponInfo> Weapons => _weapons;
    public int Count => _weapons.Length;

    /// <summary>Тело клинка меча по номеру (<see cref="WeaponInfo.Body"/> — то же, посчитанное заранее).</summary>
    public ReadOnlySpan<BladeBody> Bodies => _bodies;

    /// <summary>Номер меча по умолчанию (<c>DefaultWeapon</c>).</summary>
    public int Default { get; }

    /// <summary>
    /// Щупы земли тела клинка оружия <paramref name="weapon"/> (<see cref="IPhysicsWorld.SetGroundProbes"/>): то, что торчит за капсулой
    /// клинка (навершие, гарда, головка молота), — чтобы выпавшее оружие не уходило в землю. Таблица без мешей — пусто.
    /// </summary>
    public ReadOnlySpan<Vector4> Probes(int weapon) => (uint)weapon < (uint)_probes.Length ? _probes[weapon] : default;

    public WeaponTable(ReadOnlySpan<WeaponInfo> weapons, ReadOnlySpan<Vector3[]> points = default)
    {
        if (weapons.IsEmpty) throw new InvalidDataException($"нет оружия ({WeaponMesh.Dir}/*{WeaponMesh.Ext})");
        if (weapons.Length > PlayerInventory.MaxWeapons)
            throw new InvalidDataException($"оружия {weapons.Length} — инвентарь игрока держит не больше {PlayerInventory.MaxWeapons}");
        _weapons = weapons.ToArray();
        _bodies = [.. _weapons.Select(w => w.Body)];
        _probes = new Vector4[_weapons.Length][];
        for (int w = 0; w < _weapons.Length; w++) _probes[w] = w < points.Length ? GroundProbes(_bodies[w], points[w]) : [];
        Default = Math.Max(0, Find(DefaultName));
    }

    /// <summary>Номер меча по имени без учёта регистра; нет — −1.</summary>
    public int Find(string name)
    {
        for (int i = 0; i < _weapons.Length; i++)
            if (string.Equals(_weapons[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>
    /// Оружие врагов: номера таблицы по возрастанию без оружия только игрока (<paramref name="playerOnly"/> — имена, регистр не важен).
    /// Оружие только игрока в конце таблицы не меняет выбор врагов среди остальных.
    /// </summary>
    /// <exception cref="InvalidDataException">Врагам не осталось оружия.</exception>
    public int[] EnemyWeapons(IEnumerable<string> playerOnly)
    {
        var excluded = new HashSet<string>(playerOnly, StringComparer.OrdinalIgnoreCase);
        int[] pool = [.. Enumerable.Range(0, _weapons.Length).Where(w => !excluded.Contains(_weapons[w].Name))];
        if (pool.Length == 0)
            throw new InvalidDataException($"combat.playerOnlyWeapons: всё оружие пака ({Names}) — только игрока, врагам не осталось");
        return pool;
    }

    /// <summary>Имена мечей через запятую — для сообщений.</summary>
    public string Names => string.Join(", ", _weapons.Select(w => w.Name));

    /// <summary>Все мечи пака (<c>content.List(WeaponDir, WeaponExt)</c>).</summary>
    /// <exception cref="FileNotFoundException">В контенте нет ни одного меча.</exception>
    /// <exception cref="InvalidDataException">Меш меча испорчен, со суставами или без клинка; мечей больше 64.</exception>
    public static WeaponTable Load(IContentSource content)
    {
        var files = content.List(WeaponMesh.Dir, WeaponMesh.Ext);
        if (files.Count == 0)
            throw new FileNotFoundException($"в {content.Location} нет оружия ({WeaponMesh.Dir}/*{WeaponMesh.Ext})", WeaponMesh.Dir);
        var weapons = new WeaponInfo[files.Count];
        var points = new Vector3[files.Count][];
        for (int i = 0; i < files.Count; i++)
        {
            string path = files[i];
            WeaponBlade blade;
            using (var s = content.OpenRead(path)) blade = WeaponMesh.Read(s, content.Describe(path));
            weapons[i] = new WeaponInfo(Path.GetFileNameWithoutExtension(path), path, blade.Base, blade.Tip);
            points[i] = blade.Points;
        }
        return new WeaponTable(weapons, points);
    }

    /// <summary>Радиус щупа земли оружия, м.</summary>
    public const float ProbeRadius = 0.012f;

    /// <summary>
    /// Щупы земли по мешу (система кисти) в системе тела клинка (Y — ось, X — опорная, Z = X × Y): крайние точки меша за капсулой —
    /// навершие (дальше хвата по оси) и края поперёк оси (±X, ±Z) — сфера щупа внутри меша у крайней точки.
    /// </summary>
    public static Vector4[] GroundProbes(in BladeBody body, ReadOnlySpan<Vector3> points)
    {
        if (points.IsEmpty) return [];
        var y = body.Axis;
        var x = Vector3.Normalize(body.XHint - y * Vector3.Dot(body.XHint, y));
        var z = Vector3.Cross(x, y);
        Vector3 pommel = new(0, float.MaxValue, 0), px = default, nx = default, pz = default, nz = default;
        foreach (var p in points)
        {
            var d = p - body.Center;
            var l = new Vector3(Vector3.Dot(d, x), Vector3.Dot(d, y), Vector3.Dot(d, z));
            if (l.Y < pommel.Y) pommel = l;
            if (l.X > px.X) px = l;
            if (l.X < nx.X) nx = l;
            if (l.Z > pz.Z) pz = l;
            if (l.Z < nz.Z) nz = l;
        }
        var probes = new List<Vector4>(5);
        float reach = BladeBody.Radius + ProbeRadius;
        if (pommel.Y < -body.HalfLength - ProbeRadius) probes.Add(new Vector4(pommel.X, pommel.Y + ProbeRadius, pommel.Z, ProbeRadius));
        foreach (var e in (ReadOnlySpan<Vector3>)[px, nx, pz, nz])
        {
            float lateral = MathF.Sqrt(e.X * e.X + e.Z * e.Z);
            if (lateral <= reach) continue;   // в капсуле
            var inward = new Vector3(e.X, 0, e.Z) / lateral * ProbeRadius;
            probes.Add(new Vector4(e - inward, ProbeRadius));
        }
        return [.. probes];
    }
}
