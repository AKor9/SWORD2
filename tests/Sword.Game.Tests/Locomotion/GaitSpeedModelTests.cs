using Sword.Game.Locomotion.Motion;
using Loop = Sword.Game.Locomotion.Motion.GaitSpeedModel.Loop;

namespace Sword.Game.Tests;

/// <summary>
/// Модель скоростей данных (8д): сектор — ближайший по направлению, среднее циклов сектора, пустой сектор — среднее ближайших
/// заполненных с обеих сторон, линейная интерполяция между секторами, шаг или бег — по скорости ввода.
/// </summary>
public sealed class GaitSpeedModelTests
{
    private static float Deg(float degrees) => degrees * MathF.PI / 180;

    private static Loop Walk(float degrees, float speed) => new(GaspGait.Walk, Deg(degrees), speed);
    private static Loop Run(float degrees, float speed) => new(GaspGait.Run, Deg(degrees), speed);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(20, 0)]
    [InlineData(-20, 0)]
    [InlineData(23, 1)]
    [InlineData(90, 2)]
    [InlineData(180, 4)]
    [InlineData(-180, 4)]
    [InlineData(-135, 5)]
    [InlineData(-90, 6)]
    [InlineData(-45, 7)]
    [InlineData(-23, 7)]
    public void Sector_IsNearest(float degrees, int sector) => Assert.Equal(sector, GaitSpeedModel.SectorOf(Deg(degrees)));

    [Fact]
    public void FullSectors_AverageLoops_InterpolateLinearly()
    {
        var model = new GaitSpeedModel(
        [
            Walk(0, 2.0f), Walk(20, 2.2f), Walk(45, 2.1f), Walk(90, 1.8f), Walk(90, 1.9f), Walk(135, 1.6f), Walk(180, 1.5f),
            Walk(-135, 1.6f), Walk(-90, 1.8f), Walk(-45, 2.1f),
        ], 2.5f);
        Assert.True(model.HasWalk);
        Assert.False(model.HasRun);
        Assert.Equal([2.1f, 2.1f, 1.85f, 1.6f, 1.5f, 1.6f, 1.8f, 2.1f], model.WalkSectors.ToArray(), (a, b) => MathF.Abs(a - b) < 1e-5f);
        Assert.Equal([2, 1, 2, 1, 1, 1, 1, 1], model.WalkLoops.ToArray());
        Assert.Equal(2.1f, model.Walk(0), 1e-5f);
        Assert.Equal((2.1f + 1.85f) / 2, model.Walk(Deg(67.5f)), 1e-5f);        // между 45° и 90°
        Assert.Equal(1.85f, model.Walk(Deg(90)), 1e-5f);
        Assert.Equal((1.6f + 1.5f) / 2, model.Walk(Deg(-157.5f)), 1e-5f);      // между 180° и −135°
        Assert.Equal(2.1f, model.Walk(Deg(-22.5f)), 1e-5f);                     // между −45° и 0° — оба 2,1
        Assert.Equal(1.5f, model.Walk(MathF.PI), 1e-5f);
        Assert.Equal(1.5f, model.Walk(-MathF.PI), 1e-5f);
        Assert.True(float.IsNaN(model.Run(0)));
    }

    [Fact]
    public void EmptySector_IsMeanOfNearestFilledOnBothSides()
    {
        // заполнены 0° (2,0), 90° влево (1,8), 180° (1,5); справа (−45…−135) и 45°, 135° — пусты
        var model = new GaitSpeedModel([Walk(0, 2.0f), Walk(90, 1.8f), Walk(180, 1.5f)], 2.5f);
        var s = model.WalkSectors.ToArray();
        Assert.Equal(2.0f, s[0]);
        Assert.Equal((2.0f + 1.8f) / 2, s[1], 1e-6f);     // 45°: 0° и 90°
        Assert.Equal(1.8f, s[2]);
        Assert.Equal((1.8f + 1.5f) / 2, s[3], 1e-6f);     // 135°: 90° и 180°
        Assert.Equal(1.5f, s[4]);
        Assert.Equal((1.5f + 2.0f) / 2, s[5], 1e-6f);     // −135°: 180° и 0° (через пустые −90°, −45°)
        Assert.Equal((1.5f + 2.0f) / 2, s[6], 1e-6f);     // −90°: по заполненным данными, а не по достроенным
        Assert.Equal((1.5f + 2.0f) / 2, s[7], 1e-6f);
        Assert.Equal([1, 0, 1, 0, 1, 0, 0, 0], model.WalkLoops.ToArray());

        var single = new GaitSpeedModel([Run(-90, 3.6f)], 2.5f);
        Assert.All(single.RunSectors.ToArray(), v => Assert.Equal(3.6f, v));
    }

    [Fact]
    public void Gait_ByInputSpeed()
    {
        var model = new GaitSpeedModel([Walk(0, 2.07f), Walk(180, 1.55f), Run(0, 5.17f), Run(180, 3.1f)], 2.5f);
        Assert.Equal(2.5f, model.RunInputSpeed);
        Assert.False(model.IsRun(1.6f));
        Assert.False(model.IsRun(2.5f));
        Assert.True(model.IsRun(2.6f));
        Assert.Equal(2.07f, model.Speed(0, 1.6f), 1e-5f);
        Assert.Equal(5.17f, model.Speed(0, 4.2f), 1e-5f);
        Assert.Equal(3.1f, model.Speed(MathF.PI, 4.2f), 1e-5f);
        Assert.Equal((5.17f + 3.1f) / 2, model.Speed(Deg(90), 4.2f), 1e-5f);   // 90° достроен из 0° и 180°

        var walkOnly = new GaitSpeedModel([Walk(0, 2f)], 2.5f);
        Assert.False(walkOnly.IsRun(4.2f));
        Assert.Equal(2f, walkOnly.Speed(0, 4.2f));
        var runOnly = new GaitSpeedModel([Run(0, 5f)], 2.5f);
        Assert.True(runOnly.IsRun(1f));
        Assert.Equal(5f, runOnly.Speed(0, 1f));
        var none = new GaitSpeedModel([], 2.5f);
        Assert.False(none.HasWalk || none.HasRun);
        Assert.Equal(1.7f, none.Speed(0, 1.7f));                                   // масштаб 1
    }

    [Fact]
    public void BadInput_IsError()
    {
        Assert.Throws<ArgumentException>(() => new GaitSpeedModel([new Loop(GaspGait.Stand, 0, 1)], 2.5f));
        Assert.Throws<ArgumentException>(() => new GaitSpeedModel([Walk(0, 0)], 2.5f));
        Assert.Throws<ArgumentException>(() => new GaitSpeedModel([Walk(float.NaN, 1)], 2.5f));
        Assert.Throws<ArgumentException>(() => new GaitSpeedModel([Walk(0, 1)], -1));
    }
}
