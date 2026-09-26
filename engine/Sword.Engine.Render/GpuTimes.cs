using Sword.Engine.Rhi;

namespace Sword.Engine.Render;

/// <summary>
/// GPU-время последнего разобранного кадра: кадр целиком и его проходы — таймстемпы, которые рендер-граф
/// ставит вокруг каждого прохода сам. Разбор — после ожидания слота кадра в начале следующего кадра с тем же слотом, без стойла:
/// в кадре <c>N</c> видны времена кадра <c>N − 3</c> (<see cref="Lag"/>); после <see cref="Renderer.WaitForIdle"/> (смена
/// размера, снимок, конец прогона) — последнего отправленного (<see cref="Lag"/> = 0). Брошенный кадр не публикуется, номер
/// (<see cref="Frame"/>) только растёт. Данные обновляются на месте (0 байт на кадр): читать на потоке кадра, копировать —
/// если нужны позже.
///
/// Точность: тики GPU по <see cref="TimestampFrequency"/> (обычно 10 МГц — 0,1 мкс и лучше); частота GPU плавает (простаивающий
/// GPU сбрасывает частоту — лёгкий кадр на нём дольше), сопоставления часов CPU и GPU нет (этап 14).
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

    /// <summary>
    /// Сколько кадров отправлено после кадра <see cref="Frame"/>: в установившемся режиме — 3 (кадры в полёте), после ожидания
    /// простоя GPU — 0. Кадр клиента, которому принадлежат времена, — показанный минус <see cref="Lag"/>.
    /// </summary>
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

    /// <summary>
    /// Таймстемпы ставятся (по умолчанию — да; действует со следующего кадра). Выключатель — для замера их цены.
    /// </summary>
    public bool Enabled
    {
        get => Timer.Enabled;
        set => Timer.Enabled = value;
    }

    /// <summary>
    /// Время прохода <paramref name="name"/> (<paramref name="occurrence"/>-го с этим именем в кадре, с 0), мс; нет такого — null.
    /// </summary>
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

/// <summary>Проход кадра с GPU-временем — для итогов прогона и агента (не для каждого кадра: <see cref="RenderPassReport"/>).</summary>
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
