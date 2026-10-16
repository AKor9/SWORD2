using System.Globalization;
using System.Text;

namespace Sword.Engine.Render;

/// <summary>
/// Преамбула <c>#define</c>, которую <see cref="ShaderLibrary"/> подклеивает к каждому шейдеру до раскрытия включений:
/// число, нужное обеим сторонам, объявляется один раз в C#. Повторное объявление имени в HLSL — ошибка сборки.
/// </summary>
public sealed class ShaderContract
{
    private readonly List<Entry> _entries = [];
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);

    private readonly record struct Entry(string Name, string Value, string? Note);

    /// <summary>Имена, объявлять которые в HLSL запрещено.</summary>
    public IReadOnlyCollection<string> Names => _names;

    /// <summary>Заголовок раздела — строка комментария в преамбуле.</summary>
    public ShaderContract Section(string title)
    {
        _entries.Add(new Entry(string.Empty, string.Empty, title));
        return this;
    }

    public ShaderContract Add(string name, int value, string? note = null)
        => AddRaw(name, value.ToString(CultureInfo.InvariantCulture), note);

    public ShaderContract Add(string name, uint value, string? note = null)
        => AddRaw(name, value.ToString(CultureInfo.InvariantCulture) + "u", note);

    /// <summary>Число с плавающей точкой; точка ставится всегда, иначе <c>x / N</c> в HLSL стало бы целочисленным.</summary>
    public ShaderContract Add(string name, float value, string? note = null)
        => AddRaw(name, FormatFloat(value), note);

    /// <summary>
    /// Размер <typeparamref name="T"/> в C#, байт: шейдер сверяет свою структуру через <c>_Static_assert(sizeof(…) == …)</c>.
    /// <c>sizeof</c> в HLSL — плотная раскладка, поэтому у структур константных буферов поля держатся строками по 16 байт.
    /// </summary>
    public ShaderContract AddSize<T>(string name, string? note = null) where T : unmanaged
        => Add(name, System.Runtime.CompilerServices.Unsafe.SizeOf<T>(), note ?? $"sizeof({typeof(T).Name}) в C#, байт");

    /// <summary>
    /// Имя корневой константы: <c>AddSlot("gAccum", 10)</c> даёт <c>#define gAccum gPass10</c> (склеить <c>gPass##N</c>
    /// препроцессор HLSL не умеет). Слот за <see cref="RootLayout.ConstantCount"/> — ошибка: запись мимо корневой сигнатуры.
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

    /// <summary>Литерал для HLSL: «R» — кратчайшая запись без потери, чтобы GPU и CPU видели одно число.</summary>
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
