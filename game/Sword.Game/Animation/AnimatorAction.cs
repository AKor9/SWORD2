using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Ecs;
using Sword.Game.Bodies;
using Sword.Game.Contracts;
using Sword.Game.Data;

namespace Sword.Game.Animation;

/// <summary>
/// Действие слоя действий аниматора (<c>FighterAnimator.ActionKind</c> SWORD 2eab13b; номера — те же). С точки 4г — поворот
/// на месте, с 6в — удар и вид рывка, с 6г — реакция врага на попадание; отбитый удар и блок — этап 10 (тем же слоем).
/// </summary>
public enum ActionKind : byte
{
    /// <summary>Действия нет: поза — локомоция.</summary>
    None = 0,

    /// <summary>Удар (<c>StartAttack</c>): клип удара таблицы ударов, слоем верха тела или всем телом (слой слота).</summary>
    Attack = 1,

    /// <summary>
    /// Реакция врага на попадание (6г, <c>HitReact</c> не-<c>Agile</c>): клип реакции (сзади — <c>hit_back</c>, спереди — <c>hit_front1</c>
    /// или <c>hit_front2</c>), слой — слота; мягкий вход, отшатывание — root motion клипа; бить нельзя, ход — 0,15, поворот — 0,35.
    /// Игрок не играет реакцию действием — он вздрагивает (<see cref="Flinch"/>), управление не отнимается.
    /// </summary>
    HitReact = 4,

    /// <summary>
    /// Вид рывка (6в; на месте <c>Evade</c> SWORD, но не он): клип уклонения по направлению рывка всем телом, гаснет к концу
    /// рывка; корень ведёт контроллер рывка (<c>PlayerControl</c>), root motion клипа не применяется.
    /// </summary>
    Dash = 5,

    /// <summary>Поворот на месте клипом (<c>StartTurn</c>): курс ведёт yaw клипа, пока боец стоит.</summary>
    Turn = 6,
}

/// <summary>
/// Слой действий аниматора бойца — состояние действия <c>FighterAnimator</c> SWORD 2eab13b (<c>Kind</c>, <c>_actionClip</c>,
/// <c>ActionTime</c>, <c>_actionDuration</c>, <c>_actionWeight</c>, <c>_turnScale</c>, <c>Attack</c>, <c>_actionUpper</c>,
/// <c>_cutRecovery</c>, <c>_snapFade</c>, <c>SnapCount</c>, <c>AttackSerial</c>) и его выход тика (<c>YawDelta</c>,
/// <c>RootDelta</c>). Машина состояний боя — правило игры, поэтому она в ядре игры (раздел 6 плана: <c>FighterAnimator</c>
/// остаётся в игре) компонентом бойца, а позы слоя и снимка — в пуле поз (<see cref="ActionPoses"/>). У всех бойцов: у
/// игрока (<c>Agile</c>) — удары и вид рывка, у врагов — повороты и удары. Хешируется участником <c>ecs</c>; виден агенту
/// (<c>entity.get</c>, условия, <c>anim.pose</c> — с именем клипа).
/// </summary>
public struct AnimatorAction
{
    public ActionKind Kind;

    /// <summary>Клип действия (номер в библиотеке); −1 — действия нет.</summary>
    public int Clip;

    /// <summary>Время клипа действия, с, и его длительность (<c>ActionTime</c>, <c>_actionDuration</c>); у рывка — отрезок клипа на рывок.</summary>
    public float Time, Duration;

    /// <summary>Вес слоя 0…1: вход за 0,08 с (удар) или 0,15 с, выход за 0,2 с (<c>_actionWeight</c>); в позу — сглаженной ступенью.</summary>
    public float Weight;

    /// <summary>Во сколько раз yaw клипа поворота растянут до угла, на который надо повернуть (0,5…1,6 по модулю; <c>_turnScale</c>).</summary>
    public float TurnScale;

