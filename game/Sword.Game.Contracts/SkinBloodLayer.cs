using System.Numerics;

namespace Sword.Game.Contracts;

/// <summary>
/// Слой крови на коже бойцов (сервис клиента): модуль даёт на ключ бойца (<see cref="FighterKey"/>) bindless-индекс SRV своего буфера
/// вида по вершинам меша персонажа, клиент игры кладёт его в экземпляр скина (<c>SkinInstance.owner</c> = индекс + 1; 0 — слоя нет) и
/// смешивает вид с кожей в шейдере. Буфер — <c>StructuredBuffer&lt;uint2&gt;</c>, элемент на вершину меша (номер — <c>SV_VertexID</c>),
/// упаковка — <see cref="SkinLayerTexel"/>. Модуля нет или источник не подключён — индексов нет, кожа прежняя.
/// </summary>
public interface ISkinBloodLayer
{
    /// <summary>Подключить источник: SRV буфера бойца по ключу, −1 — у бойца слоя нет. Вызывается один раз при инициализации модуля.</summary>
    void Attach(Func<ulong, int> srvOf);
}

/// <summary>
/// Вид слоя кожи в вершине — два слова. Слой поверх кожи: альбедо = кожа · M + A (M — пропускание слоя до кожи и обратно с покрытием,
/// A — своё альбедо слоя), блик слоя — × покрытие. x — A в R11G11B10F (биты half без знака: r, g — 11 старших, b — 10; ≥ 0);
/// y — M unorm7 r | g ≪ 7 | b ≪ 14, покрытие unorm5 ≪ 21, шероховатость GGX unorm6 ≪ 26. Отражательная способность при нормальном
/// падении — <see cref="F0"/> (кровь, n ≈ 1.40). Тот же разбор — в шейдере скина игры (<c>SkinLayerOf</c> в scene.hlsl).
/// </summary>
public static class SkinLayerTexel
{
    /// <summary>F0 мокрого слоя (кровь ≈ 0.028, вода ≈ 0.02).</summary>
    public const float F0 = 0.028f;

    public static (uint X, uint Y) Pack(Vector3 add, Vector3 mul, float coverage, float roughness)
    {
        uint x = Float11(add.X) | Float11(add.Y) << 11 | Float10(add.Z) << 22;
        uint y = Unorm(mul.X, 7) | Unorm(mul.Y, 7) << 7 | Unorm(mul.Z, 7) << 14 | Unorm(coverage, 5) << 21 | Unorm(roughness, 6) << 26;
        return (x, y);
    }

    public static void Unpack(uint x, uint y, out Vector3 add, out Vector3 mul, out float coverage, out float roughness)
    {
        add = new Vector3(FromHalf((x & 0x7FF) << 4), FromHalf((x >> 11 & 0x7FF) << 4), FromHalf((x >> 22) << 5));
        mul = new Vector3((y & 0x7F) / 127f, (y >> 7 & 0x7F) / 127f, (y >> 14 & 0x7F) / 127f);
        coverage = (y >> 21 & 0x1F) / 31f;
        roughness = (y >> 26) / 63f;
    }

    /// <summary>0…1 → 0…2^bits − 1 с округлением к ближайшему (как <c>uint(saturate(v) * max + 0.5)</c> в HLSL).</summary>
    public static uint Unorm(float v, int bits)
    {
        float max = (1 << bits) - 1;
        return (uint)(Math.Clamp(v, 0f, 1f) * max + 0.5f);
    }

    private static uint HalfBits(float v) => BitConverter.HalfToUInt16Bits((Half)Math.Max(v, 0f));
    private static uint Float11(float v) => HalfBits(v) >> 4 & 0x7FF;
    private static uint Float10(float v) => HalfBits(v) >> 5 & 0x3FF;
    private static float FromHalf(uint bits) => (float)BitConverter.UInt16BitsToHalf((ushort)bits);
}
