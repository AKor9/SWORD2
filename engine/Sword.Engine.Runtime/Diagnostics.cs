namespace Sword.Engine.Runtime;

/// <summary>
/// Диагностический режим симуляции (например, трасса физики): не меняет ни мир, ни хеш, ни реплей, поэтому включает его
/// владелец симуляции между тиками (<see cref="SimulationDiagnostics.Set"/>), а не команда тика.
/// </summary>
public interface IDiagnosticMode
{
    /// <summary>Стабильный Id — как у инварианта (<see cref="IInvariant.IdRule"/>): <c>physics.trace</c>.</summary>
    string Id { get; }

    /// <summary>Название для людей («трасса физики»).</summary>
    string Title { get; }

    /// <summary>Включён ли режим сейчас.</summary>
    bool Enabled { get; }

    /// <summary>Включить или выключить (между тиками). Повторное включение включённого — без изменений.</summary>
    void SetEnabled(bool enabled);

    /// <summary>Начать окно наблюдения заново (накопленное с включения не трогается); выключенный — ничего.</summary>
    void ResetWindow();
}

/// <summary>Диагностические режимы по порядку регистрации; менять — только между тиками.</summary>
public sealed class SimulationDiagnostics
{
    private readonly (IDiagnosticMode Mode, string Owner)[] _modes;

    internal SimulationDiagnostics(IEnumerable<(IDiagnosticMode Mode, string Owner)> modes) => _modes = [.. modes];

    /// <summary>Режимов.</summary>
    public int Count => _modes.Length;

    /// <summary>Режим <paramref name="index"/> по порядку регистрации: Id, название, владелец (<c>core</c> или Id модуля), включён ли.</summary>
    public DiagnosticInfo this[int index]
    {
        get
        {
            var (mode, owner) = _modes[index];
            return new DiagnosticInfo(mode.Id, mode.Title, owner, mode.Enabled);
        }
    }

    /// <summary>Есть ли режим <paramref name="id"/>.</summary>
    public bool Has(string id) => Find(id) >= 0;

    /// <summary>Включён ли режим <paramref name="id"/>; нет такого — false.</summary>
    public bool IsEnabled(string id) => Find(id) is >= 0 and var i && _modes[i].Mode.Enabled;

    /// <summary>
    /// Включить или выключить режим <paramref name="id"/> (между тиками). Нет такого — <see cref="ArgumentException"/> со списком.
    /// </summary>
    public void Set(string id, bool enabled) => Mode(id).SetEnabled(enabled);

    /// <summary>Начать окно режима <paramref name="id"/> заново (<see cref="IDiagnosticMode.ResetWindow"/>; между тиками).</summary>
    public void ResetWindow(string id) => Mode(id).ResetWindow();

    /// <summary>Включённые режимы — перенести в другую симуляцию (пересборка симуляции): их Id.</summary>
    public IReadOnlyList<string> EnabledIds()
    {
        var ids = new List<string>();
        foreach (var (mode, _) in _modes)
            if (mode.Enabled) ids.Add(mode.Id);
        return ids;
    }

    private IDiagnosticMode Mode(string id)
    {
        int i = Find(id);
        if (i < 0)
            throw new ArgumentException($"диагностического режима «{id}» нет; есть: {(_modes.Length > 0 ? string.Join(", ", _modes.Select(m => m.Mode.Id)) : "никаких")}");
        return _modes[i].Mode;
    }

    private int Find(string id)
    {
        for (int i = 0; i < _modes.Length; i++)
            if (_modes[i].Mode.Id == id) return i;
        return -1;
    }
}

/// <summary>Диагностический режим для людей и инструментов: Id, название, владелец регистрации, включён ли.</summary>
public readonly record struct DiagnosticInfo(string Id, string Title, string Owner, bool Enabled);
