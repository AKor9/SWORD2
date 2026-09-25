using System.Numerics;
using Sword.Engine.Client;
using Sword.Game.Client.Scene;
using Sword.Game.Contracts;

namespace Sword.Game.Client;

/// <summary>
/// Действия ввода арены и их клавиши по умолчанию — как в SWORD 2eab13b (<c>PlayerController</c>, <c>RtsCamera</c>):
/// WASD — ход относительно камеры, Shift — шагом, F — камера за игроком / свободная, Z/C — поворот, +/− — зум, средняя
/// кнопка — орбита, стрелки — панорама свободной камеры, Ctrl — колесо не зумит, F1 — справка HUD; PageUp/PageDown —
/// врагов ±1, R — сброс арены (<c>Program.cs</c> SWORD, команды арены), T — следующий меч игрока (команда арены);
/// ЛКМ — рубящий удар (жест мышью перед кликом — направление), Q — укол, Пробел — рывок (фронты команды игрока); I — инвентарь,
/// 1–9 при открытом инвентаре — взять меч (команда арены);
/// F7 — капсулы тел и клинки поверх персонажей (отладочный вид SWORD2); J — суставы тел, K — контакты физики и трасса;
/// M — траектории Motion Matching игрока;
/// F2 — профайлер в HUD (F2 SWORD — «ПРОФАЙЛЕР» панели). Колесо и курсор — не
/// действия: они берутся из ввода кадра. Снимок (F4) — действие клиента движка (<c>ClientHost.Screenshot</c>).
/// Привязки меняются секцией конфига <c>input</c> (<c>--set input.move_forward=Up</c>).
/// </summary>
public static class ArenaInput
{
    public static readonly InputAction MoveForward = new("move_forward", "W");
    public static readonly InputAction MoveBack = new("move_back", "S");
    public static readonly InputAction MoveLeft = new("move_left", "A");
    public static readonly InputAction MoveRight = new("move_right", "D");
    public static readonly InputAction Walk = new("walk", "Shift");
    public static readonly InputAction CameraFollow = new("camera_follow", "F");
    public static readonly InputAction CameraRotateLeft = new("camera_rotate_left", "Z");
    public static readonly InputAction CameraRotateRight = new("camera_rotate_right", "C");
    public static readonly InputAction CameraZoomIn = new("camera_zoom_in", "Plus");
    public static readonly InputAction CameraZoomOut = new("camera_zoom_out", "Minus");
    public static readonly InputAction CameraOrbit = new("camera_orbit", "MouseMiddle");
    public static readonly InputAction CameraPanUp = new("camera_pan_up", "Up");
    public static readonly InputAction CameraPanDown = new("camera_pan_down", "Down");
    public static readonly InputAction CameraPanLeft = new("camera_pan_left", "Left");
    public static readonly InputAction CameraPanRight = new("camera_pan_right", "Right");

    /// <summary>Пока удерживается, колесо не зумит (SWORD: Ctrl+колесо — масштаб интерфейса).</summary>
    public static readonly InputAction UiModifier = new("ui_modifier", "Control");

    /// <summary>Справка HUD: показать / скрыть (SWORD: F1).</summary>
    public static readonly InputAction HudHelp = new("hud_help", "F1");

    /// <summary>
    /// Профайлер в HUD: главные зоны кадра, строка среды, последняя аномалия — показать / скрыть (SWORD: F2 — «ПРОФАЙЛЕР» панели).
    /// Включает профайлер CPU, если он выключен (выключает — только если его включил сам). Действие представления: в
    /// команды и реплей не идёт.
    /// </summary>
    public static readonly InputAction HudProfiler = new("hud_profiler", "F2");

    /// <summary>Врагов на одного больше / меньше (SWORD: PageUp / PageDown).</summary>
    public static readonly InputAction EnemiesMore = new("enemies_more", "PageUp");
    public static readonly InputAction EnemiesFewer = new("enemies_fewer", "PageDown");

