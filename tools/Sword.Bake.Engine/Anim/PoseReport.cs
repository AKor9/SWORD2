using System.Globalization;
using System.Numerics;

namespace Sword.Bake.Engine.Anim;

/// <summary>Диагностика экспорта: сырые точки костей кадра raw-экспорта.</summary>
internal static class PoseReport
{
    /// <summary>Точки костей кадра сырого экспорта в пространстве SWORD (м, без масштаба), относительно таза по XZ.</summary>
    public static void Raw(string rawDir, string clipName, int frame, TextWriter o)
    {
        var inv = CultureInfo.InvariantCulture;
        var man = RawAnimIO.ReadManifest(rawDir);
        var info = man.Clips.FirstOrDefault(c => c.Name == clipName) ?? throw new ArgumentException($"нет клипа {clipName}");
        var clip = RawAnimIO.ReadClip(rawDir, man, info);
        if (frame < 0 || frame >= info.Frames) throw new ArgumentException($"кадр {frame} вне клипа {clipName}: 0…{info.Frames - 1}");
        float unit = UeSpace.UnitOf(man);
        int pelvis = Array.IndexOf(man.Bones, "pelvis"), root = Array.IndexOf(man.Bones, "root");
        if (pelvis < 0 || root < 0) throw new InvalidDataException("в экспорте нужны кости pelvis и root");
        var pel = UeSpace.ToSwordAxes(clip.P(frame, pelvis)) * unit;
        var origin = new Vector3(pel.X, 0, pel.Z);
        var fwd = UeSpace.ToSwordAxes(Vector3.Transform(new Vector3(man.RootForward[0], man.RootForward[1], man.RootForward[2]),
            clip.R(frame, root)));
        o.WriteLine(string.Create(inv, $"{clipName} кадр {frame}: курс root ({fwd.X:F2},{fwd.Y:F2},{fwd.Z:F2}) — +Z вперёд, +X влево"));
        for (int b = 0; b < man.Bones.Length; b++)
        {
            var p = UeSpace.ToSwordAxes(clip.P(frame, b)) * unit - origin;
            o.WriteLine(string.Create(inv, $"  {man.Bones[b],-12} ({p.X,6:F3}, {p.Y,6:F3}, {p.Z,6:F3})"));
        }
    }
}
