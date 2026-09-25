using System.Numerics;

namespace Sword.Engine.Animation.Motion;

/// <summary>
/// Кинематика кадров базы (точка 8б) — только на постройку: на каждый кадр выбранных клипов (локальные номера, клипы — по
/// возрастанию номера в библиотеке, как у <see cref="MotionDatabase"/>) система корня, скорость и угловая скорость корня,
/// положения и скорости суставов <see cref="MotionSchema.Joints"/> в системе корня кадра, контакты. Корень кадра —
/// <see cref="ClipLibrary.MotionMatrix"/> (<see cref="Pose2"/>); соседи у краёв цикла — через шов на соседний оборот (последний
/// кадр цикла = первый, оборот — <c>Relative(первый, последний)</c>), у нециклического — упор в край. Положения — прямой
/// кинематикой <see cref="Skeleton.ToModel"/>, скорости — центральной разностью мировых точек, выраженной в системе корня кадра.
/// Внутренняя кухня постройки: другие точки её не зовут, 8б может уточнять её сигнатуры.
/// </summary>
public sealed class ClipKinematics
{
    private ClipKinematics()
    {
    }

    /// <summary>Кинематика клипов <paramref name="clips"/> библиотеки (номера — по строгому возрастанию) для суставов и контактов схемы.</summary>
    /// <exception cref="ArgumentException">Номера клипов вне библиотеки или не по возрастанию; суставы схемы вне скелета.</exception>
    public static ClipKinematics Compute(ClipLibrary library, ReadOnlySpan<int> clips, MotionSchema schema, in ContactParams contacts) =>
        throw new NotImplementedException("8б: кинематика клипов — ClipKinematics.Compute");

    /// <summary>Кадров (всех выбранных клипов подряд).</summary>
    public int FrameCount => throw new NotImplementedException("8б");

    /// <summary>Суставов измерено — <see cref="MotionSchema.Joints"/> (в их порядке).</summary>
    public int JointCount => throw new NotImplementedException("8б");

    /// <summary>[кадр] система корня кадра в системе первого кадра своего клипа.</summary>
    public ReadOnlySpan<Pose2> Root => throw new NotImplementedException("8б");

    /// <summary>[кадр] скорость корня в системе корня кадра, м/с (Y = 0).</summary>
    public ReadOnlySpan<Vector3> RootVelocity => throw new NotImplementedException("8б");

    /// <summary>[кадр] угловая скорость курса корня, рад/с (+ — от +Z к +X).</summary>
    public ReadOnlySpan<float> YawRate => throw new NotImplementedException("8б");

    /// <summary>[кадр·сустав] положение сустава в системе корня кадра, м.</summary>
    public ReadOnlySpan<Vector3> Positions => throw new NotImplementedException("8б");

    /// <summary>[кадр·сустав] скорость сустава (мировая), выраженная в системе корня кадра, м/с.</summary>
    public ReadOnlySpan<Vector3> Velocities => throw new NotImplementedException("8б");

    /// <summary>[кадр] контакты: бит i — сустав <see cref="MotionSchema.ContactJoints"/>[i] на земле.</summary>
    public ReadOnlySpan<byte> Contacts => throw new NotImplementedException("8б");

    /// <summary>
    /// Система корня кадра <paramref name="frame"/> клипа <paramref name="clip"/> (номер среди выбранных) в системе его первого
    /// кадра; кадр вне клипа — у цикла по оборотам (−1 — предпоследний кадр прошлого оборота), у нециклического — упор в край.
    /// </summary>
    public Pose2 RootAt(int clip, int frame) => throw new NotImplementedException("8б");
}
