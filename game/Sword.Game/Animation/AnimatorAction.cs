using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Ecs;
using Sword.Game.Bodies;
using Sword.Game.Contracts;
using Sword.Game.Data;

namespace Sword.Game.Animation;

/// <summary>
/// Действие слоя действий аниматора: поворот на месте, удар, вид рывка, реакция врага на попадание; отбитого удара и блока нет.
/// </summary>
public enum ActionKind : byte
{
    /// <summary>Действия нет: поза — локомоция.</summary>
    None = 0,

    /// <summary>Удар (<c>StartAttack</c>): клип удара таблицы ударов, слоем верха тела или всем телом (слой слота).</summary>
    Attack = 1,

    /// <summary>
    /// Реакция врага на попадание: клип реакции (сзади или спереди), слой — слота; отшатывание — root motion клипа; бить нельзя,
    /// ход — 0,15, поворот — 0,35. Игрок реакцию не играет — он вздрагивает (<see cref="Flinch"/>).
    /// </summary>
    HitReact = 4,

    /// <summary>
    /// Вид рывка: клип уклонения по направлению рывка всем телом, гаснет к концу рывка; корень ведёт контроллер рывка
    /// (<c>PlayerControl</c>), root motion клипа не применяется.
    /// </summary>
    Dash = 5,

    /// <summary>Поворот на месте клипом (<c>StartTurn</c>): курс ведёт yaw клипа, пока боец стоит.</summary>
    Turn = 6,
}

/// <summary>
/// Слой действий аниматора бойца — машина состояний боя (правило игры, поэтому в ядре) и её выход тика; позы слоя и снимка —
/// в пуле поз (<see cref="ActionPoses"/>). Хешируется участником <c>ecs</c>.
/// </summary>
public struct AnimatorAction
{
    public ActionKind Kind;

    /// <summary>Клип действия (номер в библиотеке); −1 — действия нет.</summary>
    public int Clip;

    /// <summary>Время клипа действия, с, и его длительность; у рывка — отрезок клипа на рывок.</summary>
    public float Time, Duration;

    /// <summary>Вес слоя 0…1: вход за 0,08 с (удар) или 0,15 с, выход за 0,2 с; в позу — сглаженной ступенью.</summary>
    public float Weight;

    /// <summary>Во сколько раз yaw клипа поворота растянут до угла, на который надо повернуть (0,5…1,6 по модулю).</summary>
    public float TurnScale;

    /// <summary>
    /// Выход тика: поворот курса, рад (+ — влево), из yaw клипа поворота, пока боец стоит; сдвиг корня клипом действия в системе
    /// бойца, м (<see cref="RootDelta"/>). Применяет перенос корня (Movement).
    /// </summary>
    public float YawDelta;
    public Vector3 RootDelta;

    /// <summary>Удар — номер в таблице ударов (<see cref="ICombatView.Attacks"/>: slash1–4, stab1–3); −1 — не удар.</summary>
    public int Attack;

    /// <summary>Действие — слой верха тела: на ходу ноги у локомоции.</summary>
    public bool Upper;

    /// <summary>
    /// Доля клипа действия в суставах вне маски верха в этом тике (от сглаженной скорости: стоя — 1, на ходу — 0); у действия
    /// всем телом — 1.
    /// </summary>
    public float Stand;

    /// <summary>Хвост удара обрезан — игрок побежал после окна урона, слой гаснет за 0,1 с (только <c>Agile</c>).</summary>
    public bool CutRecovery;

    /// <summary>Скорость клипа действия (секунд клипа за секунду): 1; у рывка — отрезок клипа уклонения за время рывка.</summary>
    public float Rate;

    /// <summary>
    /// Переход от снимка позы: 0 — только что снята, 1 — снимка нет; растёт на 1/0,15 с за тик.
    /// </summary>
    public float SnapFade;

    /// <summary>
    /// Снимков позы за жизнь бойца: растёт при прерывании видимого действия и при конце клипа с видимым весом. Копия — в начале
    /// стадии Animation (<see cref="ActionPoses.Snapped"/> догоняет это число).
    /// </summary>
    public int Snapshots;

    /// <summary>Номер удара бойца — растёт с каждым ударом, постройка заново его не сбрасывает.</summary>
    public int AttackSerial;

