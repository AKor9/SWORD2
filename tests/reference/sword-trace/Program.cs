using System.Numerics;
using System.Text.Json;
using Sword.Combat;
using Sword.Content;
using Sword.Game.Gameplay;

// Трассы SWORD 2eab13b для тестов SWORD2 (ReferenceTraceTests). Третий аргумент — сценарий:
// player (по умолчанию, этап 2) — игрок (Agile) без Motion Matching: встроенный KinematicSteer, FighterAnimator с
//   LegacyLocomotion, перенос корня (KinematicBegin), команды — как PlayerController.Think;
// turn (точка 4г) — боец не-Agile (враг): намерение задаёт сценарий (TurnTrace), KinematicSteer с поворотом на месте
//   клипом, KinematicBegin с YawDelta и RootDelta;
// crowd (точка 4д) — толпа: игрок (Agile) и враги (не-Agile), копии EnemyAi.Think (без удара) и ResolveRoots, порядок
//   DuelArena.Tick, команды арены по ходу; сценарий и расстановка — четвёртым аргументом (crowd.input.json из SWORD2).
//   С точки 5б — ещё капсулы тела: копии формы при постройке (Fighter.Build: Radii, полудлины из ComputeTargets и
//   IdealFoot) и PlaceBodies (без клинка, BodyPlacer, фиксации стоп) с настоящими RigidBody, Xpbd.RotationVector, Terrain.
// strikes (точка 6б) — не тики, а ассеты настоящего CharacterRig: клинки всех мечей пака (MakeWeapon) и таблица ударов
//   слотов slash1–4, stab1–3 (Analyse → StrikeWindows.Find по кончику меча по умолчанию, клип _Blocked, длительность, укол, слой).
// actions (точка 6в) — слой действий: настоящий FighterAnimator игрока (Agile) и врага (не-Agile) и PlayerAttackPicker; удары,
//   связка, жест, укол, снимок позы при прерывании, обрезка хвоста удара на бегу; копии PlayerController (буфер удара),
//   KinematicSteer/KinematicBegin (ActionsTrace).
string pakPath = args[0], outPath = args[1];
string scenarioName = args.Length > 2 ? args[2] : "player";
using var pak = PakSource.Open(pakPath);
var rig = CharacterRig.TryLoad(pak) ?? throw new InvalidOperationException("нет рига");
if (scenarioName == "strikes")
{
    StrikesTrace.Run(rig, outPath);
    return;
}
if (scenarioName == "actions")
{
    ActionsTrace.Run(rig, outPath);
    return;
}
if (scenarioName == "turn")
{
    TurnTrace.Run(rig, outPath);
    return;
}
if (scenarioName == "crowd")
{
    CrowdTrace.Run(rig, args.Length > 3 ? args[3] : throw new ArgumentException("crowd: четвёртый аргумент — вход (crowd.input.json)"), outPath);
    return;
}
if (scenarioName != "player") throw new ArgumentException($"сценарий «{scenarioName}»: player, turn, crowd, strikes или actions");
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

/// <summary>
/// Таблица оружия и ударов (точка 6б) — из настоящего <c>CharacterRig</c> SWORD 2eab13b, ничего не копируется: оружие пака
/// (<c>Weapons</c>: имя, вершин меша, клинок в системе hand_r — <c>MakeWeapon</c>), оружие по умолчанию и его клинок (по
/// нему размечены удары), удары слотов slash1–4 и stab1–3 (<c>Slashes</c>, <c>Stabs</c>: клип, клип <c>_Blocked</c>,
/// длительность, кадров, укол, слой верха, окна <c>StrikeWindow</c>). Для поиска расхождения — хеш кончика клинка по кадрам
/// клипа (FNV-1a по битам x, y, z): это копия закрытого <c>CharacterRig.Tips</c> (сэмпл клипа, <c>ToComponent</c>, кончик в
/// системе кисти), в окна она не входит — окна считает сам <c>CharacterRig</c>.
/// </summary>
static class StrikesTrace
{
    private static readonly string[] Slots = ["slash1", "slash2", "slash3", "slash4", "stab1", "stab2", "stab3"];

    public static void Run(CharacterRig rig, string outPath)
    {
        var attacks = rig.Slashes.Concat(rig.Stabs).Select((a, i) => new
        {
            slot = Slots[i],
            clip = rig.Clips.Clips[a.Clip].Name,
            blockedClip = a.BlockedClip >= 0 ? rig.Clips.Clips[a.BlockedClip].Name : null,
            duration = a.Duration,
            frames = rig.Clips.Clips[a.Clip].Count,
            thrust = a.Thrust,
            upper = a.Upper,
            tipsHash = TipsHash(rig, a.Clip),
            windows = a.Windows.Select(w => new
            {
                start = w.Start, end = w.End, peakTime = w.PeakTime, peakSpeed = w.PeakSpeed, swingDir = new[] { w.SwingDir.X, w.SwingDir.Y },
            }),
        }).ToList();
        var doc = new
        {
            purpose = "Таблица оружия и ударов SWORD 2eab13b (CharacterRig: MakeWeapon, Analyse, StrikeWindows.Find) для сверки точки 6б " +
                      "SWORD2: клинки всех мечей пака в системе hand_r, окна ударов слотов по кончику меча по умолчанию; tipsHash — " +
                      "FNV-1a по битам кончика клинка по кадрам клипа.",
            sourceCommit = "2eab13b2262f6b336c3fc9bdcd71fb3dfc5a51a4",
            pakSha256 = "0c6f717303ac76b91ef0a1bc1b0b3a692827e79aa8513a6c701a41fcab92efd0",
            sampleRate = rig.Clips.SampleRate,
            hand = rig.Mesh.JointNames[rig.Hand],
            defaultWeapon = rig.Weapons[rig.DefaultWeapon].Name,
            bladeBase = new[] { rig.BladeBase.X, rig.BladeBase.Y, rig.BladeBase.Z },
            bladeTip = new[] { rig.BladeTip.X, rig.BladeTip.Y, rig.BladeTip.Z },
            weapons = rig.Weapons.Select(w => new
            {
                name = w.Name,
                vertices = w.Mesh.Vertices.Length,
                joints = w.Mesh.JointCount,
                bladeBase = new[] { w.BladeBase.X, w.BladeBase.Y, w.BladeBase.Z },
                bladeTip = new[] { w.BladeTip.X, w.BladeTip.Y, w.BladeTip.Z },
            }),
            attacks,
        };
        File.WriteAllText(outPath, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = false }));
        Console.WriteLine($"ok: оружия {rig.Weapons.Length} ({string.Join(", ", rig.Weapons.Select(w => w.Name))}), по умолчанию " +
            $"{rig.Weapons[rig.DefaultWeapon].Name}; ударов {attacks.Count}, окон {attacks.Sum(a => a.windows.Count())} → {outPath}");
    }

    /// <summary>Хеш кончика клинка по кадрам клипа — копия <c>CharacterRig.Tips</c>, только для диагностики расхождения.</summary>
    private static string TipsHash(CharacterRig rig, int clip)
    {
        var c = rig.Clips.Clips[clip];
        var rot = new Quaternion[rig.JointCount];
        var pos = new Vector3[rig.JointCount];
        var comp = new Matrix4x4[rig.JointCount];
        ulong h = 14695981039346656037;
        void Add(float x) => h = (h ^ (uint)BitConverter.SingleToInt32Bits(x)) * 1099511628211;
        for (int f = 0; f < c.Count; f++)
        {
            rig.Clips.Sample(clip, f / rig.Clips.SampleRate, rot, pos);
            rig.Clips.ToComponent(rot, pos, comp);
            var tip = Vector3.Transform(rig.BladeTip, comp[rig.Hand]);
            Add(tip.X); Add(tip.Y); Add(tip.Z);
        }
        return h.ToString("X16");
    }
}

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

