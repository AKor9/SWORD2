using System.Numerics;

namespace Sword.Game.Client.Scene;

/// <summary>Ракурс RTS-камеры: поворот и наклон, рад; дистанция, м; точка взгляда на земле (x, z), м.</summary>
public readonly record struct CameraPose(float Yaw, float Pitch, float Distance, float TargetX = 0, float TargetZ = 0);

/// <summary>
/// Управление камерой за кадр — уже из действий ввода (клиент игры переводит их сюда; камера о клавишах не знает).
/// </summary>
public struct CameraControls
{
    /// <summary>Переключить «за игроком» / свободная (F).</summary>
    public bool ToggleFollow;

    /// <summary>Щелчки колеса (вперёд — плюс, ближе); удержание <see cref="ZoomIn"/>/<see cref="ZoomOut"/> — клавишами.</summary>
    public float Wheel;
    public bool ZoomIn, ZoomOut;

    /// <summary>Поворот клавишами (Z — влево, C — вправо).</summary>
    public bool RotateLeft, RotateRight;

    /// <summary>Орбита мышью (средняя кнопка): поворот и наклон по смещению мыши за кадр.</summary>
    public bool Orbit;
    public float MouseDeltaX, MouseDeltaY;

    /// <summary>Панорама свободной камеры (стрелки).</summary>
    public bool PanUp, PanDown, PanLeft, PanRight;
}

/// <summary>
/// RTS-камера: точка взгляда на земле, поворот, наклон, дистанция; проекция с обратным Z и бесконечной дальней
/// плоскостью. Перенос <c>Gameplay/RtsCamera.cs</c> SWORD 2eab13b: режимы «за игроком» (по умолчанию) и свободная
/// (панорама), колесо и +/− — зум со сглаживанием, Z/C и средняя кнопка — поворот, наклон — средней кнопкой.
/// Панорама краем экрана в арене SWORD выключена (<c>mouseInside: false</c>) — здесь её нет.
/// </summary>
public sealed class RtsCamera
{
    public Vector3 Target;
    public float Yaw = 0.6f;
    public float Pitch = 0.95f;
    public float Distance = 24f;
    public bool Follow = true;

    public const float Near = 0.1f;
    public const float FovY = 0.9f;

    /// <summary>Пределы зума и наклона SWORD.</summary>
    public const float MinDistance = 3f, MaxDistance = 110f, MinPitch = 0.45f, MaxPitch = 1.45f;

    public Vector3 Eye { get; private set; }

    /// <summary>Вид смотрит на столько выше точки взгляда, м.</summary>
    public const float LookAbove = 0.8f;

    /// <summary>
    /// Поворот из осей вида (x — вправо по экрану, y — вверх, z — к зрителю) в мир — по углам и дистанции, без позиций: при тех же
    /// углах тот же бит в бит, где бы ни была цель (площадка огня к камере повторяется по тикам).
    /// </summary>
    public Quaternion Rotation { get; private set; }
    public Matrix4x4 View { get; private set; }
    public Matrix4x4 Projection { get; private set; }
    public Matrix4x4 ViewProj { get; private set; }

    private float _targetDistance = 24f;

    /// <summary>Дистанция, к которой идёт зум.</summary>
    public float TargetDistance => _targetDistance;

    /// <summary>Дистанция сразу, без сглаживания (ключ запуска, снимки): 6…110 м, как <c>SetDistance</c> SWORD.</summary>
    public void SetDistance(float distance) => Distance = _targetDistance = Math.Clamp(distance, 6f, MaxDistance);

    /// <summary>Направления «вперёд» и «вправо» камеры на плоскости земли.</summary>
    public Vector3 GroundForward => new(-MathF.Sin(Yaw), 0, -MathF.Cos(Yaw));
    public Vector3 GroundRight => new(MathF.Cos(Yaw), 0, -MathF.Sin(Yaw));

