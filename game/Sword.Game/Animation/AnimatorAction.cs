using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Ecs;
using Sword.Game.Contracts;
using Sword.Game.Data;

namespace Sword.Game.Animation;

/// <summary>
/// Действие слоя действий аниматора (<c>FighterAnimator.ActionKind</c> SWORD 2eab13b; номера — те же). С точки 4г — только
/// поворот на месте; удары, отбитый удар, блок, реакции и уклонения добавит этап 6 (бой) — тем же слоем.
/// </summary>
public enum ActionKind : byte
{
    /// <summary>Действия нет: поза — локомоция.</summary>
    None = 0,

    /// <summary>Поворот на месте клипом (<c>StartTurn</c>): курс ведёт yaw клипа, пока боец стоит.</summary>
    Turn = 6,
}

/// <summary>
/// Слой действий аниматора бойца — состояние действия <c>FighterAnimator</c> SWORD 2eab13b (<c>Kind</c>, <c>_actionClip</c>,
/// <c>ActionTime</c>, <c>_actionDuration</c>, <c>_actionWeight</c>, <c>_turnScale</c>) и его выход тика (<c>YawDelta</c>,
/// <c>RootDelta</c>). Машина состояний боя — правило игры, поэтому она в ядре игры (раздел 6 плана: <c>FighterAnimator</c>
/// остаётся в игре) компонентом бойца, а поза слоя — в пуле поз (<see cref="ActionPoses"/>). Пока — у бойцов
/// не-<c>Agile</c> (враги): у игрока (<c>Agile</c>) до этапа 6 действий нет. Хешируется участником <c>ecs</c>; виден агенту
/// (<c>entity.get</c>, условия, <c>anim.pose</c> — с именем клипа).
/// </summary>
public struct AnimatorAction
{
    public ActionKind Kind;

    /// <summary>Клип действия (номер в библиотеке); −1 — действия нет.</summary>
    public int Clip;

    /// <summary>Время клипа действия, с, и его длительность (<c>ActionTime</c>, <c>_actionDuration</c>).</summary>
    public float Time, Duration;

    /// <summary>Вес слоя 0…1: вход за 0,15 с, выход за 0,2 с (<c>_actionWeight</c>); в позу — сглаженной ступенью.</summary>
    public float Weight;

    /// <summary>Во сколько раз yaw клипа поворота растянут до угла, на который надо повернуть (0,5…1,6 по модулю; <c>_turnScale</c>).</summary>
    public float TurnScale;

    /// <summary>
    /// Выход тика: поворот курса, рад (+ — влево), из yaw клипа поворота — пока боец стоит (<c>YawDelta</c>); и сдвиг
    /// корня клипом действия в системе бойца, м (<c>RootDelta</c>). Их применяет перенос корня (Movement).
    /// </summary>
    public float YawDelta;
    public Vector3 RootDelta;

    /// <summary>Действие ведёт всё тело (<c>FullBodyAction</c>): слоя верха тела до этапа 6 нет — любое действие.</summary>
    public readonly bool FullBody => Kind != ActionKind.None;

    /// <summary>Курс ведёт клип поворота — боец стоит (<c>TurnDrivesYaw</c>); пошёл — курс снова доворачивает контроллер.</summary>
    public readonly bool TurnDrivesYaw(bool moving) => Kind == ActionKind.Turn && !moving;

    /// <summary>Без действия — как после <c>FighterAnimator.Reset</c>.</summary>
    public static readonly AnimatorAction None = new() { Kind = ActionKind.None, Clip = -1 };
}

/// <summary>
/// Поза слоя действий в пуле поз: сэмпл клипа действия (<c>_actRot/_actPos</c> SWORD). Отдельно от
/// <see cref="AnimatorAction"/>: постройка бойца заново (сброс арены) обнуляет действие, а поза остаётся его. Берётся при
/// первом действии бойца (<see cref="AnimatorPoseSystem"/>), возвращается, когда компонент снят
/// (<see cref="ActionPoseReleaseSystem"/>).
/// </summary>
public struct ActionPoses
{
    public PoseHandle Layer;
}

/// <summary>Константы слоя действий — <c>FighterAnimator</c> SWORD 2eab13b (только то, что нужно повороту на месте).</summary>
public static class ActionRules
{
    /// <summary>Выход действия, с (<c>ActionOut</c>): вес гаснет за это время, выход начинается за столько до конца клипа.</summary>
    public const float ActionOut = 0.2f;

    /// <summary>Мягкий вход действия, с (<c>ActionInSoft</c>: реакция, блок, уклонение, поворот; удар входит за 0,08 — этап 6).</summary>
    public const float ActionInSoft = 0.15f;

    /// <summary>Растяжение yaw клипа поворота до нужного угла — не меньше и не больше (<c>TurnScaleMin/Max</c>).</summary>
    public const float TurnScaleMin = 0.5f, TurnScaleMax = 1.6f;

