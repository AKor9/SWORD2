using System.Numerics;

namespace Sword.Game.Client.Scene;

/// <summary>Ракурс RTS-камеры: поворот и наклон, рад; дистанция, м; точка взгляда на земле (x, z), м.</summary>
public readonly record struct CameraPose(float Yaw, float Pitch, float Distance, float TargetX = 0, float TargetZ = 0);

/// <summary>
/// RTS-камера: точка взгляда на земле, поворот, наклон, дистанция; проекция с обратным Z и бесконечной дальней
/// плоскостью. Перенос <c>Gameplay/RtsCamera.cs</c> SWORD 2eab13b — на точке 1б только построение матриц
/// (<see cref="Rebuild"/>) с исходными значениями SWORD; управление (колесо, поворот, следование) — точка 1г.
/// </summary>
public sealed class RtsCamera
{
    public Vector3 Target;
    public float Yaw = 0.6f;
    public float Pitch = 0.95f;
    public float Distance = 24f;

    public const float Near = 0.1f;
    public const float FovY = 0.9f;

    public Vector3 Eye { get; private set; }
    public Matrix4x4 View { get; private set; }
    public Matrix4x4 Projection { get; private set; }
    public Matrix4x4 ViewProj { get; private set; }

    public void Rebuild(int width, int height)
    {
        var back = new Vector3(MathF.Sin(Yaw) * MathF.Cos(Pitch), MathF.Sin(Pitch), MathF.Cos(Yaw) * MathF.Cos(Pitch));
        Eye = Target + back * Distance;
        View = Matrix4x4.CreateLookAt(Eye, Target + new Vector3(0, 0.8f, 0), Vector3.UnitY);

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
}