/// <summary>
/// Трасса толпы (точка 4д): игрок (Agile) и враги (не-Agile) с настоящим FighterAnimator SWORD на месте каждого бойца
/// (33 аниматора заводятся сразу, как AttachRig; постройка — Reset, как ResetAnimator в Fighter.Build: аниматор места
/// переживает PageDown/PageUp и сброс). Тик — порядок DuelArena.Tick: Time += dt; PlayerController.Think; EnemyAi.Think
/// врагов по местам 1…EnemyCount; KinematicBegin всех бойцов 0…ActiveCount − 1; ResolveRoots. Команды арены
/// (SetEnemyCount, Reset) — до тика, как из цикла кадра SWORD. Копии — EnemyAi.Think (строки движения: без удара, таймера и
/// Dead — этап 6; SetBlock(false) вызывается), PlayerController.Think (движение и взгляд), KinematicSteer/KinematicBegin,
/// Fighter.DirToLocal, ResolveRoots (с настоящими ArenaLayout.Pillars и ArenaRadius SWORD), Fighter.Build (поля движения) —
/// сверены построчно с 2eab13b. Расстановка (корень, курс, зерно постройки) — из входа: SWORD2 (Random(7) SWORD не
/// воспроизводится).
/// </summary>
static class CrowdTrace
{
    private const int MaxFighters = 33;
    private const float dt = 1f / 120, RunSpeed = 4.2f, WalkSpeed = 1.6f, KinTurnRate = 12f, KinAccel = 14f, KinCarryAccel = 1.5f,
        KinIdleTurnRate = 2.5f, KinTurnAnimMin = 45 * MathF.PI / 180;
    // EnemyAi SWORD 2eab13b
    private const float KeepDistance = 1.8f, Personal = 1.4f;

    private sealed class F(int index, FighterAnimator anim)
    {
        public readonly int Index = index;
        public readonly FighterAnimator Animator = anim;
        public Vector3 Root, RootVel, DesiredVel;
        public float Facing, FacingGoal, Seed;

        // 5б: тела бойца (Fighter.Bodies — 14 RigidBody, клинок 11 не ставится) и KinPlaced; визуального смещения
        // (VisualOffset, VisualYaw — Motion Matching, этап 8) и BodyY (FootPlacement, этап 9) нет — нули и null, как после
        // ResetAnimator.
        public readonly Sword.Physics.RigidBody[] Bodies = [.. Enumerable.Range(0, BodySegs.Count).Select(_ => new Sword.Physics.RigidBody())];
        public bool KinPlaced;
        public Vector2 VisualOffset;
        public float VisualYaw;
    }

    private sealed record Segment(int End, float MoveX, float MoveZ, bool Walk, Vector2? Gaze);
    private sealed record ArenaCmd(int At, string Action, int Enemies);
    private sealed record Spawn(int Tick, int Slot, float X, float Z, float Facing, float Seed);

    private static readonly F[] _f = new F[MaxFighters];
    private static int EnemyCount;
    private static int ActiveCount => EnemyCount + 1;
    private static float Time;
    private static Vector3 _cursor;
    private static bool _hasCursor;
    private static long _pairs, _pillars, _edges;

