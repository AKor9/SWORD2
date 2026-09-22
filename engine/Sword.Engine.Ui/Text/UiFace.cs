namespace Sword.Engine.Ui;

/// <summary>
/// Роль шрифта в интерфейсе: текст, заголовки, подписи, цифры. Каждую роль берёт ровно один шрифт контента (поле ролей
/// в <c>.swfont</c>). Номера и имена — <c>UiFace</c> SWORD 2eab13b (их пишет пекарь, читает эталонный пак).
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
    /// Роль по имени из файла шрифта — точное имя без учёта регистра; не роль (число, <c>Count</c>, список «A,B») —
    /// false. Отличие от SWORD: там <c>Enum.TryParse</c> складывал список через запятую в третью роль
    /// («Label,Head» → NumLight), и опечатка в манифесте молча давала шрифту чужую роль.
    /// </summary>
    public static bool TryParse(string name, out UiFace face)
    {
        for (var f = UiFace.Body; f < UiFace.Count; f++)
            if (string.Equals(name, f.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                face = f;
                return true;
            }
        face = default;
        return false;
    }

    public static string List => string.Join(", ", Enum.GetNames<UiFace>()[..(int)UiFace.Count]);
}

/// <summary>Метрики начертания в долях кегля.</summary>
public readonly record struct UiFontMetrics(float Ascent, float Descent, float LineGap);

/// <summary>Один glyph после OpenType shaping. Все расстояния — в долях em.</summary>
public readonly record struct UiShapedGlyph(uint GlyphId, uint Cluster, float XAdvance, float YAdvance, float XOffset, float YOffset);