    /// <summary>Кадр камеры (<c>RtsCamera.Update</c> SWORD): зум, поворот, следование за <paramref name="player"/> или панорама.</summary>
    public void Update(in CameraControls c, float dt, Vector3 player, int width, int height)
    {
        if (c.ToggleFollow) Follow = !Follow;

        float zoom = c.Wheel;
        if (c.ZoomIn) zoom += 4 * dt;
        if (c.ZoomOut) zoom -= 4 * dt;
        if (zoom != 0) _targetDistance = Math.Clamp(_targetDistance * MathF.Pow(0.88f, zoom), MinDistance, MaxDistance);
        Distance += (_targetDistance - Distance) * (1 - MathF.Exp(-12 * dt));

        float rot = 0;
        if (c.RotateLeft) rot -= 1.8f * dt;
        if (c.RotateRight) rot += 1.8f * dt;
        if (c.Orbit)
        {
            Yaw -= c.MouseDeltaX * 0.006f;
            Pitch = Math.Clamp(Pitch + c.MouseDeltaY * 0.004f, MinPitch, MaxPitch);
        }
        Yaw += rot;

        if (Follow)
        {
            Target += (player - Target) * (1 - MathF.Exp(-10 * dt));
        }
        else
        {
            Vector2 pan = Vector2.Zero;
            if (c.PanUp) pan.Y += 1;
            if (c.PanDown) pan.Y -= 1;
            if (c.PanLeft) pan.X -= 1;
            if (c.PanRight) pan.X += 1;
            float speed = Distance * 1.2f;
            Target += (GroundRight * pan.X + GroundForward * pan.Y) * speed * dt;
            Target.Y = 0;
            float r = new Vector2(Target.X, Target.Z).Length();
            if (r > 80) Target *= 80 / r;
        }

        Rebuild(width, height);
    }

    public void Rebuild(int width, int height)
    {
        var back = new Vector3(MathF.Sin(Yaw) * MathF.Cos(Pitch), MathF.Sin(Pitch), MathF.Cos(Yaw) * MathF.Cos(Pitch));
        Eye = Target + back * Distance;
        View = Matrix4x4.CreateLookAt(Eye, Target + new Vector3(0, LookAbove, 0), Vector3.UnitY);
        var z = Vector3.Normalize(back * Distance - new Vector3(0, LookAbove, 0));
        var x = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, z));
        var y = Vector3.Cross(z, x);
        Rotation = Quaternion.CreateFromRotationMatrix(new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1));

        // Обратный Z с бесконечной дальней плоскостью: z/w = near / -z_view.
        float ys = 1f / MathF.Tan(FovY * 0.5f);
        float xs = ys / (width / (float)Math.Max(height, 1));
        Projection = new Matrix4x4(
            xs, 0, 0, 0,
            0, ys, 0, 0,
            0, 0, 0, -1,
            0, 0, Near, 0);
        ViewProj = View * Projection;
    }

    /// <summary>Точка земли (y = 0) под курсором (<c>ScreenToGround</c> SWORD: плоскость, а не рельеф).</summary>
    public bool ScreenToGround(float mx, float my, int width, int height, out Vector3 hit)
    {
        hit = default;
        if (!Matrix4x4.Invert(ViewProj, out var inv)) return false;
        float nx = mx / width * 2 - 1, ny = 1 - my / height * 2;
        var a = Vector4.Transform(new Vector4(nx, ny, 1f, 1f), inv);
        var b = Vector4.Transform(new Vector4(nx, ny, 0.01f, 1f), inv);
        var p0 = new Vector3(a.X, a.Y, a.Z) / a.W;
        var p1 = new Vector3(b.X, b.Y, b.Z) / b.W;
        var d = p1 - p0;
        if (MathF.Abs(d.Y) < 1e-6f) return false;
        float t = -p0.Y / d.Y;
        if (t < 0) return false;
        hit = p0 + d * t;
        return true;
    }
}