    /// <summary>
    /// Что прервало последнее начатое действие (для отладки): действие до него, его время и удар (−1 — не удар).
    /// </summary>
    public ActionKind From;
    public float FromTime;
    public int FromAttack;

    /// <summary>Действие ведёт всё тело: локомоция под ним не ищет новый кадр.</summary>
    public readonly bool FullBody => Kind != ActionKind.None && !Upper;

    /// <summary>Курс ведёт клип поворота — боец стоит; пошёл — курс снова доворачивает контроллер.</summary>
    public readonly bool TurnDrivesYaw(bool moving) => Kind == ActionKind.Turn && !moving;

    /// <summary>Без действия.</summary>
    public static readonly AnimatorAction None = new()
    {
        Kind = ActionKind.None, Clip = -1, Attack = -1, Stand = 1, Rate = 1, SnapFade = 1, From = ActionKind.None, FromAttack = -1,
    };

    /// <summary>
    /// Постройка заново: без действия и снимка; номер удара и счёт снимков — прежние (номер удара не убывает — инвариант).
    /// </summary>
    public readonly AnimatorAction Rebuilt() => None with { AttackSerial = AttackSerial, Snapshots = Snapshots };
}

/// <summary>
/// Позы слоя действий в пуле поз: сэмпл клипа действия и снимок итоговой позы. Отдельно от <see cref="AnimatorAction"/>:
/// постройка заново обнуляет действие, а позы остаются; возвращает <see cref="ActionPoseReleaseSystem"/>.
/// </summary>
public struct ActionPoses
{
    public PoseHandle Layer;

    /// <summary>Снимок итоговой позы прошлого тика — от него поза уходит плавно (<see cref="AnimatorAction.SnapFade"/>).</summary>
    public PoseHandle Snap;

    /// <summary>Сколько снимков уже скопировано (догоняет <see cref="AnimatorAction.Snapshots"/> в начале стадии Animation).</summary>
    public int Snapped;

    /// <summary>
    /// Память смешивания по маске: итог слоя верха прошлого тика и его итог по кратчайшей дуге — перескочивший сустав догоняет
    /// не больше 8° за тик. −1 у клипа — смешивания по маске в прошлом тике не было.
    /// </summary>
    public PoseHandle BlendOutput, BlendShortest;
    public int BlendClip;
    public float BlendTime;

    /// <summary>
    /// Вздрагивание игрока (<see cref="Flinch"/>): первый кадр клипа реакции и кадр в момент слота — отклонение второго от первого
    /// добавляется по маске верха. Общие для обоих слотов.
    /// </summary>
    public PoseHandle FlinchRef, FlinchPose;

    /// <summary>
    /// Память перехода от снимка всем телом — как <see cref="BlendOutput"/>: снимок укола и поза реакции расходятся через 180°, и
    /// кратчайшая дуга перескакивала посреди перехода. <see cref="SnapBlend"/> — номер снимка, чей переход смешан в прошлом тике
    /// (0 — не было).
    /// </summary>
    public PoseHandle SnapOutput, SnapShortest;
    public int SnapBlend;
}

/// <summary>Константы слоя действий.</summary>
public static class ActionRules
{
    /// <summary>Выход действия, с (<c>ActionOut</c>): вес гаснет за это время, выход начинается за столько до конца клипа.</summary>
    public const float ActionOut = 0.2f;

    /// <summary>Вход удара, с (<c>ActionIn</c>): замах читается сразу.</summary>
    public const float ActionIn = 0.08f;

    /// <summary>Мягкий вход действия, с (<c>ActionInSoft</c>: реакция, блок, поворот).</summary>
    public const float ActionInSoft = 0.15f;

    /// <summary>Переход от снимка позы, с (<c>SnapFade</c>); снимок — при прерывании действия с весом больше 0,05 (<c>Begin</c>).</summary>
    public const float SnapFade = 0.15f, SnapVisible = 0.05f;

    /// <summary>
    /// Удар не-<c>Agile</c> (враг) можно продолжить следующим через 0,1 с после окна урона (<c>ComboDelay</c>); <c>Agile</c> — сразу.
    /// </summary>
    public const float ComboDelay = 0.1f;

