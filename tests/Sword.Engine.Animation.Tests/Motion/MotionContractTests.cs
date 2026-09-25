using System.Numerics;
using System.Runtime.CompilerServices;
using Sword.Engine.Animation.Motion;

namespace Sword.Engine.Animation.Tests;

/// <summary>
/// Значения контракта Motion Matching, которые игра хранит в компонентах (компоненты ECS — только <c>unmanaged</c>): итог
/// поиска с ценами по группам, хэндл, состояние и шаг проигрывателя, модель и состояние предсказания, запись истории.
/// </summary>
public sealed class MotionContractTests
{
    [Theory]
    [InlineData(typeof(Pose2))]
    [InlineData(typeof(MotionMatch))]
    [InlineData(typeof(MotionGroupCosts))]
    [InlineData(typeof(MotionPlayerHandle))]
    [InlineData(typeof(MotionPlayerState))]
    [InlineData(typeof(MotionStep))]
    [InlineData(typeof(TrajectoryState))]
    [InlineData(typeof(TrajectoryModel))]
    [InlineData(typeof(RootSample))]
    [InlineData(typeof(MotionDatabaseSummary))]
    public void ValueTypes_HoldNoReferences(Type type)
    {
        var check = typeof(RuntimeHelpers).GetMethod(nameof(RuntimeHelpers.IsReferenceOrContainsReferences))!.MakeGenericMethod(type);
        Assert.False((bool)check.Invoke(null, null)!, $"{type.Name} содержит ссылки — в компонент не положить");
    }

    [Fact]
    public void Match_CarriesCostPerGroup()
    {
        var costs = new MotionGroupCosts();
        for (int g = 0; g < MotionSchema.MaxGroups; g++) costs[g] = g * 0.5f;
        var match = new MotionMatch(7, 1.5f, 2, 3, 40, costs);
        var groups = match.Groups;
        Assert.Equal(MotionSchema.MaxGroups, ((ReadOnlySpan<float>)groups).Length);
        Assert.Equal(3.5f, match.Groups[7]);
        Assert.Equal(match, match with { });
        Assert.NotEqual(match, match with { Groups = new MotionGroupCosts() });
    }

    [Fact]
    public void ClipInfo_DirectionsFollowAxisConvention()
    {
        // + — от +Z к +X (влево персонажа); назад — π
        var left = new MotionClipInfo(0, false, 10, Vector3.Zero, new Vector3(1.2f, 0, 0), new Vector3(0, 0, -2), 0);
        Assert.Equal(MathF.PI / 2, left.ExitDirection, 1e-6f);
        Assert.Equal(MathF.PI, left.MeanDirection, 1e-6f);
        Assert.Equal(1.2f, left.ExitSpeed, 1e-6f);
    }
}
