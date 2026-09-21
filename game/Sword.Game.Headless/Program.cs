namespace Sword.Game.Headless;

/// <summary>
/// Симуляция SWORD без окна и D3D12. Тики, сценарии и агентский доступ по stdio появятся в прототипе — этап 1
/// ARCHITECTURE_PLAN.md; пока приложение только сообщает, что каркас собран.
/// </summary>
public static class Program
{
    public static int Main()
    {
        Console.WriteLine("SWORD2 Headless: каркас (этап 0). Симуляция — этап 1, см. ARCHITECTURE_PLAN.md.");
        return 0;
    }
}
