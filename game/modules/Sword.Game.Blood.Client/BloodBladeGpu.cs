using Sword.Engine.Render;
using Sword.Game.Contracts;

namespace Sword.Game.Blood;

/// <summary>
/// Кровь на клинке для шейдера оружия игры (<see cref="IWeaponBloodLayer"/>): буфер кадра на слот (<see cref="WeaponBloodLayout"/>: заголовок —
/// лезвие в пространстве меша, затем вид клеток обеих сторон по функции свежести в упаковке <see cref="SkinLayerTexel"/>), выделены при старте.
/// Вид клеток считается на CPU, когда слот изменился или сменился шаг крови (возраст), и копируется в слот кадра, когда клиент спрашивает SRV
/// оружия. Кадр — 0 Б.
/// </summary>
public sealed class BloodBladeGpu : IDisposable
{
    private readonly BloodBlade _blade;
    private readonly FrameData<ulong>[] _data;
    private readonly ulong[][] _packed;
    private readonly long[] _packedVersion, _packedStep;
    private readonly int _elements = WeaponBloodLayout.Elements(BloodBlade.Cells);

    public BloodBladeGpu(Renderer renderer, BloodBlade blade)
    {
        _blade = blade;
        int n = blade.SlotCount;
        _data = new FrameData<ulong>[n];
        _packed = new ulong[n][];
        _packedVersion = new long[n];
        _packedStep = new long[n];
        for (int s = 0; s < n; s++)
        {
            _data[s] = renderer.CreateFrameData<ulong>($"blood.blade.{s}", (uint)_elements);
            _packed[s] = new ulong[_elements];
            _packedVersion[s] = _packedStep[s] = -1;
        }
    }

    /// <summary>Слотов, вид которых пересчитан в последнем кадре, и отдано SRV.</summary>
    public int Packs { get; private set; }
    public int Served { get; private set; }

    /// <summary>Начало кадра: счёт кадра.</summary>
    public void BeginFrame() => Packs = Served = 0;

    /// <summary>
    /// Вид клеток слота (тесты; без GPU) — вычисляется так же, как для кадра: функция свежести, покрытие тонкой плёнки — толщина /
    /// <paramref name="coverMm"/> (рвётся шумом в шейдере).
    /// </summary>
    public static void Pack(BloodBlade blade, int slot, float now, in BloodLookParams look, float coverMm, Span<ulong> into)
    {
        WeaponBloodLayout.WriteHeader(into, blade.Zone(slot), BloodBlade.Cells, BloodHash.Mix((uint)blade.Key(slot) ^ (uint)blade.Weapon(slot) << 20));
        for (int side = 0; side < 2; side++)
            for (int i = 0; i < BloodBlade.Cells; i++) into[WeaponBloodLayout.Header + side * BloodBlade.Cells + i] = Cell(blade, slot, side, i, now, look, coverMm);
    }

    private static ulong Cell(BloodBlade blade, int slot, int side, int i, float now, in BloodLookParams look, float coverMm)
    {
        float h = blade.Thickness(slot, side, i);
        if (!(h > 0)) return Neutral;
        var r = BloodLook.Evaluate(now - blade.Birth(slot, side, i), h, look);
        r = r with { Coverage = r.Coverage * BloodLook.Saturate(h / MathF.Max(coverMm, 1e-4f)) };
        var (x, y) = SkinLayerTexel.Pack(r.Add, r.Mul, r.Coverage, r.Roughness);
        return WeaponBloodLayout.Cell(x, y);
    }

    /// <summary>Клетка без крови: M = 1, A = 0, покрытие 0 (как вершина кожи без крови — слой интерполируется по клетке).</summary>
    private static readonly ulong Neutral = NeutralCell();

    private static ulong NeutralCell()
    {
        var (x, y) = SkinLayerTexel.Pack(System.Numerics.Vector3.Zero, System.Numerics.Vector3.One, 0, BloodLookParams.Default.WetRoughness);
        return WeaponBloodLayout.Cell(x, y);
    }

    /// <summary>
    /// SRV буфера слота для оружия кадра (вид — на часах <paramref name="now"/>, шаг крови <paramref name="step"/>): данные — в слот кадра.
    /// </summary>
    public int Srv(int slot, float now, long step, in BloodLookParams look, float coverMm)
    {
        if ((uint)slot >= (uint)_data.Length || !_blade.Used(slot)) return -1;
        var packed = _packed[slot];
        if (_packedVersion[slot] != _blade.Version(slot) || _packedStep[slot] != step)
        {
            Pack(_blade, slot, now, look, coverMm, packed);
            _packedVersion[slot] = _blade.Version(slot);
            _packedStep[slot] = step;
            Packs++;
        }
        var data = _data[slot];
        packed.AsSpan().CopyTo(data.Slot);
        Served++;
        return data.Srv;
    }

    public void Dispose()
    {
        foreach (var d in _data) d.Dispose();
    }
}