    /// <summary>
    /// Хвост удара на бегу (<c>Agile</c>): через 0,1 с после окна урона, если ноги у локомоции (доля клипа в ногах меньше 0,5), слой
    /// гаснет за 0,1 с.
    /// </summary>
    public const float RecoveryCutAfter = 0.1f, ActionOutRun = 0.1f;

    /// <summary>Поворот курса в окне урона (<c>Agile</c>, <c>StrikeTurnScale</c>) и у занятого не-<c>Agile</c> (<c>BusyTurnScale</c>).</summary>
    public const float StrikeTurnScale = 0.6f, BusyTurnScale = 0.35f;

    /// <summary>Ход во время удара не-<c>Agile</c> (враг) — доля желаемой скорости (<c>MoveScale</c>).</summary>
    public const float AttackMoveScale = 0.35f;

    /// <summary>Ход во время реакции на попадание не-<c>Agile</c> (враг) — доля желаемой скорости (<c>MoveScale</c>).</summary>
    public const float HitReactMoveScale = 0.15f;

    /// <summary>
    /// Слой верха: скорость (м/с), до которой действие играет всё тело, и с которой ноги полностью у локомоции.
    /// </summary>
    public const float UpperStandSpeed = 0.3f, UpperMoveSpeed = 1.2f;

    /// <summary>Растяжение yaw клипа поворота до нужного угла — не меньше и не больше (<c>TurnScaleMin/Max</c>).</summary>
    public const float TurnScaleMin = 0.5f, TurnScaleMax = 1.6f;

    /// <summary>
    /// Клип поворачивает меньше — не клип поворота (0,3 рад); больше — разворот, годится в обе стороны (2,4 рад).
    /// </summary>
    public const float TurnClipMin = 0.3f, Turn180Min = 2.4f;

    /// <summary>
    /// Вид рывка: отрезок клипа уклонения, с, проигрываемый за время рывка (шаг уклонения ≈ 0,45 с); короче клип — весь клип.
    /// </summary>
    public const float DashClipSpan = 0.45f;

    /// <summary>Доля клипа действия в ногах при скорости <paramref name="speed"/>: стоя — 1, на ходу — 0.</summary>
    public static float StandWeight(float speed)
    {
        float k = Math.Clamp((speed - UpperStandSpeed) / (UpperMoveSpeed - UpperStandSpeed), 0, 1);
        return 1 - k * k * (3 - 2 * k);
    }

    /// <summary>
    /// Маска слоя верха тела: spine_01 — 0,25, spine_02 — 0,5, spine_03 — 0,75, таз и ноги — 0, остальное — 1.
    /// </summary>
    public static float[] UpperMask(Skeleton skeleton)
    {
        var segments = JointSegments.Build(skeleton);
        var names = skeleton.JointNames;
        var mask = new float[names.Length];
        for (int j = 0; j < mask.Length; j++)
            mask[j] = names[j] switch
            {
                "spine_01" => 0.25f,
                "spine_02" => 0.5f,
                "spine_03" => 0.75f,
                _ => segments[j] is BodySegs.Pelvis or BodySegs.ThighL or BodySegs.ShinL or BodySegs.FootL
                    or BodySegs.ThighR or BodySegs.ShinR or BodySegs.FootR ? 0 : 1,
            };
        return mask;
    }
}

/// <summary>
/// Логика слоя действий без поз: начало действий, правила «можно ли действовать», масштабы хода и поворота, шаг на тик.
/// Порядок вычислений float — часть поведения (эталоны держат его хешами). <c>agile</c> — игрок: ход при действии не
/// замедляется, удар и рывок прерывают любое его действие (удар — только после окна урона: связка).
/// </summary>
public static class ActionLayer
{
    /// <summary>
    /// Поворот на месте на <paramref name="delta"/> рад (+ — влево): клип 90° или разворот, ближайший по растяжению, yaw клипа
    /// растягивается до угла. Нет подходящего — false (курс доворачивает контроллер). Только из покоя.
    /// </summary>
    public static bool StartTurn(ref AnimatorAction a, float delta, ClipLibrary clips, ActionTable actions)
    {
        int clip = -1;
        float best = float.MaxValue;
        Consider(actions.TurnLeft, delta, clips, ref clip, ref best);
        Consider(actions.TurnRight, delta, clips, ref clip, ref best);
        Consider(actions.Turn180, delta, clips, ref clip, ref best);
        if (clip < 0) return false;
        Begin(ref a, ActionKind.Turn, clip, false, clips.Duration(clip), -1);
        a.TurnScale = delta / TotalYaw(clips, clip);
        return true;
    }