    public static void Run(CharacterRig rig, string inputPath, string outPath)
    {
        using var input = JsonDocument.Parse(File.ReadAllText(inputPath));
        var inp = input.RootElement;
        var segments = inp.GetProperty("segments").EnumerateArray().Select(s => new Segment(s.GetProperty("End").GetInt32(),
            s.GetProperty("move")[0].GetSingle(), s.GetProperty("move")[1].GetSingle(), s.GetProperty("Walk").GetBoolean(),
            s.GetProperty("gaze").ValueKind == JsonValueKind.Null ? null : new Vector2(s.GetProperty("gaze")[0].GetSingle(), s.GetProperty("gaze")[1].GetSingle()))).ToArray();
        var arena = inp.GetProperty("arena").EnumerateArray().Select(a => new ArenaCmd(a.GetProperty("At").GetInt32(),
            a.GetProperty("action").GetString()!, a.GetProperty("Enemies").GetInt32())).ToArray();
        var spawns = inp.GetProperty("spawns").EnumerateArray().Select(s => new Spawn(s.GetProperty("Tick").GetInt32(), s.GetProperty("Slot").GetInt32(),
            s.GetProperty("root")[0].GetSingle(), s.GetProperty("root")[1].GetSingle(), s.GetProperty("Facing").GetSingle(), s.GetProperty("Seed").GetSingle()))
            .ToDictionary(s => (s.Tick, s.Slot));
        int usedSpawns = 0, buildTick = 0;
        var shapes = new List<object>();

        // Fighter.Build (поля движения; оружие и мышцы — не у кинематического бойца) и ResetAnimator; с 5б — форма тела
        // (SetShape: Radii и полудлины процедурной позы постройки) и PlaceBuilt (PlaceBodies(f, 0) — первая постановка без
        // скорости). Постановка тел в процедурную позу внутри Build (b.Place(…, TargetRot)) не копируется: PlaceBuilt сразу
        // ставит те же тела заново (RigidBody.Place переписывает все поля постановки и скорости).
        void Build(F f, Vector3 pos, float facing, float seed)
        {
            f.Facing = f.FacingGoal = facing;
            f.Seed = seed;
            f.DesiredVel = Vector3.Zero;
            f.Root = new Vector3(pos.X, 0, pos.Z);
            f.RootVel = Vector3.Zero;
            f.Animator.Reset();
            f.KinPlaced = false;
            f.VisualOffset = Vector2.Zero;
            f.VisualYaw = 0;
            BuildShape.Apply(f.Bodies, pos, facing, f.Root);
            shapes.Add(new { tick = buildTick, slot = f.Index, radius = BodyOrder.Select(s => f.Bodies[s].Radius).ToArray(), halfLength = BodyOrder.Select(s => f.Bodies[s].HalfLen).ToArray() });
            PlaceBodies(f, 0);   // DuelArena.PlaceBuilt
        }

        // DuelArena.Spawn: корень, курс и зерно — расстановка SWORD2 этого тика и места.
        void SpawnAt(int tick, F f)
        {
            if (!spawns.TryGetValue((tick, f.Index), out var s)) throw new InvalidDataException($"нет расстановки: тик {tick}, место {f.Index}");
            usedSpawns++;
            buildTick = tick;
            Build(f, new Vector3(s.X, 0, s.Z), s.Facing, s.Seed);
        }

        // DuelArena.Reset(enemies): игрок в центр курсом 0, все места заново (неактивные — только Reset аниматора: их корень
        // SWORD задаёт снова, когда место станет активным).
        void Reset(int tick, int enemies)
        {
            EnemyCount = Math.Clamp(enemies, 0, MaxFighters - 1);
            if (!spawns.TryGetValue((tick, 0), out var p)) throw new InvalidDataException($"нет постройки игрока: тик {tick}");
            if (p.X != 0 || p.Z != 0 || p.Facing != 0) throw new InvalidDataException($"игрок построен не в центре: {p}");
            usedSpawns++;
            buildTick = tick;
            Build(_f[0], Vector3.Zero, 0, p.Seed);
            for (int i = 1; i < MaxFighters; i++)
                if (i <= EnemyCount) SpawnAt(tick, _f[i]);
                else _f[i].Animator.Reset();
        }

        // DuelArena.SetEnemyCount(n)
        void SetEnemyCount(int tick, long n)
        {
            int old = EnemyCount;
            EnemyCount = (int)Math.Clamp(n, 0, MaxFighters - 1);
            for (int i = old + 1; i <= EnemyCount; i++) SpawnAt(tick, _f[i]);
        }

        // AttachRig: аниматор каждому месту (фиксации стоп нет — путь SWORD_FOOTLOCK=off: FootPinningEnabled = false, PinFeet
        // только сбрасывает FootLock; фиксация и постановка стоп — этап 9), затем Reset(EnemyCount).
        _rig = rig;
        for (int i = 0; i < MaxFighters; i++) _f[i] = new F(i, new FighterAnimator(rig) { Agile = i == 0, FootPinningEnabled = false });
        Reset(0, inp.GetProperty("enemies").GetInt32());

        var ticks = new List<object>();
        var samples = new List<object>();
        float maxSpeed = 0;
        int total = segments[^1].End, turns = 0, arenaCommands = 0, maxActive = 0;
        var actionClipField = typeof(FighterAnimator).GetField("_actionClip", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        for (int t = 0; t < total; t++)
        {
            foreach (var a in arena.Where(a => a.At == t))
            {
                arenaCommands++;
                switch (a.Action)
                {
                    case "ChangeEnemies": SetEnemyCount(t + 1, (long)EnemyCount + a.Enemies); break;
                    case "SetEnemies": SetEnemyCount(t + 1, a.Enemies); break;
                    case "Reset": Reset(t + 1, EnemyCount); break;
                    default: throw new InvalidDataException($"команда арены {a.Action}");
                }
            }

            // DuelArena.Tick
            Time += dt;
            PlayerThink(_f[0], segments.First(s => t < s.End));
            for (int i = 1; i <= EnemyCount; i++) EnemyThink(_f[i]);
            for (int i = 0; i < ActiveCount; i++)
                if (KinematicBegin(_f[i])) turns++;
            ResolveRoots();
            for (int i = 0; i < ActiveCount; i++) PlaceBodies(_f[i], dt);   // Kinematic(f): все живые
            maxActive = Math.Max(maxActive, ActiveCount);

            int n = ActiveCount;
            var x = new float[n];
            var z = new float[n];
            var facing = new float[n];
            var clip = new int[n];
            var action = new string[n];
            var actionClip = new int[n];
            var poseHash = new string[n];
            var bodyHash = new string[n];
            bool sample = SampleTicks.Contains(t + 1);
            var sampled = sample ? new List<object>() : null;
            for (int i = 0; i < n; i++)
            {
                var f = _f[i];
                var anim = f.Animator;
                x[i] = f.Root.X;
                z[i] = f.Root.Z;
                facing[i] = f.Facing;
                clip[i] = anim.Locomotion is LegacyLocomotion legacy ? legacy.Clip : -1;
                bool acting = anim.Kind != FighterAnimator.ActionKind.None;
                action[i] = acting ? anim.Kind.ToString() : "";
                actionClip[i] = acting ? (int)actionClipField.GetValue(anim)! : -1;
                ulong h = 14695981039346656037;
                foreach (var m in anim.Component)
                    foreach (float v in new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 })
                        h = (h ^ (uint)BitConverter.SingleToInt32Bits(v)) * 1099511628211;
                poseHash[i] = h.ToString("X16");
                bodyHash[i] = BodyHash(f.Bodies).ToString("X16");
                foreach (var b in f.Bodies) maxSpeed = MathF.Max(maxSpeed, b.Vel.Length());
                sampled?.Add(BodyOrder.Select(s => Full(f.Bodies[s])).ToArray());
            }
            float[] Pelvis(int slot) => [_f[slot].Bodies[BodySegs.Pelvis].Pos.X, _f[slot].Bodies[BodySegs.Pelvis].Pos.Y, _f[slot].Bodies[BodySegs.Pelvis].Pos.Z];
            ticks.Add(new { tick = t + 1, x, z, facing, clip, action, actionClip, poseHash, bodyHash, pelvis0 = Pelvis(0), pelvis1 = Pelvis(1), pelvis12 = Pelvis(12) });
            if (sampled is not null) samples.Add(new { tick = t + 1, bodies = sampled });
        }
        if (usedSpawns != spawns.Count) throw new InvalidDataException($"расстановок во входе {spawns.Count}, использовано {usedSpawns}");

        var doc = new
        {
            purpose = "Трасса толпы SWORD 2eab13b для сверки точки 4д SWORD2: игрок (Agile) и враги (не-Agile) с настоящим FighterAnimator, " +
                      "копии EnemyAi.Think (без удара), PlayerController.Think, KinematicSteer/KinematicBegin и ResolveRoots в порядке DuelArena.Tick, " +
                      "команды арены по ходу; расстановка — SWORD2 (input). На тик — по местам 0…EnemyCount: корень (x, z), курс, клип локомоции " +
                      "(номер в clipNames), действие и его клип, хеш позы (FNV-1a по битам всех 16 чисел модельных матриц суставов). " +
                      "Точка 5б: копии формы тела при постройке (Fighter.Build) и PlaceBodies (без клинка, BodyPlacer, фиксации стоп) с настоящими " +
                      "RigidBody, Xpbd.RotationVector и Terrain; на тик по местам — bodyHash (FNV-1a по битам 13 капсул в порядке тел SWORD2: " +
                      "Pos, Rot, PrevPos, PrevRot, Vel, AngVel, Radius, HalfLen), центр капсулы таза мест 0, 1 и 12 (pelvis0, pelvis1, pelvis12 — " +
                      "ряды метрик манифеста bodies); shapes — форма каждой " +
                      "постройки (радиусы и полудлины 13 капсул); samples — все значения капсул на тиках выборки.",
            sourceCommit = "2eab13b2262f6b336c3fc9bdcd71fb3dfc5a51a4",
            pakSha256 = "0c6f717303ac76b91ef0a1bc1b0b3a692827e79aa8513a6c701a41fcab92efd0",
            tickRate = 120,
            joints = rig.JointCount,
            clips = rig.Clips.Clips.Length,
            clipNames = rig.Clips.Clips.Select(c => c.Name),
            input = inp,
            summary = new { ticks = total, maxFighters = maxActive, arenaCommands, spawns = usedSpawns, pairs = _pairs, pillars = _pillars, edges = _edges, turns, placements = _placements, maxSpeed },
            bodyOrder = BodyOrder,
            shapes,
            samples,
            ticks,
        };
        File.WriteAllText(outPath, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = false }));
        Console.WriteLine($"ok: {total} тиков, бойцов до {maxActive}, команд арены {arenaCommands}, построек {usedSpawns}, пар {_pairs}, " +
                          $"колонн {_pillars}, края {_edges}, поворотов клипом {turns}, постановок тел {_placements}, скорость капсул до {maxSpeed} м/с → {outPath}");
    }

    private static CharacterRig _rig = null!;
    private static long _placements;

    /// <summary>Тела бойца SWORD2 (сочленение из 13) — сегменты SWORD по возрастанию без клинка 11.</summary>
    private static readonly int[] BodyOrder = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 12, 13];

    /// <summary>Тики с полными значениями капсул: первые, после команд арены (постройки) и конец.</summary>
    private static readonly HashSet<int> SampleTicks = [1, 2, 481, 601, 1241, 1401, 1402, 2701, 3000];

    /// <summary>Хеш тела бойца на тик: FNV-1a по битам 13 капсул в порядке <see cref="BodyOrder"/>.</summary>
    private static ulong BodyHash(Sword.Physics.RigidBody[] bodies)
    {
        ulong h = 14695981039346656037;
        void Add(float x) => h = (h ^ (uint)BitConverter.SingleToInt32Bits(x)) * 1099511628211;
        foreach (int s in BodyOrder)
        {
            var b = bodies[s];
            Add(b.Pos.X); Add(b.Pos.Y); Add(b.Pos.Z);
            Add(b.Rot.X); Add(b.Rot.Y); Add(b.Rot.Z); Add(b.Rot.W);
            Add(b.PrevPos.X); Add(b.PrevPos.Y); Add(b.PrevPos.Z);
            Add(b.PrevRot.X); Add(b.PrevRot.Y); Add(b.PrevRot.Z); Add(b.PrevRot.W);
            Add(b.Vel.X); Add(b.Vel.Y); Add(b.Vel.Z);
            Add(b.AngVel.X); Add(b.AngVel.Y); Add(b.AngVel.Z);
            Add(b.Radius); Add(b.HalfLen);
        }
        return h;
    }

    private static float[] Full(Sword.Physics.RigidBody b) =>
    [
        b.Pos.X, b.Pos.Y, b.Pos.Z, b.Rot.X, b.Rot.Y, b.Rot.Z, b.Rot.W, b.PrevPos.X, b.PrevPos.Y, b.PrevPos.Z,
        b.PrevRot.X, b.PrevRot.Y, b.PrevRot.Z, b.PrevRot.W, b.Vel.X, b.Vel.Y, b.Vel.Z, b.AngVel.X, b.AngVel.Y, b.AngVel.Z,
        b.Radius, b.HalfLen,
    ];

    // Fighter.PoseRoot / PoseFacing / PoseWorld (VisualOffset = 0, VisualYaw = 0, BodyY = null) и DuelArena.KinWorld.
    private static Vector3 PoseRoot(F f)
    {
        var o = Sword.Anim.Loco.Pose2.Rotate(f.VisualOffset, f.Facing);
        return f.Root + new Vector3(o.X, 0, o.Y);
    }

    private static float PoseFacing(F f) => f.Facing + f.VisualYaw;

    private static Matrix4x4 KinWorld(Vector3 root, float facing, float y)
    {
        var m = Matrix4x4.CreateFromQuaternion(Quaternion.CreateFromAxisAngle(Vector3.UnitY, facing));
        m.Translation = new Vector3(root.X, y, root.Z);
        return m;
    }

    private static Matrix4x4 PoseWorld(F f)
    {
        var root = PoseRoot(f);
        float? bodyY = null;
        return KinWorld(root, PoseFacing(f), bodyY ?? Terrain.Height(root.X, root.Z));
    }

    // DuelArena.PlaceBodies (BodyPlacer = null: мир — PoseWorld, PinFeet — сброс FootLock; клинок — этап 6, JerkTrace и
    // PoseHash, BodiesPlaced — не переносятся; Detached и Severed — нет, этап 11). Сверено построчно с 2eab13b.
    private static void PlaceBodies(F f, float dt)
    {
        var rig = _rig;
        var anim = f.Animator;
        var comp = anim.Component;
        Matrix4x4 world;
        world = PoseWorld(f);
        anim.PinFeet(world, dt);
        var right = Vector3.Transform(new Vector3(-1, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, PoseFacing(f)));
        bool first = !f.KinPlaced;
        Vector3 P(int j) => Vector3.Transform(comp[j].Translation, world);

        var head = P(rig.Head);
        var neck = P(rig.Neck);
        Place(BodySegs.Pelvis, P(rig.Pelvis), P(rig.Spine));
        Place(BodySegs.Torso, P(rig.Spine), neck);
        Place(BodySegs.Head, head, head + Vector3.Normalize(head - neck) * 0.22f);
        Place(BodySegs.UArmL, P(rig.UArmL), P(rig.LArmL));
        Place(BodySegs.FArmL, P(rig.LArmL), P(rig.HandL));
        Place(BodySegs.UArmR, P(rig.UArmR), P(rig.LArmR));
        Place(BodySegs.FArmR, P(rig.LArmR), P(rig.Hand));
        Place(BodySegs.ThighL, P(rig.ThighL), P(rig.CalfL));
        Place(BodySegs.ShinL, P(rig.CalfL), P(rig.FootL));
        Place(BodySegs.FootL, P(rig.FootL), P(rig.BallL));
        Place(BodySegs.ThighR, P(rig.ThighR), P(rig.CalfR));
        Place(BodySegs.ShinR, P(rig.CalfR), P(rig.FootR));
        Place(BodySegs.FootR, P(rig.FootR), P(rig.BallR));
        f.KinPlaced = true;
        _placements++;

        void Place(int s, Vector3 a, Vector3 b)
        {
            var body = f.Bodies[s];
            var dir = b - a;
            if (dir.LengthSquared() < 1e-8f) dir = Vector3.UnitY;
            var pos = (a + b) * 0.5f;
            var rot = Sword.Anim.AnimMath.FromBasis(dir, right);
            if (first)
            {
                body.Place(pos, rot);
                return;
            }
            body.PrevPos = body.Pos;
            body.PrevRot = body.Rot;
            body.Vel = (pos - body.Pos) / dt;
            body.AngVel = Sword.Physics.Xpbd.RotationVector(body.Rot, rot) / dt;
            body.Pos = pos;
            body.Rot = rot;
        }
    }

    // PlayerController.Think: движение (WASD — направление сегмента), взгляд на курсор; блок не держат (SetBlock(false)).
    private static void PlayerThink(F p, Segment seg)
    {
        var wish = new Vector3(seg.MoveX, 0, seg.MoveZ);
        if (wish.LengthSquared() > 0) wish = Vector3.Normalize(wish);
        p.DesiredVel = wish * (seg.Walk ? WalkSpeed : RunSpeed);
        if (seg.Gaze is { } g)
        {
            _cursor = new Vector3(g.X, 0, g.Y);
            _hasCursor = true;
        }
        if (_hasCursor)
        {
            var to = _cursor - p.Root;
            to.Y = 0;
            if (to.LengthSquared() > 0.09f) p.FacingGoal = MathF.Atan2(to.X, to.Z);
        }
        p.Animator.SetBlock(false);
    }

    // EnemyAi.Think (строки движения; удар по таймеру и Dead — этап 6)
    private static void EnemyThink(F f)
    {
        var p = _f[0];
        var anim = f.Animator;
        var to = p.Root - f.Root;
        to.Y = 0;
        float dist = MathF.Max(to.Length(), 1e-3f);
        var dir = to / dist;
        f.FacingGoal = MathF.Atan2(dir.X, dir.Z);

        var side = new Vector3(dir.Z, 0, -dir.X);
        var desired = dir * Math.Clamp((dist - KeepDistance) * 2.5f, -2.0f, 3.2f) + side * MathF.Sin(Time * 0.7f + f.Seed * 10) * 0.7f;
        for (int j = 1; j <= EnemyCount; j++)
        {
            var o = _f[j];
            if (o == f) continue;
            var d = f.Root - o.Root;
            d.Y = 0;
            float l = d.Length();
            if (l < Personal && l > 1e-3f) desired += d / l * (Personal - l) * 2f;
        }
        f.DesiredVel = desired;
        anim.SetBlock(false);
    }

    // KinematicBegin (KinematicSteer — встроенный: Motion Matching у игрока нет, как в трассе игрока). Возвращает, начат ли поворот клипом.
    private static bool KinematicBegin(F f)
    {
        var anim = f.Animator;
        bool turned = false;
        // KinematicSteer
        float df = MathF.IEEERemainder(f.FacingGoal - f.Facing, MathF.Tau);
        bool standing = new Vector2(f.DesiredVel.X, f.DesiredVel.Z).LengthSquared() < 0.09f
                        && new Vector2(f.RootVel.X, f.RootVel.Z).LengthSquared() < 0.25f;
        bool idleTurn = !anim.Agile && standing && anim.Kind == FighterAnimator.ActionKind.None;
        if (idleTurn && MathF.Abs(df) > KinTurnAnimMin)
            turned = anim.StartTurn(df);
        if (!anim.TurnDrivesYaw)
        {
            float turn = idleTurn ? KinIdleTurnRate : KinTurnRate * anim.TurnScale;
            f.Facing += Math.Clamp(df, -turn * dt, turn * dt);
        }
        var desired = new Vector3(f.DesiredVel.X, 0, f.DesiredVel.Z) * anim.MoveScale;
        float accel = anim.MomentumHold > 0 ? KinCarryAccel : KinAccel;
        f.RootVel += (desired - f.RootVel) * MathF.Min(1, accel * dt);
        f.RootVel.Y = 0;

        // anim.Tick(dt, f.DirToLocal(f.RootVel)); Fighter.DirToLocal — Mirror(поворот на −Facing)
        var local = Vector3.Transform(f.RootVel, Quaternion.CreateFromAxisAngle(Vector3.UnitY, -f.Facing));
        local = new Vector3(-local.X, local.Y, local.Z);
        anim.Tick(dt, local);
        f.Facing += anim.YawDelta;
        bool evading = anim.Kind == FighterAnimator.ActionKind.Evade || anim.EvadeCarrying;
        float control = !evading ? 1 : anim.EvadeCarrying ? 0 : anim.EvadeRecovery;
        f.Root += f.RootVel * (dt * control);
        if (anim.RootDelta != Vector3.Zero)
        {
            var worldDelta = Vector3.Transform(anim.RootDelta, Quaternion.CreateFromAxisAngle(Vector3.UnitY, f.Facing));
            f.Root += worldDelta;
            if (evading && dt > 0)
                f.RootVel = Vector3.Lerp(new Vector3(worldDelta.X, 0, worldDelta.Z) / dt, f.RootVel, control);
        }
        f.Root.Y = 0;
        return turned;
    }

    // DuelArena.ResolveRoots (мёртвых нет — этап 6)
    private static void ResolveRoots()
    {
        const float r = 0.35f;
        for (int iter = 0; iter < 2; iter++)
        {
            for (int i = 0; i < ActiveCount; i++)
            {
                var a = _f[i];
                for (int j = i + 1; j < ActiveCount; j++)
                {
                    var b = _f[j];
                    var d = a.Root - b.Root;
                    float l = d.Length();
                    if (l >= 2 * r || l < 1e-4f) continue;
                    _pairs++;
                    var n = d / l;
                    var push = n * ((2 * r - l) * 0.5f);
                    a.Root += push;
                    b.Root -= push;
                    float va = Vector3.Dot(a.RootVel, n), vb = Vector3.Dot(b.RootVel, n);
                    if (va < 0) a.RootVel -= n * va;
                    if (vb > 0) b.RootVel -= n * vb;
                }
                foreach (var pl in ArenaLayout.Pillars)
                {
                    var d = new Vector3(a.Root.X - pl.X, 0, a.Root.Z - pl.Y);
                    float l = d.Length(), rr = pl.Z + r;
                    if (l >= rr || l < 1e-4f) continue;
                    _pillars++;
                    var n = d / l;
                    a.Root = new Vector3(pl.X, 0, pl.Y) + n * rr;
                    float v = Vector3.Dot(a.RootVel, n);
                    if (v < 0) a.RootVel -= n * v;
                }
                float lim = ArenaLayout.ArenaRadius - 0.6f, dist = a.Root.Length();
                if (dist > lim)
                {
                    _edges++;
                    var n = a.Root / dist;
                    a.Root = n * lim;
                    float v = Vector3.Dot(a.RootVel, n);
                    if (v > 0) a.RootVel -= n * v;
                }
            }
        }
    }
}

