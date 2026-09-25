namespace Sword.Bake;

/// <summary>
/// Сравнитель формата для <c>sword-bake diff</c> (<see cref="PakDiff"/>): равны ли по содержимому два файла с одним путём
/// в двух паках, у которых разные хеши в оглавлении. Ядро форматов не знает: сравнитель приходит из плагина проекта и
/// находится рефлексией, как обработчики и команды (неабстрактный класс с конструктором без параметров); выбирается по
/// расширению пути. Нет сравнителя — файлы «отличаются байтами».
/// </summary>
/// <remarks>
/// «Равны» — различие только в том, что по устройству формата не значимо для сверки (у <c>.swclip</c> — поле
/// <c>SourceHash</c>: в него входят сборки конвертера); всё остальное совпадает точно. Сравнитель читает только переданные
/// байты, ничего не пишет и не хранит состояния между вызовами; вызовы идут по одному.
/// </remarks>
public interface IContentComparer
{
    /// <summary>Расширения путей пака с точкой: «.swclip». Без учёта регистра; у расширения — один сравнитель на проект.</summary>
    IReadOnlyList<string> Extensions { get; }

    /// <summary>Для людей и итога diff: «клип: поля без SourceHash».</summary>
    string Title { get; }

    /// <summary>Сравнить файл <paramref name="path"/> пака A (<paramref name="a"/>) с тем же файлом пака B (<paramref name="b"/>).</summary>
    /// <param name="path">Путь в паке («anim/katana/Idle.swclip») — для текста отличия.</param>
    /// <exception cref="InvalidDataException">Файл не разбирается: diff покажет расхождение с текстом ошибки.</exception>
    ContentComparison Compare(string path, byte[] a, byte[] b);
}

/// <summary>Итог сравнителя: равны ли по содержимому и пояснение — что не сравнивалось (равны) или чем отличаются.</summary>
public readonly record struct ContentComparison(bool Equal, string Detail)
{
    /// <param name="note">Что не сравнивалось: «без SourceHash».</param>
    public static ContentComparison Same(string note = "") => new(true, note);

    /// <param name="detail">Чем отличаются, первым отличием: «повороты: кадр 12, кость 3».</param>
    public static ContentComparison Differs(string detail) => new(false, detail);
}
