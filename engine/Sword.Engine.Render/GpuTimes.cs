using Sword.Engine.Rhi;

namespace Sword.Engine.Render;

/// <summary>
/// GPU-время последнего разобранного кадра: кадр целиком и его проходы. В кадре N видны времена кадра N − 3
/// (<see cref="Lag"/>), после <see cref="Renderer.WaitForIdle"/> — последнего отправленного. Данные обновляются на месте
/// (0 Б/кадр): читать на потоке кадра. Частота GPU плавает, сопоставления часов CPU и GPU нет.
/// </summary>
public sealed class GpuFrameTimes
{
    private readonly GraphicsDevice _device;
    private GpuTimer Timer => _device.Timer;

    internal GpuFrameTimes(GraphicsDevice device) => _device = device;

    /// <summary>Времена есть (разобран хоть один кадр).</summary>
    public bool Valid => Timer.Sequence > 0;

    /// <summary>Разобранных кадров с начала работы: новое значение — новые времена.</summary>
    public long Sequence => Timer.Sequence;

    /// <summary>Номер кадра рендера (<see cref="RenderFrame.Number"/>), чьи это времена.</summary>
    public ulong Frame => Timer.Frame;

    /// <summary>Кадров отправлено после <see cref="Frame"/>: обычно 3, после ожидания простоя — 0.</summary>
    public int Lag => Valid ? (int)(_device.FrameNumber - 1 - Timer.Frame) : 0;

    /// <summary>Кадр целиком на GPU, мс: от начала списка команд до конца (проходы, барьеры между ними, копии снимка).</summary>
    public double FrameMs => Timer.FrameMs;

    /// <summary>Сумма проходов, мс (не больше <see cref="FrameMs"/>).</summary>
    public double PassesMs => Timer.PassesMs;

    /// <summary>Проходов в кадре (по порядку исполнения).</summary>
    public int Count => Timer.Count;

    /// <summary>Проходов кадра без замера (больше <see cref="GpuTimer.MaxScopes"/> − 1).</summary>
    public int Dropped => Timer.Dropped;

    public string NameOf(int pass) => Timer.NameOf(pass);

    public double MillisecondsOf(int pass) => Timer.MillisecondsOf(pass);

    /// <summary>Частота таймстемпов GPU, Гц.</summary>
    public double TimestampFrequency => Timer.Frequency;

    /// <summary>Таймстемпы ставятся (по умолчанию — да, со следующего кадра); выключатель — для замера их цены.</summary>
    public bool Enabled
    {
        get => Timer.Enabled;
        set => Timer.Enabled = value;
    }

    /// <summary>Время <paramref name="occurrence"/>-го прохода с именем <paramref name="name"/>, мс; нет такого — null.</summary>
    public double? MillisecondsOf(string name, int occurrence = 0)
    {
        var timer = Timer;
        for (int i = 0; i < timer.Count; i++)
            if (timer.NameOf(i) == name && occurrence-- == 0) return timer.MillisecondsOf(i);
        return null;
    }

    /// <summary>Проходы последнего исполненного графа с GPU-временем из этих данных — по имени и номеру вхождения имени.</summary>
    public IReadOnlyList<RenderPassSummary> Join(IReadOnlyList<RenderPassReport> passes)
    {
        var list = new List<RenderPassSummary>(passes.Count);
        for (int i = 0; i < passes.Count; i++)
        {
            var p = passes[i];
            int occurrence = 0;
            for (int k = 0; k < i; k++)
                if (passes[k].Name == p.Name) occurrence++;
            double? ms = Valid ? MillisecondsOf(p.Name, occurrence) : null;
            list.Add(new RenderPassSummary(p.Name, p.Kind, p.Phase, p.Writes, p.Reads, p.Barriers, p.Draws, p.Dispatches,
                ms is { } v ? Math.Round(v, 5) : null));
        }
        return list;
    }

    /// <summary>Сводка для итогов (<c>--json</c>): кадр, отставание, мс кадра и проходов; нет данных — null.</summary>
    public GpuFrameReport? Report() => Valid
        ? new GpuFrameReport(Frame, Lag, Math.Round(FrameMs, 5), Math.Round(PassesMs, 5), Count, Dropped, TimestampFrequency)
        : null;
}

/// <summary>Проход кадра с GPU-временем — для итогов прогона (не для каждого кадра: <see cref="RenderPassReport"/>).</summary>
/// <param name="GpuMs">GPU-время прохода, мс, из <see cref="GpuFrameTimes"/>; null — прохода в том кадре нет или времён нет.</param>
public sealed record RenderPassSummary(
    string Name,
    PassKind Kind,
    FramePhase Phase,
    IReadOnlyList<string> Writes,
    IReadOnlyList<string> Reads,
    int Barriers,
    int Draws,
    int Dispatches,
    double? GpuMs);

/// <summary>GPU-время кадра для итогов: номер кадра рендера, отставание (кадров отправлено после него), мс кадра и проходов.</summary>
public sealed record GpuFrameReport(ulong Frame, int Lag, double FrameMs, double PassesMs, int Passes, int Dropped, double TimestampFrequency);
