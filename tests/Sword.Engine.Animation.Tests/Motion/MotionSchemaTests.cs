using Sword.Engine.Animation.Motion;

namespace Sword.Engine.Animation.Tests;

/// <summary>Схема признаков: раскладка любой игры (в том числе 45 признаков), смещения групп, отказ противоречивой схеме.</summary>
public sealed class MotionSchemaTests
{
    // номера суставов ue5_manny (research-mm-data §1.4) — движку они только числа
    private const int FootL = 131, FootR = 108, CalfL = 130, CalfR = 107, Pelvis = 1, ThighL = 129, ThighR = 106, ClavicleL = 10, ClavicleR = 56;

    private static FeatureGroupSpec Group(string name, FeatureKind kind, params int[] joints) => new(name, kind, 1, 0.1f, joints);

    [Fact]
    public void FortyFiveFeatureLayout_IsDescribable()
    {
        var schema = new MotionSchema([1f / 3, 2f / 3, 1f], [-1f / 3],
        [
            new("траектория", FeatureKind.TrajectoryPosition, 3.5f, 0.2f),
            new("курс траектории", FeatureKind.TrajectoryDirection, 1.5f, 0.2f),
            new("прошлое", FeatureKind.PastPosition, 0.5f, 0.1f),
            new("скорость корня", FeatureKind.RootVelocity, 4f, 0.2f),
            new("ω корня", FeatureKind.RootYawRate, 0.5f, 0.5f),
            new("стопы", FeatureKind.JointPosition, 0.4f, 0.03f, [FootL, FootR]),
            new("v стоп", FeatureKind.JointVelocity, 0.5f, 0.2f, [FootL, FootR]),
            new("колени", FeatureKind.JointPosition, 0.25f, 0.03f, [CalfL, CalfR]),
            new("таз", FeatureKind.JointPosition, 0.25f, 0.02f, [Pelvis]),
            new("v таза", FeatureKind.JointVelocity, 0.5f, 0.1f, [Pelvis]),
            new("курс таза", FeatureKind.JointPairFacing, 0.5f, 0.1f, [ThighL, ThighR]),
            new("курс груди", FeatureKind.JointPairFacing, 0.25f, 0.1f, [ClavicleL, ClavicleR]),
        ], [FootL, FootR]);

        Assert.Equal(45, schema.FeatureCount);
        Assert.Equal(12, schema.GroupCount);
        Assert.Equal([0, 6, 12, 14, 16, 17, 23, 29, 35, 38, 41, 43], schema.Groups.ToArray().Select(g => g.Offset));
        Assert.Equal([6, 6, 2, 2, 1, 6, 6, 6, 3, 3, 2, 2], schema.Groups.ToArray().Select(g => g.Size));
        Assert.Equal([3.5f, 1.5f, 0.5f, 4f, 0.5f, 0.4f, 0.5f, 0.25f, 0.25f, 0.5f, 0.5f, 0.25f], schema.DefaultWeights.ToArray());
        Assert.Equal(0.02f, schema.Groups[schema.Find("таз")].MinStd);
        Assert.Equal([ThighL, ThighR], schema.GroupJoints(schema.Find("курс таза")).ToArray());
        // суставы, которые измеряет кинематика: без повторов, в порядке появления (группы, затем контакты)
        Assert.Equal([FootL, FootR, CalfL, CalfR, Pelvis, ThighL, ThighR, ClavicleL, ClavicleR], schema.Joints.ToArray());
        Assert.Equal([FootL, FootR], schema.ContactJoints.ToArray());
        Assert.Equal(4, schema.JointSlot(Pelvis));
        Assert.Equal(-1, schema.JointSlot(6));
        Assert.Equal(1f, schema.Horizon);
        Assert.Equal(1f / 3, schema.PastDepth);
        // шов графа сравнивает «позу» — всё, кроме траектории: с 14-го признака
        Assert.Equal(14, schema.Groups.ToArray().Where(g => !MotionSchema.IsTrajectory(g.Kind)).Min(g => g.Offset));
        Assert.Equal(3, schema.Find(FeatureKind.RootVelocity));
    }

    [Fact]
    public void KindCategories_CoverEveryKindOnce()
    {
        foreach (var kind in Enum.GetValues<FeatureKind>())
        {
            int n = (MotionSchema.IsTrajectory(kind) ? 1 : 0) + (MotionSchema.IsRoot(kind) ? 1 : 0) + (MotionSchema.IsJoint(kind) ? 1 : 0);
            Assert.True(n == 1, $"{kind}: в {n} видах");
        }
    }

