using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Sword.Bake.Engine.Anim;

/// <summary>
/// Промежуточный формат экспорта (пишет tools/ue/export_gasp.py, в тестах — <see cref="RawAnimIO.Write"/>):
/// manifest.json + clips/&lt;file&gt;. В .bin: кадры × кости × 7 float32 (pos xyz, quat xyzw,
/// component space с root motion в кости root), затем кадры × кривые float32.
/// </summary>
public sealed class RawManifest
{
    public string Format { get; set; } = "sword-rawanim";
    public int Version { get; set; } = 1;
    /// <summary>"cm" (UE) или "m".</summary>
    public string Units { get; set; } = "cm";
    /// <summary>"ue": X, Y, Z-вверх, левая система.</summary>
    public string Axes { get; set; } = "ue";
    /// <summary>Направление «вперёд» персонажа в локальной системе кости root (у Manny — +Y).</summary>
    public float[] RootForward { get; set; } = [0, 1, 0];
    public string[] Bones { get; set; } = [];
    /// <summary>Родитель каждой кости (индекс в <see cref="Bones"/>, −1 — корень); есть при экспорте полного скелета.</summary>
    public int[] Parents { get; set; } = [];
    public List<RawClipInfo> Clips { get; set; } = [];
}

public sealed class RawClipInfo
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string File { get; set; } = "";
    public float Fps { get; set; }
    public int Frames { get; set; }
    public bool Loop { get; set; }
    public string[] Curves { get; set; } = [];
}

public sealed class RawClip(RawClipInfo info, int bones)
{
    public readonly RawClipInfo Info = info;
    public readonly int BoneCount = bones;
    public readonly Vector3[] Pos = new Vector3[info.Frames * bones];
    public readonly Quaternion[] Rot = new Quaternion[info.Frames * bones];
    public readonly float[] Curves = new float[info.Frames * info.Curves.Length];

    public Vector3 P(int frame, int bone) => Pos[frame * BoneCount + bone];
    public Quaternion R(int frame, int bone) => Rot[frame * BoneCount + bone];
    public float Curve(int frame, int curve) => Curves[frame * Info.Curves.Length + curve];
}

public static class RawAnimIO
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static RawManifest ReadManifest(string dir)
    {
        string path = Path.Combine(dir, "manifest.json");
        var m = JsonSerializer.Deserialize<RawManifest>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException($"{path}: пустой manifest");
        if (m.Format != "sword-rawanim" || m.Version != 1)
            throw new InvalidDataException($"{path}: формат {m.Format} v{m.Version}, ожидается sword-rawanim v1");
        if (m.Axes != "ue") throw new InvalidDataException($"{path}: оси {m.Axes} не поддерживаются");
        if (m.Units is not ("cm" or "m")) throw new InvalidDataException($"{path}: единицы {m.Units}");
        if (m.RootForward.Length != 3) throw new InvalidDataException($"{path}: rootForward — 3 числа");
        return m;
    }

    /// <summary>Экспорт полного скелета: у каждой кости есть родитель (нужно skin и import).</summary>
    public static void RequireFullSkeleton(RawManifest m)
    {
        if (m.Parents.Length != m.Bones.Length)
            throw new InvalidDataException("в экспорте нет родителей костей — экспортируйте с \"bones\": [\"*\"]");
    }

    public static RawClip ReadClip(string dir, RawManifest m, RawClipInfo info)
    {
        string path = Path.Combine(dir, "clips", info.File);
        var bytes = File.ReadAllBytes(path);
        var floats = MemoryMarshal.Cast<byte, float>(bytes);
        int bones = m.Bones.Length;
        long expected = (long)info.Frames * bones * 7 + (long)info.Frames * info.Curves.Length;
        if (bytes.Length % 4 != 0 || floats.Length != expected)
            throw new InvalidDataException($"{path}: {floats.Length} float, ожидается {expected}");
        if (info.Frames < 2 || !(info.Fps > 0)) throw new InvalidDataException($"{path}: кадров {info.Frames}, fps {info.Fps}");

        var clip = new RawClip(info, bones);
        int o = 0;
        for (int i = 0; i < info.Frames * bones; i++, o += 7)
        {
            clip.Pos[i] = new Vector3(floats[o], floats[o + 1], floats[o + 2]);
            clip.Rot[i] = Quaternion.Normalize(new Quaternion(floats[o + 3], floats[o + 4], floats[o + 5], floats[o + 6]));
        }
        floats[o..].CopyTo(clip.Curves);
        return clip;
    }

    public static void Write(string dir, RawManifest m, IReadOnlyList<RawClip> clips)
    {
        Directory.CreateDirectory(Path.Combine(dir, "clips"));
        m.Clips = clips.Select(c => c.Info).ToList();
        foreach (var c in clips)
        {
            using var w = new BinaryWriter(File.Create(Path.Combine(dir, "clips", c.Info.File)));
            for (int i = 0; i < c.Pos.Length; i++)
            {
                w.Write(c.Pos[i].X); w.Write(c.Pos[i].Y); w.Write(c.Pos[i].Z);
                w.Write(c.Rot[i].X); w.Write(c.Rot[i].Y); w.Write(c.Rot[i].Z); w.Write(c.Rot[i].W);
            }
            foreach (float x in c.Curves) w.Write(x);
        }
        File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(m, Json));
    }
}
