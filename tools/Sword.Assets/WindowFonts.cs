using System.Security.Cryptography;
using System.Text;
using Sword.Bake.Font;
using Sword.Engine.Formats;
using Sword.Engine.Ui;

namespace Sword.Assets;

/// <summary>
/// Шрифты собственного интерфейса менеджера ресурсов — системные шрифты Windows, запечённые на лету пекарем шрифтов
/// (<see cref="UiFontBaker"/> пекарни). От Content/ и пака
/// не зависят: окно открывается и для проекта без единого ассета. Запечённое кэшируется в
/// <c>%LOCALAPPDATA%\SWORD Assets\fonts</c> по размеру и времени ttf, ролям, версии формата и сборке менеджера.
/// </summary>
internal static class WindowFonts
{
    /// <summary>Файл шрифта Windows → роли интерфейса (<see cref="UiVectorFontFormat.RoleNames"/>).</summary>
    internal static readonly (string File, string[] Roles)[] Faces =
    [
        ("segoeui.ttf", ["Body"]),
        ("seguisb.ttf", ["Head", "Label"]),
        ("consola.ttf", ["Num", "NumLight"]),
    ];

    public static string CacheDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SWORD Assets", "fonts");

    /// <summary>Шрифты интерфейса, готовые для UI-прохода рендера (<c>RendererOptions.UiFonts</c>).</summary>
    /// <exception cref="FileNotFoundException">Нет системного шрифта.</exception>
    public static UiFonts Load(Action<string> log, string? cacheDir = null) => new(Files(log, cacheDir));

    /// <summary>Файлы <c>.swfont</c> системных шрифтов: из кэша или запечённые сейчас (и положенные в кэш).</summary>
    public static IReadOnlyList<UiFontFile> Files(Action<string> log, string? cacheDir = null)
    {
        cacheDir ??= CacheDir;
        string fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        var files = new List<UiFontFile>(Faces.Length);
        foreach (var (file, roles) in Faces)
        {
            string ttf = Path.Combine(fontsDir, file);
            if (!File.Exists(ttf)) throw new FileNotFoundException($"нет системного шрифта {ttf} — он нужен интерфейсу менеджера", ttf);
            var fi = new FileInfo(ttf);
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{file}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{string.Join(",", roles)}|{UiVectorFontFormat.Version}|{typeof(UiFontBaker).Assembly.ManifestModule.ModuleVersionId}")))[..16];
            string name = Path.GetFileNameWithoutExtension(file);
            string cached = Path.Combine(cacheDir, $"{name}-{key}{UiVectorFontFormat.Extension}");
            byte[] bytes;
            if (File.Exists(cached)) bytes = File.ReadAllBytes(cached);
            else
            {
                bytes = UiFontBaker.Bake(File.ReadAllBytes(ttf), ttf, name, roles, out string report);
                log($"[assets] шрифт интерфейса {report}");
                try
                {
                    Directory.CreateDirectory(cacheDir);
                    foreach (var old in Directory.GetFiles(cacheDir, $"{name}-*{UiVectorFontFormat.Extension}")) File.Delete(old);
                    File.WriteAllBytes(cached, bytes);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // кэш — ускорение запуска
            }
            using var stream = new MemoryStream(bytes, writable: false);
            files.Add(UiFontFile.Read(stream, cached));
        }
        return files;
    }
}
