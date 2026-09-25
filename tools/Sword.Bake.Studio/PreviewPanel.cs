using Sword.Engine.Ui;

namespace Sword.Bake.Studio;

/// <summary>
/// Вкладка «Просмотр» единицы сборки и ассета контента. Пока пустая: предпросмотры обработчиков (<see cref="IAssetPreview"/>
/// — анимация, меш, текстура, шрифт, оружие) и 3D-сцена переносятся отдельно; интерфейс панели для видов уже тот.
/// </summary>
internal sealed class PreviewPanel : IDisposable
{
    /// <summary>Панель забирает ↑↓ (проигрыватель клипов) — дереву их не отдавать.</summary>
    public bool TakesVerticalKeys => false;

    public void Draw(UiContext ui, UiRect r, StudioApp app, AssetManifest asset) => Empty(ui, r);

    public void DrawContent(UiContext ui, UiRect r, StudioApp app, ContentAsset content) => Empty(ui, r);

    private static void Empty(UiContext ui, UiRect r) =>
        ui.Label(r.SplitTop(ui.S(28), out _), "нет предпросмотра", ui.Theme.TextDim, UiAlign.Centre);

    public void Dispose()
    {
    }
}
