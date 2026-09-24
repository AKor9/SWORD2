using System.Numerics;
using Sword.Engine.Formats;

namespace Sword.Game.Data;

/// <summary>Клинок меча в системе кисти (hand_r): основание, кончик и вершин в меше.</summary>
public readonly record struct WeaponBlade(Vector3 Base, Vector3 Tip, int Vertices);

/// <summary>
/// Меш оружия (<c>weapons/имя.swmesh</c>, печёт weapon.static) → клинок — <c>CharacterRig.MakeWeapon</c> SWORD 2eab13b: кончик —
/// самая дальняя от кисти (начала координат меша) вершина, основание — <see cref="BaseOffset"/> от кисти по той же оси.
/// Меш — формат <c>.swmesh</c> без суставов: вершины уже в системе кисти (формат знает только эта сборка данных и рендер —
/// игре он не виден, раздел 4 плана).
/// </summary>
public static class WeaponMesh
{
    /// <summary>Каталог оружия в контенте и расширение: по файлу на меч, имя файла — имя меча (<c>WeaponDir</c>, <c>WeaponExt</c>).</summary>
    public const string Dir = "weapons", Ext = ".swmesh";

    /// <summary>Основание клинка — на таком расстоянии от кисти, м (у гарды).</summary>
    public const float BaseOffset = 0.22f;

    /// <exception cref="InvalidDataException">
    /// Не .swmesh или испорчен; у меша есть суставы (оружие — жёсткое вложение в кисть); все вершины в кисти — клинка нет (в
    /// SWORD — NaN в основании).
    /// </exception>
    public static WeaponBlade Read(Stream mesh, string name)
    {
        var data = SkinnedMeshData.Read(mesh, name);
        if (data.JointCount != 0)
            throw new InvalidDataException($"{name}: у меша оружия суставов {data.JointCount} — ожидается статический меш (вершины в системе кисти)");
        var tip = Vector3.Zero;
        foreach (var v in data.Vertices)
            if (v.Position.LengthSquared() > tip.LengthSquared()) tip = v.Position;
        if (tip == Vector3.Zero) throw new InvalidDataException($"{name}: все вершины меша оружия в кисти — клинка нет");
        return new WeaponBlade(Vector3.Normalize(tip) * BaseOffset, tip, data.Vertices.Length);
    }
}
