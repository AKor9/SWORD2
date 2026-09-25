using Sword.Game.Locomotion.Motion;

namespace Sword.Game.Tests;

/// <summary>
/// Разбор имён GASP на синтетических строках всех семейств пака: вид, походка, направления входа и выхода, нога, сторона и угол,
/// фигура, хвост; префикс стиля пропускается; опечатка в обязательной части — ошибка с причиной, а не «неизвестное».
/// </summary>
public sealed class GaspClipNameTests
{
    private const GaspDirection No = GaspDirection.None;

    private static GaspClipName Name(GaspKind kind, GaspGait gait, GaspDirection @in = No, GaspDirection @out = No, GaspSide foot = GaspSide.None,
        GaspSide side = GaspSide.None, int degrees = 0, string figure = "", string tail = "", GaspGait? from = null, string style = "M_Neutral") =>
        new(style, kind, gait, from ?? gait, @in, @out, foot, side, degrees, figure, tail);

    public static TheoryData<string, GaspClipName> Families => new()
    {
        { "M_Neutral_Stand_Idle_Loop", Name(GaspKind.Idle, GaspGait.Stand) },
        { "M_Neutral_Stand_Turn_090_L", Name(GaspKind.TurnInPlace, GaspGait.Stand, side: GaspSide.Left, degrees: 90) },
        { "M_Neutral_Stand_Turn_180_R", Name(GaspKind.TurnInPlace, GaspGait.Stand, side: GaspSide.Right, degrees: 180) },
        { "M_Neutral_Walk_Loop_BL", Name(GaspKind.Loop, GaspGait.Walk, GaspDirection.BL, GaspDirection.BL) },
        { "M_Neutral_Run_Loop_F_R_20", Name(GaspKind.Loop, GaspGait.Run, GaspDirection.F, GaspDirection.F, side: GaspSide.Right, degrees: 20) },
        { "M_Neutral_Walk_Start_FR_Rfoot", Name(GaspKind.Start, GaspGait.Walk, @out: GaspDirection.FR, foot: GaspSide.Right) },
        { "M_Neutral_Run_Stop_LL_Lfoot", Name(GaspKind.Stop, GaspGait.Run, GaspDirection.LL, foot: GaspSide.Left) },
        { "M_Neutral_Walk_Pivot_FL_BR_Rfoot", Name(GaspKind.Pivot, GaspGait.Walk, GaspDirection.FL, GaspDirection.BR, GaspSide.Right) },
        { "M_Neutral_Run_Reface_Start_B_R_180", Name(GaspKind.Reface, GaspGait.Run, @out: GaspDirection.B, side: GaspSide.Right, degrees: 180) },
        { "M_Neutral_Walk_Reface_Start_F_L_180_backstep", Name(GaspKind.Reface, GaspGait.Walk, @out: GaspDirection.F, side: GaspSide.Left, degrees: 180, tail: "backstep") },
        { "M_Neutral_Walk_Turn_R_135_Lfoot", Name(GaspKind.MoveTurn, GaspGait.Walk, foot: GaspSide.Left, side: GaspSide.Right, degrees: 135) },
        { "M_Neutral_Run_Arc_Wide_L", Name(GaspKind.Arc, GaspGait.Run, side: GaspSide.Left, figure: "Wide") },
        { "M_Neutral_Walk_Arc_F_Tight_R", Name(GaspKind.Arc, GaspGait.Walk, GaspDirection.F, GaspDirection.F, side: GaspSide.Right, figure: "Tight") },
        { "M_Neutral_Run_Box_F_RL_Lfoot", Name(GaspKind.DirectionChange, GaspGait.Run, GaspDirection.F, GaspDirection.RL, GaspSide.Left, figure: "Box") },
        { "M_Neutral_Walk_Diamond_BL_BR_Rfoot", Name(GaspKind.DirectionChange, GaspGait.Walk, GaspDirection.BL, GaspDirection.BR, GaspSide.Right, figure: "Diamond") },
        { "M_Neutral_Run_Hourglass_LR_FR_Lfoot", Name(GaspKind.DirectionChange, GaspGait.Run, GaspDirection.LR, GaspDirection.FR, GaspSide.Left, figure: "Hourglass") },
        { "M_Neutral_Walk_Prism_FL_F_Lfoot", Name(GaspKind.DirectionChange, GaspGait.Walk, GaspDirection.FL, GaspDirection.F, GaspSide.Left, figure: "Prism") },
        { "M_Neutral_Walk_Shuffle_LR_to_LL_Rfoot", Name(GaspKind.DirectionChange, GaspGait.Walk, GaspDirection.LR, GaspDirection.LL, GaspSide.Right, figure: "Shuffle") },
        { "M_Neutral_Walk_Shuffle_F_RFF_to_LFF_Lfoot", Name(GaspKind.DirectionChange, GaspGait.Walk, GaspDirection.F, GaspDirection.F, GaspSide.Left, figure: "Shuffle", tail: "RFF_to_LFF") },
        { "M_Neutral_Run_Spin_F_B_Lfoot", Name(GaspKind.DirectionChange, GaspGait.Run, GaspDirection.F, GaspDirection.B, GaspSide.Left, figure: "Spin") },
        { "M_Neutral_Walk_Spin_LL_to_F_Rfoot", Name(GaspKind.DirectionChange, GaspGait.Walk, GaspDirection.LL, GaspDirection.F, GaspSide.Right, figure: "Spin") },
        { "M_Neutral_Walk_Circle_Strafe_R", Name(GaspKind.CircleStrafe, GaspGait.Walk, side: GaspSide.Right) },
        { "M_Neutral_Transition_Walk_to_Run_Lfoot", Name(GaspKind.GaitTransition, GaspGait.Run, foot: GaspSide.Left, from: GaspGait.Walk) },
        { "M_Neutral_Transition_Run_to_Walk_Rfoot", Name(GaspKind.GaitTransition, GaspGait.Walk, foot: GaspSide.Right, from: GaspGait.Run) },
        // стиль — всё до походки: другой или никакого
        { "F_Happy_Run_Loop_RR", Name(GaspKind.Loop, GaspGait.Run, GaspDirection.RR, GaspDirection.RR, style: "F_Happy") },
        { "Walk_Loop_F", Name(GaspKind.Loop, GaspGait.Walk, GaspDirection.F, GaspDirection.F, style: "") },
    };

