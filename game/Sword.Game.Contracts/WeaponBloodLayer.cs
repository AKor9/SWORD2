using System.Numerics;

namespace Sword.Game.Contracts;

/// <summary>
/// Лезвие оружия для крови — зона «blade» его <c>.swblood</c> в пространстве меша оружия (оно же система кисти, как вершины меша):
/// начало лезвия <see cref="Origin"/> (у гарды), ось длины <see cref="AxisU"/> (к острию, единичная) на <see cref="Length"/>, ось ширины
/// <see cref="AxisV"/> на ±<see cref="Half"/>; нормаль плоскости — стороны лезвия. <see cref="BodyToMesh"/> — точка системы тела клинка
/// (<see cref="BodySegs.Weapon"/>, <see cref="BladeBody"/>) → пространство меша.
/// </summary>
public readonly record struct BladeBloodZone(Vector3 Origin, Vector3 AxisU, Vector3 AxisV, float Length, float Half, Matrix4x4 BodyToMesh)
{
    /// <summary>Нормаль плоскости лезвия: сторона 0 — с её стороны.</summary>
    public Vector3 Normal => Vector3.Normalize(Vector3.Cross(AxisU, AxisV));

    /// <summary>Доля длины лезвия (0 — начало у гарды, 1 — острие) точки пространства меша; вне лезвия — за пределами 0…1.</summary>
    public float Along(Vector3 mesh) => Vector3.Dot(mesh - Origin, AxisU) / Length;
}

/// <summary>
/// Слой крови на оружии (сервис клиента): модуль даёт на ключ бойца (<see cref="FighterKey"/>) и номер оружия bindless-индекс SRV буфера
/// крови его лезвия; клиент кладёт индекс + 1 в <c>owner</c> экземпляров частей оружия с битом слоя оружия в флагах материала, шейдер
/// оружия читает слой по положению точки на лезвии. Буфер — <c>StructuredBuffer&lt;uint2&gt;</c>, раскладка — <see cref="WeaponBloodLayout"/>.
/// Модуля нет или источник не подключён — индексов нет, оружие прежнее.
/// </summary>
public interface IWeaponBloodLayer
{
    /// <summary>Лезвие оружия таблицы <paramref name="weapon"/>; false — зоны лезвия нет (нет <c>.swblood</c>): крови на оружии нет.</summary>
    bool TryBlade(int weapon, out BladeBloodZone zone);

    /// <summary>
    /// Подключить источник: SRV буфера по ключу бойца и номеру оружия в его руке (или выпавшего), −1 — крови нет. Вызывается один раз при
    /// инициализации модуля.
    /// </summary>
    void Attach(Func<ulong, int, int> srvOf);
}

/// <summary>
/// Раскладка буфера крови лезвия (<c>uint2</c> на элемент): заголовок <see cref="Header"/> элементов — геометрия лезвия в пространстве
/// меша (float подряд по словам: начало x, y, z; клеток на сторону (uint); ось длины / длина x, y, z; ось ширины / полуширина x, y, z;
/// нормаль x, y, z; зерно шума (uint)), затем клетки вдоль лезвия: сторона 0 (по нормали) — <c>Header + i</c>, сторона 1 —
/// <c>Header + cells + i</c>, вид клетки — <see cref="SkinLayerTexel"/>. Тот же разбор — <c>WeaponLayerOf</c> в scene.hlsl игры.
/// </summary>
public static class WeaponBloodLayout
{
    public const int Header = 8;

    public static int Elements(int cells) => Header + 2 * cells;

    /// <summary>Заголовок в <paramref name="into"/> (не меньше <see cref="Header"/> элементов).</summary>
    public static void WriteHeader(Span<ulong> into, in BladeBloodZone zone, int cells, uint seed)
    {
        Span<uint> w = stackalloc uint[2 * Header];
        w.Clear();
        var u = zone.AxisU / zone.Length;
        var v = zone.AxisV / zone.Half;
        var n = zone.Normal;
        w[0] = F(zone.Origin.X);
        w[1] = F(zone.Origin.Y);
        w[2] = F(zone.Origin.Z);
        w[3] = (uint)cells;
        w[4] = F(u.X);
        w[5] = F(u.Y);
        w[6] = F(u.Z);
        w[7] = F(v.X);
        w[8] = F(v.Y);
        w[9] = F(v.Z);
        w[10] = F(n.X);
        w[11] = F(n.Y);
        w[12] = F(n.Z);
        w[13] = seed;
        for (int i = 0; i < Header; i++) into[i] = w[2 * i] | (ulong)w[2 * i + 1] << 32;
    }

    /// <summary>Элемент вида клетки: x — младшие 32 бита.</summary>
    public static ulong Cell(uint x, uint y) => x | (ulong)y << 32;

    private static uint F(float v) => BitConverter.SingleToUInt32Bits(v);
}