    /// <summary>Сброс арены: игрок в центре, враги заново (SWORD: R).</summary>
    public static readonly InputAction ArenaReset = new("arena_reset", "R");

    /// <summary>Следующий меч инвентаря игрока по кругу (SWORD: T, <c>NextPlayerWeapon</c>; команда арены в реплей).</summary>
    public static readonly InputAction NextWeapon = new("next_weapon", "T");

    /// <summary>
    /// Рубящий удар (SWORD: ЛКМ): фронт — удар в команде игрока ровно одним тиком; жест мышью перед кликом выбирает направление
    /// (<see cref="StrikeGesture"/>), без жеста — следующий удар связки.
    /// </summary>
    public static readonly InputAction Attack = new("attack", "MouseLeft");

    /// <summary>Укол (SWORD: Q) — фронт в команде игрока.</summary>
    public static readonly InputAction Stab = new("stab", "Q");

    /// <summary>Рывок (Пробел; в SWORD — уклонение, в SWORD2 — рывок) — фронт в команде игрока.</summary>
    public static readonly InputAction Dash = new("dash", "Space");

    /// <summary>
    /// Капсулы тел и клинки поверх персонажей: показать / скрыть (в SWORD отладочного вида капсул нет, F2, F3, F5, F6
    /// заняты — F7). Действие представления: в команды и реплей не идёт.
    /// </summary>
    public static readonly InputAction DebugBodies = new("debug_bodies", "F7");

    /// <summary>
    /// Суставы тел поверх персонажей (оверлей <c>joints</c>): точки суставов, отрезок до точки на родителе, выход за предел —
    /// показать / скрыть. F-клавиши заняты (F1–F4, F7; F5, F6 — у SWORD), J — joints. Действие представления: в команды и реплей не идёт.
    /// </summary>
    public static readonly InputAction DebugJoints = new("debug_joints", "J");

    /// <summary>
    /// Контакты последнего подшага физики поверх кадра (оверлей <c>contacts</c>): показать / скрыть — и включить трассу физики
    /// (диагностический режим <c>physics.trace</c>: мир, хеш и реплей от неё не зависят), пока показаны; выключает её клавиша только
    /// тогда, когда включила сама (включённую агентом оставляет). K — контакты. В команды и реплей не идёт.
    /// </summary>
    public static readonly InputAction DebugContacts = new("debug_contacts", "K");

    /// <summary>
    /// Траектории Motion Matching игрока поверх кадра (оверлей <c>mm</c>): желаемая траектория, траектория найденного кадра, история
    /// корня, стопы на земле — показать / скрыть. M — Motion Matching. Действие представления: в команды и реплей не идёт.
    /// </summary>
    public static readonly InputAction DebugMotion = new("debug_motion", "M");

    /// <summary>
    /// Инвентарь (SWORD: I, <c>InventoryUi</c>): открыть / закрыть панель мечей (<see cref="InventoryPanel"/>). Действие
    /// представления: в команды и реплей идёт только выбор меча.
    /// </summary>
    public static readonly InputAction Inventory = new("inventory", "I");

    /// <summary>
    /// Меч инвентаря 1–9 (SWORD: цифры при открытом инвентаре — <c>EquipPlayerWeapon(Owned[k])</c>): при открытой панели — команда
    /// арены «взять меч» (<see cref="ArenaAction.EquipWeapon"/>) ровно одним тиком, после PageUp, PageDown, R, T.
    /// </summary>
    public static readonly IReadOnlyList<InputAction> Weapons =
        [.. Enumerable.Range(1, 9).Select(k => new InputAction($"weapon_{k}", $"D{k}"))];

    public static readonly IReadOnlyList<InputAction> All =
    [
        MoveForward, MoveBack, MoveLeft, MoveRight, Walk, CameraFollow, CameraRotateLeft, CameraRotateRight,
        CameraZoomIn, CameraZoomOut, CameraOrbit, CameraPanUp, CameraPanDown, CameraPanLeft, CameraPanRight, UiModifier,
        HudHelp, HudProfiler, EnemiesMore, EnemiesFewer, ArenaReset, NextWeapon, DebugBodies, Attack, Stab, Dash, Inventory, .. Weapons,
        DebugJoints, DebugContacts, DebugMotion,
    ];

