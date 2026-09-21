using System.Text;
using Sword.Engine.Diagnostics;
using Vortice.Dxc;

namespace Sword.Engine.Rhi;

public sealed record ShaderKey(string File, string Entry, DxcShaderStage Stage, string? Defines = null);

/// <summary>
/// Компиляция HLSL в DXIL в рантайме плюс горячая перезагрузка.
///
/// Директивы <c>#include</c> раскрываются текстуально своими силами, а не через
/// <c>IDxcIncludeHandler</c>. Это не упрощение, а требование: перекомпилировать
/// шейдер надо при изменении любого из его заголовков, а значит список файлов,
/// от которых он зависит, всё равно нужно знать. Раскрывая включения самому,
/// получаешь этот список бесплатно и точным.
/// </summary>
public sealed class ShaderLibrary
{
    private readonly string _root;
    private readonly Dictionary<ShaderKey, CompiledShader> _cache = [];

    /// <summary>
    /// Преамбула контракта: <c>#define</c> для всех чисел, объявленных в C#
    /// и обязанных быть теми же в шейдере. Подклеивается к каждому шейдеру
    /// перед компиляцией (собирает <c>ShaderContract</c> в Render).
    /// </summary>
    private readonly string _preamble;

    /// <summary>Имена из преамбулы. Объявлять их в HLSL запрещено.</summary>
    private readonly HashSet<string> _contractNames;

    /// <summary>
    /// Строгий IEEE обязателен для шейдеров симуляции: без него DXC волен свернуть
    /// умножение со сложением в FMA, а это другое округление и другой результат.
    /// Промежуточная арифметика в стероидах steering идёт во float, состояние
    /// округляется обратно в Q22.10 каждый тик, и повторяемость держится именно
    /// на том, что операции не переставляются между сборками.
    /// </summary>
    private static readonly DxcCompilerOptions SimOptions = new()
    {
        ShaderModel = DxcShaderModel.Model6_6,
        IEEEStrictness = true,
        EnableStrictness = true,
        Enable16bitTypes = true,
        OptimizationLevel = 3,
        AllResourcesBound = true,
    };

    private static readonly DxcCompilerOptions RenderOptions = new()
    {
        ShaderModel = DxcShaderModel.Model6_6,
        EnableStrictness = true,
        Enable16bitTypes = true,
        OptimizationLevel = 3,
        AllResourcesBound = true,
    };

    /// <summary>
    /// Компилирует шейдер, заданный строкой прямо в коде. Нужен для того, что
    /// не является частью игры и не подлежит правке на ходу, — сейчас это только
    /// бэкенд интерфейса, чей шейдер зафиксирован форматом вершин Dear ImGui.
    /// </summary>
    public static ReadOnlyMemory<byte> CompileInline(string source, string entry, DxcShaderStage stage, string name)
    {
        using var result = DxcCompiler.Compile(stage, source, entry, RenderOptions, name);

        if (result.GetStatus().Failure)
            throw new InvalidOperationException($"Ошибка компиляции {name}:{entry}\n{result.GetErrors()}");

        return result.GetObjectBytecodeMemory();
    }

    /// <param name="preamble">Текст преамбулы контракта (<c>#define</c>), подклеивается к каждому шейдеру.</param>
    /// <param name="contractNames">Имена из преамбулы: объявлять их в HLSL запрещено.</param>
    public ShaderLibrary(string shaderRoot, string preamble = "", IReadOnlyCollection<string>? contractNames = null)
    {
        _root = Path.GetFullPath(shaderRoot);
        if (!Directory.Exists(_root))
            throw new DirectoryNotFoundException($"Каталог шейдеров не найден: {_root}");

        _preamble = preamble;
        _contractNames = new HashSet<string>(contractNames ?? [], StringComparer.Ordinal);
    }

    public sealed class CompiledShader
    {
        public required ReadOnlyMemory<byte> Bytecode { get; init; }
        public required IReadOnlyList<string> Dependencies { get; init; }
        public required DateTime CompiledAt { get; init; }
    }

