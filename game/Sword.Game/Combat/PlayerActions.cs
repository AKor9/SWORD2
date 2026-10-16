using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Animation;
using Sword.Game.Contracts;
using Sword.Game.Data;

namespace Sword.Game.Combat;

/// <summary>
/// Правила боя тика — ресурс-значение (хешируется <c>ecs</c>) из секции <c>combat</c>: нападают ли враги, неуязвим ли игрок, рывок,
/// урон ударов игрока и острота его клинка.
/// </summary>
public struct CombatRules
{
    /// <summary>Враги бьют по таймеру ИИ (<c>combat.enemiesAttack</c>); нет — мирные враги.</summary>
    public bool EnemiesAttack;

    /// <summary>Игрок неуязвим (<c>combat.playerInvulnerable</c>).</summary>
    public bool PlayerInvulnerable;

    /// <summary>Рывок: пиковая прибавка скорости, м/с, время, с, перезарядка, с, и неуязвимость с начала рывка, с.</summary>
    public float DashSpeed, DashTime, DashCooldown, DashInvulnerable;

    /// <summary>Урон ударов игрока — множитель, и острота его клинка (порог отруба ÷ острота).</summary>
    public float PlayerDamage, PlayerSharpness;

    /// <summary>Дистанция прибавки рывка, м: скорость·время·½ (<see cref="DashRules.Fade"/>).</summary>
    public readonly float DashDistance => DashSpeed * DashTime * 0.5f;

    /// <summary>Правила из секции конфига; числа вне пределов — ошибка конфига.</summary>
    public static CombatRules From(CombatSettings s)
    {
        static float Check(float value, string key, bool positive)
        {
            if (!float.IsFinite(value) || value < 0 || positive && value == 0)
                throw new InvalidDataException($"combat.{key} = {value}: нужно конечное {(positive ? "больше 0" : "не меньше 0")}");
            return value;
        }
        return new CombatRules
        {
            EnemiesAttack = s.EnemiesAttack,
            PlayerInvulnerable = s.PlayerInvulnerable,
            DashSpeed = Check(s.DashSpeed, "dashSpeed", false),
            DashTime = Check(s.DashTime, "dashTime", true),
            DashCooldown = Check(s.DashCooldown, "dashCooldown", false),
            DashInvulnerable = Check(s.DashInvulnerable, "dashInvulnerable", false),
            PlayerDamage = Check(s.PlayerDamage, "playerDamage", false),
            PlayerSharpness = Check(s.PlayerSharpness, "playerSharpness", false),
        };
    }
}

/// <summary>
/// Управление игрока в симуляции: нажатие удара ждёт в буфере 0,3 с, рывка — 0,2 с, пока действие станет возможным; перезарядка
/// и неуязвимость рывка; рывок — прибавка скорости в сторону хода, гаснущая за время рывка. Хешируется.
/// </summary>
public struct PlayerControl
{
    /// <summary>Сколько ещё ждёт нажатие удара, с; 0 — не ждёт.</summary>
    public float SwingBuffer;

    /// <summary>Нажатие в буфере — укол (Q), а не рубящий.</summary>
    public bool SwingStab;

    /// <summary>Жест нажатия — направление замаха в системе бойца; ноль — связка.</summary>
    public Vector2 SwingGesture;

    /// <summary>Сколько ещё ждёт нажатие рывка, с, и перезарядка рывка, с.</summary>
    public float DashBuffer, DashCooldown;

    /// <summary>Рывок идёт: сколько прошло, с, и его направление по земле (единичное, мир).</summary>
    public bool Dashing;
    public float DashElapsed;
    public Vector3 DashDirection;

    /// <summary>Прибавка скорости рывка этого тика, м/с (мир) — корень сдвигается на неё сверх скорости хода.</summary>
    public Vector3 Boost;

    /// <summary>Неуязвимость рывка — сколько ещё, с (уклонение = неуязвимость).</summary>
    public float Invulnerable;

    /// <summary>Рывков за жизнь игрока.</summary>
    public int Dashes;

    public static readonly PlayerControl Idle = new();
}

