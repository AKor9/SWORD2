namespace Sword.Game.Client.Scene;

/// <summary>
/// Материал поверхности сцены для бликов (<c>ShadeMaterial</c> в lighting.hlsli): шероховатость GGX (младшие 8 бит), металл (бит 8) и
/// текстура (биты 9–10: 0 — нет, иначе слой + 1 массивов <c>textures/materials/*.swtex</c>, трипланар в позе привязки). У скина — поле
/// <c>Flags</c> экземпляра (<c>GpuSkinInstance</c>), у пола и колонн — числа шейдера.
/// </summary>
public static class SceneMaterial
{
    /// <summary>Бит металла: f0 — альбедо, рассеянного света нет.</summary>
    public const uint Metal = 0x100;

    /// <summary>Сдвиг номера текстуры (слой + 1) в материале.</summary>
    public const int TextureShift = 9;

    /// <summary>Слои текстур материалов: ткань костюма (Fabric036) и шлифованная сталь (Metal009), ambientCG CC0.</summary>
    public const uint Fabric = 1, BrushedSteel = 2;

    /// <summary>Пути массивов текстур в паке: цвет (sRGB), нормали (OpenGL), шероховатость — слой на текстуру.</summary>
    public const string AlbedoPath = "textures/materials/albedo.swtex", NormalsPath = "textures/materials/normals.swtex",
        RoughnessPath = "textures/materials/roughness.swtex";

    public static uint Pack(float roughness, bool metal = false, uint texture = 0) =>
        (uint)MathF.Round(Math.Clamp(roughness, 0f, 1f) * 255) | (metal ? Metal : 0) | (texture << TextureShift);

    /// <summary>Тело бойца: костюм из гладкой ткани.</summary>
    public static readonly uint Body = Pack(0.7f, texture: Fabric);

    /// <summary>Клинок и металл оружия: шлифованная сталь.</summary>
    public static readonly uint Steel = Pack(0.3f, metal: true, texture: BrushedSteel);

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
