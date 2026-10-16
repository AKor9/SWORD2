using System.Numerics;
using System.Text.Json;

namespace Sword.Bake.Anim;

/// <summary>
/// Соглашения скелета UE Mannequin (UE4, UE5 Manny) и пака мечника: имена костей кистей и пальцев,
/// суффикс зеркальных клипов, значение «grip» у крепления меча, чтение skeleton.json.
/// </summary>
public static class UeRig
{
    public static readonly string[] Sides = ["l", "r"];
    /// <summary>Пальцы от большого к мизинцу.</summary>
    public static readonly string[] Fingers = ["thumb", "index", "middle", "ring", "pinky"];
    /// <summary>Пальцы без большого — кольцо, которое сжимает рукоять.</summary>
    public static readonly string[] GripFingers = Fingers[1..];

    public static string Hand(string side) => $"hand_{side}";
    public static string Phalanx(string finger, int index, string side) => $"{finger}_0{index}_{side}";
    public static string Metacarpal(string finger, string side) => $"{finger}_metacarpal_{side}";

    /// <summary>Клипы пака с мечом в левой руке.</summary>
    public const string MirroredSuffix = "_Mirrored";
    public static bool IsMirrored(string clipName) => clipName.EndsWith(MirroredSuffix, StringComparison.Ordinal);

    /// <summary>Значение крепления «grip»: меч по кулаку правой кисти (<see cref="SkinBake.GripAttachment"/>).</summary>
    public const string GripAttach = "grip";

    /// <summary>
    /// Поза покоя скелета из skeleton.json (tools/ue/export_meshes.py): компонентные t (единицы экспорта) и q по имени кости.
    /// Скелет персонажа — ключ "character"; старые выгрузки — "uefn".
    /// </summary>
    public static Dictionary<string, (Vector3 T, Quaternion Q)> ReadRefPose(string skeletonJson)
    {
        using var skel = JsonDocument.Parse(File.ReadAllText(skeletonJson));
        var root = skel.RootElement.TryGetProperty("character", out var character) ? character
            : skel.RootElement.TryGetProperty("uefn", out var uefn) ? uefn
            : throw new InvalidDataException($"{skeletonJson}: нет скелета \"character\"");
        var pose = new Dictionary<string, (Vector3, Quaternion)>();
        foreach (var b in root.GetProperty("bones").EnumerateArray())
        {
            var w = b.GetProperty("world");
            var t = w.GetProperty("t");
            var q = w.GetProperty("q");
            pose[b.GetProperty("name").GetString()!] = (
                new Vector3(t[0].GetSingle(), t[1].GetSingle(), t[2].GetSingle()),
                Quaternion.Normalize(new Quaternion(q[0].GetSingle(), q[1].GetSingle(), q[2].GetSingle(), q[3].GetSingle())));
        }
        return pose;
    }
}