    private static float TotalYaw(ClipLibrary clips, int clip) => clips.RootYawAt(clip, clips.Duration(clip));

    private static void Consider(int c, float delta, ClipLibrary clips, ref int clip, ref float best)
    {
        float total = TotalYaw(clips, c);
        if (MathF.Abs(total) < ActionRules.TurnClipMin) return;
        // разворот годится в обе стороны, 90° — только в свою
        bool is180 = MathF.Abs(total) > ActionRules.Turn180Min;
        if (!is180 && MathF.Sign(total) != MathF.Sign(delta)) return;
        float scale = MathF.Abs(delta / total);
        if (scale < ActionRules.TurnScaleMin || scale > ActionRules.TurnScaleMax) return;
        float err = MathF.Abs(MathF.Log(scale));
        if (err < best)
        {
            best = err;
            clip = c;
        }
    }

    /// <summary>
    /// Удар <paramref name="attack"/> таблицы ударов: номер удара растёт, клип с начала, слой — слота. Зовущий проверяет
    /// <see cref="CanAttack"/>.
    /// </summary>
    public static void StartAttack(ref AnimatorAction a, int attack, in AttackInfo info, ClipLibrary clips)
    {
        a.AttackSerial++;
        Begin(ref a, ActionKind.Attack, info.Clip, info.Upper, clips.Duration(info.Clip), attack);
    }

    /// <summary>
    /// Реакция врага на попадание: клип реакции с начала, слой — слота; прерывает любое действие. Номер удара не меняется.
    /// </summary>
    public static void StartHitReact(ref AnimatorAction a, int clip, bool upper, ClipLibrary clips) =>
        Begin(ref a, ActionKind.HitReact, clip, upper, clips.Duration(clip), -1);

    /// <summary>
    /// Вид рывка: отрезок <see cref="ActionRules.DashClipSpan"/> клипа уклонения всем телом проигрывается за
    /// <paramref name="dashTime"/> с; вес гаснет вместе с прибавкой скорости. Прерывает любое действие.
    /// </summary>
    public static void StartDash(ref AnimatorAction a, int clip, float dashTime, ClipLibrary clips)
    {
        float span = MathF.Min(ActionRules.DashClipSpan, clips.Duration(clip));
        Begin(ref a, ActionKind.Dash, clip, false, span, -1);
        a.Rate = span / dashTime;
    }

    /// <summary>
    /// Смена действия: прерывание видимого действия — снимок итоговой позы прошлого тика (без него суставы прыгали на 0,3–1 м за
    /// тик); клип с начала.
    /// </summary>
    private static void Begin(ref AnimatorAction a, ActionKind kind, int clip, bool upper, float duration, int attack)
    {
        a.From = a.Kind;
        a.FromTime = a.Kind == ActionKind.None ? 0 : a.Time;
        a.FromAttack = a.Attack;
        if (a.Kind != ActionKind.None && a.Weight > ActionRules.SnapVisible)
        {
            Snapshot(ref a);
            a.Weight = 1;
        }
        a.Kind = kind;
        a.Upper = upper;
        a.CutRecovery = false;
        a.Clip = clip;
        a.Duration = duration;
        a.Time = 0;
        a.Rate = 1;
        a.Attack = attack;
    }

    /// <summary>Запросить снимок итоговой позы прошлого тика (<c>Snapshot</c>): копия — в начале стадии Animation.</summary>
    private static void Snapshot(ref AnimatorAction a)
    {
        a.Snapshots++;
        a.SnapFade = 0;
    }

    /// <summary>Удар в окне урона (<c>Striking</c>).</summary>
    public static bool Striking(in AnimatorAction a, ReadOnlySpan<AttackInfo> attacks) =>
        a.Kind == ActionKind.Attack && attacks[a.Attack].InWindow(a.Time);

    /// <summary>Занят действием, которое замедляет поворот не-<c>Agile</c>: удар, реакция на попадание.</summary>
    public static bool Busy(in AnimatorAction a) => a.Kind is ActionKind.Attack or ActionKind.HitReact;

