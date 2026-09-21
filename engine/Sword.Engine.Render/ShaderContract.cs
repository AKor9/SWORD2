using System.Globalization;
using System.Text;

namespace Sword.Engine.Render;

/// <summary>
/// Преамбула <c>#define</c>, которую <see cref="ShaderLibrary"/> подклеивает
/// к КАЖДОМУ шейдеру перед компиляцией.
///
/// Нужна затем, чтобы число, которое обязаны знать обе стороны, было объявлено
/// ОДИН раз — в C#, — а шейдер получал его готовым. Раньше такие числа жили
/// парами: <c>#define BODY_MAX 256</c> в заголовке и <c>public const int
/// BodyMax = 256</c> рядом с буфером, связанные только комментарием «обязано
/// совпадать». Комментарий не компилируется, поэтому пары расходились молча:
/// поднять одну сторону значило либо оставить нечищеный хвост, либо писать
/// за границу выделения — и ни ошибки, ни предупреждения нигде.
///
/// Подстановка текстовая и идёт ПЕРЕД раскрытием включений, поэтому имя видно
/// всем заголовкам и годится всюду, где годится литерал: в <c>[numthreads]</c>,
/// в <c>#if</c>, в размере массива, в <c>groupshared</c>.
///
/// Значения вклеиваются в текст исходника, а не передаются через <c>-D</c>:
/// так они видны в сообщении об ошибке компилятора, а не только в ключе кеша.
///
/// Второе объявление того же имени в самом HLSL — ошибка, и её ловит
/// <see cref="ShaderLibrary"/>. Именно это делает единственность объявления
/// свойством сборки, а не соглашением: вернуть <c>#define BODY_MAX</c> в
/// заголовок нельзя — шейдер перестанет компилироваться.
/// </summary>
public sealed class ShaderContract
{
    private readonly List<Entry> _entries = [];
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);

    private readonly record struct Entry(string Name, string Value, string? Note);

    /// <summary>Имена, объявлять которые в HLSL запрещено.</summary>
    public IReadOnlyCollection<string> Names => _names;

    /// <summary>
    /// Заголовок раздела: одна строка комментария в преамбуле. Нужна только
    /// человеку, который читает преамбулу в сообщении об ошибке.
    /// </summary>
    public ShaderContract Section(string title)
    {
        _entries.Add(new Entry(string.Empty, string.Empty, title));
        return this;
    }

    public ShaderContract Add(string name, int value, string? note = null)
        => AddRaw(name, value.ToString(CultureInfo.InvariantCulture), note);

    public ShaderContract Add(string name, uint value, string? note = null)
        => AddRaw(name, value.ToString(CultureInfo.InvariantCulture) + "u", note);

    /// <summary>
    /// Число с плавающей точкой. Точка ставится ВСЕГДА: <c>10000</c> в HLSL —
    /// целое, и выражение вроде <c>x / UNITS_PER_AU</c> молча стало бы
    /// целочисленным делением.
    /// </summary>
    public ShaderContract Add(string name, float value, string? note = null)
        => AddRaw(name, FormatFloat(value), note);

    /// <summary>
    /// Размер структуры <typeparamref name="T"/> в C#, байт. Шейдер сверяет с ним свою структуру той же раскладки:
    /// <c>_Static_assert(sizeof(SkinVertex) == SKIN_VERTEX_SIZE, "…")</c> — расхождение роняет компиляцию шейдера,
    /// а не превращает данные в мусор молча. Так сверяются обе стороны (в SWORD — только C# с числом в коде,
    /// <c>SimLayout.Verify</c>). <c>sizeof</c> в HLSL — плотная раскладка: для структур константных буферов поля
    /// держатся строками по 16 байт, иначе упаковка cbuffer разойдётся с ней незаметно для проверки.
    /// </summary>
    public ShaderContract AddSize<T>(string name, string? note = null) where T : unmanaged
        => Add(name, System.Runtime.CompilerServices.Unsafe.SizeOf<T>(), note ?? $"sizeof({typeof(T).Name}) в C#, байт");

    /// <summary>
    /// Имя корневой константы: <c>AddSlot("gAccum", 10)</c> даёт
    /// <c>#define gAccum gPass10</c>.
    ///
    /// Слот отличается от прочих чисел контракта тем, что шейдеру нужно не само
    /// число, а ИМЯ объявленного в <c>cbuffer PassConstants</c> скаляра: скаляры
    /// пакуются по четыре на регистр, и заменить двадцать имён массивом значило бы
    /// раздуть корневые константы вчетверо (см. pass.hlsli). Собрать это имя
    /// из числа умеет только текстовая подстановка, а склеить <c>gPass##N</c>
    /// внутри HLSL нечем — препроцессор шейдера склейку в этом виде не выполняет.
    /// Поэтому имя собирает C#, и объявление слота остаётся ровно одно — здесь.
    ///
    /// Заодно проверяется, что слот вообще существует. Корневая константа за
    /// <see cref="RootLayout.ConstantCount"/> — это запись мимо корневой сигнатуры:
    /// ни компилятор, ни слой отладки о ней не скажут, шейдер просто прочтёт мусор.
    /// </summary>
    public ShaderContract AddSlot(string name, int slot, string? note = null)
    {
        if (slot < 0 || slot >= RootLayout.ConstantCount)
            throw new ArgumentOutOfRangeException(
                nameof(slot), slot,
                $"Контракт шейдеров: имя {name} просит корневую константу {slot}, " +
                $"а их всего {RootLayout.ConstantCount} " +
                "(RootLayout.ConstantCount).");

        return AddRaw(name, "gPass" + slot.ToString(CultureInfo.InvariantCulture), note);
    }

    /// <summary>Готовый текст подстановки. Для выражений и шестнадцатеричных литералов.</summary>
    public ShaderContract AddRaw(string name, string text, string? note = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!_names.Add(name))
            throw new InvalidOperationException(
                $"Контракт шейдеров: имя {name} объявлено дважды в C#. " +
                "Преамбула собирается из всех проектов сразу, поэтому имена в ней общие.");

        _entries.Add(new Entry(name, text, note));
        return this;
    }

    /// <summary>
    /// Литерал с плавающей точкой в том виде, в каком его поймёт HLSL.
    /// «R» даёт кратчайшую запись, которая читается обратно в то же число, —
    /// это важно: константа, вклеенная с потерей, сделала бы GPU и CPU
    /// разными ровно там, где преамбула обязана делать их одинаковыми.
    /// </summary>
    private static string FormatFloat(float value)
    {
        string text = value.ToString("R", CultureInfo.InvariantCulture);
        bool hasPoint = text.Contains('.') || text.Contains('e') || text.Contains('E');
        return hasPoint ? text : text + ".0";
    }

    /// <summary>Текст преамбулы. Завершается переводом строки.</summary>
    public string Build()
    {
        var sb = new StringBuilder(_entries.Count * 48);
        sb.AppendLine("// ─── контракт: объявлено в C#, см. ShaderContract ───────────────────────────");
        sb.AppendLine("// Правка здесь бесполезна: текст собирается из констант C# при компиляции.");

        foreach (var (name, value, note) in _entries)
        {
            if (name.Length == 0)
            {
                sb.AppendLine();
                sb.Append("// ── ").Append(note).AppendLine();
                continue;
            }

            sb.Append("#define ").Append(name).Append(' ').Append(value);
            if (!string.IsNullOrEmpty(note)) sb.Append("   // ").Append(note);
            sb.AppendLine();
        }

        sb.AppendLine();
        return sb.ToString();
    }
}