    /// <summary>
    /// Выход тика: поворот курса, рад (+ — влево), из yaw клипа поворота — пока боец стоит (<c>YawDelta</c>); и сдвиг
    /// корня клипом действия в системе бойца, м (<c>RootDelta</c>). Их применяет перенос корня (Movement).
    /// </summary>
    public float YawDelta;
    public Vector3 RootDelta;

    /// <summary>Удар — номер в таблице ударов (<see cref="ICombatView.Attacks"/>: slash1–4, stab1–3); −1 — не удар (<c>Attack</c> SWORD).</summary>
    public int Attack;

    /// <summary>Действие — слой верха тела (<c>_actionUpper</c>): на ходу ноги у локомоции.</summary>
    public bool Upper;

    /// <summary>
    /// Доля клипа действия в суставах вне маски верха в этом тике (<c>CharacterRig.StandWeight</c> сглаженной скорости: стоя — 1,
    /// на ходу — 0); у действия всем телом — 1. Её берёт смешивание по маске (<see cref="ActionLayer.Pose"/>).
    /// </summary>
    public float Stand;

    /// <summary>Хвост удара обрезан — игрок побежал после окна урона, слой гаснет за 0,1 с (<c>_cutRecovery</c>, только <c>Agile</c>).</summary>
    public bool CutRecovery;

    /// <summary>Скорость клипа действия (секунд клипа за секунду): 1; у рывка — отрезок клипа уклонения за время рывка.</summary>
    public float Rate;

    /// <summary>
    /// Переход от снимка позы (<c>_snapFade</c>): 0 — только что снята, 1 — снимка нет. Растёт на 1/0,15 с за тик; поза бойца —
    /// смешивание со снимком с весом сглаженной ступени от <c>1 − SnapFade</c>.
    /// </summary>
    public float SnapFade;

    /// <summary>
    /// Снимков позы за жизнь бойца (<c>SnapCount</c>): растёт при прерывании видимого действия (вес больше 0,05) и при конце
    /// клипа с видимым весом. Снимок — копия итоговой позы прошлого тика: запрос здесь, копия — в начале стадии Animation
    /// (<see cref="ActionPoses.Snapped"/> догоняет это число).
    /// </summary>
    public int Snapshots;

    /// <summary>Номер удара бойца — растёт с каждым ударом, постройка заново его не сбрасывает (<c>AttackSerial</c>).</summary>
    public int AttackSerial;

    /// <summary>
    /// Что прервало последнее начатое действие (для проверки и отладки): действие до него, его время и удар (−1 — не удар).
    /// Удар начинается только из состояния, где боец может ударить (инвариант <c>animation.actions</c>).
    /// </summary>
    public ActionKind From;
    public float FromTime;
    public int FromAttack;

    /// <summary>Действие ведёт всё тело (<c>FullBodyAction</c>): локомоция под ним не ищет новый кадр.</summary>
    public readonly bool FullBody => Kind != ActionKind.None && !Upper;

    /// <summary>Курс ведёт клип поворота — боец стоит (<c>TurnDrivesYaw</c>); пошёл — курс снова доворачивает контроллер.</summary>
    public readonly bool TurnDrivesYaw(bool moving) => Kind == ActionKind.Turn && !moving;

    /// <summary>Без действия — как после <c>FighterAnimator.Reset</c>.</summary>
    public static readonly AnimatorAction None = new()
    {
        Kind = ActionKind.None, Clip = -1, Attack = -1, Stand = 1, Rate = 1, SnapFade = 1, From = ActionKind.None, FromAttack = -1,
    };

    /// <summary>
    /// Постройка заново (<c>FighterAnimator.Reset</c>): без действия и снимка, а номер удара и счёт снимков — прежние
    /// (<c>Reset</c> SWORD их не сбрасывает; номер удара не убывает — инвариант).
    /// </summary>
    public readonly AnimatorAction Rebuilt() => None with { AttackSerial = AttackSerial, Snapshots = Snapshots };
}