    /// <summary>
    /// Можно начать новое действие: свободен, поворот, удар после окна урона (<c>Agile</c> — сразу, иначе через 0,1 с), вид рывка.
    /// </summary>
    public static bool CanAct(in AnimatorAction a, bool agile, ReadOnlySpan<AttackInfo> attacks) => CanAct(a.Kind, a.Time, a.Attack, agile, attacks);

    /// <summary><see cref="CanAct(in AnimatorAction, bool, ReadOnlySpan{AttackInfo})"/> по действию, его времени и удару.</summary>
    public static bool CanAct(ActionKind kind, float time, int attack, bool agile, ReadOnlySpan<AttackInfo> attacks) =>
        kind is ActionKind.None or ActionKind.Turn or ActionKind.Dash
        || kind == ActionKind.Attack && (uint)attack < (uint)attacks.Length && time > attacks[attack].ActiveEnd + (agile ? 0 : ActionRules.ComboDelay);

    /// <summary>Можно ударить (<c>CanAttack</c>): всё, где можно действовать (удар прерывает и вид рывка).</summary>
    public static bool CanAttack(in AnimatorAction a, bool agile, ReadOnlySpan<AttackInfo> attacks) => CanAct(a, agile, attacks);

    /// <summary>
    /// Доля скорости поворота курса: занятый не-<c>Agile</c> — 0,35; <c>Agile</c> в окне урона — 0,6; иначе — 1.
    /// </summary>
    public static float TurnScale(in AnimatorAction a, bool agile, ReadOnlySpan<AttackInfo> attacks) =>
        !agile ? Busy(a) ? ActionRules.BusyTurnScale : 1
        : a.Kind == ActionKind.Attack ? Striking(a, attacks) ? ActionRules.StrikeTurnScale : 1 : 1;

    /// <summary>
    /// Доля желаемой скорости хода: у игрока — 1 всегда; у врага удар — 0,35, реакция — 0,15, остальное — 1.
    /// </summary>
    public static float MoveScale(in AnimatorAction a, bool agile) =>
        agile ? 1 : a.Kind switch
        {
            ActionKind.Attack => ActionRules.AttackMoveScale,
            ActionKind.HitReact => ActionRules.HitReactMoveScale,
            _ => 1,
        };

    /// <summary>
    /// Шаг действия на тик после слоя движения: <paramref name="moving"/> — «идёт» этого тика, <paramref name="speed"/> —
    /// сглаженная скорость. Время, вес (выход — за 0,2 с до конца клипа), сдвиг корня, поворот курса, обрезка хвоста удара на
    /// бегу; кончился с видимым весом — снимок. Затем переход от снимка.
    /// </summary>
    public static void Tick(ref AnimatorAction a, bool moving, float speed, float dt, ClipLibrary clips, ReadOnlySpan<AttackInfo> attacks, bool agile)
    {
        a.RootDelta = Vector3.Zero;
        a.YawDelta = 0;
        if (a.Kind != ActionKind.None) Step(ref a, moving, speed, dt, clips, attacks, agile);
        if (a.SnapFade < 1) a.SnapFade = MathF.Min(1, a.SnapFade + dt / ActionRules.SnapFade);
    }

