using System.Numerics;

namespace Sword.Engine.Ui.Tests;

/// <summary>
/// Кадры виджетов в тесте: ввод кадра (<see cref="UiInput"/>) заполняет тест так же, как клиент — адаптер окна;
/// <see cref="Frame"/> — BeginFrame → виджеты → EndFrame, затем события кадра сбрасываются, зажатые кнопки остаются,
/// время идёт на 1/60 с.
/// </summary>
internal sealed class UiDriver(UiFonts? fonts = null, int width = 200, int height = 200)
{
    public UiContext Ui { get; } = new(fonts);
    public UiInput Input { get; } = new() { MouseInside = true };
    public int Width { get; } = width;
    public int Height { get; } = height;

    public void Frame(Action<UiContext> widgets)
    {
        Ui.BeginFrame(Input, Width, Height, 1);
        widgets(Ui);
        Ui.EndFrame();
        Input.BeginFrame();
        Input.Time += 1 / 60.0;
    }

    public UiDriver Move(float x, float y)
    {
        Input.Mouse = new Vector2(x, y);
        return this;
    }

    /// <summary>Нажать левую кнопку в точке (на следующем кадре).</summary>
    public UiDriver Press(float x, float y, int button = 0)
    {
        Move(x, y);
        Input.SetMouseButton(button, down: true, pressed: true, released: false);
        return this;
    }

    /// <summary>Отпустить кнопку в точке (на следующем кадре).</summary>
    public UiDriver Release(float x, float y, int button = 0)
    {
        Move(x, y);
        Input.SetMouseButton(button, down: false, pressed: false, released: true);
        return this;
    }

    /// <summary>Щелчок: кадр с нажатием и кадр с отпусканием в той же точке.</summary>
    public void Click(float x, float y, Action<UiContext> widgets)
    {
        Press(x, y).Frame(widgets);
        Release(x, y).Frame(widgets);
    }
}
