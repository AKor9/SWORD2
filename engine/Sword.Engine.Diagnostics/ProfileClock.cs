namespace Sword.Engine.Diagnostics;

/// <summary>Поддельные часы профайлера для тестов: итоги не зависят от нагрузки машины. У продукта часов нет — профайлер
/// читает <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>.</summary>
internal abstract class ProfileClock
{
    /// <summary>Сейчас — отсчёты <see cref="System.Diagnostics.Stopwatch"/>.</summary>
    public abstract long Timestamp();
}
