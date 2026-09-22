using System.Numerics;
using System.Text.Json;
using Sword.Combat;
using Sword.Content;

// Трасса SWORD 2eab13b: игрок (Agile) без Motion Matching — встроенный KinematicSteer, FighterAnimator с LegacyLocomotion,
// перенос корня (KinematicBegin), команды — как PlayerController.Think. Итог — JSON для теста SWORD2.
string pakPath = args[0], outPath = args[1];
using var pak = PakSource.Open(pakPath);
var rig = CharacterRig.TryLoad(pak) ?? throw new InvalidOperationException("нет рига");
var anim = new FighterAnimator(rig) { Agile = true };

const float dt = 1f / 120, RunSpeed = 4.2f, WalkSpeed = 1.6f, KinTurnRate = 12f, KinAccel = 14f;
var root = Vector3.Zero;
float facing = 0, facingGoal = 0;
var rootVel = Vector3.Zero;
Vector3 cursor = default;
bool hasCursor = false;

var ticks = new List<object>();
var segments = Scenario.Segments;
int total = segments[^1].End;
for (int t = 0; t < total; t++)
{
    var seg = segments.First(s => t < s.End);
    // PlayerController.Think
    var wish = new Vector3(seg.MoveX, 0, seg.MoveZ);
    if (wish.LengthSquared() > 0) wish = Vector3.Normalize(wish);
    var desired = wish * (seg.Walk ? WalkSpeed : RunSpeed);
    if (seg.Gaze is { } g) { cursor = new Vector3(g.X, 0, g.Y); hasCursor = true; }
    if (hasCursor)
    {
        var to = cursor - root;
        to.Y = 0;
        if (to.LengthSquared() > 0.09f) facingGoal = MathF.Atan2(to.X, to.Z);
    }
    // KinematicBegin: KinematicSteer (Agile, без действий: TurnScale 1, MoveScale 1), anim.Tick, перенос
    float df = MathF.IEEERemainder(facingGoal - facing, MathF.Tau);
    float turn = KinTurnRate * anim.TurnScale;
    facing += Math.Clamp(df, -turn * dt, turn * dt);
    var d = new Vector3(desired.X, 0, desired.Z) * anim.MoveScale;
    rootVel += (d - rootVel) * MathF.Min(1, KinAccel * dt);
    rootVel.Y = 0;
    var local = Vector3.Transform(rootVel, Quaternion.CreateFromAxisAngle(Vector3.UnitY, -facing));
    local = new Vector3(-local.X, local.Y, local.Z);
    anim.Tick(dt, local);
    facing += anim.YawDelta;
    root += rootVel * dt;
    root.Y = 0;

    ulong h = 14695981039346656037;
    foreach (var m in anim.Component)
        foreach (float x in new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 })
            h = (h ^ (uint)BitConverter.SingleToInt32Bits(x)) * 1099511628211;
    int clip = anim.Locomotion is LegacyLocomotion legacy ? legacy.Clip : -1;
    var pelvis = anim.Component[rig.Pelvis].Translation;
    var footL = anim.Component[rig.FootL].Translation;
    ticks.Add(new
    {
        tick = t + 1,
        root = new[] { root.X, root.Z },
        facing,
        clip = clip >= 0 ? rig.Clips.Clips[clip].Name : "",
        poseHash = h.ToString("X16"),
        pelvis = new[] { pelvis.X, pelvis.Y, pelvis.Z },
        footL = new[] { footL.X, footL.Y, footL.Z },
    });
}

var doc = new
{
    purpose = "Трасса поз SWORD 2eab13b для сверки этапа 2 SWORD2: игрок без Motion Matching, LegacyLocomotion, " +
              "KinematicSteer и перенос корня; хеш — FNV-1a по битам всех 16 чисел модельных матриц суставов после тика.",
    sourceCommit = "2eab13b2262f6b336c3fc9bdcd71fb3dfc5a51a4",
    pakSha256 = "0c6f717303ac76b91ef0a1bc1b0b3a692827e79aa8513a6c701a41fcab92efd0",
    tickRate = 120,
    joints = rig.JointCount,
    clips = rig.Clips.Clips.Length,
    segments = segments.Select(s => new { s.End, move = new[] { s.MoveX, s.MoveZ }, s.Walk, gaze = s.Gaze is { } g ? new[] { g.X, g.Y } : null }),
    ticks,
};
File.WriteAllText(outPath, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = false }));
Console.WriteLine($"ok: {total} тиков, клипов {rig.Clips.Clips.Length}, суставов {rig.JointCount} → {outPath}");

static class Scenario
{
    public sealed record Segment(int End, float MoveX, float MoveZ, bool Walk, Vector2? Gaze);

    // Стоит; бег вперёд лицом вперёд; шаг вбок (+X) лицом вперёд; бег по диагонали назад, взгляд вправо-вбок;
    // стоп; бег назад с разворотом взгляда.
    public static readonly Segment[] Segments =
    [
        new(60, 0, 0, false, null),
        new(300, 0, 1, false, new Vector2(0, 30)),
        new(420, 1, 0, true, null),
        new(600, -1, -1, false, new Vector2(30, 0)),
        new(720, 0, 0, false, null),
        new(900, 0, -1, false, new Vector2(0, -30)),
    ];
}