    [Theory]
    [MemberData(nameof(Families))]
    public void Families_Parse(string shortName, GaspClipName expected)
    {
        Assert.True(GaspClipName.TryParse(shortName, out var name, out string error), error);
        Assert.Equal("", error);
        Assert.Equal(expected, name);
        Assert.Equal(expected, GaspClipName.Parse(shortName));
    }

    [Theory]
    [InlineData("M_Neutral_Jump_Up", "нет походки")]
    [InlineData("M_Neutral_Walk", "нет вида")]
    [InlineData("M_Neutral_Walk_Hop_F", "неизвестный вид «Hop»")]
    [InlineData("M_Neutral_Walk_Start_F_Lfot", "нет ведущей ноги")]
    [InlineData("M_Neutral_Walk_Start_X_Lfoot", "нет направления")]
    [InlineData("M_Neutral_Walk_Idle_Loop", "нужна Stand")]
    [InlineData("M_Neutral_Stand_Loop_F", "у походки Stand")]
    [InlineData("M_Neutral_Run_Loop_F_L", "нет угла")]
    [InlineData("M_Neutral_Run_Turn_L_Lfoot", "нет угла")]
    [InlineData("M_Neutral_Stand_Turn_090", "нет стороны")]
    [InlineData("M_Neutral_Walk_Box_F_Lfoot", "нужно два направления")]
    [InlineData("M_Neutral_Walk_Box_F_LL", "нет ведущей ноги")]
    [InlineData("M_Neutral_Walk_Box_F_RLL_Lfoot", "нет направления «куда»")]
    [InlineData("M_Neutral_Walk_Shuffle_RFF_to_LFF_Lfoot", "нет направления «откуда»")]
    [InlineData("M_Neutral_Walk_Circle_L", "Circle без Strafe")]
    [InlineData("M_Neutral_Walk_Arc_F_Small", "нет стороны")]
    [InlineData("M_Neutral_Transition_Walk_to_Walk_Lfoot", "смена походки Walk → Walk")]
    [InlineData("M_Neutral_Transition_Walk_Run_Lfoot", "нет «to»")]
    public void Typos_AreErrors_WithReason(string shortName, string reason)
    {
        Assert.False(GaspClipName.TryParse(shortName, out _, out string error));
        Assert.Contains(reason, error);
        var e = Assert.Throws<InvalidDataException>(() => GaspClipName.Parse(shortName));
        Assert.Contains(shortName, e.Message);
        Assert.Contains(reason, e.Message);
    }

    [Fact]
    public void DirectionAngles_PlusIsLeft()
    {
        Assert.Equal(0, GaspClipName.Angle(GaspDirection.F));
        Assert.Equal(MathF.PI / 4, GaspClipName.Angle(GaspDirection.FL));
        Assert.Equal(MathF.PI / 2, GaspClipName.Angle(GaspDirection.LL));
        Assert.Equal(MathF.PI / 2, GaspClipName.Angle(GaspDirection.LR));
        Assert.Equal(3 * MathF.PI / 4, GaspClipName.Angle(GaspDirection.BL));
        Assert.Equal(MathF.PI, GaspClipName.Angle(GaspDirection.B));
        Assert.Equal(-3 * MathF.PI / 4, GaspClipName.Angle(GaspDirection.BR));
        Assert.Equal(-MathF.PI / 2, GaspClipName.Angle(GaspDirection.RL));
        Assert.Equal(-MathF.PI / 2, GaspClipName.Angle(GaspDirection.RR));
        Assert.Equal(-MathF.PI / 4, GaspClipName.Angle(GaspDirection.FR));
        Assert.Equal(0, GaspClipName.Angle(GaspDirection.None));
    }
}