/// <summary>
/// Позы слоя действий в пуле поз: сэмпл клипа действия (<c>_actRot/_actPos</c> SWORD) и снимок итоговой позы
/// (<c>_snapRot/_snapPos</c>). Отдельно от <see cref="AnimatorAction"/>: постройка бойца заново (сброс арены) обнуляет
/// действие, а позы остаются его. Поза слоя берётся при первом действии бойца (<see cref="AnimatorPoseSystem"/>), снимка —
/// при первом снимке (<see cref="ActionSnapshotSystem"/>); возвращаются, когда компонент снят (<see cref="ActionPoseReleaseSystem"/>).
/// </summary>
public struct ActionPoses
{
    public PoseHandle Layer;

    /// <summary>Снимок итоговой позы прошлого тика — от него поза уходит плавно (<see cref="AnimatorAction.SnapFade"/>).</summary>
    public PoseHandle Snap;

    /// <summary>Сколько снимков уже скопировано (догоняет <see cref="AnimatorAction.Snapshots"/> в начале стадии Animation).</summary>
    public int Snapped;

    /// <summary>
    /// Память смешивания по маске (6в, <c>PosePool.BlendMasked</c>): итог слоя верха прошлого тика и его итог по кратчайшей дуге —
    /// сустав, у которого кратчайшая дуга перескочила (таз укола на полушаге), догоняет её не больше 8° за тик. Клип и время клипа,
    /// при которых память записана: тот же клип дальше — память действует; −1 — смешивания по маске в прошлом тике не было.
    /// </summary>
    public PoseHandle BlendOutput, BlendShortest;
    public int BlendClip;
    public float BlendTime;

    /// <summary>
    /// Вздрагивание игрока (6г, <see cref="Flinch"/>): первый кадр клипа реакции и его кадр в момент слота — отклонение второго от
    /// первого добавляется к позе по маске верха. Общие для обоих слотов (задания пула исполняются по порядку); берутся при первом
    /// вздрагивании.
    /// </summary>
    public PoseHandle FlinchRef, FlinchPose;

    /// <summary>
    /// Память перехода от снимка (6ж): итог перехода прошлого тика и его итог по кратчайшей дуге — как <see cref="BlendOutput"/>, но для
    /// смешивания со снимком всем телом: снимок укола (таз и плечи до 165° от стойки) и поза реакции на попадание расходятся через 180°, и
    /// кратчайшая дуга перескакивала посреди перехода (плечо 45…66 м/с за тик — <c>physics.kinematicSpeed</c>, толпа 6ж). Берутся при
    /// первом переходе от снимка. <see cref="SnapBlend"/> — номер снимка (<see cref="Snapped"/>), чей переход смешан в прошлом тике; 0 —
    /// перехода не было.
    /// </summary>
    public PoseHandle SnapOutput, SnapShortest;
    public int SnapBlend;
}

