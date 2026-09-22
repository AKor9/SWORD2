using System.Reflection;

namespace Sword.Architecture.Tests;

/// <summary>
/// Публичная поверхность сборок, через которые игра работает с движком: ни одна публичная или защищённая сигнатура не
/// выставляет типы нижних слоёв. Матрица ссылок (<see cref="ArchitectureRules"/>) этого не видит: Render вправе
/// ссылаться на Rhi, но не вправе отдавать его типы наружу — иначе игра обращается к Rhi в обход Render.
/// </summary>
public sealed class PublicSurfaceTests
{
    private static readonly string Root = SolutionModel.FindRepoRoot();

    /// <summary>Сборка → префиксы имён сборок, чьи типы не должны встречаться в её публичных сигнатурах.</summary>
    public static readonly Dictionary<string, string[]> Hidden = new()
    {
        ["Sword.Engine.Render"] = ["Vortice.", "Sword.Engine.Rhi", "Sword.Engine.Formats", "Sword.Engine.Content"],
        ["Sword.Engine.Client"] = ["Vortice.", "Sword.Engine.Rhi", "Sword.Engine.Formats", "Sword.Engine.Content"],
    };

    [Fact]
    public void GameFacingAssemblies_HideLowerLayers()
    {
        var projects = SolutionModel.Load(Root).ToDictionary(p => p.Name);
        string configuration = SolutionModel.TestConfiguration();
        // Сборки берутся из выхода игры: библиотека не копирует к себе зависимости (Vortice), exe — копирует все,
        // и LoadFrom находит их рядом.
        string? app = SolutionModel.BuiltAssembly(Root, projects["Sword.Game.App"], configuration);
        Assert.True(app is not null, $"нет собранной игры — соберите решение ({configuration})");
        var problems = new List<string>();
        foreach (var (name, hidden) in Hidden)
        {
            string dll = Path.Combine(Path.GetDirectoryName(app)!, name + ".dll");
            if (!File.Exists(dll))
            {
                problems.Add($"{name}: нет {dll}");
                continue;
            }
            var assembly = Assembly.LoadFrom(dll);
            foreach (var (member, type) in PublicSignatureTypes(assembly))
            {
                string owner = type.Assembly.GetName().Name ?? "";
                if (hidden.Any(h => owner.StartsWith(h, StringComparison.Ordinal)))
                    problems.Add($"{name}: {member} выставляет {type.FullName} из {owner}");
            }
        }
        Assert.True(problems.Count == 0, "Публичная поверхность раскрывает нижние слои:\n" + string.Join("\n", problems.Distinct()));
    }

    /// <summary>Типы ECS, через которые мир меняется: их не должно быть среди достижимых из доступа клиента.</summary>
    public static readonly string[] MutatingEcs =
    [
        "Sword.Engine.Ecs.World", "Sword.Engine.Ecs.CommandBuffer", "Sword.Engine.Ecs.QueryBuilder", "Sword.Engine.Ecs.Query",
        "Sword.Engine.Ecs.QueryIterator", "Sword.Engine.Ecs.ChunkView", "Sword.Engine.Ecs.SystemContext",
        "Sword.Engine.Ecs.WorldBuilder", "Sword.Engine.Runtime.Simulation",
    ];

