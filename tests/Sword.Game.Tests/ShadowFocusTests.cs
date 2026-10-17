using System.Numerics;
using Sword.Game.Client.Scene;

namespace Sword.Game.Tests;

/// <summary>
/// Шары каскадов теней (<see cref="ShadowFocus"/>): у низкой камеры вблизи цель камеры (игрок) с тенью — внутри ближнего каскада
/// под любым углом (иначе его тень режет шов с размытым дальним); у камеры сверху ближний — вокруг точки взгляда, как был.
/// </summary>
public class ShadowFocusTests
{
    private static RtsCamera Camera(float yaw, float pitch, float distance, Vector3 target)
    {
        var camera = new RtsCamera { Yaw = yaw, Pitch = pitch, Target = target };
        camera.SetDistance(distance);
        camera.Rebuild(1920, 1080);
        return camera;
    }

    [Theory]
    [InlineData(RtsCamera.MinDistance)]
    [InlineData(6f)]
    [InlineData(12f)]
    public void LowCloseCamera_TargetInsideNearCascadeWithMargin(float distance)
    {
        var target = new Vector3(5, 0, -3);
        for (int k = 0; k < 16; k++)
        {
            var camera = Camera(k * MathF.PI / 8, RtsCamera.MinPitch, distance, target);
            var s = ShadowFocus.Of(camera.ViewProj, target);
            float reach = Vector3.Distance(s.NearCenter, target) + ShadowFocus.KeepMargin;
            Assert.True(reach <= s.NearRadius + 1e-3f, $"угол {k}: цель в {Vector3.Distance(s.NearCenter, target):0.##} м от центра, радиус {s.NearRadius:0.##}");
            Assert.InRange(s.NearRadius, ShadowFocus.MinRadius, ShadowFocus.MaxNearRadius);
        }
    }

    [Fact]
    public void TopDownCamera_NearAroundLookPoint()
    {
        var target = new Vector3(-2, 0, 4);
        var camera = Camera(0.6f, RtsCamera.MaxPitch, 24f, target);
        var s = ShadowFocus.Of(camera.ViewProj, target);
        // Сверху точка взгляда — почти у цели: центр ближнего — между ними, в пределах метра от цели.
        Assert.True(Vector3.Distance(s.NearCenter, new Vector3(target.X, 0, target.Z)) < 1f);
        Assert.Equal(Math.Clamp(ShadowFocus.NearShare * Vector3.Distance(camera.Eye, s.NearCenter), ShadowFocus.MinRadius, ShadowFocus.MaxNearRadius),
            s.NearRadius, 0.5f);
    }
}