/// <summary>
/// Форма тела при постройке (точка 5б) — копия части <c>Fighter.Build</c> SWORD 2eab13b: радиусы <c>Radii</c>, полудлины —
/// половина расстояния между концами сегмента процедурной позы (<c>ComputeTargets(new Vector3(pos.X, _groundY, pos.Z))</c>
/// после <c>_foot[leg] = IdealFoot(leg, 0)</c>), <c>SetShape(Masses[s], Radii[s], half)</c>. Состояние бойца — как сразу
/// после <c>Build</c> у кинематического бойца (живой рэгдолл у него не тикает, <c>AttachRig</c> — до первого тика):
/// <c>_torsoYaw</c>, наклоны, <c>_upperLead</c>, <c>PlanVel</c>, <c>RootVel</c>, <c>_captureShift</c> — 0; <c>HandLocal</c>,
/// <c>BladeDir</c> — значения <c>Build</c>; <c>TwoHanded</c> — true; стопы в опоре с курсом постройки. Сверено построчно
/// с <c>Fighter.cs</c> (<c>Build</c>, <c>Radii</c>, <c>Masses</c>), <c>Fighter.Pose.cs</c> (<c>ComputeTargets</c> — только
/// <c>_a/_b</c>), <c>Fighter.Locomotion.cs</c> (<c>IdealFoot</c>, константы) 2eab13b. Настоящие — <c>AnimMath.Ik2Bone</c>,
/// <c>SwordBody</c>, <c>Terrain</c>, <c>RigidBody.SetShape</c>.
/// </summary>
static class BuildShape
{
    private static readonly float[] Masses = [12, 20, 5, 2.5f, 2, 2.5f, 2, 8, 4.5f, 8, 4.5f, 1.4f, 1.2f, 1.2f];
    private static readonly float[] Radii = [.15f, .17f, .13f, .065f, .055f, .065f, .055f, .09f, .07f, .09f, .07f, .03f, .045f, .045f];
    private const float StandHeight = 0.95f, BladeLength = 1.0f, GripOffset = 0.15f;
    private const float AnkleY = 0.09f, FootLength = 0.2f, ToeOut = 0.12f;
    private const float MaxLead = 0.3f, StrideGrowth = 0.08f, LateralLead = 0.6f, StepReach = 0.55f;
    private const int Segs = BodySegs.Count;

