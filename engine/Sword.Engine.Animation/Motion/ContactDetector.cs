namespace Sword.Engine.Animation.Motion;

/// <summary>
/// Контакт сустава стопы с землёй без разметки из экспорта: низко (над «полом» клипа) и почти не движется; гистерезис по
/// обоим порогам. Внутренние серии короче <see cref="ContactParams.MinRun"/> переворачиваются слева направо со слиянием —
/// чередование коротких серий не уходит в противофазу; серии у краёв (и у шва цикла) не трогаются.
/// </summary>
public static class ContactDetector
{
    /// <summary>Контакты одного сустава по кадрам клипа — в <paramref name="contact"/> (той же длины).</summary>
    /// <param name="height">[кадр] высота сустава, м (в системе персонажа; «пол» — минимум по клипу).</param>
    /// <param name="speed">[кадр] горизонтальная скорость сустава, м/с.</param>
    /// <exception cref="ArgumentException">Длины не совпадают или пороги противоречивы (выход ниже входа, серия меньше 1).</exception>
    public static void Detect(ReadOnlySpan<float> height, ReadOnlySpan<float> speed, in ContactParams parameters, Span<bool> contact)
    {
        Validate(parameters);
        if (speed.Length != height.Length || contact.Length != height.Length)
            throw new ArgumentException($"кадров: высот {height.Length}, скоростей {speed.Length}, контактов {contact.Length} — нужны равные");
        if (height.IsEmpty) return;

        float floor = float.PositiveInfinity;
        foreach (float h in height) floor = MathF.Min(floor, h);
        bool on = false;
        for (int i = 0; i < height.Length; i++)
        {
            float h = height[i] - floor, s = speed[i];
            on = on
                ? h <= parameters.HeightExit && s <= parameters.SpeedExit
                : h <= parameters.HeightEnter && s < parameters.SpeedEnter;
            contact[i] = on;
        }
        RemoveShortRuns(contact, parameters.MinRun);
    }

    /// <summary>Пороги детектора непротиворечивы: конечны, выход не ниже входа, серия ≥ 1.</summary>
    /// <exception cref="ArgumentException">Иначе — сообщение называет, что не так.</exception>
    internal static void Validate(in ContactParams parameters)
    {
        var p = parameters;
        if (!float.IsFinite(p.HeightEnter) || !float.IsFinite(p.HeightExit) || !float.IsFinite(p.SpeedEnter) || !float.IsFinite(p.SpeedExit))
            throw new ArgumentException($"пороги контактов не числа: {p}", nameof(parameters));
        if (p.HeightExit < p.HeightEnter)
            throw new ArgumentException($"высота выхода {p.HeightExit} ниже высоты входа {p.HeightEnter}", nameof(parameters));
        if (p.SpeedExit < p.SpeedEnter)
            throw new ArgumentException($"скорость выхода {p.SpeedExit} меньше скорости входа {p.SpeedEnter}", nameof(parameters));
        if (p.MinRun < 1) throw new ArgumentException($"серия {p.MinRun} кадров, а нужна не меньше 1", nameof(parameters));
    }

    /// <summary>Внутренние серии короче <paramref name="minRun"/> — перевернуть (слева направо, со слиянием).</summary>
    private static void RemoveShortRuns(Span<bool> contact, int minRun)
    {
        int n = contact.Length, start = 0, previous = 0;
        while (start < n)
        {
            int end = start + 1;
            while (end < n && contact[end] == contact[start]) end++;
            if (start > 0 && end < n && end - start < minRun)
            {
                // серия сливается с соседями — проход продолжается с начала прошлой (она уже не короче minRun или у края)
                contact[start..end].Fill(!contact[start]);
                start = previous;
                continue;
            }
            previous = start;
            start = end;
        }
    }
}