/// <summary>
/// Состояние выбора удара игрока: следующий удар связки, клип последнего удара и когда он начат. Постройка заново его не сбрасывает.
/// </summary>
public struct AttackCombo
{
    public int Next;
    public int LastClip;
    public float LastTime;

    public static readonly AttackCombo Start = new() { Next = 0, LastClip = -1, LastTime = -1e3f };
}

/// <summary>Константы управления игрока.</summary>
public static class PlayerControlRules
{
    /// <summary>Нажатие удара и рывка ждёт, с (<c>SwingBufferTime</c>, <c>LungeBufferTime</c>).</summary>
    public const float SwingBufferTime = 0.3f, DashBufferTime = 0.2f;

    /// <summary>Пауза, после которой связка начинается сначала, с (<c>ComboReset</c>).</summary>
    public const float ComboReset = 1.6f;

    /// <summary>Сдвиг мыши перед кликом, с которого он — жест, пиксели (<c>GestureMouse</c>; порог считает клиент).</summary>
    public const float GestureMouse = 12f;

    /// <summary>Рывок по умолчанию (секция <c>combat</c>): прибавка 9,5 м/с за 0,3 с — 1,43 м, перезарядка 0,6 с, неуязвимость 0,3 с.</summary>
    public const float DashSpeed = 9.5f, DashTime = 0.3f, DashCooldown = 0.6f, DashInvulnerable = 0.3f;

    /// <summary>Поток ГСЧ выбора укола игрока.</summary>
    public static readonly uint PickStream = SimRandom.Stream("combat.player-pick");
}

/// <summary>
/// Выбор удара игрока: укол — только по намерению (Q), случайный; рубящий — жест выбирает ближайший по направлению замах, без
/// жеста — следующий удар связки slash1–4 по кругу; клип подряд не повторяется; пауза дольше 1,6 с — связка с начала.
/// </summary>
public static class AttackPicker
{
    /// <summary>
    /// Удар — номер в таблице ударов (<paramref name="attacks"/>: рубящие <paramref name="slashes"/>, затем уколы).
    /// <paramref name="stabRoll"/> — случайный номер укола 0…уколов−1 (счётчиковый ГСЧ).
    /// </summary>
    public static int Pick(ref AttackCombo combo, float time, Vector2 gesture, bool stab, int stabRoll, ReadOnlySpan<AttackInfo> attacks, int slashes)
    {
        if (time - combo.LastTime > PlayerControlRules.ComboReset)
        {
            combo.Next = 0;
            combo.LastClip = -1;
        }
        combo.LastTime = time;
        int pick;
        int stabs = attacks.Length - slashes;
        if (stab && stabs > 0)
        {
            int k = stabRoll;
            if (stabs > 1 && attacks[slashes + k].Clip == combo.LastClip) k = (k + 1) % stabs;
            pick = slashes + k;
        }
        else
        {
            int k;
            if (gesture != Vector2.Zero && float.IsFinite(gesture.X) && float.IsFinite(gesture.Y))
            {
                var want = Vector2.Normalize(gesture);
                k = 0;
                for (int i = 1; i < slashes; i++)
                    if (Vector2.Dot(attacks[i].SwingDir, want) > Vector2.Dot(attacks[k].SwingDir, want)) k = i;
            }
            else
            {
                k = combo.Next % slashes;
                // в слотах один и тот же клип — дальше по связке, чтобы удар не повторился
                for (int n = 1; n < slashes && attacks[k].Clip == combo.LastClip; n++) k = (combo.Next + n) % slashes;
            }
            combo.Next = k + 1;   // после жеста связка продолжается со следующего за ним удара
            pick = k;
        }
        combo.LastClip = attacks[pick].Clip;
        return pick;
    }
}