    private static Vector3 Mirror(Vector3 v) => new(-v.X, v.Y, v.Z);

    public static void Apply(Sword.Physics.RigidBody[] bodies, Vector3 pos, float facing, Vector3 root)
    {
        // Fighter.Build: состояние после сброса
        float Facing = facing;
        float _torsoYaw = 0, _leanSide = 0, _leanFwd = 0, _upperLead = 0;
        var HandLocal = new Vector3(0.25f, 1.3f, 0.35f);
        var BladeDir = Vector3.Normalize(new Vector3(0.1f, 0.9f, 0.4f));
        bool TwoHanded = true;
        var Root = root;
        var RootVel = Vector3.Zero;
        var PlanVel = Vector3.Zero;
        var _captureShift = Vector3.Zero;
        float _groundY = Terrain.Height(pos.X, pos.Z);
        var _foot = new Vector3[2];
        var _footYaw = new float[2];
        for (int leg = 0; leg < 2; leg++)
        {
            _footYaw[leg] = facing;
            _foot[leg] = IdealFoot(leg, 0);
        }
        var _a = new Vector3[Segs];
        var _b = new Vector3[Segs];
        var _x = new Vector3[Segs];
        ComputeTargets(new Vector3(pos.X, _groundY, pos.Z));
        for (int s = 0; s < Segs; s++)
        {
            if (s == BodySegs.Weapon) continue;   // клинок — этап 6
            float half = s == BodySegs.Weapon ? (GripOffset + BladeLength) * 0.5f : Vector3.Distance(_a[s], _b[s]) * 0.5f;
            bodies[s].SetShape(Masses[s], Radii[s], half);
        }

        Vector3 IdealFoot(int leg, float lead)
        {
            var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, Facing);
            var offset = PlanVel * lead;
            float ol = offset.Length();
            float maxLead = MaxLead + StrideGrowth * RootVel.Length();
            if (ol > maxLead) offset *= maxLead / ol;
            var fwd = new Vector3(MathF.Sin(Facing), 0, MathF.Cos(Facing));
            float fwdPart = Vector3.Dot(offset, fwd);
            var sidePart = offset - fwd * fwdPart;
            float sideLen = sidePart.Length(), sideMax = maxLead * LateralLead;
            if (sideLen > sideMax) offset = fwd * fwdPart + sidePart * (sideMax / sideLen);
            offset += _captureShift;
            float reach = offset.Length();
            if (reach > StepReach) offset *= StepReach / reach;
            var p = Root + Vector3.Transform(Mirror(new Vector3(leg == 0 ? -0.12f : 0.12f, 0, 0)), yaw) + offset;
            return new Vector3(p.X, Terrain.Height(p.X, p.Z) + AnkleY, p.Z);
        }

