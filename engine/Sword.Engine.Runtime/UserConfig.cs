using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sword.Engine.Runtime;

/// <summary>
/// Файл пользователя <c>sword.user.json</c> (слой конфига вне git, раздел 7 плана): запись и чтение одной секции — ручки панели
/// F3 (точка 6е: «сохранить», «загрузить»). Запись бережная: файл переписывается по лексемам, меняются только заданные ключи
/// секции — другие секции, другие ключи этой секции (например, <c>combat.playerWeapon</c>), порядок и комментарии остаются
/// (комментарий — блоком <c>/* … */</c>); отступы — по два пробела, строки — CRLF, UTF-8 без BOM. Замена — через временный файл
/// рядом: оборванная запись не портит файл. Файл читается тем же разбором, что и конфиг (комментарии, запятая в конце).
/// </summary>
public static class UserConfig
{
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, AllowDuplicateProperties = false,
    };

    /// <summary>
    /// Путь <c>sword.user.json</c>: рядом с <c>sword.json</c> в каталоге <paramref name="start"/> или ближайшем выше (как его ищет
    /// <see cref="ConfigSet.Load"/>); <c>sword.json</c> нигде нет — в самом <paramref name="start"/>.
    /// </summary>
    public static string PathFrom(string start)
    {
        for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, ConfigSet.FileName))) return Path.Combine(d.FullName, ConfigSet.UserFileName);
        return Path.Combine(start, ConfigSet.UserFileName);
    }

    /// <summary>
    /// Секция <paramref name="section"/> файла (регистр имени не важен); файла или секции нет — null. Испорченный файл —
    /// <see cref="InvalidDataException"/> с путём.
    /// </summary>
    public static JsonObject? ReadSection(string path, string section)
    {
        if (!File.Exists(path)) return null;
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path), documentOptions: ReadOptions);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{path}: {e.Message}", e);
        }
        if (root is not JsonObject obj) throw new InvalidDataException($"{path}: ожидается объект секций");
        foreach (var (key, value) in obj)
            if (string.Equals(key, section, StringComparison.OrdinalIgnoreCase))
                return value as JsonObject ?? throw new InvalidDataException($"{path}: {key} — ожидается объект ключей");
        return null;
    }

    /// <summary>
    /// Записать в секцию <paramref name="section"/> файла <paramref name="path"/> ключи <paramref name="changes"/>: значение —
    /// поставить (есть ключ в другом регистре — заменить его), null — убрать ключ (значение по умолчанию). Нет файла — создаётся,
    /// нет секции — дописывается в конец; секция опустела — остаётся пустой.
    /// </summary>
    public static void WriteSection(string path, string section, IReadOnlyList<(string Key, JsonNode? Value)> changes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        byte[] source = File.Exists(path) ? File.ReadAllBytes(path) : "{}"u8.ToArray();
        ReadOnlySpan<byte> text = source;
        if (text.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) text = text[3..];
        if (text.Trim(" \t\r\n"u8).IsEmpty) text = "{}"u8;
        byte[] output;
        try
        {
            output = Rewrite(text, section, changes);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{path}: {e.Message}", e);
        }
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is not null) Directory.CreateDirectory(dir);
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, output);
        File.Move(temp, path, overwrite: true);
    }

    private static byte[] Rewrite(ReadOnlySpan<byte> text, string section, IReadOnlyList<(string Key, JsonNode? Value)> changes)
    {
        var reader = new Utf8JsonReader(text, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Allow, AllowTrailingCommas = true });
        var buffer = new MemoryStream();
        var done = new bool[changes.Count];
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = true, NewLine = "\r\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            int depth = 0;
            bool sectionSeen = false, sectionNext = false, inSection = false;
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        writer.WriteStartObject();
                        depth++;
                        if (sectionNext) inSection = true;
                        sectionNext = false;
                        break;
                    case JsonTokenType.EndObject:
                        if (inSection && depth == 2)
                        {
                            WriteRest(writer, changes, done);
                            inSection = false;
                        }
                        if (depth == 1 && !sectionSeen)
                        {
                            writer.WritePropertyName(section);
                            writer.WriteStartObject();
                            WriteRest(writer, changes, done);
                            writer.WriteEndObject();
                        }
                        writer.WriteEndObject();
                        depth--;
                        break;
                    case JsonTokenType.StartArray:
                        if (depth == 0) throw new JsonException("ожидается объект секций");   // иначе запись молча пропала бы
                        if (sectionNext) throw new JsonException($"{section}: ожидается объект ключей, а не массив");
                        writer.WriteStartArray();
                        depth++;
                        break;
                    case JsonTokenType.EndArray:
                        writer.WriteEndArray();
                        depth--;
                        break;
                    case JsonTokenType.PropertyName:
                    {
                        string name = reader.GetString()!;
                        if (depth == 1 && string.Equals(name, section, StringComparison.OrdinalIgnoreCase))
                        {
                            if (sectionSeen) throw new JsonException($"секция {section} встречается дважды");
                            sectionSeen = sectionNext = true;
                            writer.WritePropertyName(name);
                            break;
                        }
                        int change = inSection && depth == 2 ? Find(changes, name) : -1;
                        if (change < 0)
                        {
                            writer.WritePropertyName(name);
                            break;
                        }
                        reader.Skip();   // прежнее значение ключа — вместо него новое (или ничего)
                        if (changes[change].Value is { } value)
                        {
                            writer.WritePropertyName(changes[change].Key);
                            value.WriteTo(writer);
                        }
                        done[change] = true;
                        break;
                    }
                    case JsonTokenType.Comment:
                        // Блок не может содержать «*/»: такой комментарий разбивается пробелом.
                        writer.WriteCommentValue(reader.GetComment().Replace("*/", "* /", StringComparison.Ordinal));
                        break;
                    default:
                        if (depth == 0) throw new JsonException("ожидается объект секций");
                        if (sectionNext) throw new JsonException($"{section}: ожидается объект ключей");
                        WriteValue(writer, ref reader);
                        break;
                }
            }
        }
        buffer.Write("\r\n"u8);
        return buffer.ToArray();
    }

    /// <summary>
    /// Значение с тем же смыслом: число — десятичным (знаки после точки те же: 2.50 остаётся 2.50; вне десятичного — double), строка —
    /// тем же текстом (экранирование — пишущего).
    /// </summary>
    private static void WriteValue(Utf8JsonWriter writer, ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                if (reader.TryGetDecimal(out decimal m)) writer.WriteNumberValue(m);
                else writer.WriteNumberValue(reader.GetDouble());
                break;
            case JsonTokenType.String:
                writer.WriteStringValue(reader.GetString());
                break;
            case JsonTokenType.True: writer.WriteBooleanValue(true); break;
            case JsonTokenType.False: writer.WriteBooleanValue(false); break;
            case JsonTokenType.Null: writer.WriteNullValue(); break;
            default: throw new JsonException($"неожиданная лексема {reader.TokenType}");
        }
    }

    private static int Find(IReadOnlyList<(string Key, JsonNode? Value)> changes, string name)
    {
        for (int i = 0; i < changes.Count; i++)
            if (string.Equals(changes[i].Key, name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>Ключи, которых в секции не было, — в её конец.</summary>
    private static void WriteRest(Utf8JsonWriter writer, IReadOnlyList<(string Key, JsonNode? Value)> changes, bool[] done)
    {
        for (int i = 0; i < changes.Count; i++)
        {
            if (done[i]) continue;
            done[i] = true;
            if (changes[i].Value is not { } value) continue;
            writer.WritePropertyName(changes[i].Key);
            value.WriteTo(writer);
        }
    }

    /// <summary>Значения секции для ручек: число — число, флаг — 1 или 0; прочее (строки, объекты) — пропускается.</summary>
    public static Dictionary<string, double> Numbers(JsonObject? section)
    {
        var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (section is null) return values;
        foreach (var (key, node) in section)
        {
            if (node is not JsonValue v) continue;
            switch (v.GetValueKind())
            {
                case JsonValueKind.Number: values[key] = v.GetValue<double>(); break;
                case JsonValueKind.True: values[key] = 1; break;
                case JsonValueKind.False: values[key] = 0; break;
            }
        }
        return values;
    }
}