    /// <summary>
    /// Клип поворачивает меньше — не клип поворота (0,3 рад); больше — разворот, годится в обе стороны (2,4 рад);
    /// литералы <c>StartTurn</c>.
    /// </summary>
    public const float TurnClipMin = 0.3f, Turn180Min = 2.4f;
}

/// <summary>
/// Логика слоя действий (<c>FighterAnimator</c> SWORD 2eab13b) без поз: начало поворота и шаг действия на тик. Позу по
/// этому состоянию ставит <see cref="AnimatorPoseSystem"/> заданиями пулу поз. Порядок вычислений float — как в SWORD
/// (сверка — <c>ReferenceTraceTests</c>, трасса поворота врага).
///
/// Снимка позы (<c>Snapshot</c>, <c>_snapFade</c>) здесь нет: в поворот он попадает только при конце клипа с видимым весом
/// (больше 0,05), а выход начинается за 0,2 с до конца клипа и за эти 24 тика гасит вес целиком (по 1/24 за тик из не
/// больше чем 1); прерывание действия новым (<c>Begin</c> при видимом весе) у поворота тоже не бывает — он начинается
/// только из покоя. Снимок и прерывания придут с действиями боя (этап 6); трасса SWORD подтверждает: снимков 0.
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
        a.Kind = ActionKind.Turn;
        a.Clip = clip;
        a.Duration = clips.Duration(clip);
        a.Time = 0;
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
    /// Шаг действия на тик (<c>TickActions</c> без позы) после слоя движения: <paramref name="moving"/> — «идёт» этого тика.
    /// Время клипа, сдвиг корня клипом (с весом слоя до шага), поворот курса yaw клипа (пока стоит), вес — вход или выход
    /// (выход — за 0,2 с до конца клипа или пошёл во время поворота); клип кончился или вес ушёл в ноль на выходе —
    /// действия нет.
    /// </summary>
    public static void Tick(ref AnimatorAction a, bool moving, float dt, ClipLibrary clips)
    {
        a.RootDelta = Vector3.Zero;
        a.YawDelta = 0;
        if (a.Kind == ActionKind.None) return;
        float before = a.Time;
        a.Time += dt;
        // всё тело (stand = 1): сдвиг корня — с весом слоя, как и поза
        a.RootDelta = (clips.RootMotionAt(a.Clip, a.Time) - clips.RootMotionAt(a.Clip, before)) * a.Weight;
        // поворот ведёт курс, пока боец стоит; пошёл — клип гаснет, курс доворачивает контроллер
        if (a.TurnDrivesYaw(moving))
            a.YawDelta = (clips.RootYawAt(a.Clip, a.Time) - clips.RootYawAt(a.Clip, before)) * a.TurnScale;
        bool ending = a.Time >= a.Duration - ActionRules.ActionOut || a.Kind == ActionKind.Turn && moving;
        a.Weight = ending ? MathF.Max(0, a.Weight - dt / ActionRules.ActionOut) : MathF.Min(1, a.Weight + dt / ActionRules.ActionInSoft);
        if (a.Time >= a.Duration || ending && a.Weight <= 0)
        {
            a.Kind = ActionKind.None;
            a.Clip = -1;
            a.Weight = 0;
        }
    }

    /// <summary>Сглаженная ступень веса (<c>w·w·(3 − 2w)</c>) — вес слоя действия в позе.</summary>
    public static float Smooth(float w) => w * w * (3 - 2 * w);

    /// <summary>
    /// Задания пулу поз слоя действий бойца (<c>TickActions</c>: поверх локомоции, до прямой кинематики): идёт действие —
    /// сэмпл клипа в позу слоя и смешивание в позу бойца всем телом со сглаженным весом. Поза слоя берётся при первом
    /// действии.
    /// </summary>
    public static void Pose(in AnimatorAction a, ref ActionPoses poses, PoseHandle pose, PosePool pool)
    {
        if (a.Kind == ActionKind.None) return;
        if (poses.Layer.IsNone) poses.Layer = pool.Alloc();
        pool.Sample(poses.Layer, a.Clip, a.Time);
        pool.Blend(pose, poses.Layer, Smooth(a.Weight));
    }
}

/// <summary>End: снятая поза слоя действий (боец удалён или компонент снят) возвращается в пул.</summary>
public sealed class ActionPoseReleaseSystem : IReactiveSystem<Removed<ActionPoses>>
{
    public void Setup(SystemSetup s) => s.Label(GameLabels.ActionRelease);

    public void React(in SystemContext ctx, ReadOnlySpan<Removed<ActionPoses>> removed)
    {
        var pool = ctx.World.Resource<PosePool>();
        foreach (var r in removed)
            if (pool.IsAlive(r.Value.Layer)) pool.Free(r.Value.Layer);
    }
}
