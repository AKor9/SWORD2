using System.Numerics;

namespace Sword.Engine.Client;

/// <summary>Камера кадра: матрица вида-проекции, глаз, точка взгляда, параметры RTS-камеры.</summary>
public readonly record struct CameraView(Matrix4x4 ViewProj, Vector3 Eye, Vector3 Target, float Yaw, float Pitch, float Distance, bool Follow)
{
    /// <summary>Поворот из осей вида (x — вправо, y — вверх, z — к зрителю) в мир; default — игра его не дала. Считается по
    /// углам и дистанции камеры, а не по позициям: при тех же углах — тот же бит в бит.</summary>
    public Quaternion Rotation { get; init; }
}

/// <summary>
/// Камера клиентской части игры для клиентских модулей (их проходы рисуют той же камерой). Необязательна: игра без неё даёт
/// модулям нулевую камеру.
/// </summary>
public interface IClientObservable
{
    /// <summary>Камера последнего кадра.</summary>
    CameraView Camera { get; }
}
