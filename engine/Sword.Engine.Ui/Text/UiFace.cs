using Sword.Engine.Formats;

namespace Sword.Engine.Ui;

/// <summary>
/// Роль шрифта в интерфейсе; каждую роль берёт ровно один шрифт контента. Номера и имена пишет пекарь, читает и эталонный пак.
/// </summary>
public enum UiFace
{
    Body = 0,
    Head = 1,
    Label = 2,
    NumLight = 3,
    Num = 4,
    /// <summary>Число ролей, не роль.</summary>
    Count = 5,
}

public static class UiFaces
{
    /// <summary>
    /// Роль по имени без учёта регистра; не роль (число, <c>Count</c>, список «A,B») — false: <c>Enum.TryParse</c> молча
    /// дал бы опечатке в манифесте чужую роль. Имена совпадают с <see cref="UiVectorFontFormat.RoleNames"/> (тест).
    /// </summary>
    public static bool TryParse(string name, out UiFace face)
    {
        bool known = UiVectorFontFormat.TryParseRole(name, out int role);
        face = known ? (UiFace)role : default;
        return known;
    }

    public static string List => UiVectorFontFormat.RoleList;
}

/// <summary>Метрики начертания в долях кегля.</summary>
public readonly record struct UiFontMetrics(float Ascent, float Descent, float LineGap);

/// <summary>Один glyph после OpenType shaping. Все расстояния — в долях em.</summary>
public readonly record struct UiShapedGlyph(uint GlyphId, uint Cluster, float XAdvance, float YAdvance, float XOffset, float YOffset);