    [Fact]
    public void SimulationLink_ReachesOnlyReadOnlyWorld()
    {
        // Клиентская часть игры видит симуляцию только через SimulationLink: всё, что из него достижимо по публичным
        // сигнатурам (транзитивно, по типам движка), не должно давать изменить мир мимо команд тика.
        var projects = SolutionModel.Load(Root).ToDictionary(p => p.Name);
        string? app = SolutionModel.BuiltAssembly(Root, projects["Sword.Game.App"], SolutionModel.TestConfiguration());
        Assert.True(app is not null, "нет собранной игры — соберите решение");
        var client = Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(app)!, "Sword.Engine.Client.dll"));
        var link = client.GetType("Sword.Engine.Client.SimulationLink", throwOnError: true)!;

        var seen = new HashSet<Type>();
        var queue = new Queue<Type>([link]);
        var problems = new List<string>();
        while (queue.Count > 0)
        {
            var type = queue.Dequeue();
            if (!seen.Add(type)) continue;
            // Обобщённый параметр обходчик сигнатур не разворачивает: T подставляет вызывающий. Подставить можно только
            // чистые данные — иначе копия структуры со ссылкой (на общий список) давала бы изменить симуляцию.
            // Исключение — вид только для чтения (IReadView): его интерфейсы проверяет ReadViews_OnlyRead.
            foreach (var (member, parameter) in GenericParameters(type))
                if (!IsUnmanaged(parameter) && !IsReadView(parameter))
                    problems.Add($"{member}<{parameter.Name}> — параметр не unmanaged: через него отдаются ссылки");
            foreach (var (member, t) in SignatureTypes(type))
            {
                var open = t.IsGenericType ? t.GetGenericTypeDefinition() : t;
                if (MutatingEcs.Contains(open.FullName))
                    problems.Add($"{member} → {open.FullName}");
                else if (open == typeof(Span<>))
                    problems.Add($"{member} → Span<T> (запись на месте)");
                else if (open.Assembly.GetName().Name?.StartsWith("Sword.Engine.", StringComparison.Ordinal) == true)
                    queue.Enqueue(open);
            }
        }
        Assert.True(problems.Count == 0, "Из SimulationLink достижимо изменение мира:\n" + string.Join("\n", problems.Distinct()));
        Assert.Contains(seen, t => t.FullName == "Sword.Engine.Ecs.ReadChunkView");   // обход действительно дошёл до чанков
    }

    private const string ReadViewName = "Sword.Engine.Ecs.IReadView";

    /// <summary>Параметр ограничен видом только для чтения (<c>where T : IReadView</c>).</summary>
    public static bool IsReadView(Type parameter) =>
        parameter.GetGenericParameterConstraints().Any(c => c.FullName == ReadViewName);

    /// <summary>
    /// Каждый вид только для чтения в сборках продукта (интерфейс, наследующий <c>IReadView</c>) только читает: иначе через
    /// <c>WorldReader.View</c> клиент получил бы путь изменить симуляцию мимо команд тика.
    /// </summary>
    [Fact]
    public void ReadViews_OnlyRead()
    {
        var projects = SolutionModel.Load(Root).ToDictionary(p => p.Name);
        string? app = SolutionModel.BuiltAssembly(Root, projects["Sword.Game.App"], SolutionModel.TestConfiguration());
        Assert.True(app is not null, "нет собранной игры — соберите решение");
        var views = Directory.GetFiles(Path.GetDirectoryName(app)!, "Sword.*.dll")
            .Select(Assembly.LoadFrom)
            .SelectMany(a => a.GetExportedTypes())
            .Where(t => t.IsInterface && t.FullName != ReadViewName && t.GetInterfaces().Any(i => i.FullName == ReadViewName))
            .ToList();
        Assert.Contains(views, v => v.FullName == "Sword.Game.Contracts.IAnimationView");   // обход нашёл виды
        var problems = views.SelectMany(ReadViewProblems).ToList();
        Assert.True(problems.Count == 0, "Вид только для чтения даёт изменить данные:\n" + string.Join("\n", problems));
    }

    /// <summary>Что в интерфейсе вида даёт писать: сеттер, массив, <c>Span</c>, <c>ref</c>, ссылка на объект, изменяющий тип ECS.</summary>
    public static IEnumerable<string> ReadViewProblems(Type view)
    {
        foreach (var p in view.GetProperties())
            if (p.SetMethod is not null) yield return $"{view.Name}.{p.Name}: сеттер";
        foreach (var m in view.GetMethods().Where(m => !m.IsSpecialName || m.Name.StartsWith("get_", StringComparison.Ordinal)))
        {
            var signature = m.GetParameters().Select(p => (p.ParameterType, p.IsOut)).Prepend((m.ReturnType, false));
            foreach (var (type, isOut) in signature)
            {
                if (type.IsByRef && !isOut) { yield return $"{view.Name}.{m.Name}: ref"; continue; }
                var raw = type.IsByRef ? type.GetElementType()! : type;
                if (raw.IsArray || raw.IsPointer) { yield return $"{view.Name}.{m.Name}: {raw.Name} — массив или указатель"; continue; }
                foreach (var t in Expand(raw))
                {
                    var open = t.IsGenericType ? t.GetGenericTypeDefinition() : t;
                    if (open == typeof(Span<>)) yield return $"{view.Name}.{m.Name}: Span<T>";
                    else if (MutatingEcs.Contains(open.FullName)) yield return $"{view.Name}.{m.Name}: {open.FullName}";
                    else if (!open.IsValueType && open != typeof(string) && open != typeof(void))
                        yield return $"{view.Name}.{m.Name}: {open.Name} — ссылка на объект";
                }
            }
        }
    }

    public interface IBadView
    {
        int Count { get; set; }
        int[] Items();
        Span<int> Write();
        ref int Slot(int i);
        List<int> Shared();
    }

    public interface IGoodView
    {
        int Count { get; }
        string Name(int i);
        ReadOnlySpan<System.Numerics.Matrix4x4> Pose(int i);
        bool TryGet(int i, out ReadOnlySpan<float> values);
    }

    [Fact]
    public void ReadViewCheck_CatchesEveryWritePath()
    {
        // Сам проверяющий: без этого зелёный ReadViews_OnlyRead ничего не доказывает.
        var bad = ReadViewProblems(typeof(IBadView)).ToList();
        Assert.Contains(bad, p => p.Contains("сеттер"));
        Assert.Contains(bad, p => p.Contains("Items") && p.Contains("массив"));
        Assert.Contains(bad, p => p.Contains("Write") && p.Contains("Span"));
        Assert.Contains(bad, p => p.Contains("Slot") && p.Contains("ref"));
        Assert.Contains(bad, p => p.Contains("Shared") && p.Contains("ссылка"));
        Assert.Empty(ReadViewProblems(typeof(IGoodView)));
    }

    /// <summary>Обобщённые параметры видимых методов типа и самого типа.</summary>
    public static IEnumerable<(string Member, Type Parameter)> GenericParameters(Type type)
    {
        const BindingFlags visible = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        string at = type.FullName ?? type.Name;
        foreach (var p in type.GetGenericArguments().Where(a => a.IsGenericParameter)) yield return (at, p);
        foreach (var m in type.GetMethods(visible).Where(m => m.IsGenericMethodDefinition))
            foreach (var p in m.GetGenericArguments()) yield return ($"{at}.{m.Name}", p);
    }

    /// <summary>Ограничение <c>unmanaged</c>: компилятор помечает параметр атрибутом IsUnmanagedAttribute.</summary>
    public static bool IsUnmanaged(Type parameter) =>
        parameter.CustomAttributes.Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.IsUnmanagedAttribute");

    [Fact]
    public void GenericConstraintCheck_TellsUnmanagedFromStruct()
    {
        var found = GenericParameters(typeof(Sample)).ToDictionary(x => x.Member.Split('.')[^1], x => x.Parameter);
        Assert.True(IsUnmanaged(found["Data"]));
        Assert.False(IsUnmanaged(found["AnyStruct"]));   // структура может нести List<T>
    }

    [Fact]
    public void Walker_FindsTypesInsideGenericsArraysAndNestedTypes()
    {
        // Сам обходчик: без этой проверки зелёный тест выше ничего не доказывает.
        var found = PublicSignatureTypes(typeof(Sample).Assembly)
            .Where(x => x.Member.Contains("PublicSurfaceTests+Sample", StringComparison.Ordinal))
            .Select(x => x.Type).ToHashSet();
        Assert.Contains(typeof(Uri), found);                 // внутри Action<List<Uri[]>>
        Assert.Contains(typeof(Version), found);             // защищённый член
        Assert.Contains(typeof(DateOnly), found);            // вложенный тип
        Assert.DoesNotContain(typeof(TimeOnly), found);      // внутренний член не в счёт
    }

    public class Sample
    {
        public Action<List<Uri[]>>? Callback;
        protected Version? Protected() => null;
        internal TimeOnly Internal() => default;
        public static T Data<T>() where T : unmanaged => default;
        public static T AnyStruct<T>() where T : struct => default;

        public sealed class Nested
        {
            public DateOnly Value { get; set; }
        }
    }

    /// <summary>Типы из публичных и защищённых сигнатур всех видимых снаружи типов: член → тип (с разворотом обобщений).</summary>
    public static IEnumerable<(string Member, Type Type)> PublicSignatureTypes(Assembly assembly)
    {
        foreach (var type in assembly.GetExportedTypes())
            foreach (var x in SignatureTypes(type)) yield return x;
    }

    /// <summary>Типы из публичных и защищённых сигнатур одного типа (операторы преобразования — тоже).</summary>
    public static IEnumerable<(string Member, Type Type)> SignatureTypes(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
            | BindingFlags.DeclaredOnly;
        string at = type.FullName ?? type.Name;
        if (type.BaseType is { } baseType)
            foreach (var t in Expand(baseType)) yield return (at + " : base", t);
        foreach (var i in type.GetInterfaces())
            foreach (var t in Expand(i)) yield return (at + " : interface", t);

        foreach (var member in type.GetMembers(all))
        {
            string where = $"{at}.{member.Name}";
            IEnumerable<Type> types = member switch
            {
                FieldInfo f when f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly => [f.FieldType],
                PropertyInfo p when Visible(p.GetMethod) || Visible(p.SetMethod) =>
                    [p.PropertyType, .. p.GetIndexParameters().Select(x => x.ParameterType)],
                EventInfo e when Visible(e.AddMethod) => [e.EventHandlerType!],
                MethodInfo m when Visible(m) && (!m.IsSpecialName || m.Name.StartsWith("op_", StringComparison.Ordinal)) =>
                    [m.ReturnType, .. m.GetParameters().Select(x => x.ParameterType)],
                ConstructorInfo c when Visible(c) => c.GetParameters().Select(x => x.ParameterType),
                _ => [],
            };
            foreach (var t in types.SelectMany(Expand)) yield return (where, t);
        }
    }

    private static bool Visible(MethodBase? m) => m is not null && (m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly);

    /// <summary>Тип и всё, из чего он собран: элементы массивов, ссылок и указателей, аргументы обобщений.</summary>
    private static IEnumerable<Type> Expand(Type type)
    {
        if (type.HasElementType)
        {
            foreach (var t in Expand(type.GetElementType()!)) yield return t;
            yield break;
        }
        if (type.IsGenericParameter) yield break;
        yield return type;
        if (type.IsGenericType)
            foreach (var arg in type.GetGenericArguments())
                foreach (var t in Expand(arg)) yield return t;
    }
}