        // Fighter.ComputeTargets — концы сегментов (_a, _b; оси _x — для полноты копии), без TargetRot, HandWorld, головы
        void ComputeTargets(Vector3 origin)
        {
            var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, Facing);
            var ty = Quaternion.CreateFromAxisAngle(Vector3.UnitY, _torsoYaw);
            Vector3 T(Vector3 v) => Vector3.Transform(v, ty);

            float pelvisY = StandHeight;
            float _pelvisHeight = pelvisY - 0.01f;

            _a[BodySegs.Pelvis] = new(0, pelvisY - 0.06f, 0); _b[BodySegs.Pelvis] = new(0, pelvisY + 0.1f, 0); _x[BodySegs.Pelvis] = Vector3.UnitX;
            var chest = new Vector3(_leanSide * 0.55f, pelvisY + 0.55f, _leanFwd * 0.55f);
            _a[BodySegs.Torso] = new(0, pelvisY + 0.16f, 0); _b[BodySegs.Torso] = chest; _x[BodySegs.Torso] = T(Vector3.UnitX);
            _a[BodySegs.Head] = chest + T(new(0, 0.2f, 0.02f)); _b[BodySegs.Head] = chest + T(new(0, 0.3f, 0.03f)); _x[BodySegs.Head] = T(Vector3.UnitX);

            var shR = chest + T(new(0.24f, -0.04f, 0));
            var shL = chest + T(new(-0.24f, -0.04f, 0));
            var (eR, hR) = Sword.Anim.AnimMath.Ik2Bone(shR, HandLocal, shR + T(new(0.35f, -0.6f, -0.25f)), 0.3f, 0.28f);
            _a[BodySegs.UArmR] = shR; _b[BodySegs.UArmR] = eR;
            _a[BodySegs.FArmR] = eR; _b[BodySegs.FArmR] = hR;

            _a[BodySegs.Weapon] = hR - BladeDir * GripOffset; _b[BodySegs.Weapon] = hR + BladeDir * BladeLength;

            var offTarget = TwoHanded ? hR - BladeDir * 0.13f : shL + T(new(-0.12f, -0.45f, 0.12f));
            var (eL, hL) = Sword.Anim.AnimMath.Ik2Bone(shL, offTarget, shL + T(new(-0.35f, -0.6f, -0.25f)), 0.3f, 0.28f);
            _a[BodySegs.UArmL] = shL; _b[BodySegs.UArmL] = eL;
            _a[BodySegs.FArmL] = eL; _b[BodySegs.FArmL] = hL;

            var yawUpper = Quaternion.CreateFromAxisAngle(Vector3.UnitY, Facing + _upperLead);
            for (int i = 0; i < Segs; i++)
            {
                if (i is >= BodySegs.ThighL and <= BodySegs.ShinR or BodySegs.FootL or BodySegs.FootR) continue;
                var q = i == BodySegs.Pelvis ? yaw : yawUpper;
                _a[i] = origin + Vector3.Transform(Mirror(_a[i]), q);
                _b[i] = origin + Vector3.Transform(Mirror(_b[i]), q);
            }

            var fwdW = new Vector3(MathF.Sin(Facing), 0, MathF.Cos(Facing));
            for (int leg = 0; leg < 2; leg++)
            {
                int th = leg == 0 ? BodySegs.ThighL : BodySegs.ThighR;
                var hip = origin + Vector3.Transform(Mirror(new Vector3(leg == 0 ? -0.1f : 0.1f, _pelvisHeight - 0.05f, 0)), yaw);
                var (knee, ankle) = Sword.Anim.AnimMath.Ik2Bone(hip, _foot[leg], hip + fwdW * 0.6f - Vector3.UnitY * 0.3f, Sword.Anim.SwordBody.ThighLength, Sword.Anim.SwordBody.ShinLength);
                _a[th] = hip; _b[th] = knee;
                _a[th + 1] = knee; _b[th + 1] = ankle;

                int foot = leg == 0 ? BodySegs.FootL : BodySegs.FootR;
                bool swing = false;   // _phase[leg] = Stance после Build
                var n = Terrain.Normal(ankle.X, ankle.Z);
                float fy = _footYaw[leg] + (leg == 0 ? ToeOut : -ToeOut);
                var ff = new Vector3(MathF.Sin(fy), 0, MathF.Cos(fy));
                var along = Vector3.Normalize(ff - n * Vector3.Dot(ff, n));
                float heelLift = MathF.Max(0, ankle.Y - Terrain.Height(ankle.X, ankle.Z) - AnkleY);
                _a[foot] = ankle;
                _b[foot] = ankle + along * FootLength + n * (swing ? 0.05f : Radii[foot] - AnkleY - heelLift);
            }
        }
    }
}

