using System.Security.Cryptography;
using System.Text.Json;
using Sword.Engine.Content;

namespace Sword.Bake;

/// <summary>
/// Локальный кэш проекта — <c>.bake/cache.json</c> (вне git, только для этой машины): хеши исходников, чтобы не
/// перечитывать файлы, чьи размер и время не менялись, и ошибки последних сборок. Что лежит в паке, он не решает —
/// это сведения самого пака (<see cref="PakBuildInfo"/>). Потерян или испорчен — просто пересчитывается.
/// </summary>
internal sealed class LocalCache
{
    private const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    /// <summary>Исходники по полному пути: размер, время, хеш.</summary>
    public Dictionary<string, PakFileRecord> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Ассеты, чья последняя сборка упала.</summary>
    public Dictionary<string, AssetFailure> Failures { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static LocalCache Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<LocalCache>(File.ReadAllText(path), PakBuildInfo.Json) is { Version: CurrentVersion } c)
            {
                c.Files = new(c.Files, StringComparer.OrdinalIgnoreCase);
                c.Failures = new(c.Failures, StringComparer.OrdinalIgnoreCase);
                return c;
            }
        }
        catch (Exception e) when (e is JsonException or IOException) { }
        return new LocalCache();
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            byte[] json;
            lock (Files) json = JsonSerializer.SerializeToUtf8Bytes(this, PakBuildInfo.Json);   // Describe пишет из потоков сборки
            File.WriteAllBytes(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // кэш — ускорение, не данные
    }

    /// <summary>Запись о файле с хешем; хеш пересчитывается, только если размер или время отличаются от кэша.</summary>
    public PakFileRecord Describe(string fullPath)
    {
        var fi = new FileInfo(fullPath);
        long size = fi.Length, mtime = fi.LastWriteTimeUtc.Ticks;
        lock (Files)
        {
            if (Files.TryGetValue(fullPath, out var known) && known.Size == size && known.Mtime == mtime)
                return new PakFileRecord { Path = fullPath, Size = size, Mtime = mtime, Hash = known.Hash };
        }
        var rec = new PakFileRecord { Path = fullPath, Size = size, Mtime = mtime, Hash = HashFile(fullPath) };
        lock (Files) Files[fullPath] = rec;
        return rec;
    }

    /// <summary>
    /// Совпадает ли файл с записью сборки: размер и время — быстро; время другое, а размер тот же — по хешу.
    /// </summary>
    public bool Matches(PakFileRecord recorded, string fullPath)
    {
        var fi = new FileInfo(fullPath);
        if (!fi.Exists || fi.Length != recorded.Size) return false;
        if (fi.LastWriteTimeUtc.Ticks == recorded.Mtime) return true;
        return Describe(fullPath).Hash == recorded.Hash;
    }

    public static string HashFile(string fullPath)
    {
        using var s = File.OpenRead(fullPath);
        return Convert.ToHexString(SHA256.HashData(s))[..32];
    }
}

/// <summary>Упавшая сборка ассета: сообщение и то, при каких параметрах и исходниках она упала.</summary>
public sealed class AssetFailure
{
    public string Message { get; set; } = "";
    public DateTime Utc { get; set; }
    public string ParamsHash { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    /// <summary>Отпечаток корней исходников: заданный после ошибки корень снимает её.</summary>
    public string RootsHash { get; set; } = "";
    /// <summary>Входы, которые обработчик успел зарегистрировать: их изменение после ошибки снимает её.</summary>
    public List<string> Inputs { get; set; } = [];
    public List<string> InputDirs { get; set; } = [];
    /// <summary>Файлы и каталоги, которых не было: появились — ошибка снята.</summary>
    public List<string> Missing { get; set; } = [];
}
