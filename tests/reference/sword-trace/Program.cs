using System.Numerics;
using System.Text.Json;
using Sword.Combat;
using Sword.Content;

// Трассы SWORD 2eab13b для тестов SWORD2 (ReferenceTraceTests). Третий аргумент — сценарий:
// player (по умолчанию, этап 2) — игрок (Agile) без Motion Matching: встроенный KinematicSteer, FighterAnimator с
//   LegacyLocomotion, перенос корня (KinematicBegin), команды — как PlayerController.Think;
// turn (точка 4г) — боец не-Agile (враг): намерение задаёт сценарий (TurnTrace), KinematicSteer с поворотом на месте
//   клипом, KinematicBegin с YawDelta и RootDelta.
string pakPath = args[0], outPath = args[1];
string scenarioName = args.Length > 2 ? args[2] : "player";
using var pak = PakSource.Open(pakPath);
var rig = CharacterRig.TryLoad(pak) ?? throw new InvalidOperationException("нет рига");
if (scenarioName == "turn")
{
    TurnTrace.Run(rig, outPath);
    return;
}
if (scenarioName != "player") throw new ArgumentException($"сценарий «{scenarioName}»: player или turn");
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

/// <summary>
/// Трасса поворота на месте (точка 4г): боец не-Agile (враг) с настоящим FighterAnimator SWORD; намерение (желаемая
/// скорость, курс-цель) задаёт сценарий — в SWORD2 его дают команды врагу (EnemyCommand) вместо ИИ. Курс-цель скачет
/// стоя больше чем на 45° (поворот клипом влево, вправо, разворот), меньше 45° (2,5 рад/с), поворот прерывается ходьбой,
/// поворот на ходу (12 рад/с), остановка с отставшим курсом, поворот при медленном дрейфе. Копия — KinematicSteer и
/// KinematicBegin (DuelArena.Kinematic.cs), Fighter.DirToLocal; сверена построчно.
/// </summary>
static class TurnTrace
{
    public sealed record Segment(int End, float VelX, float VelZ, float FacingGoal);

    /// <summary>
    /// Начало — корень и курс врага места 1 в SWORD2 при сборке мира (arena.enemies = 1, sim.seed = 1): расстановка
    /// SWORD2 (счётчиковый ГСЧ) не та, что у Random(7) SWORD, поэтому трасса стартует оттуда же, где SWORD2.
    /// </summary>
    public static readonly Vector3 StartRoot = new(-4.9501686f, 0, -4.690737f);
    public const float StartFacing = 0.81230116f;

    public static readonly Segment[] Segments =
    [
        new(60, 0, 0, 0.8123f),        // стоит, курс-цель почти на месте — 2,5 рад/с
        new(240, 0, 0, 2.4f),          // +1,59 рад стоя — поворот клипом влево
        new(420, 0, 0, 1.1f),          // −1,3 рад — вправо
        new(660, 0, 0, 4.2f),          // +3,1 рад — разворот
        new(720, 0, 0, 4.7f),          // +0,5 рад (меньше 45°) — 2,5 рад/с
        new(750, 0, 0, 6.4f),          // +1,7 рад — поворот клипом начался…
        new(900, -1.1f, -1.1f, 6.4f),  // …и прерван ходьбой: клип гаснет, курс — контроллер
        new(1000, -1.1f, -1.1f, 5.4f), // поворот на ходу — 12 рад/с
        new(1150, 0, 0, 8f),           // остановка с отставшим на 2,6 рад курсом — поворот, когда встал
        new(1300, 0, 0, 7.2f),         // −0,8 рад — на грани 45°
        new(1440, 0.2f, 0.1f, 9.5f),   // медленный дрейф (стоит) и +2,3 рад
    ];

    private const float dt = 1f / 120, KinTurnRate = 12f, KinAccel = 14f, KinCarryAccel = 1.5f,
        KinIdleTurnRate = 2.5f, KinTurnAnimMin = 45 * MathF.PI / 180;

    private static T Private<T>(FighterAnimator anim, string field) =>
        (T)typeof(FighterAnimator).GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(anim)!;

