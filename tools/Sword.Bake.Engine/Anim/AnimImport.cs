using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Sword.Engine.Animation;
using Sword.Engine.Formats;

namespace Sword.Bake.Engine.Anim;

/// <summary>
/// Импорт экспорта в библиотеку контента: raw-экспорт полного скелета → anim/&lt;набор&gt;/*.swclip, набор каждого клипа —
/// по маршрутизации (<see cref="Route"/>). В один набор могут класть клипы несколько единиц сборки (1HM и Combat Master —
/// в sword_1h), поэтому импорт трогает только свои клипы: с тем же хешем исходника не перезаписывает, которых больше нет
/// в экспорте или которые ушли в другой набор — удаляет. Скелет библиотеки пишет единица anim.skeleton, не импорт.
/// </summary>
internal static partial class AnimImport
{
    /// <summary>
    /// Версия формата импорта и смысла хеша исходника (<see cref="SourceHash"/>). Кода конвертера хеш не видит: правка
    /// выпечки клипа (корень, пальцы, оси, запись) — поднять версию, иначе прежние клипы останутся в паке.
    /// </summary>
    public const string ToolVersion = "sword-animbake import 4";   // 2 — поворот root motion; 3 — MVID конвертера в хеше; 4 — без MVID (SWORD2)

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9_.-]*[a-z0-9_-])?$")]
    private static partial Regex IdRegex();

    [GeneratedRegex(@"^(con|prn|aux|nul|com[0-9]|lpt[0-9])(\..*)?$")]
    private static partial Regex DeviceNameRegex();

    /// <summary>
    /// ID набора или скелета — имя папки и файла: a-z, 0-9, _ . -, без точки в конце и не имя устройства Windows.
    /// Точку в конце Windows отбрасывает: «gasp.» — это папка «gasp», и импорт удалил бы её клипы как устаревшие.
    /// </summary>
    internal static bool IsValidId(string id) => IdRegex().IsMatch(id) && !DeviceNameRegex().IsMatch(id);

    /// <summary>
    /// Набор каждого клипа: набор → маски папок пути UE клипа (<see cref="RawClipInfo.Path"/>, «*» и «?»); пустой список —
    /// все клипы. Клип должен попасть ровно в один набор — иначе ошибка, а не молча потерянный или задвоенный клип.
    /// </summary>
    /// <exception cref="ArgumentException">Нет наборов, неверное имя, клип вне наборов или в нескольких.</exception>
    public static Dictionary<string, string> Route(RawManifest man, IReadOnlyDictionary<string, List<string>> sets)
    {
        if (sets.Count == 0) throw new ArgumentException("sets: нужен хотя бы один набор, например {\"sword_1h\": []}");
        foreach (var name in sets.Keys)
            if (!IsValidId(name)) throw new ArgumentException($"sets: имя набора «{name}» — только a-z, 0-9, _ . -, без точки в конце");
        var masks = sets.ToDictionary(kv => kv.Key, kv => kv.Value.Select(m => new Regex("^" + Regex.Escape(m).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToList());
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var clip in man.Clips)
        {
            var folders = clip.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var hits = masks.Where(kv => kv.Value.Count == 0 || kv.Value.Any(m => folders.Any(m.IsMatch))).Select(kv => kv.Key).ToList();
            result[clip.Name] = hits.Count switch
            {
                1 => hits[0],
                0 => throw new ArgumentException($"клип {clip.Name} ({clip.Path}) не попал ни в один набор sets"),
                _ => throw new ArgumentException($"клип {clip.Name} ({clip.Path}) попал в несколько наборов: {string.Join(", ", hits)}"),
            };
        }
        return result;
    }

    /// <summary>Файл клипа в каталоге контента: anim/&lt;набор&gt;/&lt;имя&gt;.swclip.</summary>
    public static string ClipPath(string content, string set, string name) =>
        Path.Combine(content, ClipLibrary.AnimDir, set, name + ClipLibrary.ClipExt);

    /// <param name="setOf">Набор каждого клипа экспорта (<see cref="Route"/>).</param>
    /// <param name="content">Каталог выходов ассета: здесь его прежние клипы (инкрементальная сборка), сюда же новые.</param>
    /// <param name="fingerSrcDir">Raw-экспорт исходного скелета: пальцы берутся из него (<see cref="FingerTransfer"/>).</param>
    /// <param name="refPosePath">skeleton.json с позой покоя целевого скелета — нужен вместе с <paramref name="fingerSrcDir"/>.</param>
    public static void Run(string animDir, IReadOnlyDictionary<string, string> setOf, string skeletonId, string content, TextWriter log,
        string? fingerSrcDir = null, string? refPosePath = null)
    {
        if (!IsValidId(skeletonId)) throw new ArgumentException($"скелет «{skeletonId}»: только a-z, 0-9, _ . -, без точки в конце и не имя устройства");
        content = Path.GetFullPath(content);
        var man = RawAnimIO.ReadManifest(animDir);
        RawAnimIO.RequireFullSkeleton(man);
        uint skeletonHash = SkeletonFile.ComputeHash(man.Bones, man.Parents);

        // ── какие клипы изменились ──
        var invalid = Path.GetInvalidFileNameChars();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in man.Clips)
        {
            if (c.Name.Length == 0 || c.Name.IndexOfAny(invalid) >= 0)
                throw new InvalidDataException($"имя клипа «{c.Name}» не годится для имени файла");
            if (!names.Add(c.Name)) throw new InvalidDataException($"клип «{c.Name}» встречается дважды (без учёта регистра)");
            if (!setOf.ContainsKey(c.Name)) throw new ArgumentException($"клипу {c.Name} не назначен набор");
        }
        var fingers = FingerSource.Open(fingerSrcDir, refPosePath, man);
        var hashes = man.Clips.ToDictionary(c => c.Name, c => SourceHash(animDir, man, c, fingers));
        string ClipFileOf(string name) => ClipPath(content, setOf[name], name);
        var changed = man.Clips.Where(c =>
        {
            string p = ClipFileOf(c.Name);
            if (!File.Exists(p)) return true;
            try
            {
                ClipFile h;
                using (var file = File.OpenRead(p)) h = ClipFile.Read(file, p);
                return h.SourceHash != hashes[c.Name] || h.Skeleton != skeletonId || h.SkeletonHash != skeletonHash;
            }
            catch (InvalidDataException) { return true; }
        }).ToList();

        // ── запись ──
        if (changed.Count > 0)
        {
            var sub = new RawManifest
            {
                Format = man.Format, Version = man.Version, Units = man.Units, Axes = man.Axes,
                RootForward = man.RootForward, Bones = man.Bones, Parents = man.Parents, Clips = changed,
            };
            var clips = SkinBake.BuildClips(new ClipSource(sub, animDir, fingers), log);
            int jc = clips.JointCount;
            for (int i = 0; i < changed.Count; i++)
            {
                var info = changed[i];
                var c = clips.Clips[i];
                var file = new ClipFile
                {
                    Skeleton = skeletonId, SkeletonHash = skeletonHash, SampleRate = clips.SampleRate,
                    Frames = c.Count, Joints = jc, Loop = c.Loop,
                    SourcePath = info.Path, SourceHash = hashes[info.Name],
                    LocalRot = clips.LocalRot.Slice(c.Start * jc, c.Count * jc).ToArray(),
                    LocalPos = clips.LocalPos.Slice(c.Start * jc, c.Count * jc).ToArray(),
                    RootMotion = clips.RootMotion.Slice(c.Start, c.Count).ToArray(),
                    RootYaw = clips.RootYaw.Slice(c.Start, c.Count).ToArray(),
                };
                ContentFiles.Write(ClipFileOf(info.Name), file.Write);
            }
        }

        // ── устаревшие: свои прежние клипы, которых нет в экспорте или которые ушли в другой набор ──
        var expected = man.Clips.Select(c => Path.GetFullPath(ClipFileOf(c.Name))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int removed = 0;
        string animRoot = Path.Combine(content, ClipLibrary.AnimDir);
        if (Directory.Exists(animRoot))
            foreach (var file in Directory.GetFiles(animRoot, "*", SearchOption.AllDirectories))
            {
                if (expected.Contains(Path.GetFullPath(file))) continue;
                File.Delete(file);
                log.WriteLine($"  удалён {Path.GetRelativePath(content, file).Replace('\\', '/')} — нет в экспорте или в этом наборе");
                removed++;
            }

        foreach (var set in setOf.Values.Distinct().Order(StringComparer.Ordinal))
            log.WriteLine($"набор {set}: клипов {setOf.Count(kv => kv.Value == set)}");
        log.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"импорт {animDir}: клипов {man.Clips.Count}, записано {changed.Count}, без изменений {man.Clips.Count - changed.Count}, удалено {removed}"));
    }

    /// <summary>
    /// SHA-256 исходного .bin, пространства экспорта, источника пальцев (его .bin, скелет, пространство, параметры клипа)
    /// с позой покоя, параметров клипа и версии импорта (<see cref="ToolVersion"/>). Кости и родители цели покрывает хеш
    /// скелета. Сборки конвертера (их MVID) в хеш не входят: смена Debug ↔ Release и пересборка без правок не перепекают
    /// клипы; правка кода выпечки клипа — поднять <see cref="ToolVersion"/>.
    /// </summary>
    internal static string SourceHash(string animDir, RawManifest man, RawClipInfo info, FingerSource? fingers)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(File.ReadAllBytes(Path.Combine(animDir, "clips", info.File)));
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"|{ToolVersion}|{info.Fps}|{info.Frames}|{info.Loop}|{Space(man)}");
        if (fingers?.Manifest.Clips.FirstOrDefault(c => c.Name == info.Name) is { } src)
        {
            var fm = fingers.Manifest;
            sha.AppendData(File.ReadAllBytes(Path.Combine(fingers.Dir, "clips", src.File)));
            sha.AppendData(File.ReadAllBytes(fingers.RefPosePath));
            text.Append(CultureInfo.InvariantCulture,
                $"|пальцы|{src.Fps}|{src.Frames}|{Space(fm)}|{string.Join(",", fm.Bones)}|{string.Join(",", fm.Parents)}");
        }
        sha.AppendData(Encoding.UTF8.GetBytes(text.ToString()));
        return Convert.ToHexString(sha.GetHashAndReset())[..32];

        static string Space(RawManifest m) =>
            string.Create(CultureInfo.InvariantCulture, $"{m.Units}|{m.Axes}|{m.RootForward[0]},{m.RootForward[1]},{m.RootForward[2]}");
    }
}
