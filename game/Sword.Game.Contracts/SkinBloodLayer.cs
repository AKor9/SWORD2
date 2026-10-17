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
/// Вид слоя кожи в вершине — два слова: x — half альбедо r | half g ≪ 16; y — half b | unorm8 покрытие ≪ 16 | unorm8 шероховатость ≪ 24.
/// Альбедо линейное; покрытие 0…1 — доля кожи под слоем; шероховатость GGX 0…1. Отражательная способность при нормальном падении —
/// <see cref="F0"/> (вода, кровь). Тот же разбор — в шейдере скина игры (<c>SkinLayerOf</c> в scene.hlsl).
/// </summary>
public static class SkinLayerTexel
{
    /// <summary>F0 мокрого слоя (вода ≈ 0.02, кровь ≈ 0.03).</summary>
    public const float F0 = 0.03f;

    public static (uint X, uint Y) Pack(Vector3 albedo, float coverage, float roughness)
    {
        uint x = BitConverter.HalfToUInt16Bits((Half)albedo.X) | (uint)BitConverter.HalfToUInt16Bits((Half)albedo.Y) << 16;
        uint y = BitConverter.HalfToUInt16Bits((Half)albedo.Z) | Unorm8(coverage) << 16 | Unorm8(roughness) << 24;
        return (x, y);
    }

    public static void Unpack(uint x, uint y, out Vector3 albedo, out float coverage, out float roughness)
    {
        albedo = new Vector3((float)BitConverter.UInt16BitsToHalf((ushort)x), (float)BitConverter.UInt16BitsToHalf((ushort)(x >> 16)),
            (float)BitConverter.UInt16BitsToHalf((ushort)y));
        coverage = (y >> 16 & 0xFF) / 255f;
        roughness = (y >> 24) / 255f;
    }

    /// <summary>0…1 → 0…255 с округлением к ближайшему (как <c>uint(saturate(v) * 255 + 0.5)</c> в HLSL).</summary>
    public static uint Unorm8(float v) => (uint)(Math.Clamp(v, 0f, 1f) * 255f + 0.5f);
}
