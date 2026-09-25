using Sword.Engine.Animation.Motion;

namespace Sword.Engine.Animation.Tests;

/// <summary>
/// Детектор контактов стоп: гистерезис по высоте и по скорости (между порогами входа и выхода состояние держится), внутренние
/// серии короче <see cref="ContactParams.MinRun"/> переворачиваются, серии у краёв клипа — нет; высота — от «пола» клипа.
/// </summary>
public sealed class ContactDetectorTests
{
    private static readonly ContactParams Thresholds = new(0.05f, 0.07f, 0.35f, 0.55f, 1);

    private static bool[] Detect(float[] height, float[] speed, ContactParams? parameters = null)
    {
        var contact = new bool[height.Length];
        ContactDetector.Detect(height, speed, parameters ?? Thresholds, contact);
        return contact;
    }

    private static bool[] Pattern(string pattern) => [.. pattern.Select(c => c == '#')];

    [Fact]
    public void Height_HasHysteresis()
    {
        // вход — не выше 0,05; выход — выше 0,07; между — держится прошлое состояние
        float[] height = [0, 0.06f, 0.069f, 0.08f, 0.06f, 0.05f, 0.07f, 0.071f];
        Assert.Equal(Pattern("###..##."), Detect(height, new float[height.Length]));
    }

    [Fact]
    public void Speed_HasHysteresis()
    {
        // вход — медленнее 0,35; выход — быстрее 0,55
        float[] speed = [0.1f, 0.5f, 0.55f, 0.6f, 0.4f, 0.35f, 0.34f, 0.54f, 0.56f];
        Assert.Equal(Pattern("###...##."), Detect(new float[speed.Length], speed));
    }

    [Fact]
    public void ShortInternalRuns_Flip_EdgeRunsStay()
    {
        var parameters = Thresholds with { MinRun = 2 };
        float[] Heights(string pattern) => [.. pattern.Select(c => c == '#' ? 0f : 0.2f)];

        // одиночный отрыв среди опоры и одиночная опора среди переноса — шум; одиночные серии у краёв остаются
        string raw = ".#####.#####....#....#";
        Assert.Equal(Pattern(".###########.........#"), Detect(Heights(raw), new float[raw.Length], parameters));

        // чередование коротких серий сливается слева направо, а не переворачивается в противофазу
        raw = "###.#.###";
        Assert.Equal(Pattern("#########"), Detect(Heights(raw), new float[raw.Length], parameters));

        // серия ровно MinRun — не шум
        raw = "###..###";
        Assert.Equal(Pattern(raw), Detect(Heights(raw), new float[raw.Length], parameters));

        // MinRun = 1 — ничего не переворачивается
        raw = "#.#.#";
        Assert.Equal(Pattern(raw), Detect(Heights(raw), new float[raw.Length], Thresholds));
    }

    [Fact]
    public void Height_IsAboveClipFloor()
    {
        float[] height = [0.08f, 0.1f, 0.2f, 0.14f, 0.09f];
        float[] speed = new float[height.Length];
        var contact = Detect(height, speed);
        Assert.Equal(Pattern("##..#"), contact);
        // тот же клип на полметра выше — те же контакты: «пол» — минимум клипа
        Assert.Equal(contact, Detect([.. height.Select(h => h + 0.5f)], speed));
    }

    [Fact]
    public void EmptyClip_IsFine() => Assert.Empty(Detect([], []));

    [Theory]
    [InlineData("длины")]
    [InlineData("высота выхода ниже входа")]
    [InlineData("скорость выхода ниже входа")]
    [InlineData("серия 0")]
    [InlineData("не число")]
    public void RejectsInconsistentInput(string what)
    {
        float[] height = [0, 0.1f], speed = [0, 0];
        var contact = new bool[2];
        var p = Thresholds;
        switch (what)
        {
            case "длины": speed = [0]; break;
            case "высота выхода ниже входа": p = p with { HeightExit = 0.01f }; break;
            case "скорость выхода ниже входа": p = p with { SpeedExit = 0.1f }; break;
            case "серия 0": p = p with { MinRun = 0 }; break;
            case "не число": p = p with { HeightEnter = float.NaN }; break;
            default: throw new ArgumentOutOfRangeException(nameof(what), what, "нет такого случая");
        }
        Assert.ThrowsAny<ArgumentException>(() => ContactDetector.Detect(height, speed, p, contact));
    }
}