/// <summary>Константы слоя действий — <c>FighterAnimator</c> и <c>CharacterRig</c> SWORD 2eab13b.</summary>
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
    /// гаснет за 0,1 с (<c>RecoveryCutAfter</c>, <c>ActionOutRun</c>).
    /// </summary>
    public const float RecoveryCutAfter = 0.1f, ActionOutRun = 0.1f;

    /// <summary>Поворот курса в окне урона (<c>Agile</c>, <c>StrikeTurnScale</c>) и у занятого не-<c>Agile</c> (<c>BusyTurnScale</c>).</summary>
    public const float StrikeTurnScale = 0.6f, BusyTurnScale = 0.35f;

    /// <summary>Ход во время удара не-<c>Agile</c> (враг) — доля желаемой скорости (<c>MoveScale</c>).</summary>
    public const float AttackMoveScale = 0.35f;

    /// <summary>Ход во время реакции на попадание не-<c>Agile</c> (враг) — доля желаемой скорости (<c>MoveScale</c>, 6г).</summary>
    public const float HitReactMoveScale = 0.15f;

    /// <summary>
    /// Слой верха: скорость (м/с), до которой действие играет всё тело, и с которой ноги полностью у локомоции
    /// (<c>UpperStandSpeed</c>, <c>UpperMoveSpeed</c>).
    /// </summary>
    public const float UpperStandSpeed = 0.3f, UpperMoveSpeed = 1.2f;

    /// <summary>Растяжение yaw клипа поворота до нужного угла — не меньше и не больше (<c>TurnScaleMin/Max</c>).</summary>
    public const float TurnScaleMin = 0.5f, TurnScaleMax = 1.6f;

    /// <summary>
    /// Клип поворачивает меньше — не клип поворота (0,3 рад); больше — разворот, годится в обе стороны (2,4 рад);
    /// литералы <c>StartTurn</c>.
    /// </summary>
    public const float TurnClipMin = 0.3f, Turn180Min = 2.4f;

    /// <summary>
    /// Вид рывка (6в): отрезок клипа уклонения, с, который проигрывается за время рывка — шаг уклонения до момента, где его
    /// рывок кончался в SWORD (<c>FindRecoverTime</c> на эталонном паке: 0,38…0,55 с, в среднем ≈ 0,45); короче клип — весь клип.
    /// </summary>
    public const float DashClipSpan = 0.45f;

    /// <summary>Доля клипа действия в ногах при скорости <paramref name="speed"/> (<c>CharacterRig.StandWeight</c>): стоя — 1, на ходу — 0.</summary>
    public static float StandWeight(float speed)
    {
        float k = Math.Clamp((speed - UpperStandSpeed) / (UpperMoveSpeed - UpperStandSpeed), 0, 1);
        return 1 - k * k * (3 - 2 * k);
    }

    /// <summary>
    /// Маска слоя верха тела (<c>CharacterRig.UpperMask</c>): spine_01 — 0,25, spine_02 — 0,5, spine_03 — 0,75, таз и ноги
    /// (сегменты таза, бёдер, голеней, стоп — <see cref="JointSegments"/>) — 0, остальное (корпус, руки, голова) — 1.
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
/// Логика слоя действий (<c>FighterAnimator</c> SWORD 2eab13b) без поз: начало поворота, удара и вида рывка, правила
/// «можно ли действовать» и масштабы хода и поворота, шаг действия на тик. Позу по этому состоянию ставят
/// <see cref="ActionSnapshotSystem"/> и <see cref="AnimatorPoseSystem"/> заданиями пулу поз. Порядок вычислений float — часть
/// поведения (эталоны SWORD2 держат его хешами; побитовые трассы SWORD сняты на 7а).
///
/// <c>agile</c> — игрок (<c>Agile = f.IsPlayer</c> SWORD). Отзывчивость SWORD2 (6в): у игрока ход при любом действии не
/// замедляется, поворот — по правилам <c>Agile</c>, удар и рывок прерывают любое его действие (удар — удар только после окна
/// урона: связка), у врагов — правила SWORD.
/// </summary>
public static class ActionLayer
{
    /// <summary>
    /// Поворот на месте на <paramref name="delta"/> рад (+ — влево; <c>StartTurn</c>): клип 90° в свою сторону или разворот,
    /// ближайший по растяжению (|ln scale| меньше), yaw клипа растягивается до угла. Нет подходящего — false, действие не
    /// меняется (курс доворачивает контроллер). Начинается только из покоя (<see cref="ActionKind.None"/>) — так зовёт
    /// <c>KinematicSteer</c>; вес — прежний (0).
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
    /// Удар <paramref name="attack"/> таблицы ударов (<c>StartAttack</c>): номер удара растёт, клип удара с начала, слой — слоя
    /// слота. Зовущий проверяет <see cref="CanAttack"/>.
    /// </summary>
    public static void StartAttack(ref AnimatorAction a, int attack, in AttackInfo info, ClipLibrary clips)
    {
        a.AttackSerial++;
        Begin(ref a, ActionKind.Attack, info.Clip, info.Upper, clips.Duration(info.Clip), attack);
    }