    public static void Run(CharacterRig rig, string outPath)
    {
        var anim = new FighterAnimator(rig) { Agile = false };
        var root = StartRoot;
        float facing = StartFacing;
        var rootVel = Vector3.Zero;
        var ticks = new List<object>();
        int total = Segments[^1].End, turns = 0, idleTicks = 0, drivenTicks = 0, snapshots = 0;
        var turnClips = new SortedSet<string>(StringComparer.Ordinal);
        for (int t = 0; t < total; t++)
        {
            var seg = Segments.First(s => t < s.End);
            var desiredVel = new Vector3(seg.VelX, 0, seg.VelZ);
            float facingGoal = seg.FacingGoal;

            // KinematicSteer (DuelArena.Kinematic.cs), боец не-Agile
            float df = MathF.IEEERemainder(facingGoal - facing, MathF.Tau);
            bool standing = new Vector2(desiredVel.X, desiredVel.Z).LengthSquared() < 0.09f
                            && new Vector2(rootVel.X, rootVel.Z).LengthSquared() < 0.25f;
            bool idleTurn = !anim.Agile && standing && anim.Kind == FighterAnimator.ActionKind.None;
            if (idleTurn && MathF.Abs(df) > KinTurnAnimMin)
                if (anim.StartTurn(df)) turns++;
            if (!anim.TurnDrivesYaw)
            {
                float turn = idleTurn ? KinIdleTurnRate : KinTurnRate * anim.TurnScale;
                facing += Math.Clamp(df, -turn * dt, turn * dt);
                if (idleTurn) idleTicks++;
            }
            else drivenTicks++;
            var desired = new Vector3(desiredVel.X, 0, desiredVel.Z) * anim.MoveScale;
            float accel = anim.MomentumHold > 0 ? KinCarryAccel : KinAccel;
            rootVel += (desired - rootVel) * MathF.Min(1, accel * dt);
            rootVel.Y = 0;

            // KinematicBegin: anim.Tick по новой скорости, курс клипом, перенос корня и сдвиг клипом действия
            var local = Vector3.Transform(rootVel, Quaternion.CreateFromAxisAngle(Vector3.UnitY, -facing));
            local = new Vector3(-local.X, local.Y, local.Z);
            float fadeBefore = Private<float>(anim, "_snapFade");
            anim.Tick(dt, local);
            if (Private<float>(anim, "_snapFade") < fadeBefore) snapshots++;
            facing += anim.YawDelta;
            bool evading = anim.Kind == FighterAnimator.ActionKind.Evade || anim.EvadeCarrying;
            float control = !evading ? 1 : anim.EvadeCarrying ? 0 : anim.EvadeRecovery;
            root += rootVel * (dt * control);
            if (anim.RootDelta != Vector3.Zero)
            {
                var worldDelta = Vector3.Transform(anim.RootDelta, Quaternion.CreateFromAxisAngle(Vector3.UnitY, facing));
                root += worldDelta;
                if (evading && dt > 0)
                    rootVel = Vector3.Lerp(new Vector3(worldDelta.X, 0, worldDelta.Z) / dt, rootVel, control);
            }
            root.Y = 0;

            ulong h = 14695981039346656037;
            foreach (var m in anim.Component)
                foreach (float x in new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 })
                    h = (h ^ (uint)BitConverter.SingleToInt32Bits(x)) * 1099511628211;
            int clip = anim.Locomotion is LegacyLocomotion legacy ? legacy.Clip : -1;
            bool acting = anim.Kind != FighterAnimator.ActionKind.None;
            string actionName = acting ? rig.Clips.Clips[Private<int>(anim, "_actionClip")].Name : "";
            if (acting) turnClips.Add(actionName);
            ticks.Add(new
            {
                tick = t + 1,
                root = new[] { root.X, root.Z },
                facing,
                clip = clip >= 0 ? rig.Clips.Clips[clip].Name : "",
                action = anim.Kind.ToString(),
                actionClip = actionName,
                actionTime = acting ? anim.ActionTime : 0,
                actionWeight = Private<float>(anim, "_actionWeight"),
                turnScale = acting ? Private<float>(anim, "_turnScale") : 0,
                yawDelta = anim.YawDelta,
                rootDelta = new[] { anim.RootDelta.X, anim.RootDelta.Z },
                snapFade = Private<float>(anim, "_snapFade"),
                poseHash = h.ToString("X16"),
            });
        }

        var doc = new
        {
            purpose = "Трасса поворота на месте SWORD 2eab13b для сверки точки 4г SWORD2: боец не-Agile, намерение — сценарий " +
                      "(в SWORD2 — EnemyCommand), KinematicSteer с поворотом клипом, FighterAnimator (слой действий), KinematicBegin " +
                      "с YawDelta и RootDelta; хеш — FNV-1a по битам всех 16 чисел модельных матриц суставов после тика.",
            sourceCommit = "2eab13b2262f6b336c3fc9bdcd71fb3dfc5a51a4",
            pakSha256 = "0c6f717303ac76b91ef0a1bc1b0b3a692827e79aa8513a6c701a41fcab92efd0",
            tickRate = 120,
            joints = rig.JointCount,
            clips = rig.Clips.Clips.Length,
            start = new { root = new[] { StartRoot.X, StartRoot.Z }, facing = StartFacing },
            segments = Segments.Select(s => new { s.End, velocity = new[] { s.VelX, s.VelZ }, s.FacingGoal }),
            summary = new { turns, turnClips, idleTurnTicks = idleTicks, clipDrivenTicks = drivenTicks, snapshots },
            ticks,
        };
        File.WriteAllText(outPath, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = false }));
        Console.WriteLine($"ok: {total} тиков, поворотов клипом {turns} ({string.Join(", ", turnClips)}), тиков 2,5 рад/с {idleTicks}, " +
                          $"курс ведёт клип {drivenTicks}, снимков {snapshots} → {outPath}");
    }
}