    /// <summary>Управление камерой за кадр из ввода кадра.</summary>
    public static CameraControls Camera(in FrameInput input) => new()
    {
        ToggleFollow = input.WasPressed(CameraFollow),
        Wheel = input.IsDown(UiModifier) ? 0 : input.Wheel,   // wheelZoom: !Control SWORD
        ZoomIn = input.IsDown(CameraZoomIn),
        ZoomOut = input.IsDown(CameraZoomOut),
        RotateLeft = input.IsDown(CameraRotateLeft),
        RotateRight = input.IsDown(CameraRotateRight),
        Orbit = input.IsDown(CameraOrbit),
        MouseDeltaX = input.MouseDeltaX,
        MouseDeltaY = input.MouseDeltaY,
        PanUp = input.IsDown(CameraPanUp),
        PanDown = input.IsDown(CameraPanDown),
        PanLeft = input.IsDown(CameraPanLeft),
        PanRight = input.IsDown(CameraPanRight),
    };

    /// <summary>Удерживаемое управление игроком за кадр.</summary>
    public static PlayerKeys Player(in FrameInput input) => new(
        input.IsDown(MoveForward), input.IsDown(MoveBack), input.IsDown(MoveLeft), input.IsDown(MoveRight), input.IsDown(Walk));
}

/// <summary>
/// Команды арены тика из фронтов ввода (раздел 7 плана: нажатие — ровно одним тиком, первым после кадра, где оно было) в
/// порядке <c>Program.cs</c> SWORD: PageUp, PageDown, R, T. Возвращает, сколько команд записано в <paramref name="into"/>
/// (не больше <see cref="Max"/>).
/// </summary>
public static class ArenaIntent
{
    public const int Max = 4;

    public static int Commands(in TickInput input, Span<ArenaCommand> into)
    {
        int n = 0;
        if (input.WasPressed(ArenaInput.EnemiesMore)) into[n++] = new ArenaCommand { Action = ArenaAction.ChangeEnemies, Enemies = 1 };
        if (input.WasPressed(ArenaInput.EnemiesFewer)) into[n++] = new ArenaCommand { Action = ArenaAction.ChangeEnemies, Enemies = -1 };
        if (input.WasPressed(ArenaInput.ArenaReset)) into[n++] = new ArenaCommand { Action = ArenaAction.Reset };
        if (input.WasPressed(ArenaInput.NextWeapon)) into[n++] = new ArenaCommand { Action = ArenaAction.NextWeapon };
        return n;
    }
}

/// <summary>
/// Жест удара (<c>PlayerController.HandleInput</c> SWORD 2eab13b — <c>_swingMouse</c>): сдвиг мыши за последние кадры с
/// затуханием и выбор удара по нему. Отличия от SWORD: затухание — по времени, а не ×0,85 за кадр (полупериод тот же, что у ×0,85
/// при 60 Гц, ≈ 0,071 с: жест не зависит от частоты кадров); направление — не экранное: выбирается удар, чья траектория на экране
/// ближе к жесту (направление замаха бойца → мир по курсу, как показан игрок → экран камерой кадра), в команду — его направление
/// в системе бойца (x — вправо бойца, y — вверх).
/// </summary>
public static class StrikeGesture
{
    /// <summary>Сдвиг мыши, с которого клик — жест, пикс. (<c>GestureMouse</c>).</summary>
    public const float Threshold = 12f;

    /// <summary>Затухание SWORD: ×0,85 за кадр при 60 кадрах в секунду.</summary>
    public const float SwordFrameDecay = 0.85f, SwordFrameRate = 60f;

    /// <summary>Высота над землёй, на которой замах переносится на экран, м (грудь бойца).</summary>
    public const float ChestHeight = 1.3f;