/// <summary>
/// Трасса слоя действий (точка 6в): настоящий <c>FighterAnimator</c> SWORD у игрока (<c>Agile</c>) и врага (не-<c>Agile</c>) — удары
/// разных слотов стоя и на ходу, связка из 4 (нажатие ждёт в буфере 0,3 с), жест, укол, прерывание видимого действия (снимок позы:
/// связка, удар врага из удара и из поворота), обрезка хвоста удара на бегу (<c>Agile</c>), удар врага на ходу (<c>MoveScale</c>
/// 0,35), шагом (доля клипа в ногах между 0 и 1). Настоящие — <c>FighterAnimator</c> (<c>StartAttack</c>, <c>StartTurn</c>,
/// <c>Tick</c>, <c>CanAttack</c>, <c>CanAct</c>, <c>TurnScale</c>, <c>MoveScale</c>, <c>MomentumHold</c>, <c>EvadeRecovery</c>) и
/// <c>PlayerAttackPicker</c> (<c>Pick</c>; укол — <c>Random(1)</c>: в паке у уколов один клип). Копии, сверенные построчно с
/// 2eab13b: <c>PlayerController.HandleInput</c>/<c>Think</c> (буфер удара, <c>anim.CanAttack</c>; рывка нет — он в SWORD2 новый),
/// <c>KinematicSteer</c>/<c>KinematicBegin</c> (как в трассе поворота, обе ветки), <c>Fighter.DirToLocal</c>; удар врага — как
/// <c>EnemyAi.Think</c> (<c>anim.CanAct</c> → <c>StartAttack</c>), но по сценарию (в SWORD2 — <c>EnemyCommand.Attack</c>), без
/// таймера и дистанции. Жест — в системе бойца (x вправо, y вверх); в <c>Pick</c> он идёт экранным сдвигом мыши (x, −y)·30, как
/// клиент SWORD. Бойцы далеко друг от друга, от колонн и края — столкновений корней нет; фиксация стоп (<c>PinFeet</c>) — этап 9.
/// </summary>
static class ActionsTrace
{
    public sealed record Move(int End, float X, float Z, bool Walk, Vector2? Gaze);
    public sealed record Press(int Tick, bool Stab, Vector2 Gesture);
    public sealed record EnemyMove(int End, float VelX, float VelZ, float FacingGoal);
    public sealed record EnemyStrike(int Tick, int Attack);

    /// <summary>Враг места 1 при сборке мира SWORD2 (arena.enemies = 1, sim.seed = 1) — как у трассы поворота.</summary>
    public static readonly Vector3 EnemyStart = new(-4.9501686f, 0, -4.690737f);
    public const float EnemyFacing = 0.81230116f;

    /// <summary>Игрок: стоит, связка из 4, жесты, укол, удар на бегу (хвост обрезан), удар и бег после окна, удар шагом.</summary>
    public static readonly Move[] PlayerMoves =
    [
        new(1040, 0, 0, false, new Vector2(0, 30)),
        new(1200, 0, 1, false, new Vector2(0, 40)),
        new(1320, 0, 0, false, null),
        new(1440, 0, 1, false, null),
        new(1560, 0.6f, 1, true, new Vector2(10, 60)),
        new(1680, 0, 0, false, null),
    ];

    /// <summary>Нажатия игрока (тик — индекс цикла, как сегменты): ЛКМ (жест — ноль или направление), Q.</summary>
    public static readonly Press[] PlayerPresses =
    [
        new(60, false, default), new(100, false, default), new(150, false, default), new(205, false, default),   // связка slash1–4
        new(520, false, new Vector2(1, -0.2f)), new(700, false, new Vector2(-1, -0.4f)), new(760, false, new Vector2(0.2f, -1)),   // жесты
        new(860, true, default),                                                                              // укол
        new(1080, false, default),                                                                            // на бегу
        new(1260, false, default),                                                                            // стоя, потом бег
        new(1450, false, default),                                                                            // шагом
    ];

    /// <summary>Враг: стоит курсом постройки, поворот (+1,6 рад) с ударом из поворота, ход, стоит.</summary>
    public static readonly EnemyMove[] EnemyMoves =
    [
        new(450, 0, 0, EnemyFacing),
        new(700, 0, 0, EnemyFacing + 1.6f),
        new(900, 0.8f, 0.6f, EnemyFacing + 1.6f),
        new(1680, 0, 0, EnemyFacing + 1.6f),
    ];

    /// <summary>Удары врага: номер удара таблицы (0–3 — slash1–4, 4–6 — stab1–3); не может действовать — ничего (как ИИ).</summary>
    public static readonly EnemyStrike[] EnemyStrikes =
    [
        new(30, 0), new(95, 1), new(250, 4), new(470, 2), new(750, 3), new(1000, 0), new(1070, 1),
    ];

    private const float dt = 1f / 120, RunSpeed = 4.2f, WalkSpeed = 1.6f, KinTurnRate = 12f, KinAccel = 14f, KinCarryAccel = 1.5f,
        KinIdleTurnRate = 2.5f, KinTurnAnimMin = 45 * MathF.PI / 180, SwingBufferTime = 0.3f;

    private sealed class F(FighterAnimator anim)
    {
        public readonly FighterAnimator Animator = anim;
        public Vector3 Root, RootVel, DesiredVel;
        public float Facing, FacingGoal;
    }

    private static T Private<T>(FighterAnimator anim, string field) =>
        (T)typeof(FighterAnimator).GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(anim)!;

