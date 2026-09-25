namespace Sword.Engine.Animation.Motion;

/// <summary>
/// Контакт сустава стопы с землёй без разметки из экспорта (точка 8б; поднят с этапа 9 — нужен базе): сустав низко и почти
/// не движется по земле. Гистерезис по обоим порогам (<see cref="ContactParams"/>), короткие внутренние серии — шум
/// (переворачиваются). Высота считается от самой низкой точки сустава в клипе («пол» клипа). Общий для базы Motion Matching и,
/// позже, фиксации стоп (этап 9).
/// </summary>
public static class ContactDetector
{
    /// <summary>
    /// Контакты одного сустава по кадрам клипа — в <paramref name="contact"/> (той же длины).
    /// </summary>
    /// <param name="height">[кадр] высота сустава, м (в системе персонажа; «пол» — минимум по клипу).</param>
    /// <param name="speed">[кадр] горизонтальная скорость сустава, м/с.</param>
    /// <exception cref="ArgumentException">Длины не совпадают или пороги противоречивы (выход ниже входа, серия меньше 1).</exception>
    public static void Detect(ReadOnlySpan<float> height, ReadOnlySpan<float> speed, in ContactParams parameters, Span<bool> contact) =>
        throw new NotImplementedException("8б: детектор контактов — ContactDetector.Detect");
}