    public CompiledShader Get(ShaderKey key, bool simulation)
    {
        if (_cache.TryGetValue(key, out var cached)) return cached;
        var compiled = Compile(key, simulation);
        _cache[key] = compiled;
        return compiled;
    }

    /// <summary>Возвращает ключи шейдеров, чьи исходники изменились с момента компиляции.</summary>
    public List<ShaderKey> FindStale()
    {
        var stale = new List<ShaderKey>();
        foreach (var (key, shader) in _cache)
        {
            foreach (var dep in shader.Dependencies)
            {
                if (!File.Exists(dep) || File.GetLastWriteTimeUtc(dep) > shader.CompiledAt)
                {
                    stale.Add(key);
                    break;
                }
            }
        }
        return stale;
    }

    public void Invalidate(ShaderKey key) => _cache.Remove(key);

    private CompiledShader Compile(ShaderKey key, bool simulation)
    {
        // Метка снимается ДО чтения исходников. Снятая после компиляции она
        // была моложе файла, сохранённого во время самой компиляции (по F5
        // это сотни миллисекунд на десятки конвейеров), и такая правка
        // не считалась изменившейся до следующего сохранения.
        DateTime compiledAt = DateTime.UtcNow;

        string path = Path.Combine(_root, key.File);
        var dependencies = new List<string>();
        string body = ExpandIncludes(path, dependencies, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        CheckContractNotRedefined(key, body);

        // Тело начинается с собственной «#line 1 "файл"» (см. ExpandIncludes),
        // и она же гасит сдвиг, который вносит преамбула: без неё DXC считал
        // бы строки от начала преамбулы.
        string source = _preamble + body;

        int b0 = CountB0Declarations(source);
        if (b0 > 1)
            throw new InvalidOperationException(
                $"{key.File}:{key.Entry} — объявлений register(b0): {b0}. " +
                "Общие заголовки не имеют права занимать b0; вероятно, включён чужой common.hlsli.");

        DxcDefine[]? defines = null;
        if (!string.IsNullOrEmpty(key.Defines))
        {
            defines = key.Defines.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(d =>
                {
                    int eq = d.IndexOf('=');
                    return eq < 0
                        ? new DxcDefine { Name = d, Value = "1" }
                        : new DxcDefine { Name = d[..eq], Value = d[(eq + 1)..] };
                })
                .ToArray();
        }

        var options = simulation ? SimOptions : RenderOptions;
        using var result = DxcCompiler.Compile(key.Stage, source, key.Entry, options, key.File, defines);

        string errors = result.GetErrors();
        if (result.GetStatus().Failure)
            throw new InvalidOperationException($"Ошибка компиляции {key.File}:{key.Entry}\n{errors}");

        if (!string.IsNullOrWhiteSpace(errors))
            Log.Warning("shader", $"{key.File}:{key.Entry}\n{errors}");

        return new CompiledShader
        {
            Bytecode = result.GetObjectBytecodeMemory(),
            Dependencies = dependencies,
            CompiledAt = compiledAt,
        };
    }

    /// <summary>
    /// Ловит попытку объявить в HLSL имя, которое уже пришло из контракта.
    ///
    /// Это и есть то, чем единственность объявления держится. Пары «число
    /// в C# и то же число в заголовке» связывались комментарием «обязано
    /// совпадать» и расходились молча — комментарий не компилируется.
    /// Теперь вторая половина пары не просто не нужна: её появление
    /// останавливает сборку с указанием, где живёт первая.
    ///
    /// Проверка идёт по РАСКРЫТОМУ тексту, поэтому находит объявление в любом
    /// из включённых заголовков, а не только в самом шейдере.
    /// </summary>
    private void CheckContractNotRedefined(ShaderKey key, string expanded)
    {
        if (_contractNames.Count == 0) return;

        foreach (var line in expanded.AsSpan().EnumerateLines())
        {
            var code = line.TrimStart();
            if (!code.StartsWith("#define", StringComparison.Ordinal)) continue;

            code = code["#define".Length..].TrimStart();
            int end = 0;
            while (end < code.Length && (char.IsLetterOrDigit(code[end]) || code[end] == '_')) end++;
            if (end == 0) continue;

            string name = code[..end].ToString();
            if (_contractNames.Contains(name))
                throw new InvalidOperationException(
                    $"{key.File}:{key.Entry} — HLSL объявляет {name}, которое уже приходит " +
                    "из контракта (ShaderContract, собирается из констант C#). " +
                    "Это ровно та пара объявлений, которую контракт и убирает: удалите #define, " +
                    "имя уже видно.");
        }
    }

    /// <summary>
    /// Сколько раз шейдер объявляет <c>register(b0)</c>.
    ///
    /// Регистр <c>b0</c> занимает корневой CBV — голый адрес без типа, куда
    /// каждая сцена кладёт свою структуру. Два объявления в одном шейдере
    /// означают включённый чужой <c>common.hlsli</c>: компилятор возьмёт первое
    /// и не скажет ни слова, слой отладки тоже, а шейдер будет читать свои
    /// константы как чужие.
    ///
    /// Комментарии отбрасываются, и это не педантизм: первая же версия проверки
    /// споткнулась о собственное объяснение — фраза «считает объявления
    /// register(b0)» в шапке заголовка системы засчиталась за объявление.
    /// Проверка, которую ловит прозаический текст, ловит не то, что ищет.
    /// Отбрасываются ОБА вида, и блочные тоже: построчный разбор видел
    /// только «//», и та же фраза в «/* … */» считалась бы снова.
    /// </summary>
    private static int CountB0Declarations(string source)
    {
        var code = new StringBuilder(source.Length);
        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            char next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                code.Append('\n');
                continue;
            }

            if (c == '/' && next == '*')
            {
                int end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? source.Length : end + 1;
                code.Append(' ');
                continue;
            }

            code.Append(c);
        }