    public static void Run(CharacterRig rig, string outPath)
    {
        var attacks = rig.Slashes.Concat(rig.Stabs).ToArray();
        var player = new F(new FighterAnimator(rig) { Agile = true });
        var enemy = new F(new FighterAnimator(rig) { Agile = false }) { Root = EnemyStart, Facing = EnemyFacing, FacingGoal = EnemyFacing };
        var picker = new PlayerAttackPicker();
        var rng = new Random(1);
        float time = 0, swingBuffer = 0;
        Vector2 swingMouse = default;
        bool swingStab = false, hasCursor = false;
        Vector3 cursor = default;
        int total = PlayerMoves[^1].End, playerStarts = 0, enemyStarts = 0, enemyIgnored = 0, cuts = 0;
        var kinds = new SortedSet<string>(StringComparer.Ordinal);
        var ticks = new List<object>();
        for (int t = 0; t < total; t++)
        {
            time += dt;   // DuelArena.Tick: Time += dt
            // PlayerController.HandleInput (нажатия кадра) и Think: буфер, ход, взгляд, удар
            foreach (var p in PlayerPresses)
                if (p.Tick == t)
                {
                    swingBuffer = SwingBufferTime;
                    if (p.Stab) swingStab = true;
                    else
                    {
                        swingMouse = p.Gesture == default ? default : new Vector2(p.Gesture.X, -p.Gesture.Y) * 30;
                        swingStab = false;
                    }
                }
            swingBuffer -= dt;
            var move = PlayerMoves.First(m => t < m.End);
            var wish = new Vector3(move.X, 0, move.Z);
            if (wish.LengthSquared() > 0) wish = Vector3.Normalize(wish);
            player.DesiredVel = wish * (move.Walk ? WalkSpeed : RunSpeed);
            if (move.Gaze is { } g)
            {
                cursor = new Vector3(g.X, 0, g.Y);
                hasCursor = true;
            }
            if (hasCursor)
            {
                var to = cursor - player.Root;
                to.Y = 0;
                if (to.LengthSquared() > 0.09f) player.FacingGoal = MathF.Atan2(to.X, to.Z);
            }
            player.Animator.SetBlock(false);
            if (swingBuffer > 0 && player.Animator.CanAttack)
            {
                swingBuffer = 0;
                player.Animator.StartAttack(picker.Pick(rig, time, swingMouse, swingStab, rng));
                playerStarts++;
            }
            // EnemyAi.Think: намерение — сценарий; удар — по сценарию, когда может действовать
            var em = EnemyMoves.First(m => t < m.End);
            enemy.DesiredVel = new Vector3(em.VelX, 0, em.VelZ);
            enemy.FacingGoal = em.FacingGoal;
            enemy.Animator.SetBlock(false);
            foreach (var s in EnemyStrikes)
                if (s.Tick == t)
                {
                    if (enemy.Animator.CanAct)
                    {
                        enemy.Animator.StartAttack(attacks[s.Attack]);
                        enemyStarts++;
                    }
                    else enemyIgnored++;
                }
            // KinematicBegin по местам: игрок, враг
            Begin(player);
            Begin(enemy);
            if (player.Animator.Kind != FighterAnimator.ActionKind.None && Private<bool>(player.Animator, "_cutRecovery")) cuts++;
            kinds.Add(player.Animator.Kind.ToString());
            kinds.Add(enemy.Animator.Kind.ToString());
            ticks.Add(new { tick = t + 1, player = Row(rig, player), enemy = Row(rig, enemy) });
        }

        var doc = new
        {
            purpose = "Трасса слоя действий SWORD 2eab13b для сверки точки 6в SWORD2: игрок (Agile) и враг (не-Agile) с настоящим " +
                      "FighterAnimator — удары стоя, на ходу и шагом, связка, жест, укол, прерывание (снимок позы), обрезка хвоста удара на " +
                      "бегу, удар врага из поворота и на ходу; PlayerAttackPicker настоящий, PlayerController (буфер удара), " +
                      "KinematicSteer/KinematicBegin — копии; хеш — FNV-1a по битам всех 16 чисел модельных матриц суставов после тика.",
            sourceCommit = "2eab13b2262f6b336c3fc9bdcd71fb3dfc5a51a4",
            pakSha256 = "0c6f717303ac76b91ef0a1bc1b0b3a692827e79aa8513a6c701a41fcab92efd0",
            tickRate = 120,
            joints = rig.JointCount,
            clips = rig.Clips.Clips.Length,
            enemyStart = new { root = new[] { EnemyStart.X, EnemyStart.Z }, facing = EnemyFacing },
            playerMoves = PlayerMoves.Select(m => new { m.End, move = new[] { m.X, m.Z }, m.Walk, gaze = m.Gaze is { } g ? new[] { g.X, g.Y } : null }),
            playerPresses = PlayerPresses.Select(p => new { p.Tick, p.Stab, gesture = new[] { p.Gesture.X, p.Gesture.Y } }),
            enemyMoves = EnemyMoves.Select(m => new { m.End, velocity = new[] { m.VelX, m.VelZ }, m.FacingGoal }),
            enemyStrikes = EnemyStrikes.Select(s => new { s.Tick, s.Attack }),
            summary = new
            {
                playerAttacks = player.Animator.AttackSerial, playerSnapshots = player.Animator.SnapCount, enemyAttacks = enemy.Animator.AttackSerial,
                enemySnapshots = enemy.Animator.SnapCount, enemyIgnored, cutRecoveryTicks = cuts, kinds,
            },
            ticks,
        };
        File.WriteAllText(outPath, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = false }));
        Console.WriteLine($"ok: {total} тиков; игрок — ударов {player.Animator.AttackSerial} (начато {playerStarts}), снимков {player.Animator.SnapCount}, " +
                          $"тиков с обрезанным хвостом {cuts}; враг — ударов {enemy.Animator.AttackSerial}, снимков {enemy.Animator.SnapCount}, " +
                          $"пропущено {enemyIgnored}; действия {string.Join(", ", kinds)} → {outPath}");

        // KinematicSteer и KinematicBegin (DuelArena.Kinematic.cs) — обе ветки: Agile и не-Agile
        void Begin(F f)
        {
            var anim = f.Animator;
            float df = MathF.IEEERemainder(f.FacingGoal - f.Facing, MathF.Tau);
            bool standing = new Vector2(f.DesiredVel.X, f.DesiredVel.Z).LengthSquared() < 0.09f
                            && new Vector2(f.RootVel.X, f.RootVel.Z).LengthSquared() < 0.25f;
            bool idleTurn = !anim.Agile && standing && anim.Kind == FighterAnimator.ActionKind.None;
            if (idleTurn && MathF.Abs(df) > KinTurnAnimMin)
                anim.StartTurn(df);
            if (!anim.TurnDrivesYaw)
            {
                float turn = idleTurn ? KinIdleTurnRate : KinTurnRate * anim.TurnScale;
                f.Facing += Math.Clamp(df, -turn * dt, turn * dt);
            }
            var desired = new Vector3(f.DesiredVel.X, 0, f.DesiredVel.Z) * anim.MoveScale;
            float accel = anim.MomentumHold > 0 ? KinCarryAccel : KinAccel;
            f.RootVel += (desired - f.RootVel) * MathF.Min(1, accel * dt);
            f.RootVel.Y = 0;

            var local = Vector3.Transform(f.RootVel, Quaternion.CreateFromAxisAngle(Vector3.UnitY, -f.Facing));
            local = new Vector3(-local.X, local.Y, local.Z);
            anim.Tick(dt, local);
            f.Facing += anim.YawDelta;
            bool evading = anim.Kind == FighterAnimator.ActionKind.Evade || anim.EvadeCarrying;
            float control = !evading ? 1 : anim.EvadeCarrying ? 0 : anim.EvadeRecovery;
            f.Root += f.RootVel * (dt * control);
            if (anim.RootDelta != Vector3.Zero)
            {
                var worldDelta = Vector3.Transform(anim.RootDelta, Quaternion.CreateFromAxisAngle(Vector3.UnitY, f.Facing));
                f.Root += worldDelta;
                if (evading && dt > 0)
                    f.RootVel = Vector3.Lerp(new Vector3(worldDelta.X, 0, worldDelta.Z) / dt, f.RootVel, control);
            }
            f.Root.Y = 0;
        }
    }

    private static object Row(CharacterRig rig, F f)
    {
        var anim = f.Animator;
        ulong h = 14695981039346656037;
        foreach (var m in anim.Component)
            foreach (float x in new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 })
                h = (h ^ (uint)BitConverter.SingleToInt32Bits(x)) * 1099511628211;
        int clip = anim.Locomotion is LegacyLocomotion legacy ? legacy.Clip : -1;
        bool acting = anim.Kind != FighterAnimator.ActionKind.None;
        return new
        {
            root = new[] { f.Root.X, f.Root.Z },
            facing = f.Facing,
            clip = clip >= 0 ? rig.Clips.Clips[clip].Name : "",
            action = anim.Kind.ToString(),
            actionClip = acting ? rig.Clips.Clips[Private<int>(anim, "_actionClip")].Name : "",
            actionTime = acting ? anim.ActionTime : 0,
            actionWeight = Private<float>(anim, "_actionWeight"),
            upper = acting && Private<bool>(anim, "_actionUpper"),
            cutRecovery = acting && Private<bool>(anim, "_cutRecovery"),
            yawDelta = anim.YawDelta,
            rootDelta = new[] { anim.RootDelta.X, anim.RootDelta.Z },
            snapFade = Private<float>(anim, "_snapFade"),
            snapshots = anim.SnapCount,
            attackSerial = anim.AttackSerial,
            poseHash = h.ToString("X16"),
        };
    }
}
