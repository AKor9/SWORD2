namespace Sword.Game.Client.Scene;

/// <summary>
/// Материал поверхности сцены для бликов (<c>ShadeMaterial</c> в lighting.hlsli): шероховатость GGX (младшие 8 бит) и металл (бит 8).
/// У скина — поле <c>Flags</c> экземпляра (<c>GpuSkinInstance</c>), у пола и колонн — числа шейдера.
/// </summary>
public static class SceneMaterial
{
    /// <summary>Бит металла: f0 — альбедо, рассеянного света нет.</summary>
    public const uint Metal = 0x100;

    public static uint Pack(float roughness, bool metal = false) =>
        (uint)MathF.Round(Math.Clamp(roughness, 0f, 1f) * 255) | (metal ? Metal : 0);

    /// <summary>Тело бойца: матовый пластик манекена.</summary>
    public static readonly uint Body = Pack(0.55f);

    /// <summary>Клинок и металл оружия: полированная сталь.</summary>
    public static readonly uint Steel = Pack(0.28f, metal: true);

    /// <summary>Дерево рукояти факела.</summary>
    public static readonly uint Wood = Pack(0.75f);

    /// <summary>Обугленное (шишка факела).</summary>
    public static readonly uint Charred = Pack(0.95f);

    /// <summary>Каменный пол и рельеф арены.</summary>
    public static readonly uint Ground = Pack(0.85f);

    /// <summary>Колонны.</summary>
    public static readonly uint Pillar = Pack(0.7f);

    /// <summary>Материал части оружия по имени материала секции меша (как <c>ArenaClient.WeaponMaterialColour</c>): прочее — сталь.</summary>
    public static uint OfWeaponPart(string material) => material.ToLowerInvariant() switch
    {
        "wood" => Wood,
        "charred" => Charred,
        _ => Steel,
    };
}