/// <summary>
/// Действия игрока из команды тика: фронты — в буферы, таймеры — минус тик, рывок раньше удара (удар выходит из рывка), удар — когда
/// слой действий позволяет (<see cref="ActionLayer.CanAttack"/>).
/// </summary>
public static class PlayerActions
{
    /// <summary>Фронты команды тика — в буферы (<c>HandleInput</c>): ЛКМ — рубящий с жестом, Q — укол, Пробел — рывок.</summary>
    public static void Press(in PlayerCommand c, ref PlayerControl control)
    {
        if (c.Attack)
        {
            control.SwingBuffer = PlayerControlRules.SwingBufferTime;
            control.SwingGesture = float.IsFinite(c.Gesture.X) && float.IsFinite(c.Gesture.Y) ? c.Gesture : Vector2.Zero;
            control.SwingStab = false;
        }
        // укол — только отдельной клавишей (Q после ЛКМ в одном тике — укол)
        if (c.Stab)
        {
            control.SwingBuffer = PlayerControlRules.SwingBufferTime;
            control.SwingStab = true;
        }
        if (c.Dash) control.DashBuffer = PlayerControlRules.DashBufferTime;
    }

    /// <summary>
    /// Тик управления: таймеры — минус тик (не ниже 0); рывок из буфера, если перезарядился; удар из буфера, если слой действий
    /// позволяет. <paramref name="move"/> — направление хода (мир); <paramref name="stabRoll"/> — случайный номер укола.
    /// </summary>
    public static void Think(ref PlayerControl control, ref AttackCombo combo, ref AnimatorAction action, in Transform t, Vector3 move, float time,
        float dt, int stabRoll, in CombatRules rules, ReadOnlySpan<AttackInfo> attacks, int slashes, ClipLibrary clips, ActionTable actions)
    {
        control.DashCooldown = MathF.Max(0, control.DashCooldown - dt);
        control.SwingBuffer = MathF.Max(0, control.SwingBuffer - dt);
        control.DashBuffer = MathF.Max(0, control.DashBuffer - dt);
        control.Invulnerable = MathF.Max(0, control.Invulnerable - dt);
        if (control.DashBuffer > 0 && control.DashCooldown <= 0)
        {
            StartDash(ref control, ref action, t, move, rules, clips, actions);
            control.DashCooldown = rules.DashCooldown;
            control.DashBuffer = 0;
        }
        if (control.SwingBuffer > 0 && ActionLayer.CanAttack(action, true, attacks))
        {
            control.SwingBuffer = 0;
            int attack = AttackPicker.Pick(ref combo, time, control.SwingGesture, control.SwingStab, stabRoll, attacks, slashes);
            ActionLayer.StartAttack(ref action, attack, attacks[attack], clips);
        }
    }

    /// <summary>
    /// Рывок в сторону хода (без хода — назад от курса): направление, неуязвимость, вид — клип уклонения по ближайшей стороне.
    /// </summary>
    public static void StartDash(ref PlayerControl control, ref AnimatorAction action, in Transform t, Vector3 move, in CombatRules rules,
        ClipLibrary clips, ActionTable actions)
    {
        var forward = new Vector3(MathF.Sin(t.Facing), 0, MathF.Cos(t.Facing));
        var dir = move.LengthSquared() > 0.01f ? Vector3.Normalize(move) : -forward;
        control.Dashing = true;
        control.DashElapsed = 0;
        control.DashDirection = dir;
        control.Invulnerable = rules.DashInvulnerable;
        control.Dashes++;
        var local = AnimatorRules.DirToLocal(dir, t.Facing);
        int slot = MathF.Abs(local.Z) >= MathF.Abs(local.X)
            ? local.Z > 0 ? ActionSlots.EvadeForward : ActionSlots.EvadeBack
            : local.X > 0 ? ActionSlots.EvadeRight : ActionSlots.EvadeLeft;
        ActionLayer.StartDash(ref action, actions.Clip[slot], rules.DashTime, clips);
    }

    /// <summary>
    /// Прибавка скорости рывка на тик: пиковая × <see cref="DashRules.Fade"/>; время вышло — 0. Скорость хода рывок не трогает: после
    /// него ход — заданный WASD, без инерции.
    /// </summary>
    public static void Boost(ref PlayerControl control, in CombatRules rules, float dt)
    {
        if (!control.Dashing)
        {
            control.Boost = Vector3.Zero;
            return;
        }
        control.Boost = control.DashDirection * (rules.DashSpeed * DashRules.Fade(control.DashElapsed / rules.DashTime));
        control.DashElapsed += dt;
        if (control.DashElapsed >= rules.DashTime) control.Dashing = false;
    }
}
