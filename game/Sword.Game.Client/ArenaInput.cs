using System.Numerics;
using Sword.Engine.Client;
using Sword.Game.Client.Scene;
using Sword.Game.Contracts;

namespace Sword.Game.Client;

/// <summary>
/// Действия ввода арены и их клавиши по умолчанию — как в SWORD 2eab13b (<c>PlayerController</c>, <c>RtsCamera</c>):
/// WASD — ход относительно камеры, Shift — шагом, F — камера за игроком / свободная, Z/C — поворот, +/− — зум, средняя
/// кнопка — орбита, стрелки — панорама свободной камеры, Ctrl — колесо не зумит. Колесо и курсор — не действия: они
/// берутся из ввода кадра.
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

    /// <summary>Пока удерживается, колесо не зумит (SWORD: Ctrl+колесо — масштаб интерфейса, точка 1д).</summary>
    public static readonly InputAction UiModifier = new("ui_modifier", "Control");

    public static readonly IReadOnlyList<InputAction> All =
    [
        MoveForward, MoveBack, MoveLeft, MoveRight, Walk, CameraFollow, CameraRotateLeft, CameraRotateRight,
        CameraZoomIn, CameraZoomOut, CameraOrbit, CameraPanUp, CameraPanDown, CameraPanLeft, CameraPanRight, UiModifier,
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