    /// <summary>
    /// Реакция врага на попадание (6г; <c>HitReact</c> SWORD не-<c>Agile</c> — <c>Begin(HitReact, clip, Upper(slot))</c>): клип реакции
    /// с начала, слой — слоя слота; прерывает любое действие (снимок позы, если оно видно). Номер удара не меняется.
    /// </summary>
    public static void StartHitReact(ref AnimatorAction a, int clip, bool upper, ClipLibrary clips) =>
        Begin(ref a, ActionKind.HitReact, clip, upper, clips.Duration(clip), -1);

    /// <summary>
    /// Вид рывка (6в): клип уклонения <paramref name="clip"/> всем телом; его отрезок <see cref="ActionRules.DashClipSpan"/> (не
    /// длиннее клипа) проигрывается за <paramref name="dashTime"/> с — время рывка; вес гаснет вместе с прибавкой скорости.
    /// Прерывает любое действие (снимок позы, если оно видно).
    /// </summary>
    public static void StartDash(ref AnimatorAction a, int clip, float dashTime, ClipLibrary clips)
    {
        float span = MathF.Min(ActionRules.DashClipSpan, clips.Duration(clip));
        Begin(ref a, ActionKind.Dash, clip, false, span, -1);
        a.Rate = span / dashTime;
    }

    /// <summary>
    /// Смена действия (<c>Begin</c>): прерывание видимого действия (вес больше 0,05) — снимок итоговой позы прошлого тика и вес
    /// 1 (без снимка новый клип подставлялся за тик — суставы прыгали на 0,3–1 м); клип с начала.
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

    /// <summary>Удар прошёл окно урона — восстановление (<c>Recovering</c>).</summary>
    public static bool Recovering(in AnimatorAction a, ReadOnlySpan<AttackInfo> attacks) =>
        a.Kind == ActionKind.Attack && a.Time > attacks[a.Attack].ActiveEnd;

    /// <summary>Занят действием, которое замедляет поворот не-<c>Agile</c> (<c>Busy</c>: удар, реакция на попадание; отбитый удар — этап 10).</summary>
    public static bool Busy(in AnimatorAction a) => a.Kind is ActionKind.Attack or ActionKind.HitReact;

    /// <summary>
    /// Можно начать новое действие (<c>CanAct</c>): свободен, поворот, удар после окна урона (связка: <c>Agile</c> — сразу,
    /// иначе через 0,1 с), вид рывка (его прерывает любое действие).
    /// </summary>
    public static bool CanAct(in AnimatorAction a, bool agile, ReadOnlySpan<AttackInfo> attacks) => CanAct(a.Kind, a.Time, a.Attack, agile, attacks);

    /// <summary><see cref="CanAct(in AnimatorAction, bool, ReadOnlySpan{AttackInfo})"/> по действию, его времени и удару.</summary>
    public static bool CanAct(ActionKind kind, float time, int attack, bool agile, ReadOnlySpan<AttackInfo> attacks) =>
        kind is ActionKind.None or ActionKind.Turn or ActionKind.Dash
        || kind == ActionKind.Attack && (uint)attack < (uint)attacks.Length && time > attacks[attack].ActiveEnd + (agile ? 0 : ActionRules.ComboDelay);

    /// <summary>Можно ударить (<c>CanAttack</c>): всё, где можно действовать (удар прерывает и вид рывка).</summary>
    public static bool CanAttack(in AnimatorAction a, bool agile, ReadOnlySpan<AttackInfo> attacks) => CanAct(a, agile, attacks);

