using System.Numerics;
using Sword.Bake.Anim;
using ClipSource = Sword.Bake.Anim.ClipSource;

namespace Sword.Bake.Tests;

/// <summary>
/// Хват оружия (<see cref="SkinBake.GripAttachment"/>) на синтетической кисти: фаланги 01…03 каждого пальца лежат дугой на
/// окружности вокруг заданной оси рукояти, дуга открыта к ладони (центр суставов сдвинут к кончикам пальцев), рукоять идёт
/// наискосок линии костяшек, кисть в клипе повёрнута и сдвинута.
/// </summary>
public sealed class GripTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sword-grip-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    /// <summary>Ось рукояти в системе кисти (UE, см): середина хвата и направление от мизинца к указательному.</summary>
    private static readonly Vector3 Centre = new(2, 9, -3), Axis = Vector3.Normalize(new Vector3(-0.3f, 0.2f, 1));

    /// <summary>Окружность охвата пальцев, см, и углы фаланг 01, 02, 03 на ней, рад.</summary>
    private const float Radius = 3;
    private static readonly float[] Angles = [0.3f, 1.6f, 2.7f];

    /// <summary>Сдвиг дуги от пальца к пальцу, рад: костяшки идут не вдоль оси — рукоять лежит наискосок ладони.</summary>
    private const float Twist = 0.25f;

    /// <summary>Клип на <paramref name="frames"/> кадров: кисть в <paramref name="handAt"/> с поворотом <paramref name="hand"/>, пальцы вокруг оси.</summary>
    private ClipSource Write(Quaternion hand, Vector3 handAt, int frames = 4)
    {
        string[] fingers = UeRig.GripFingers;
        var bones = new List<string> { UeRig.Hand("r") };
        foreach (string f in fingers)
            for (int k = 1; k <= 3; k++) bones.Add(UeRig.Phalanx(f, k, "r"));
        var man = new RawManifest { Bones = [.. bones], Parents = [-1, .. Enumerable.Repeat(0, bones.Count - 1)] };
        var info = new RawClipInfo { Name = "Idle", Path = "/Game/Test/Idle", File = "Idle.bin", Fps = 30, Frames = frames, Loop = true };
        var clip = new RawClip(info, bones.Count);
        // базис окружности: u — к тыльной стороне пальцев, v — к их кончикам
        var u = Vector3.Normalize(Vector3.Cross(Axis, Vector3.UnitZ));
        var v = Vector3.Cross(Axis, u);
        for (int fr = 0; fr < frames; fr++)
        {
            clip.Pos[fr * bones.Count] = handAt;
            clip.Rot[fr * bones.Count] = hand;
            for (int f = 0; f < fingers.Length; f++)
            {
                var along = Centre + Axis * (4.5f - 3 * f);   // указательный — дальше всех по оси
                for (int k = 0; k < 3; k++)
                {
                    float angle = Angles[k] + Twist * f;
                    var local = along + Radius * (MathF.Cos(angle) * u + MathF.Sin(angle) * v);
                    int at = fr * bones.Count + 1 + f * 3 + k;
                    clip.Pos[at] = handAt + Vector3.Transform(local, hand);
                    clip.Rot[at] = hand;
                }
            }
        }
        RawAnimIO.Write(_dir, man, [clip]);
        return new ClipSource(RawAnimIO.ReadManifest(_dir), _dir);
    }

    [Fact]
    public void Grip_HandleAxisThroughFingerCurlCentres()
    {
        var hand = Quaternion.CreateFromYawPitchRoll(0.7f, -0.4f, 1.1f);
        var (t, q) = SkinBake.GripAttachment(Write(hand, new Vector3(30, -12, 95)), gripAlong: 9, TextWriter.Null);
        var blade = Vector3.Transform(Vector3.UnitZ, q);
        Assert.True(Vector3.Dot(blade, Axis) > 0.99999f, $"клинок {blade}, ось рукояти {Axis}");
        var grip = t + blade * 9;   // середина хвата — 9 см от навершия по клинку
        Assert.True(Vector3.Distance(grip, Centre) < 1e-3f, $"середина хвата {grip}, ожидается {Centre}");
        // кромка (+Y меча) — поперёк клинка, к костяшкам (к средней фаланге 01 от оси)
        var edge = Vector3.Transform(Vector3.UnitY, q);
        Assert.True(MathF.Abs(Vector3.Dot(edge, blade)) < 1e-5f);
        var u = Vector3.Normalize(Vector3.Cross(Axis, Vector3.UnitZ));
        var v = Vector3.Cross(Axis, u);
        var middle01 = Centre + Axis * 1.5f + Radius * (MathF.Cos(Angles[0] + Twist) * u + MathF.Sin(Angles[0] + Twist) * v);
        Assert.True(Vector3.Dot(edge, middle01) > 0, "кромка смотрит от костяшек");
    }

    [Fact]
    public void Grip_WithoutFingerBones_ExplainsError()
    {
        var man = new RawManifest { Bones = [UeRig.Hand("r")], Parents = [-1] };
        var info = new RawClipInfo { Name = "Idle", Path = "/Game/Test/Idle", File = "Idle.bin", Fps = 30, Frames = 1, Loop = true };
        var clip = new RawClip(info, 1);
        clip.Rot[0] = Quaternion.Identity;
        RawAnimIO.Write(_dir, man, [clip]);
        var e = Assert.Throws<InvalidDataException>(() =>
            SkinBake.GripAttachment(new ClipSource(RawAnimIO.ReadManifest(_dir), _dir), 9, TextWriter.Null));
        Assert.Contains("фаланги", e.Message);
    }
}