    private static void Step(ref AnimatorAction a, bool moving, float speed, float dt, ClipLibrary clips, ReadOnlySpan<AttackInfo> attacks, bool agile)
    {
        float before = a.Time;
        a.Time += dt * a.Rate;   // скорость 1 — точно dt
        // Слой верха на ходу: ноги у локомоции, выпад не двигает — идёт только стоя (stand → 1).
        float stand = a.Upper ? ActionRules.StandWeight(speed) : 1;
        a.Stand = stand;
        // Root motion действия (выпад удара) — с весом слоя, как и поза; рывок ведёт контроллер, клип уклонения — только вид.
        if (a.Kind != ActionKind.Dash)
            a.RootDelta = (clips.RootMotionAt(a.Clip, a.Time) - clips.RootMotionAt(a.Clip, before)) * (a.Weight * stand);
        // поворот ведёт курс, пока боец стоит; пошёл — клип гаснет, курс доворачивает контроллер
        if (a.TurnDrivesYaw(moving))
            a.YawDelta = (clips.RootYawAt(a.Clip, a.Time) - clips.RootYawAt(a.Clip, before)) * a.TurnScale;
        // игрок на ходу: хвост удара слоя верха гаснет раньше — руки уходят в бег, а не доигрывают восстановление
        if (agile && a.Upper && a.Kind == ActionKind.Attack && a.Time > attacks[a.Attack].ActiveEnd + ActionRules.RecoveryCutAfter && stand < 0.5f)
            a.CutRecovery = true;
        bool ending;
        if (a.Kind == ActionKind.Dash)
        {
            // вид рывка гаснет вместе с прибавкой скорости (DashRules.Fade), входит — как удар
            a.Weight = MathF.Min(MathF.Min(1, a.Weight + dt / ActionRules.ActionIn), DashRules.Fade(a.Time / a.Duration));
            ending = true;
        }
        else
        {
            ending = a.CutRecovery || a.Time >= a.Duration - ActionRules.ActionOut || a.Kind == ActionKind.Turn && moving;
            // удар входит быстро (замах читается сразу), поворот — мягче
            float blendIn = a.Kind == ActionKind.Attack ? ActionRules.ActionIn : ActionRules.ActionInSoft;
            a.Weight = ending ? MathF.Max(0, a.Weight - dt / (a.CutRecovery ? ActionRules.ActionOutRun : ActionRules.ActionOut))
                : MathF.Min(1, a.Weight + dt / blendIn);
        }
        if (a.Time >= a.Duration || ending && a.Weight <= 0)
        {
            // клип кончился раньше, чем вес ушёл в ноль, — уйти от позы плавно
            if (a.Weight > ActionRules.SnapVisible) Snapshot(ref a);
            a.Kind = ActionKind.None;
            a.Clip = -1;
            a.Attack = -1;
            a.Weight = 0;
        }
    }

    /// <summary>Сглаженная ступень веса (<c>w·w·(3 − 2w)</c>) — вес слоя действия и снимка в позе.</summary>
    public static float Smooth(float w) => w * w * (3 - 2 * w);

    /// <summary>
    /// Задания пулу поз слоя действий (поверх локомоции, до прямой кинематики): сэмпл клипа и смешивание в позу бойца — всем телом
    /// или по маске верха с памятью смешивания (перескок дуги — не больше 8° за тик); затем переход от снимка маской
    /// <paramref name="full"/> с памятью (без перескока — побитово как <c>PosePool.Blend</c>).
    /// </summary>
    public static void Pose(in AnimatorAction a, ref ActionPoses poses, PoseHandle pose, PosePool pool, PoseMask upper, PoseMask full)
    {
        bool masked = a.Kind != ActionKind.None && a.Upper;
        if (a.Kind != ActionKind.None)
        {
            if (poses.Layer.IsNone) poses.Layer = pool.Alloc();
            pool.Sample(poses.Layer, a.Clip, a.Time);
            float w = Smooth(a.Weight);
            if (masked)
            {
                // память — прошлый тик того же клипа, идущего дальше (новое действие или клип заново — только записать)
                bool continuous = !poses.BlendOutput.IsNone && poses.BlendClip == a.Clip && a.Time > poses.BlendTime;
                if (poses.BlendOutput.IsNone)
                {
                    poses.BlendOutput = pool.Alloc();
                    poses.BlendShortest = pool.Alloc();
                }
                pool.BlendMasked(pose, poses.Layer, w, upper, a.Stand, new BlendMemory(poses.BlendOutput, poses.BlendShortest, continuous));
                poses.BlendClip = a.Clip;
                poses.BlendTime = a.Time;
            }
            else pool.Blend(pose, poses.Layer, w);
        }
        if (!masked) poses.BlendClip = -1;
        if (a.SnapFade < 1 && !poses.Snap.IsNone)
        {
            // память — прошлый тик перехода от того же снимка (новый снимок — только записать)
            bool continuous = !poses.SnapOutput.IsNone && poses.SnapBlend == poses.Snapped;
            if (poses.SnapOutput.IsNone)
            {
                poses.SnapOutput = pool.Alloc();
                poses.SnapShortest = pool.Alloc();
            }
            pool.BlendMasked(pose, poses.Snap, Smooth(1 - a.SnapFade), full, 1, new BlendMemory(poses.SnapOutput, poses.SnapShortest, continuous));
            poses.SnapBlend = poses.Snapped;
        }
        else if (poses.SnapBlend != 0 && poses.SnapBlend == poses.Snapped && !poses.Snap.IsNone
            && pool.Following(new BlendMemory(poses.SnapOutput, poses.SnapShortest, true)))
        {
            // переход погас, а сустав ещё догоняет кратчайшую дугу после перескока: догон — дальше (вес 0: цель — поза без снимка)
            pool.BlendMasked(pose, poses.Snap, 0, full, 1, new BlendMemory(poses.SnapOutput, poses.SnapShortest, true));
        }
        else poses.SnapBlend = 0;
    }
}

