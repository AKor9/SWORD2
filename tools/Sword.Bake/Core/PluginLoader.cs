using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;

namespace Sword.Bake;

/// <summary>
/// Загрузка сборок-плагинов в общий контекст процесса: типы ядра (<see cref="IAssetProcessor"/>) у плагина и хоста
/// одни и те же. Зависимости плагина ищутся по его .deps.json (проект плагина собирается с EnableDynamicLoading).
/// Плагин загружается из теневой копии его каталога: исходный каталог остаётся свободным, и плагин можно
/// пересобрать, не закрывая Studio (новый код подхватит следующий запуск).
/// </summary>
internal static class PluginLoader
{
    private static readonly object Lock = new();
    private static readonly Dictionary<string, Assembly> Loaded = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<AssemblyDependencyResolver> Resolvers = [];
    private static bool _hooked;

    /// <summary>Конфигурация сборки хоста — подставляется вместо <c>${config}</c> в путях плагинов.</summary>
    public static string HostConfiguration =>
        (Assembly.GetEntryAssembly() ?? typeof(PluginLoader).Assembly).GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration is { Length: > 0 } c
            ? c
            : "Release";

    /// <exception cref="FileNotFoundException">Нет сборки плагина.</exception>
    public static Assembly Load(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path))
            throw new FileNotFoundException($"нет сборки {path} — соберите проект плагина (конфигурация {HostConfiguration})", path);
        lock (Lock)
        {
            if (Loaded.TryGetValue(path, out var known)) return known;
            string shadow = Shadow(path);
            if (!_hooked)
            {
                AssemblyLoadContext.Default.Resolving += Resolve;
                AssemblyLoadContext.Default.ResolvingUnmanagedDll += ResolveNative;
                _hooked = true;
            }
            Resolvers.Add(new AssemblyDependencyResolver(shadow));
            var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(shadow);
            Loaded[path] = asm;
            return asm;
        }
    }

    private static Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
    {
        lock (Lock)
            foreach (var r in Resolvers)
                if (r.ResolveAssemblyToPath(name) is { } p && File.Exists(p))
                    return context.LoadFromAssemblyPath(p);
        return null;
    }

    private static nint ResolveNative(Assembly assembly, string name)
    {
        lock (Lock)
            foreach (var r in Resolvers)
                if (r.ResolveUnmanagedDllToPath(name) is { } p && File.Exists(p))
                    return System.Runtime.InteropServices.NativeLibrary.Load(p);
        return 0;
    }

    /// <summary>Копия каталога плагина во временном каталоге; путь копии сборки. Та же версия файлов — та же копия.</summary>
    private static string Shadow(string path)
    {
        string dir = Path.GetDirectoryName(path)!;
        var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var key = new StringBuilder(path);
        foreach (var f in files)
        {
            var fi = new FileInfo(f);
            key.Append('|').Append(Path.GetRelativePath(dir, f)).Append(':').Append(fi.Length).Append(':').Append(fi.LastWriteTimeUtc.Ticks);
        }
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToString())))[..16];
        string target = Path.Combine(Path.GetTempPath(), "sword-bake", "plugins", $"{Path.GetFileNameWithoutExtension(path)}-{hash}");
        string done = Path.Combine(target, ".complete");
        if (!File.Exists(done))
        {
            foreach (var f in files)
            {
                string to = Path.Combine(target, Path.GetRelativePath(dir, f));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                try { File.Copy(f, to, overwrite: true); }
                catch (IOException) when (File.Exists(to)) { }   // копию уже держит другой процесс пекарни — она та же
            }
            File.WriteAllText(done, "");
        }
        return Path.Combine(target, Path.GetFileName(path));
    }
}