        return System.Text.RegularExpressions.Regex.Count(code.ToString(), @"register\s*\(\s*b0\s*\)");
    }

    /// <summary>
    /// Рекурсивно вклеивает <c>#include "..."</c>. Повторное включение одного файла
    /// пропускается, поэтому защита от двойного включения в самих .hlsli не нужна.
    ///
    /// Каждый файл открывается директивой <c>#line 1 "имя"</c>, а после
    /// включения ставится <c>#line N "имя"</c> обратно на следующую строку
    /// включающего. Без этого ошибка внутри заголовка называла главный файл
    /// и номер строки в СКЛЕЕННОМ тексте — искать её приходилось глазами.
    /// Раз раскрытие своё, точные координаты достаются бесплатно.
    /// </summary>
    private string ExpandIncludes(string path, List<string> dependencies, HashSet<string> visited)
    {
        path = Path.GetFullPath(path);
        if (!visited.Add(path)) return string.Empty;
        if (!File.Exists(path)) throw new FileNotFoundException($"Шейдер не найден: {path}");

        dependencies.Add(path);
        string? dir = Path.GetDirectoryName(path);
        var sb = new StringBuilder();

        // Имя относительно корня и с прямыми косыми: обратную DXC читает
        // в строке директивы как экранирование.
        string lineName = Path.GetRelativePath(_root, path).Replace('\\', '/');
        sb.Append("#line 1 \"").Append(lineName).AppendLine("\"");

        int lineNo = 0;
        foreach (string line in File.ReadLines(path))
        {
            lineNo++;
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("#include", StringComparison.Ordinal))
            {
                int first = trimmed.IndexOf('"');
                int last = trimmed.LastIndexOf('"');
                if (first >= 0 && last > first)
                {
                    string included = trimmed.Substring(first + 1, last - first - 1);
                    string resolved = Path.Combine(dir ?? _root, included);
                    if (!File.Exists(resolved)) resolved = Path.Combine(_root, included);
                    sb.Append(ExpandIncludes(resolved, dependencies, visited));
                    sb.Append("#line ").Append(lineNo + 1).Append(" \"").Append(lineName).AppendLine("\"");
                    continue;
                }
            }
            sb.AppendLine(line);
        }

        return sb.ToString();
    }
}