/// <summary>
/// Прибавка скорости рывка: доля от пиковой по ходу рывка <c>u</c> = 0…1 — <c>1 − smoothstep(u)</c>; интеграл — ½, поэтому
/// дистанция прибавки — скорость·время/2.
/// </summary>
public static class DashRules
{
    public static float Fade(float u)
    {
        float k = Math.Clamp(u, 0, 1);
        return 1 - k * k * (3 - 2 * k);
    }
}

/// <summary>
/// Animation, первой: снимок позы — у бойца, чей слой действий попросил снимок, итоговая поза прошлого тика копируется заданием
/// пулу; задание первое в очереди, поэтому в позе бойца ещё прошлый тик. Копия здесь: другие стадии в пул посреди тика не лезут.
/// </summary>
public sealed class ActionSnapshotSystem : IContinuousSystem
{
    private Query _query = null!;

    public void Setup(SystemSetup s)
    {
        s.Label(GameLabels.ActionSnapshot).Before(GameLabels.AnimatorPose).Reads<PoseRef>().Reads<AnimatorAction>().Writes<ActionPoses>();
        _query = s.Query().With<Fighter>().Read<PoseRef>().Read<AnimatorAction>().Write<ActionPoses>();
    }

    public void Run(in SystemContext ctx)
    {
        var pool = ctx.World.Resource<PosePool>();
        foreach (var chunk in ctx.Iterate(_query))
        {
            var poses = chunk.Write<ActionPoses>();
            var refs = chunk.Read<PoseRef>()[..poses.Length];
            var a = chunk.Read<AnimatorAction>()[..poses.Length];
            for (int i = 0; i < poses.Length; i++)
            {
                if (a[i].Snapshots == poses[i].Snapped || refs[i].Generation == 0) continue;
                if (poses[i].Snap.IsNone) poses[i].Snap = pool.Alloc();
                pool.Copy(poses[i].Snap, new PoseHandle(refs[i].Index, refs[i].Generation));
                poses[i].Snapped = a[i].Snapshots;
            }
        }
    }
}

/// <summary>End: снятые позы слоя действий (слой, снимок, память смешивания и перехода от снимка, вздрагивание; боец удалён или компонент снят) возвращаются в пул.</summary>
public sealed class ActionPoseReleaseSystem : IReactiveSystem<Removed<ActionPoses>>
{
    public void Setup(SystemSetup s) => s.Label(GameLabels.ActionRelease);

    public void React(in SystemContext ctx, ReadOnlySpan<Removed<ActionPoses>> removed)
    {
        var pool = ctx.World.Resource<PosePool>();
        foreach (var r in removed)
        {
            if (pool.IsAlive(r.Value.Layer)) pool.Free(r.Value.Layer);
            if (pool.IsAlive(r.Value.Snap)) pool.Free(r.Value.Snap);
            if (pool.IsAlive(r.Value.BlendOutput)) pool.Free(r.Value.BlendOutput);
            if (pool.IsAlive(r.Value.BlendShortest)) pool.Free(r.Value.BlendShortest);
            if (pool.IsAlive(r.Value.FlinchRef)) pool.Free(r.Value.FlinchRef);
            if (pool.IsAlive(r.Value.FlinchPose)) pool.Free(r.Value.FlinchPose);
            if (pool.IsAlive(r.Value.SnapOutput)) pool.Free(r.Value.SnapOutput);
            if (pool.IsAlive(r.Value.SnapShortest)) pool.Free(r.Value.SnapShortest);
        }
    }
}
