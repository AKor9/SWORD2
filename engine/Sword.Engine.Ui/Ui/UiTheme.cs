namespace Sword.Engine.Ui;

/// <summary>
/// Палитра и размеры интерфейса — единственный источник цветов: виджеты, HUD, панели игры берут их отсюда,
/// а не задают числами на месте. Размеры — в логических пикселях, <see cref="UiContext"/> умножает их на масштаб.
/// Перенос <c>Widgets/UiTheme.cs</c> SWORD 2eab13b, значения те же.
/// </summary>
public sealed class UiTheme
{
    /// <summary>Общая тема: её получает <see cref="UiContext"/> по умолчанию; её читает HUD, рисующий списком квадов напрямую.</summary>
    public static UiTheme Default { get; } = new();

    private static uint C(byte r, byte g, byte b, byte a = 255) => UiDrawList.Rgba(r, g, b, a);

    public uint Background = C(6, 11, 15);
    public uint Panel = C(12, 20, 26);
    public uint PanelAlt = C(17, 28, 35);
    public uint Border = C(120, 210, 230, 28);
    public uint BorderStrong = C(120, 210, 230, 80);

    public uint Text = C(214, 232, 238);
    public uint TextDim = C(128, 152, 162);
    public uint TextFaint = C(78, 98, 108);

    public uint Accent = C(90, 205, 230);
    public uint AccentHot = C(140, 225, 245);
    public uint AccentActive = C(60, 170, 200);
    public uint AccentText = C(4, 16, 22);
    public uint Good = C(100, 225, 170);
    public uint Warn = C(240, 190, 80);
    public uint Bad = C(240, 85, 85);

    public uint Button = C(22, 38, 46);
    public uint ButtonHot = C(32, 56, 66);
    public uint ButtonActive = C(42, 74, 86);
    public uint Field = C(4, 9, 12);
    public uint RowHover = C(120, 210, 230, 18);
    public uint RowSelected = C(90, 205, 230, 56);
    public uint Selection = C(90, 205, 230, 90);
    public uint Scrollbar = C(120, 210, 230, 45);
    public uint ScrollbarHot = C(120, 210, 230, 90);
    public uint ScrollbarActive = C(90, 205, 230, 180);
    public uint Popup = C(10, 18, 24, 250);
    public uint Shadow = C(0, 0, 0, 110);

    // HUD и оверлеи поверх сцены
    public uint Overlay = C(4, 10, 14, 180);
    public uint OverlayStrong = C(4, 10, 14, 215);
    public uint Rule = C(120, 210, 230, 34);
    public uint Track = C(120, 210, 230, 26);
    public uint TrackFaint = C(120, 210, 230, 16);
    public uint Cpu = C(240, 170, 60);
    public uint Gpu = C(90, 205, 230);
    public uint HealthTrack = C(50, 20, 20);
    public uint HealthHigh = C(230, 70, 55);
    public uint HealthLow = C(140, 25, 20);
    public uint DeathTint = C(40, 0, 0, 110);

    public float FontSize = 14;
    public float SmallFontSize = 12;
    public float RowHeight = 22;
    public float Padding = 8;
    public float Radius = 4;
    public float ScrollbarWidth = 8;
}