    [Theory]
    [InlineData("будущее не растёт")]
    [InlineData("будущее с нуля")]
    [InlineData("прошлое не в прошлом")]
    [InlineData("прошлое не от ближнего")]
    [InlineData("нет групп")]
    [InlineData("групп больше предела")]
    [InlineData("нет имени")]
    [InlineData("имя повторяется")]
    [InlineData("вид корня дважды")]
    [InlineData("траектория без будущего")]
    [InlineData("прошлое без моментов")]
    [InlineData("суставы без суставов")]
    [InlineData("корень с суставами")]
    [InlineData("пары нечётные")]
    [InlineData("пара из одного")]
    [InlineData("сустав меньше нуля")]
    [InlineData("сустав повторяется")]
    [InlineData("сустав в двух группах")]
    [InlineData("вес меньше нуля")]
    [InlineData("вес не число")]
    [InlineData("пол разброса ноль")]
    [InlineData("неизвестный вид")]
    [InlineData("контакт повторяется")]
    [InlineData("контактов больше предела")]
    [InlineData("контакт меньше нуля")]
    public void ContradictorySchema_Throws(string what)
    {
        FeatureGroupSpec root = new("root", FeatureKind.RootVelocity, 1, 0.1f), traj = new("traj", FeatureKind.TrajectoryPosition, 1, 0.1f);
        float[] future = [0.5f, 1], past = [-0.3f];
        Action make = what switch
        {
            "будущее не растёт" => () => _ = new MotionSchema([0.5f, 0.5f], past, [traj]),
            "будущее с нуля" => () => _ = new MotionSchema([0, 1], past, [traj]),
            "прошлое не в прошлом" => () => _ = new MotionSchema(future, [0], [root]),
            "прошлое не от ближнего" => () => _ = new MotionSchema(future, [-0.2f, -0.1f], [root]),
            "нет групп" => () => _ = new MotionSchema(future, past, []),
            "групп больше предела" => () => _ = new MotionSchema(future, past,
                [.. Enumerable.Range(0, MotionSchema.MaxGroups + 1).Select(i => Group($"g{i}", FeatureKind.JointPosition, i))]),
            "нет имени" => () => _ = new MotionSchema(future, past, [root with { Name = " " }]),
            "имя повторяется" => () => _ = new MotionSchema(future, past, [root, traj with { Name = "root" }]),
            "вид корня дважды" => () => _ = new MotionSchema(future, past, [root, root with { Name = "root2" }]),
            "траектория без будущего" => () => _ = new MotionSchema([], past, [traj]),
            "прошлое без моментов" => () => _ = new MotionSchema(future, [], [new("past", FeatureKind.PastPosition, 1, 0.1f)]),
            "суставы без суставов" => () => _ = new MotionSchema(future, past, [Group("j", FeatureKind.JointPosition)]),
            "корень с суставами" => () => _ = new MotionSchema(future, past, [root with { Joints = [1] }]),
            "пары нечётные" => () => _ = new MotionSchema(future, past, [Group("f", FeatureKind.JointPairFacing, 1, 2, 3)]),
            "пара из одного" => () => _ = new MotionSchema(future, past, [Group("f", FeatureKind.JointPairFacing, 2, 2)]),
            "сустав меньше нуля" => () => _ = new MotionSchema(future, past, [Group("j", FeatureKind.JointVelocity, -1)]),
            "сустав повторяется" => () => _ = new MotionSchema(future, past, [Group("j", FeatureKind.JointPosition, 3, 4, 3)]),
            "сустав в двух группах" => () => _ = new MotionSchema(future, past,
                [Group("a", FeatureKind.JointPosition, 3, 4), Group("b", FeatureKind.JointPosition, 5, 4)]),
            "вес меньше нуля" => () => _ = new MotionSchema(future, past, [root with { DefaultWeight = -0.1f }]),
            "вес не число" => () => _ = new MotionSchema(future, past, [root with { DefaultWeight = float.NaN }]),
            "пол разброса ноль" => () => _ = new MotionSchema(future, past, [root with { MinStd = 0 }]),
            "неизвестный вид" => () => _ = new MotionSchema(future, past, [root with { Kind = (FeatureKind)99 }]),
            "контакт повторяется" => () => _ = new MotionSchema(future, past, [root], [3, 5, 3]),
            "контактов больше предела" => () => _ = new MotionSchema(future, past, [root],
                [.. Enumerable.Range(0, MotionSchema.MaxContactJoints + 1)]),
            "контакт меньше нуля" => () => _ = new MotionSchema(future, past, [root], [-2]),
            _ => throw new ArgumentOutOfRangeException(nameof(what), what, "нет такого случая"),
        };
        var e = Assert.ThrowsAny<ArgumentException>(make);
        Assert.False(string.IsNullOrWhiteSpace(e.Message));
    }

    [Fact]
    public void SameJointInDifferentKinds_IsAllowed()
    {
        // положение и скорость одного сустава — разные признаки; пары курса могут делить суставы с положениями
        var schema = new MotionSchema([], [],
            [Group("p", FeatureKind.JointPosition, 1, 2), Group("v", FeatureKind.JointVelocity, 1), Group("f", FeatureKind.JointPairFacing, 2, 1)]);
        Assert.Equal(6 + 3 + 2, schema.FeatureCount);
        Assert.Equal([1, 2], schema.Joints.ToArray());
    }

    [Fact]
    public void Schema_CopiesItsInput()
    {
        float[] future = [0.5f, 1];
        int[] joints = [1, 2];
        int[] contacts = [2];
        var schema = new MotionSchema(future, [], [Group("j", FeatureKind.JointPosition, joints), new("t", FeatureKind.TrajectoryPosition, 1, 0.1f)], contacts);
        future[0] = 0.7f;
        joints[0] = 9;
        contacts[0] = 9;
        Assert.Equal([0.5f, 1], schema.FutureTimes.ToArray());
        Assert.Equal([1, 2], schema.GroupJoints(0).ToArray());
        Assert.Equal([2], schema.ContactJoints.ToArray());
        Assert.Equal([1, 2], schema.Joints.ToArray());
    }
}
