using System.Numerics;
using Sword.Engine.Client;
using Sword.Game.Client.Scene;
using Sword.Game.Contracts;

namespace Sword.Game.Client;

/// <summary>
/// Действия ввода арены и их клавиши по умолчанию — как в SWORD 2eab13b (<c>PlayerController</c>, <c>RtsCamera</c>):
/// WASD — ход относительно камеры, Shift — шагом, F — камера за игроком / свободная, Z/C — поворот, +/− — зум, средняя
/// кнопка — орбита, стрелки — панорама свободной камеры, Ctrl — колесо не зумит, F1 — справка HUD; PageUp/PageDown —
/// врагов ±1, R — сброс арены (<c>Program.cs</c> SWORD, этап 4а — команды арены), T — следующий меч игрока (6б, команда арены);
/// F7 — капсулы тел и клинки поверх персонажей (5в, 6б; отладочный вид SWORD2); F2 — профайлер в HUD (точка Пв; F2 SWORD — «ПРОФАЙЛЕР» панели). Колесо и курсор — не
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
    /// Профайлер в HUD: главные зоны кадра, строка среды, последняя аномалия — показать / скрыть (SWORD: F2 — «ПРОФАЙЛЕР» панели;
    /// точка Пв). Включает профайлер CPU, если он выключен (выключает — только если его включил сам). Действие представления: в
    /// команды и реплей не идёт.
    /// </summary>
    public static readonly InputAction HudProfiler = new("hud_profiler", "F2");

    /// <summary>Врагов на одного больше / меньше (SWORD: PageUp / PageDown).</summary>
    public static readonly InputAction EnemiesMore = new("enemies_more", "PageUp");
    public static readonly InputAction EnemiesFewer = new("enemies_fewer", "PageDown");

    /// <summary>Сброс арены: игрок в центре, враги заново (SWORD: R).</summary>
    public static readonly InputAction ArenaReset = new("arena_reset", "R");

    /// <summary>Следующий меч инвентаря игрока по кругу (SWORD: T, <c>NextPlayerWeapon</c>; 6б — команда арены в реплей).</summary>
    public static readonly InputAction NextWeapon = new("next_weapon", "T");

    /// <summary>
    /// Капсулы тел и клинки поверх персонажей: показать / скрыть (5в, 6б; в SWORD отладочного вида капсул нет, F2, F3, F5, F6
    /// заняты — F7). Действие представления: в команды и реплей не идёт.
    /// </summary>
    public static readonly InputAction DebugBodies = new("debug_bodies", "F7");

    public static readonly IReadOnlyList<InputAction> All =
    [
        MoveForward, MoveBack, MoveLeft, MoveRight, Walk, CameraFollow, CameraRotateLeft, CameraRotateRight,
        CameraZoomIn, CameraZoomOut, CameraOrbit, CameraPanUp, CameraPanDown, CameraPanLeft, CameraPanRight, UiModifier,
        HudHelp, HudProfiler, EnemiesMore, EnemiesFewer, ArenaReset, NextWeapon, DebugBodies,
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