    /// <summary>
    /// Доля скорости поворота курса (<c>TurnScale</c>): занятый не-<c>Agile</c> (удар, реакция) — 0,35; <c>Agile</c> — удар доводится
    /// мышью в замахе и восстановлении, замедляясь до 0,6 только в окне урона; иначе — 1.
    /// </summary>
    public static float TurnScale(in AnimatorAction a, bool agile, ReadOnlySpan<AttackInfo> attacks) =>
        !agile ? Busy(a) ? ActionRules.BusyTurnScale : 1
        : a.Kind == ActionKind.Attack ? Striking(a, attacks) ? ActionRules.StrikeTurnScale : 1 : 1;

    /// <summary>
    /// Доля желаемой скорости хода (<c>MoveScale</c>): у игрока — 1 при любом действии (SWORD2: игрок управляет ходом всегда; в
    /// SWORD у <c>Agile</c> — 1 только для слоя верха); у врага удар — 0,35, реакция на попадание — 0,15 (6г), остальное — 1.
    /// </summary>
    public static float MoveScale(in AnimatorAction a, bool agile) =>
        agile ? 1 : a.Kind switch
        {
            ActionKind.Attack => ActionRules.AttackMoveScale,
            ActionKind.HitReact => ActionRules.HitReactMoveScale,
            _ => 1,
        };

    /// <summary>
    /// Шаг действия на тик (<c>TickActions</c> без позы) после слоя движения: <paramref name="moving"/> — «идёт» этого тика,
    /// <paramref name="speed"/> — сглаженная скорость (для слоя верха). Время клипа, доля клипа в ногах, сдвиг корня клипом (с
    /// весом слоя и долей в ногах до шага; у рывка — нет), поворот курса yaw клипа (пока стоит), обрезка хвоста удара на бегу
    /// (<c>Agile</c>), вес — вход или выход (выход — за 0,2 с до конца клипа, пошёл во время поворота, обрезан хвост; у рывка —
    /// вместе с прибавкой скорости); клип кончился или вес ушёл в ноль на выходе — действия нет (кончился с видимым весом —
    /// снимок). Затем переход от снимка.
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
    /// Задания пулу поз слоя действий бойца (<c>TickActions</c>: поверх локомоции, до прямой кинематики): идёт действие — сэмпл
    /// клипа в позу слоя и смешивание в позу бойца со сглаженным весом — всем телом или по маске верха
    /// (<paramref name="upper"/>, доля клипа вне маски — <see cref="AnimatorAction.Stand"/>; с памятью смешивания
    /// <see cref="ActionPoses.BlendOutput"/> — отличие от SWORD: перескок кратчайшей дуги сустава доворачивается не больше 8° за тик);
    /// затем переход от снимка позы — смешиванием по маске всего тела <paramref name="full"/> с памятью
    /// (<see cref="ActionPoses.SnapOutput"/>, 6ж: без перескока дуги — побитово как <c>PosePool.Blend</c>). Позы слоя и памяти берутся при
    /// первом действии и первом переходе.
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
/// Прибавка скорости рывка (6в, новое — не <c>Evade</c> SWORD): доля от пиковой по ходу рывка <c>u</c> = 0…1 —
/// <c>1 − smoothstep(u)</c>: сразу полная, к концу плавно в ноль (без рывка скорости в конце); её интеграл — ½, поэтому
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
/// Animation, первой (до слоёв позы): снимок позы (<c>FighterAnimator.Snapshot</c>) — у бойца, чей слой действий попросил снимок
/// в этом тике (<see cref="AnimatorAction.Snapshots"/> больше <see cref="ActionPoses.Snapped"/>), итоговая поза прошлого тика
/// копируется в позу снимка заданием пулу. Задание стоит в очереди первым: пул исполняет её по порядку, и в этот момент в
/// позе бойца ещё поза прошлого тика (слои позы этого тика — задания после). Копия — здесь, а не там, где снимок запрошен
/// (Intent, Gameplay): системы других стадий в пул посреди тика не лезут.
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
