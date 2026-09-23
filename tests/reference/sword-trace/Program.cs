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
string pakPath = args[0], outPath = args[1];
string scenarioName = args.Length > 2 ? args[2] : "player";
using var pak = PakSource.Open(pakPath);
var rig = CharacterRig.TryLoad(pak) ?? throw new InvalidOperationException("нет рига");
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
if (scenarioName != "player") throw new ArgumentException($"сценарий «{scenarioName}»: player, turn или crowd");
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
        int usedSpawns = 0;

        // Fighter.Build (поля движения; тело, оружие, мышцы — не у кинематического бойца) и ResetAnimator.
        void Build(F f, Vector3 pos, float facing, float seed)
        {
            f.Facing = f.FacingGoal = facing;
            f.Seed = seed;
            f.DesiredVel = Vector3.Zero;
            f.Root = new Vector3(pos.X, 0, pos.Z);
            f.RootVel = Vector3.Zero;
            f.Animator.Reset();
        }

        // DuelArena.Spawn: корень, курс и зерно — расстановка SWORD2 этого тика и места.
        void SpawnAt(int tick, F f)
        {
            if (!spawns.TryGetValue((tick, f.Index), out var s)) throw new InvalidDataException($"нет расстановки: тик {tick}, место {f.Index}");
            usedSpawns++;
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

        // AttachRig: аниматор каждому месту, затем Reset(EnemyCount).
        for (int i = 0; i < MaxFighters; i++) _f[i] = new F(i, new FighterAnimator(rig) { Agile = i == 0 });
        Reset(0, inp.GetProperty("enemies").GetInt32());

        var ticks = new List<object>();
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
            maxActive = Math.Max(maxActive, ActiveCount);

            int n = ActiveCount;
            var x = new float[n];
            var z = new float[n];
            var facing = new float[n];
            var clip = new int[n];
            var action = new string[n];
            var actionClip = new int[n];
            var poseHash = new string[n];
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
            }
            ticks.Add(new { tick = t + 1, x, z, facing, clip, action, actionClip, poseHash });
        }
        if (usedSpawns != spawns.Count) throw new InvalidDataException($"расстановок во входе {spawns.Count}, использовано {usedSpawns}");

        var doc = new
        {
            purpose = "Трасса толпы SWORD 2eab13b для сверки точки 4д SWORD2: игрок (Agile) и враги (не-Agile) с настоящим FighterAnimator, " +
                      "копии EnemyAi.Think (без удара), PlayerController.Think, KinematicSteer/KinematicBegin и ResolveRoots в порядке DuelArena.Tick, " +
                      "команды арены по ходу; расстановка — SWORD2 (input). На тик — по местам 0…EnemyCount: корень (x, z), курс, клип локомоции " +
                      "(номер в clipNames), действие и его клип, хеш позы (FNV-1a по битам всех 16 чисел модельных матриц суставов).",
            sourceCommit = "2eab13b2262f6b336c3fc9bdcd71fb3dfc5a51a4",
            pakSha256 = "0c6f717303ac76b91ef0a1bc1b0b3a692827e79aa8513a6c701a41fcab92efd0",
            tickRate = 120,
            joints = rig.JointCount,
            clips = rig.Clips.Clips.Length,
            clipNames = rig.Clips.Clips.Select(c => c.Name),
            input = inp,
            summary = new { ticks = total, maxFighters = maxActive, arenaCommands, spawns = usedSpawns, pairs = _pairs, pillars = _pillars, edges = _edges, turns },
            ticks,
        };
        File.WriteAllText(outPath, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = false }));
        Console.WriteLine($"ok: {total} тиков, бойцов до {maxActive}, команд арены {arenaCommands}, построек {usedSpawns}, пар {_pairs}, " +
                          $"колонн {_pillars}, края {_edges}, поворотов клипом {turns} → {outPath}");
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