    /// <summary>Сдвиг мыши за кадр длиной <paramref name="seconds"/>: прежний гаснет (0,85^(60·с)), новый прибавляется.</summary>
    public static Vector2 Accumulate(Vector2 mouse, float dx, float dy, float seconds) =>
        mouse * MathF.Pow(SwordFrameDecay, MathF.Max(0, seconds) * SwordFrameRate) + new Vector2(dx, dy);

    /// <summary>
    /// Жест в систему бойца: сдвиг мыши <paramref name="mouse"/> (экран: x вправо, y вниз) короче порога — ноль (связка); иначе —
    /// направление замаха из <paramref name="swings"/> (система бойца), чья траектория на экране ближе всего к жесту: замах на
    /// груди бойца (<paramref name="root"/>, курс <paramref name="facing"/>) переносится в мир и камерой <paramref name="viewProj"/>
    /// на экран <paramref name="width"/>×<paramref name="height"/>. Ни один замах не виден на экране (за камерой) — ноль.
    /// </summary>
    public static Vector2 Pick(Vector2 mouse, ReadOnlySpan<Vector2> swings, in Matrix4x4 viewProj, int width, int height, Vector3 root, float facing)
    {
        if (mouse.Length() < Threshold || swings.IsEmpty) return Vector2.Zero;
        var want = Vector2.Normalize(mouse);
        var chest = root + new Vector3(0, ChestHeight, 0);
        if (!ToScreen(chest, viewProj, width, height, out var from)) return Vector2.Zero;
        // система бойца: x — вправо бойца (в мире (−cos f, 0, sin f): +X персонажа — лево), y — вверх
        var right = new Vector3(-MathF.Cos(facing), 0, MathF.Sin(facing));
        int best = -1;
        float bestDot = float.NegativeInfinity;
        for (int i = 0; i < swings.Length; i++)
        {
            var world = chest + (right * swings[i].X + Vector3.UnitY * swings[i].Y) * 0.5f;
            if (!ToScreen(world, viewProj, width, height, out var to)) continue;
            var d = to - from;
            if (d.LengthSquared() < 1e-6f) continue;
            float dot = Vector2.Dot(Vector2.Normalize(d), want);
            if (dot > bestDot)
            {
                bestDot = dot;
                best = i;
            }
        }
        return best < 0 ? Vector2.Zero : swings[best];
    }

    /// <summary>Точка мира на экране, пикс. (x вправо, y вниз); за камерой — false.</summary>
    public static bool ToScreen(Vector3 p, in Matrix4x4 viewProj, int width, int height, out Vector2 screen)
    {
        var clip = Vector4.Transform(new Vector4(p, 1), viewProj);
        screen = default;
        if (clip.W <= 1e-6f) return false;
        screen = new Vector2((clip.X / clip.W + 1) * 0.5f * width, (1 - clip.Y / clip.W) * 0.5f * height);
        return true;
    }
}

/// <summary>Клавиши игрока за кадр: ход и шаг.</summary>
public readonly record struct PlayerKeys(bool Forward, bool Back, bool Left, bool Right, bool Walk);

/// <summary>
/// Команда игрока из ввода кадра (<c>PlayerController.Think</c> SWORD, часть ввода): направление WASD — в мировых
/// координатах по камере этого кадра (симуляция камеры не знает, раздел 7), Shift — шагом; курсор на земле — точка
/// взгляда. Направление не нормируется: это делает симуляция, как <c>Think</c>.
/// </summary>
public static class PlayerIntent
{
    public static PlayerCommand Command(PlayerKeys keys, RtsCamera camera, bool hasCursor, Vector3 cursor)
    {
        var wish = Vector3.Zero;
        if (keys.Forward) wish += camera.GroundForward;
        if (keys.Back) wish -= camera.GroundForward;
        if (keys.Right) wish += camera.GroundRight;
        if (keys.Left) wish -= camera.GroundRight;
        return new PlayerCommand
        {
            Move = new Vector2(wish.X, wish.Z),
            Walk = keys.Walk,
            HasGaze = hasCursor,
            Gaze = hasCursor ? cursor with { Y = 0 } : default,
        };
    }
}
